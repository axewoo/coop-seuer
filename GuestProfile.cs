using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ObenseuerCore;
using UnityEngine;

internal static class GuestProfile
{
    public const string Channel = "profile";
    public const string BlobName = "profile";
    private const float Periodic = 60f;
    private const float Debounce = 2f;
    private const float Resend = 10f;
    private const float SettleSeconds = 5f;
    private const float PendingTtl = 300f;
    private const int MaxBytes = 8 * 1024 * 1024;
    private const string DocumentName = "Profile";
    private static readonly string[] CoreGuids = { "Inventory", "Money", "PlayerStats", "PlayerIdentity", "PlayerHandItems" };

    private static readonly Type HeaderType = typeof(SaveController).GetNestedType("SaveDataHeader", BindingFlags.NonPublic | BindingFlags.Public);
    private static readonly Type EntryType = typeof(SaveController).GetNestedType("ObjectDataHeader", BindingFlags.NonPublic | BindingFlags.Public);
    private static readonly FieldInfo GlobalField = typeof(SaveController).GetField("tempSavedata_Global", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly FieldInfo LevelField = typeof(SaveController).GetField("tempSavedata_Level", BindingFlags.NonPublic | BindingFlags.Static);
    private static bool installed;
    private static float nextPeriodic;
    private static float nextAllowed;
    private static float nextTransfer;
    private static float lastSend;
    private static byte[] pending;
    private static byte[] lastPacked;
    private static string pendingSha = "";
    private static string ackedSha = "";
    private static bool due;
    private static string dueReason = "";
    private static float loadedAt;
    private static float pendingAt;

    public static bool Ready
    {
        get { return HeaderType != null && EntryType != null && GlobalField != null && LevelField != null; }
    }

    public static string LastAck
    {
        get { return ackedSha; }
    }

    public static void Init()
    {
        if (installed) return;
        installed = true;
        SaveController.SavingDone += OnSavingDone;
        NetBlob.Finished += OnFinished;
        if (Ready) CoopLog.Log.Info("coop: profile capture ready");
        else CoopLog.Log.Error("coop: the profile capture cannot see SaveController.SaveDataHeader or tempSavedata_Global, guest profiles are off");
    }

    public static void Tick()
    {
        try
        {
            if (!GuestSession.Loaded)
            {
                if (pending != null && Time.unscaledTime - pendingAt > PendingTtl)
                {
                    CoopLog.Log.Info("coop: the pending profile waited " + PendingTtl + " s without a load or a PACK and was dropped");
                    Forget();
                }
                loadedAt = 0f;
                return;
            }
            if (loadedAt == 0f)
            {
                loadedAt = Time.unscaledTime;
                lastPacked = null;
            }
            float now = Time.unscaledTime;
            if (due)
            {
                if (!Settled() || SaveData.Busy) return;
                Capture(dueReason);
                return;
            }
            if (now >= nextPeriodic)
            {
                if (!Settled() || SaveData.Busy) return;
                Capture("timer");
                return;
            }
            if (Session.IsGuest && pending != null && pendingSha != ackedSha && now - lastSend >= Resend && now >= nextAllowed) Send("resend");
        }
        catch (Exception e)
        {
            CoopLog.Once("GuestProfile.Tick", e);
        }
    }

    public static void Capture(string reason)
    {
        if (CoopDebug.On("prof")) CoopDebug.Event("prof", "ask", "a profile capture was asked for by '" + reason + "'");
        float now = Time.unscaledTime;
        if (now < nextAllowed) return;
        if (!GuestSession.Loaded) return;
        if (!Settled())
        {
            CoopLog.Log.Debug("coop.profile", "capture waits for the spawn to settle");
            if (CoopDebug.On("prof")) CoopDebug.Event("prof", "skip", "the capture asked for by '" + reason + "' was skipped, the spawn has settled for " + (loadedAt <= 0f ? 0f : Time.unscaledTime - loadedAt).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s of " + SettleSeconds);
            return;
        }
        if (SaveData.Busy)
        {
            CoopLog.Log.Debug("coop.profile", "capture skipped: the game is saving");
            if (CoopDebug.On("prof")) CoopDebug.Event("prof", "skip", "the capture asked for by '" + reason + "' was skipped, the game is saving");
            return;
        }
        nextAllowed = now + Debounce;
        nextPeriodic = now + Periodic;
        string error;
        byte[] bytes = Build(out error);
        if (bytes == null)
        {
            CoopLog.Log.Error("coop: profile capture (" + reason + ") failed: " + error);
            return;
        }
        Mirror(bytes);
        due = false;
        dueReason = "";
        if (!Session.IsGuest) return;
        pending = bytes;
        pendingSha = Sha8(bytes);
        pendingAt = Time.unscaledTime;
        Send(reason);
    }

    public static byte[] Build(out string error)
    {
        error = null;
        if (!Ready)
        {
            error = "the save controller header or list fields are gone";
            return null;
        }
        List<SavableScript> players = PlayerObjects(out error);
        if (players == null) return null;
        object oldGlobal = GlobalField.GetValue(null);
        object oldLevel = LevelField.GetValue(null);
        IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(EntryType));
        string json;
        try
        {
            GlobalField.SetValue(null, list);
            LevelField.SetValue(null, list);
            for (int i = 0; i < players.Count; i++)
            {
                try
                {
                    players[i].OnSavingGame();
                }
                catch (Exception e)
                {
                    CoopLog.Once("GuestProfile.OnSavingGame", e);
                }
            }
            if (list.Count == 0)
            {
                error = "no player entry was saved";
                return null;
            }
            object header = Activator.CreateInstance(HeaderType, list, DocumentName, GuestSession.Scene, "coop", "NONE");
            json = SaveController.Serialize(HeaderType, header);
        }
        catch (Exception e)
        {
            error = "the serialization threw: " + e.Message;
            return null;
        }
        finally
        {
            GlobalField.SetValue(null, oldGlobal);
            LevelField.SetValue(null, oldLevel);
        }
        byte[] bytes = new UTF8Encoding(false).GetBytes(json);
        if (bytes.Length > MaxBytes)
        {
            error = "the profile is " + bytes.Length + " bytes";
            return null;
        }
        if (!GuestSplice.IsProfile(bytes, out error)) return null;
        if (!Complete(bytes, players, out error)) return null;
        return bytes;
    }

    private static bool Complete(byte[] bytes, List<SavableScript> players, out string error)
    {
        error = null;
        List<string> entries;
        if (!GuestSplice.Guids(bytes, out entries, out error)) return false;
        HashSet<string> have = new HashSet<string>(entries, StringComparer.Ordinal);
        if (have.Count != entries.Count)
        {
            error = "the capture holds one guid twice";
            return false;
        }
        if (entries.Count != players.Count)
        {
            error = "the capture produced " + entries.Count + " entries for " + players.Count + " player objects";
            return false;
        }
        for (int i = 0; i < players.Count; i++)
        {
            string guid = SaveController.GetObjectGUID(players[i]);
            if (guid == null || !have.Contains(guid))
            {
                error = "the player object " + (guid == null ? "without a guid" : guid) + " produced no entry";
                return false;
            }
        }
        for (int i = 0; i < CoreGuids.Length; i++)
        {
            if (!have.Contains(CoreGuids[i]))
            {
                error = "the core player entry " + CoreGuids[i] + " is missing";
                return false;
            }
        }
        if (lastPacked == null) return true;
        List<string> stored;
        if (!GuestSplice.Guids(lastPacked, out stored, out error)) return false;
        for (int i = 0; i < stored.Count; i++)
        {
            if (!have.Contains(stored[i]))
            {
                error = "the entry " + stored[i] + " of the stored profile is missing from the capture";
                return false;
            }
        }
        return true;
    }

    private static List<SavableScript> PlayerObjects(out string error)
    {
        error = null;
        ISet<string> wanted = GuestSplice.RemoveSet();
        SavableScript[] all;
        try
        {
            all = UnityEngine.Object.FindObjectsByType<SavableScript>(FindObjectsSortMode.None);
        }
        catch (Exception e)
        {
            error = "the savable scan threw: " + e.Message;
            return null;
        }
        List<SavableScript> found = new List<SavableScript>();
        HashSet<string> guids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < all.Length; i++)
        {
            SavableScript savable = all[i];
            if (savable == null) continue;
            string guid = SaveController.GetObjectGUID(savable);
            if (guid == null || !wanted.Contains(guid)) continue;
            if (!guids.Add(guid)) continue;
            found.Add(savable);
        }
        if (found.Count == 0)
        {
            error = "none of the " + wanted.Count + " player objects are loaded";
            return null;
        }
        found.Sort(CompareGuid);
        return found;
    }

