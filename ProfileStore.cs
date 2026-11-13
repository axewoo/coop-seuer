using System;
using System.Collections.Generic;
using System.IO;
using ObenseuerCore;
using UnityEngine;

internal static class ProfileStore
{
    private const int MaxBytes = 8 * 1024 * 1024;
    private const string Suffix = ".json";
    private const string AutosaveSlot = "Autosave";
    private const string AutosaveSlot2 = "Autosave2";
    private static readonly Dictionary<ulong, float> lastStoredAt = new Dictionary<ulong, float>();
    private static bool installed;

    public static void Init()
    {
        if (installed) return;
        installed = true;
        NetBlob.Received += OnBlob;
        CoopLog.Log.Info("coop: host profile store ready");
    }

    private static void OnBlob(ulong peer, string modId, string name, byte[] data)
    {
        try
        {
            if (modId != Plugin.PluginGuid || name != GuestProfile.BlobName) return;
            if (!Session.IsHost) return;
            if (!Session.Peers.Contains(peer))
            {
                CoopLog.Log.Error("coop: a profile from " + peer + " was ignored, that peer is not in the lobby");
                return;
            }
            Receive(peer, data);
        }
        catch (Exception e)
        {
            CoopLog.Once("ProfileStore.OnBlob", e);
        }
    }

    public static bool Receive(ulong guest, byte[] data)
    {
        string error;
        if (data == null || data.Length == 0 || data.Length > MaxBytes)
        {
            CoopLog.Log.Error("coop: the profile from " + guest + " is " + (data == null ? 0 : data.Length) + " bytes");
            Refuse(guest, "invalid", data);
            return false;
        }
        if (!GuestSplice.IsProfile(data, out error))
        {
            CoopLog.Log.Error("coop: the profile from " + guest + " was refused: " + error);
            Refuse(guest, "invalid", data);
            return false;
        }
        List<string> paths = PathsFor(guest, out error);
        if (paths == null)
        {
            if (string.IsNullOrEmpty(SaveController.LastSaveFolderPath) || string.IsNullOrEmpty(GameRefs.AreaId))
            {
                CoopLog.Log.Info("coop: the profile from " + guest + " is waiting, the host is not in a world and the guest retries the upload later");
                return false;
            }
            CoopLog.Log.Error("coop: the profile from " + guest + " was not stored: " + error);
            if (CoopDebug.On("prof")) CoopDebug.Event("prof", "nostore", "the profile of " + guest + " was not stored: " + error);
            return false;
        }
        string stale;
        if (Stale(paths, data, out stale))
        {
            CoopLog.Log.Error("coop: the profile from " + guest + " was refused: " + stale);
            Refuse(guest, "stale", data);
            return false;
        }
        int written = 0;
        for (int i = 0; i < paths.Count; i++)
        {
            if (Write(paths[i], data, out error)) written++;
            else CoopLog.Log.Error("coop: the profile of " + guest + " was not written to " + paths[i] + ": " + error);
        }
        if (written == 0)
        {
            CoopLog.Log.Error("coop: the profile from " + guest + " reached no slot");
            return false;
        }
        lastStoredAt[guest] = Time.unscaledTime;
        string sha = GuestProfile.Sha8(data);
        Session.Send(guest, GuestProfile.Channel, "PACK|" + sha, true);
        CoopLog.Log.Info("coop: stored the profile of " + guest + " in " + written + " of " + paths.Count + " slot(s) (" + data.Length + " bytes, " + sha + ")");
        return true;
    }

    private static void Refuse(ulong guest, string kind, byte[] data)
    {
        if (data == null || data.Length == 0) return;
        Session.Send(guest, GuestProfile.Channel, "NAK|" + kind + "|" + GuestProfile.Sha8(data), true);
    }

    public static bool StoredAfter(ulong guest, float when)
    {
        float at;
        if (!lastStoredAt.TryGetValue(guest, out at)) return false;
        return at >= when;
    }

