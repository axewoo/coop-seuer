using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using ObenseuerCore;
using UnityEngine;

internal static partial class WorldShip
{
    public const int Format = 1;
    public const string BlobName = "join-ticket";
    private const int MaxManifestBytes = 65536;
    private const int MaxFiles = 16;
    private const int MaxNameLength = 80;
    private const int MarkStep = 25;
    private const float SaveTimeout = 15f;
    public const string FreshMode = "fresh";
    public const string ResumeMode = "resume";
    public const string GlobalsFile = "Globals.tnmt";
    public const string DialogueFile = "Globals.dialogue";
    public const string ProfileFile = "profile.json";
    public const string InfoFile = "Info.tnmt";
    public const string SceneSuffix = ".tnmt";
    public const string CoopFolder = "coop";
    public const string WorldIdFile = "world.id";
    private const string AutosaveSlot = "Autosave";
    private const string AutosaveSlot2 = "Autosave2";
    private const int MaxWorldIdLength = 64;
    private const int MaxHostNameLength = 32;

    private static readonly MethodInfo HeaderDeserialize = typeof(SaveController).GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(string), typeof(Type), typeof(string), typeof(Exception).MakeByRefType() }, null);
    private static readonly Type HeaderType = typeof(SaveController).GetNestedType("SaveDataHeader", BindingFlags.NonPublic | BindingFlags.Public);
    private static readonly FieldInfo HeaderLevel = HeaderType == null ? null : HeaderType.GetField("LevelName", BindingFlags.Public | BindingFlags.Instance);

    public static event Action<WorldTicket> Received;
    public static event Action Idle;

    private enum SendState { Idle, Saving, Sending }

    private static SendState state;
    private static ulong guest;
    private static float deadline;
    private static bool saved;
    private static int transfer;
    private static int nextMark;
    private static int blobSize;
    private static bool travel;
    private static DateTime followAt;
    private static WorldTicket latest;
    private static string refusal = "";
    private static string gotoScene;
    private static string gotoEntry;
    private static string stopReason = "";
    private static float stoppedAt = -1000f;

    public static WorldTicket Latest
    {
        get { return latest; }
    }

    public static bool Busy
    {
        get { return state != SendState.Idle; }
    }

    public static string Refusal
    {
        get { return refusal; }
    }

    public static string StopReason
    {
        get { return stopReason; }
    }

    public static float StoppedAt
    {
        get { return stoppedAt; }
    }

    [Serializable]
    public sealed class TicketEntry
    {
        public string name;
        public int size;
        public string sha256;
    }

    [Serializable]
    public sealed class TicketManifest
    {
        public int format;
        public string mode;
        public string worldId;
        public string hostScene;
        public string guest;
        public string hostName;
        public float hostX;
        public float hostY;
        public float hostZ;
        public float hostYaw;
        public string gameBuild;
        public string modVersion;
        public TicketEntry[] files;
    }

    public sealed class WorldTicket
    {
        public readonly TicketManifest Manifest;
        public readonly Dictionary<string, byte[]> Files;

        public WorldTicket(TicketManifest manifest, Dictionary<string, byte[]> files)
        {
            Manifest = manifest;
            Files = files;
        }
    }

    public static void Init()
    {
        NetBlob.Received += OnBlob;
        NetBlob.Finished += OnFinished;
        Events.Saved += OnSaved;
        SaveController.LoadingDone += OnHostLevelLoaded;
    }

    public static bool SendTo(ulong guestSteamId)
    {
        if (!Session.IsHost) return Refuse("this machine is not the host");
        if (!SaveForTicket(guestSteamId, false)) return false;
        travel = false;
        CoopLog.Log.Info("coop: blob save started for " + guestSteamId);
        return true;
    }

    private static bool SaveForTicket(ulong guestSteamId, bool followTicket)
    {
        deferring = false;
        if (Session.LocalSavesDisabled()) return Refuse("the host's difficulty has saving turned off");
        if (state != SendState.Idle) return Refuse("another ticket is still being made");
        if (!Session.Peers.Contains(guestSteamId)) return Refuse(guestSteamId + " is not a lobby member");
        if (!SaveSlots.SaveNow())
        {
            if (followTicket)
            {
                deferring = PendingSaveReason() != null;
                if (deferring) return false;
            }
            return Refuse("the game refused the save");
        }
        guest = guestSteamId;
        saved = false;
        followAt = followTicket ? DateTime.UtcNow : DateTime.MinValue;
        deadline = Time.realtimeSinceStartup + SaveTimeout;
        state = SendState.Saving;
        return true;
    }

    public static bool SendAfterTravel(ulong guestSteamId, DateTime saveNotBefore)
    {
        return SendTravel(guestSteamId, saveNotBefore);
    }

    public static bool SendFollow(ulong guestSteamId)
    {
        if (!Session.IsHost) return Refuse("this machine is not the host");
        if (!SaveForTicket(guestSteamId, true)) return false;
        travel = true;
        CoopLog.Log.Info("coop: follow ticket for " + guestSteamId + " starts a fresh save of the host scene");
        return true;
    }

    public static bool SendGoto(ulong guestSteamId, string scene, string entry)
    {
        if (!Session.IsHost) return Refuse("this machine is not the host");
        if (!SaveForTicket(guestSteamId, true)) return false;
        travel = true;
        gotoScene = scene;
        gotoEntry = entry;
        CoopLog.Log.Info("coop: goto ticket for " + guestSteamId + " to " + scene + " starts a fresh save");
        return true;
    }

    private static bool SendTravel(ulong guestSteamId, DateTime saveNotBefore)
    {
        if (!Session.IsHost) return Refuse("this machine is not the host");
        if (Session.LocalSavesDisabled()) return Refuse("the host's difficulty has saving turned off");
        if (state != SendState.Idle) return Refuse("another ticket is still being made");
        if (!Session.Peers.Contains(guestSteamId)) return Refuse(guestSteamId + " is not a lobby member");
        string reason;
        if (!TravelSaveOk(saveNotBefore, out reason)) return Refuse(reason);
        guest = guestSteamId;
        saved = true;
        transfer = 0;
        travel = true;
        followAt = DateTime.MinValue;
        deadline = Time.realtimeSinceStartup + SaveTimeout;
        state = SendState.Saving;
        CoopLog.Log.Info("coop: travel ticket for " + guestSteamId + " reuses the save ChangeLevel wrote");
        return true;
    }

    private static bool TravelSaveOk(DateTime notBefore, out string reason)
    {
        reason = null;
        string folder = NewestAutosave();
        if (folder == null)
        {
            reason = "there is no autosave folder to send";
            return false;
        }
        if (notBefore != DateTime.MinValue)
        {
            DateTime written = File.GetLastWriteTimeUtc(Path.Combine(folder, GlobalsFile));
            if (written < notBefore.AddSeconds(-2))
            {
                reason = "the newest autosave " + folder + " is older than the save this ticket waits for";
                return false;
            }
        }
        string scene = GameRefs.AreaId;
        if (string.IsNullOrEmpty(scene))
        {
            reason = "the host is not in a world";
            return false;
        }
        string readError;
        string named = SavedLevel(folder, out readError);
        if (readError != null)
        {
            reason = "the ChangeLevel save " + folder + " could not be read: " + readError;
            return false;
        }
        if (!string.Equals(named, scene, StringComparison.Ordinal))
        {
            reason = "the newest autosave names level " + (string.IsNullOrEmpty(named) ? "none" : named) + ", not " + scene;
            return false;
        }
        if (!File.Exists(Path.Combine(Path.Combine(folder, CoopFolder), WorldIdFile)))
        {
            CoopLog.Log.Info("coop: the travel save " + folder + " has no " + CoopFolder + "/" + WorldIdFile + ", the session world id is written there instead");
        }
        return true;
    }

    private static string SavedLevel(string folder, out string error)
    {
        error = null;
        string path = Path.Combine(folder, InfoFile);
        if (!File.Exists(path))
        {
            error = "there is no " + InfoFile;
            return null;
        }
        if (HeaderDeserialize == null || HeaderType == null || HeaderLevel == null)
        {
            error = "the game has no SaveController.SaveDataHeader.LevelName";
            return null;
        }
        object[] args = new object[] { InfoFile, HeaderType, SaveController.ReadSaveFile(path), null };
        object parsed;
        try
        {
            parsed = HeaderDeserialize.Invoke(null, args);
        }
        catch (Exception e)
        {
            error = "the parser threw: " + e.Message;
            return null;
        }
        if (parsed == null)
        {
            Exception inner = args[3] as Exception;
            error = inner != null ? inner.Message : "the parser returned nothing";
            return null;
        }
        return HeaderLevel.GetValue(parsed) as string;
    }

    public static void Tick()
    {
        if (state == SendState.Idle) return;
        if (!Session.IsHost || !Session.Peers.Contains(guest))
        {
            Stop("the guest left");
            return;
        }
        if (state == SendState.Sending) Watch();
        else if (saved) Ship();
        else if (Time.realtimeSinceStartup > deadline) Stop("the save did not finish in time");
    }

    private static void OnSaved()
    {
        if (state == SendState.Saving) saved = true;
    }

    private static void OnFinished(int id, bool delivered)
    {
        if (state != SendState.Sending || id != transfer) return;
        state = SendState.Idle;
        if (delivered) CoopLog.Log.Info("coop: blob delivered to " + guest);
        else CoopLog.Log.Error("coop: blob not delivered to " + guest + " (" + BlobName + ", " + blobSize + " bytes, cancelled, timed out, rejected or the peer left)");
        if (CoopDebug.On("net")) CoopDebug.Event("net", "blob", "the " + BlobName + " blob of " + blobSize + " bytes to " + guest + " " + (delivered ? "was delivered" : "failed"));
        if (Idle != null) Idle();
    }

    private static void Ship()
    {
        if (followAt != DateTime.MinValue)
        {
            string stale;
            if (!TravelSaveOk(followAt, out stale))
            {
                Stop(stale);
                return;
            }
        }
        string folder = NewestAutosave();
        string scene = gotoScene ?? GameRefs.AreaId;
        if (folder == null || string.IsNullOrEmpty(scene))
        {
            Stop("no save folder or scene");
            return;
        }
        List<string> names = new List<string>();
        List<byte[]> data = new List<byte[]>();
        string worldId = EnsureWorldId(folder);
        if (!IsWorldId(worldId))
        {
            Stop("the host has no world id");
            return;
        }
        string error = Collect(folder, scene, names, data, travel);
        if (error != null)
        {
            Stop(error);
            return;
        }
        TicketManifest manifest = new TicketManifest();
        manifest.mode = names.Contains(ProfileFile) ? ResumeMode : FreshMode;
        manifest.worldId = worldId;
        manifest.hostScene = scene;
        manifest.guest = guest.ToString();
        manifest.hostName = HostName();
        Transform body = GameRefs.LocalPlayer;
        if (gotoScene != null)
        {
            int at = names.IndexOf(GlobalsFile);
            if (at >= 0) data[at] = FreeRoam.Retarget(data[at], gotoScene, gotoEntry);
            body = null;
            gotoScene = null;
        }
        if (body != null)
        {
            manifest.hostX = body.position.x;
            manifest.hostY = body.position.y;
            manifest.hostZ = body.position.z;
            manifest.hostYaw = body.eulerAngles.y;
        }
        manifest.gameBuild = Core.GameBuild;
        manifest.modVersion = Plugin.Version;
        byte[] blob = Pack(manifest, names.ToArray(), data.ToArray());
        blobSize = blob.Length;
        transfer = NetBlob.Send(guest, Plugin.PluginGuid, BlobName, blob);
        if (transfer < 0)
        {
            Stop("NetBlob.Send refused the ticket");
            return;
        }
        state = SendState.Sending;
        nextMark = MarkStep;
        CoopLog.Log.Info("coop: blob " + blob.Length + " bytes to " + guest + ":" + Describe(manifest));
        if (CoopDebug.On("world")) CoopDebug.Event("world", "state", "Saving to Sending, the " + BlobName + " blob of " + blob.Length + " bytes goes to " + guest + " as transfer " + transfer);
    }

    private static void Watch()
    {
        float done = NetBlob.Progress(transfer);
        if (done < 0f) return;
        while (nextMark < 100 && done * 100f >= nextMark)
        {
            CoopLog.Log.Info("coop: blob " + nextMark + "% to " + guest);
            nextMark += MarkStep;
        }
    }

    private static void Stop(string reason)
    {
        int id = transfer;
        bool sending = state == SendState.Sending;
        state = SendState.Idle;
        gotoScene = null;
        stopReason = reason;
        stoppedAt = Time.unscaledTime;
        if (sending) NetBlob.Cancel(id);
        CoopLog.Log.Error("coop: blob for " + guest + " stopped: " + reason);
        if (Idle != null) Idle();
    }

    private static bool Refuse(string reason)
    {
        refusal = reason;
        CoopLog.Log.Error("coop: blob refused: " + reason);
        return false;
    }

    private static string NewestAutosave()
    {
        string last = SaveController.LastSaveFolderPath;
        if (string.IsNullOrEmpty(last)) return null;
        string character = Path.GetDirectoryName(last);
        if (string.IsNullOrEmpty(character)) return null;
        string best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (string slot in new string[] { AutosaveSlot, AutosaveSlot2 })
        {
            string folder = Path.Combine(character, slot);
            string globals = Path.Combine(folder, GlobalsFile);
            if (!File.Exists(globals)) continue;
            DateTime written = File.GetLastWriteTimeUtc(globals);
            if (best == null || written > bestTime)
            {
                best = folder;
                bestTime = written;
            }
        }
        return best;
    }

    private static string Collect(string folder, string scene, List<string> names, List<byte[]> data, bool travel)
    {
        foreach (string name in new string[] { GlobalsFile, scene + SceneSuffix, DialogueFile, InfoFile, WorldIdFile })
        {
            string path = name == WorldIdFile ? Path.Combine(Path.Combine(folder, CoopFolder), WorldIdFile) : Path.Combine(folder, name);
            if (name == scene + SceneSuffix) path = FreeRoam.NewestScene(folder, scene) ?? path;
            if (!File.Exists(path))
            {
                if (name == InfoFile || name == WorldIdFile) continue;
                if (travel && name == scene + SceneSuffix) continue;
                return "missing " + name + " in " + folder;
            }
            names.Add(name);
            data.Add(File.ReadAllBytes(path));
        }
        string profile = ProfileStore.Find(folder, guest);
        if (profile != null)
        {
            names.Add(ProfileFile);
            data.Add(File.ReadAllBytes(profile));
        }
        return null;
    }

    private static string EnsureWorldId(string folder)
    {
        string coop = Path.Combine(folder, CoopFolder);
        string path = Path.Combine(coop, WorldIdFile);
        string owner = Session.WorldSlot;
        if (owner.Length == 0) owner = SaveOwner(folder);
        try
        {
            if (File.Exists(path))
            {
                string existing = File.ReadAllText(path).Trim();
                if (IsWorldId(existing))
                {
                    Session.AdoptWorldId(existing, owner);
                    return existing;
                }
            }
            string id = Session.WorldIdFor(owner);
            if (!IsWorldId(id))
            {
                id = Guid.NewGuid().ToString("N");
                Session.AdoptWorldId(id, owner);
            }
            Directory.CreateDirectory(coop);
            File.WriteAllText(path, id, new UTF8Encoding(false));
            CoopLog.Log.Info("coop: world id " + id + " written to " + path);
            return id;
        }
        catch (Exception e)
        {
            CoopLog.Log.Error("coop: could not write the world id: " + e.Message);
            return "";
        }
    }

    private static string SaveOwner(string folder)
    {
        string owner = Path.GetDirectoryName(folder);
        return owner == null ? "" : owner;
    }

    private static string HostName()
    {
        string name = Session.DisplayName(Session.Me);
        if (name.Length > MaxHostNameLength) name = name.Substring(0, MaxHostNameLength);
        return name;
    }

    private static bool IsWorldId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxWorldIdLength) return false;
        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) continue;
            return false;
        }
        return true;
    }

    private static void OnBlob(ulong peer, string modId, string name, byte[] data)
    {
        if (modId != Plugin.PluginGuid || name != BlobName) return;
        if (Session.IsHost || peer != Session.HostId)
        {
            CoopLog.Log.Error("coop: ticket ignored from " + peer + ": not the host");
            return;
        }
        WorldTicket ticket;
        string error;
        if (!Unpack(data, out ticket, out error))
        {
            CoopLog.Log.Error("coop: ticket refused from " + peer + ": " + error);
            return;
        }
        latest = ticket;
        CoopLog.Log.Info("coop: ticket ok from " + peer + ":" + Describe(ticket.Manifest));
        if (CoopDebug.On("world")) CoopDebug.Event("world", "recv", "the " + BlobName + " blob of " + (data == null ? 0 : data.Length) + " bytes arrived from " + peer
            + ", scene " + ticket.Manifest.hostScene + ", mode " + ticket.Manifest.mode + ", files " + ticket.Files.Count);
        if (Received != null) Received(ticket);
    }
}