    private static int CompareGuid(SavableScript left, SavableScript right)
    {
        string a = SaveController.GetObjectGUID(left);
        string b = SaveController.GetObjectGUID(right);
        if (a == null) a = "";
        if (b == null) b = "";
        return string.CompareOrdinal(a, b);
    }

    public static bool Send(string reason)
    {
        if (pending == null || !GuestSession.Loaded || !Session.IsGuest) return false;
        ulong host = GuestSession.HostId;
        if (host == 0) return false;
        lastSend = Time.unscaledTime;
        int id = NetBlob.Send(host, Plugin.PluginGuid, BlobName, pending);
        if (id < 0)
        {
            CoopLog.Log.Error("coop: the profile blob was refused for " + host);
            return false;
        }
        CoopLog.Log.Info("coop: profile " + pending.Length + " bytes (" + pendingSha + ", " + reason + ") to " + host);
        return true;
    }

    public static void Stage(byte[] bytes, string reason)
    {
        if (bytes == null || bytes.Length == 0) return;
        lastPacked = bytes;
        pending = bytes;
        pendingSha = Sha8(bytes);
        pendingAt = Time.unscaledTime;
        lastSend = 0f;
        ackedSha = "";
        nextPeriodic = Time.unscaledTime + Periodic;
        CoopLog.Log.Info("coop: the profile is staged for the host (" + bytes.Length + " bytes, " + pendingSha + ", " + reason + ")");
    }