    public static List<string> PathsFor(ulong guest, out string error)
    {
        List<string> folders = LocalFolders(out error);
        if (folders == null) return null;
        List<string> paths = new List<string>();
        for (int i = 0; i < folders.Count; i++) paths.Add(Path.Combine(folders[i], guest.ToString() + Suffix));
        return paths;
    }

    internal static List<string> LocalFolders(out string error)
    {
        error = null;
        string last = SaveController.LastSaveFolderPath;
        if (string.IsNullOrEmpty(last))
        {
            error = "the host has no save folder yet";
            return null;
        }
        List<string> folders = new List<string>();
        Add(folders, last);
        AddSlots(folders, last);
        List<string> dirs = new List<string>();
        for (int i = 0; i < folders.Count; i++)
        {
            string dir = Path.Combine(folders[i], WorldShip.CoopFolder);
            string full;
            if (!Safe(dir, out full))
            {
                error = "the coop folder " + dir + " is outside the game data folder";
                return null;
            }
            dirs.Add(full);
        }
        if (dirs.Count == 0)
        {
            error = "the host has no save folder yet";
            return null;
        }
        return dirs;
    }

    public static string Find(string folder, ulong guest)
    {
        DateTime when;
        return Newest(Candidates(folder, guest), out when);
    }

    public static List<string> Candidates(string folder, ulong guest)
    {
        List<string> folders = new List<string>();
        Add(folders, folder);
        Add(folders, SaveController.LastSaveFolderPath);
        AddSlots(folders, folder);
        AddSlots(folders, SaveController.LastSaveFolderPath);
        List<string> paths = new List<string>();
        for (int i = 0; i < folders.Count; i++)
        {
            string path = Path.Combine(Path.Combine(folders[i], WorldShip.CoopFolder), guest.ToString() + Suffix);
            try
            {
                if (File.Exists(path)) paths.Add(path);
            }
            catch (Exception)
            {
            }
        }
        return paths;
    }

    private static void AddSlots(List<string> folders, string folder)
    {
        string character = Character(folder);
        if (string.IsNullOrEmpty(character)) return;
        string one = Path.Combine(character, AutosaveSlot);
        if (Directory.Exists(one)) Add(folders, one);
        string two = Path.Combine(character, AutosaveSlot2);
        if (Directory.Exists(two)) Add(folders, two);
    }

    private static string Character(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return null;
        try
        {
            string trimmed = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetDirectoryName(trimmed);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Add(List<string> folders, string folder)
    {
        if (string.IsNullOrEmpty(folder)) return;
        string full;
        try
        {
            full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return;
        }
        for (int i = 0; i < folders.Count; i++)
        {
            if (string.Equals(folders[i], full, StringComparison.OrdinalIgnoreCase)) return;
        }
        folders.Add(full);
    }

    private static bool Stale(List<string> paths, byte[] data, out string reason)
    {
        reason = null;
        DateTime stored;
        string newest = Newest(paths, out stored);
        if (newest == null) return false;
        DateTimeOffset incoming;
        if (!ProfileDate.TryRead(data, out incoming)) return false;
        if (incoming.UtcDateTime >= stored) return false;
        reason = "the upload is dated " + incoming.UtcDateTime.ToString("o") + ", older than the copy at " + newest + " (" + stored.ToString("o") + ")";
        return true;
    }

    private static string Newest(List<string> paths, out DateTime when)
    {
        when = DateTime.MinValue;
        string best = null;
        DateTime bestWritten = DateTime.MinValue;
        for (int i = 0; i < paths.Count; i++)
        {
            DateTime stamp;
            DateTime written;
            if (!Written(paths[i], out stamp, out written)) continue;
            if (best == null || stamp > when || (stamp == when && written > bestWritten))
            {
                best = paths[i];
                when = stamp;
                bestWritten = written;
            }
        }
        return best;
    }

    private static bool Written(string path, out DateTime when, out DateTime written)
    {
        when = DateTime.MinValue;
        written = DateTime.MinValue;
        try
        {
            if (!File.Exists(path)) return false;
            DateTimeOffset stamp;
            if (!ProfileDate.TryRead(path, out stamp)) return false;
            when = stamp.UtcDateTime;
            written = File.GetLastWriteTimeUtc(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool Safe(string path, out string full)
    {
        full = null;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return false;
        }
        string root = Path.GetFullPath(Application.persistentDataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Write(string path, byte[] data, out string error)
    {
        error = null;
        string temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(temp, data);
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path);
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (Exception)
            {
            }
            return false;
        }
    }

    public static bool SelfCheck()
    {
        string root = Path.Combine(Path.GetTempPath(), "coop-profile-store-" + Guid.NewGuid().ToString("N"));
        bool ok = false;
        try
        {
            const ulong guest = 424242;
            string character = Path.Combine(root, "Connor_R");
            string last = Path.Combine(character, "Slot9");
            string one = Path.Combine(character, AutosaveSlot);
            string two = Path.Combine(character, AutosaveSlot2);
            string error;
            string older = Path.Combine(one, WorldShip.CoopFolder, guest.ToString() + Suffix);
            string newer = Path.Combine(two, WorldShip.CoopFolder, guest.ToString() + Suffix);
            string broken = Path.Combine(last, WorldShip.CoopFolder, guest.ToString() + Suffix);
            Directory.CreateDirectory(Path.GetDirectoryName(older));
            Directory.CreateDirectory(Path.GetDirectoryName(newer));
            Directory.CreateDirectory(Path.GetDirectoryName(broken));
            bool wrote = Write(older, Document("2026-10-16T18:06:23.1033859-04:00"), out error);
            wrote = wrote && Write(newer, Document("2026-10-16T20:06:23.1033859-04:00"), out error);
            wrote = wrote && Write(broken, new byte[] { 7, 7, 7 }, out error);
            File.SetLastWriteTimeUtc(older, new DateTime(2026, 10, 16, 23, 6, 23, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(newer, new DateTime(2026, 10, 16, 22, 6, 23, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(broken, new DateTime(2026, 10, 17, 1, 6, 23, DateTimeKind.Utc));
            ok = wrote && string.Equals(Find(last, guest), newer, StringComparison.OrdinalIgnoreCase);
            bool replaced = Write(newer, Document("2026-10-16T20:06:23.1033859-04:00"), out error);
            File.SetLastWriteTimeUtc(newer, new DateTime(2026, 10, 16, 22, 6, 23, DateTimeKind.Utc));
            ok = ok && replaced && File.Exists(newer + ".bak") && string.Equals(Find(last, guest), newer, StringComparison.OrdinalIgnoreCase);
            File.SetLastWriteTimeUtc(broken, new DateTime(2026, 10, 17, 1, 6, 23, DateTimeKind.Utc));
            ok = ok && string.Equals(Find(last, guest), newer, StringComparison.OrdinalIgnoreCase);
            ok = ok && Write(broken, Document("2026-10-16T20:06:23.1033859-04:00"), out error);
            File.SetLastWriteTimeUtc(broken, new DateTime(2026, 10, 16, 22, 6, 24, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(newer, new DateTime(2026, 10, 16, 22, 6, 23, DateTimeKind.Utc));
            ok = ok && string.Equals(Find(last, guest), broken, StringComparison.OrdinalIgnoreCase);
            File.SetLastWriteTimeUtc(broken, new DateTime(2026, 10, 16, 22, 6, 22, DateTimeKind.Utc));
            ok = ok && string.Equals(Find(last, guest), newer, StringComparison.OrdinalIgnoreCase);
            ok = ok && Find(last, guest + 1) == null;
        }
        catch (Exception e)
        {
            CoopLog.Once("ProfileStore.SelfCheck", e);
            ok = false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (Exception)
            {
            }
        }
        CoopLog.Log.Info("coop: profile store self check " + (ok ? "passed" : "FAILED"));
        return ok;
    }

    private static byte[] Document(string date)
    {
        string json = "{\"SaveName\":\"Profile\",\"LevelName\":\"Open Sewer Tenement\",\"CharacterName\":\"coop\",\"Date\":\"" + date + "\",\"Data\":[]}";
        return new System.Text.UTF8Encoding(false).GetBytes(json);
    }
}