    public static void OnQuit()
    {
        if (!GuestSession.Loaded) return;
        byte[] bytes = null;
        if (!SaveData.Busy)
        {
            string error;
            bytes = Build(out error);
            if (bytes == null) CoopLog.Log.Error("coop: the quit capture failed: " + error);
        }
        if (bytes == null) bytes = pending;
        if (bytes != null) Mirror(bytes);
    }

    public static void FlushNow(string reason)
    {
        if (!GuestSession.Loaded) return;
        due = true;
        dueReason = reason;
        nextAllowed = 0f;
    }

    public static void AfterTransfer()
    {
        if (!Session.IsGuest || !GuestSession.InHostWorld || SaveData.Busy) return;
        float now = Time.unscaledTime;
        if (now < nextTransfer) return;
        nextTransfer = now + Debounce;
        FlushNow("a transfer moved items or money");
    }

    public static void MirrorNow()
    {
        if (SaveData.Busy) return;
        string error;
        byte[] bytes = Build(out error);
        if (bytes == null)
        {
            CoopLog.Log.Error("coop: the follow profile capture failed: " + error);
            return;
        }
        Mirror(bytes);
    }

    public static void OnLeave()
    {
        if (!GuestSession.Loaded) return;
        due = true;
        dueReason = "leaving the world";
        Capture(dueReason);
        if (pending != null) pendingAt = Time.unscaledTime;
    }

    public static void OnChannel(ulong from, string text)
    {
        if (!Session.IsGuest || from != Session.HostId) return;
        if (text == null)
        {
            CoopLog.Log.Debug("coop.profile", "ignored an empty profile message");
            return;
        }
        if (text.StartsWith("NAK|", StringComparison.Ordinal))
        {
            Refused(text.Substring(4).Trim());
            return;
        }
        if (!text.StartsWith("PACK|", StringComparison.Ordinal))
        {
            CoopLog.Log.Debug("coop.profile", "ignored the profile message '" + text + "'");
            return;
        }
        string sha = text.Substring(5).Trim();
        bool ours = string.Equals(sha, pendingSha, StringComparison.Ordinal);
        if (ours && pending != null) lastPacked = pending;
        ackedSha = sha;
        if (ours)
        {
            pending = null;
            pendingSha = "";
            pendingAt = 0f;
        }
        CoopLog.Log.Info("coop: the host stored the profile " + sha);
    }

    private static void Refused(string reason)
    {
        string kind = reason;
        string sha = "";
        int at = reason.IndexOf('|');
        if (at >= 0)
        {
            kind = reason.Substring(0, at);
            sha = reason.Substring(at + 1).Trim();
        }
        CoopLog.Log.Error("coop: the host refused the profile (" + kind + ", " + (sha.Length == 0 ? "no sha" : sha) + ")");
        if (sha.Length == 0 || !string.Equals(sha, pendingSha, StringComparison.Ordinal))
        {
            CoopLog.Log.Info("coop: the refusal does not name the pending profile, so it is kept for a resend");
            return;
        }
        pending = null;
        pendingSha = "";
        pendingAt = 0f;
        lastSend = 0f;
    }

    private static bool Settled()
    {
        if (loadedAt <= 0f) return false;
        return Time.unscaledTime - loadedAt >= SettleSeconds;
    }

    private static void Forget()
    {
        pending = null;
        pendingSha = "";
        pendingAt = 0f;
        ackedSha = "";
        due = false;
        dueReason = "";
        nextPeriodic = 0f;
        nextAllowed = 0f;
        loadedAt = 0f;
        lastPacked = null;
    }

    private static void OnSavingDone()
    {
        try
        {
            if (!GuestSession.Loaded) return;
            due = true;
            dueReason = "the autosave finished";
        }
        catch (Exception e)
        {
            CoopLog.Once("GuestProfile.OnSavingDone", e);
        }
    }

    private static void OnFinished(int id, bool delivered)
    {
        if (!delivered && pending != null)
        {
            CoopLog.Log.Error("coop: the profile blob was not delivered, it will be resent");
            lastSend = 0f;
        }
    }

    private static void Mirror(byte[] bytes)
    {
        string folder = GuestSession.Folder;
        if (string.IsNullOrEmpty(folder)) return;
        string full;
        if (!GuestWorld.PathOk(folder, out full)) return;
        string path = Path.Combine(full, WorldShip.ProfileFile);
        string temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(full);
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path);
        }
        catch (Exception e)
        {
            CoopLog.Once("GuestProfile.Mirror", e);
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (Exception)
            {
            }
        }
    }

    public static string Sha8(byte[] data)
    {
        using (SHA256 sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(data);
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < 4; i++) text.Append(hash[i].ToString("x2"));
            return text.ToString();
        }
    }
}
