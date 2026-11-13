using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using ObenseuerCore;
using UnityEngine;

internal static partial class PrisonStore
{
    private const string FilePrefix = "prison.";
    private const string FileSuffix = ".json";
    private const int MaxRecord = 4 * 1024 * 1024;
    private const float PeerCooldown = 5f;
    private const float PeerPrune = 30f;
    private const string Blob = "blob";
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Dictionary<string, float> peerAt = new Dictionary<string, float>();
    private static float nextPeerPrune;
    private static string notedFolder;

    private static List<string> Paths(string id, out string error)
    {
        error = null;
        if (Session.IsGuest)
        {
            List<string> mirror = MirrorPaths(id);
            if (mirror == null)
            {
                error = "the guest world folder is not known yet";
                return null;
            }
            return mirror;
        }
        List<string> paths = LocalPaths(id);
        if (paths == null) error = "the host has no save folder yet";
        return paths;
    }

    private static List<string> LocalPaths(string id)
    {
        string error;
        List<string> folders = ProfileStore.LocalFolders(out error);
        if (folders == null) return null;
        List<string> paths = new List<string>();
        for (int i = 0; i < folders.Count; i++) paths.Add(Path.Combine(folders[i], FilePrefix + id + FileSuffix));
        return paths;
    }

    private static List<string> MirrorPaths(string id)
    {
        string folder = GuestSession.Folder;
        if (string.IsNullOrEmpty(folder)) return null;
        string full;
        if (!GuestWorld.PathOk(folder, out full)) return null;
        List<string> paths = new List<string>();
        paths.Add(Path.Combine(full, FilePrefix + id + FileSuffix));
        return paths;
    }

    private static bool Store(Record rec, byte[] data)
    {
        if (data == null)
        {
            CoopLog.Log.Error("coop: prison: the record of " + rec.steamId + " is larger than " + MaxRecord + " bytes and was not stored");
            return false;
        }
        string error;
        List<string> paths = Paths(rec.steamId, out error);
        if (paths == null)
        {
            CoopLog.Log.Error("coop: prison: the record of " + rec.steamId + " was not stored: " + error);
            return false;
        }
        int written = 0;
        for (int i = 0; i < paths.Count; i++)
        {
            if (WriteFile(paths[i], data, out error)) written++;
            else CoopLog.Log.Error("coop: prison: the record of " + rec.steamId + " was not written to " + paths[i] + ": " + error);
        }
        if (written == 0)
        {
            CoopLog.Log.Error("coop: prison: the record of " + rec.steamId + " reached no folder");
            return false;
        }
        NoteFolder(paths[0]);
        return true;
    }

    private static Record Read(string id, out string from)
    {
        from = null;
        string error;
        List<string> paths = Paths(id, out error);
        if (paths == null) return null;
        Record best = null;
        for (int i = 0; i < paths.Count; i++)
        {
            Record found = ReadFile(paths[i]);
            if (found == null || found.steamId != id) continue;
            if (best == null || found.savedAt > best.savedAt)
            {
                best = found;
                from = paths[i];
            }
        }
        return best;
    }

    private static Record ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            byte[] data = File.ReadAllBytes(path);
            return Parse(Text(data));
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonStore.ReadFile", e);
            return null;
        }
    }

    private static void DeleteFiles(string id)
    {
        Remove(LocalPaths(id));
        Remove(MirrorPaths(id));
    }

    private static void Remove(List<string> paths)
    {
        if (paths == null) return;
        for (int i = 0; i < paths.Count; i++)
        {
            Gone(paths[i]);
            Gone(paths[i] + ".bak");
            Gone(paths[i] + ".tmp");
        }
    }

    private static void Gone(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonStore.DeleteFiles", e);
        }
    }

    private static bool WriteFile(string path, byte[] data, out string error)
    {
        error = null;
        string temp = path + ".tmp";
        string backup = path + ".bak";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(temp, data);
            if (File.Exists(path))
            {
                File.Replace(temp, path, backup);
                Gone(backup);
            }
            else
            {
                File.Move(temp, path);
            }
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            Gone(temp);
            return false;
        }
    }

    private static void NoteFolder(string path)
    {
        string folder = Path.GetDirectoryName(path);
        if (folder == null || folder == notedFolder) return;
        notedFolder = folder;
        CoopLog.Log.Info("coop: prison: the store resolved its folders, the record is written to " + folder);
    }

    private static void Handle(ulong from, string payload)
    {
        try
        {
            if (!ready || !PrisonLocal.Ready) return;
            string kind;
            ulong id;
            if (!PrisonStoreWire.TryParse(payload, out kind, out id)) return;
            if (!Session.IsHost || !Session.Peers.Contains(from) || id != from) return;
            if (!RateOk(from, kind)) return;
            if (kind == PrisonStoreWire.Request) Answer(from);
            else if (kind == PrisonStoreWire.Released) MarkReleased(PrisonStoreWire.Id(from));
            else if (kind == PrisonStoreWire.Deleted) DeleteGuest(PrisonStoreWire.Id(from));
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonStore.Handle", e);
        }
    }

    private static void Answer(ulong guest)
    {
        string from;
        Record found = Read(PrisonStoreWire.Id(guest), out from);
        if (found == null)
        {
            if (CoopDebug.On("prison")) CoopDebug.Event("prison", "PRQ miss", "no record is stored for " + PrisonStoreWire.Id(guest));
            return;
        }
        if (found.released)
        {
            if (CoopDebug.On("prison")) CoopDebug.Event("prison", "PRQ released", "the record of " + PrisonStoreWire.Id(guest) + " is released and is not shipped");
            return;
        }
        byte[] data = Bytes(found);
        if (data == null) return;
        int sent = NetBlob.Send(guest, Plugin.PluginGuid, PrisonStoreWire.BlobName(found.steamId), data);
        if (sent < 0)
        {
            CoopLog.Log.Error("coop: prison: the record of " + PrisonStoreWire.Id(guest) + " was refused by the blob layer");
            return;
        }
        CoopLog.Log.Info("coop: prison: the record of " + PrisonStoreWire.Id(guest) + " was sent to the guest (" + data.Length + " bytes)");
        if (CoopDebug.On("prison")) CoopDebug.Event("prison", "PRQ answered", "the record of " + PrisonStoreWire.Id(guest) + " was sent from " + from);
    }

    private static void MarkReleased(string id)
    {
        string from;
        Record found = Read(id, out from);
        if (found == null || found.released) return;
        found.released = true;
        Store(found, Bytes(found));
        CoopLog.Log.Info("coop: prison: the record of " + id + " stored here is released and is never shipped");
    }

    private static void DeleteGuest(string id)
    {
        DeleteFiles(id);
        CoopLog.Log.Info("coop: prison: the stored record of " + id + " was deleted at the request of that guest");
    }

    private static void HostBlob(ulong peer, Record rec, int bytes)
    {
        if (!Session.Peers.Contains(peer) || rec.steamId != PrisonStoreWire.Id(peer)) return;
        if (rec.released)
        {
            MarkReleased(rec.steamId);
            return;
        }
        if (!Store(rec, Bytes(rec))) return;
        CoopLog.Log.Info("coop: prison: the record of " + rec.steamId + " from the guest was stored (" + bytes + " bytes)");
        if (CoopDebug.On("prison")) CoopDebug.Event("prison", "host stored", "the record of " + rec.steamId + " (" + bytes + " bytes) was stored on the host");
    }

    private static void GuestBlob(Record rec, int bytes)
    {
        if (rec.steamId != Id()) return;
        if (pending)
        {
            if (CoopDebug.On("prison")) CoopDebug.Event("prison", "late blob", "the record of " + rec.steamId + " arrived while an arrest is in flight and was dropped");
            return;
        }
        if (mine != null && rec.savedAt <= mine.savedAt)
        {
            if (CoopDebug.On("prison")) CoopDebug.Event("prison", "old blob", "the record of " + rec.steamId + " is not newer than the one held here and was dropped");
            return;
        }
        if (rec.released)
        {
            if (CoopDebug.On("prison")) CoopDebug.Event("prison", "released blob", "the record of " + rec.steamId + " arrived released and was refused");
            return;
        }
        if (KnownOtherWorld(rec.worldId))
        {
            if (CoopDebug.On("prison")) CoopDebug.Event("prison", "other world", "the record of " + rec.steamId + " names another world and was refused");
            return;
        }
        mine = rec;
        previous = !SessionRecord(rec);
        refused = false;
        restoreAttempts = 0;
        releasedAt = 0f;
        cellSeenAt = 0f;
        Store(rec, Bytes(rec));
        CoopLog.Log.Info("coop: prison: the record of " + rec.steamId + " was received from the host (" + bytes + " bytes)");
        if (CoopDebug.On("prison")) CoopDebug.Event("prison", "received", "the record of " + rec.steamId + " (" + bytes + " bytes) came from the host");
    }

    private static void OnBlob(ulong peer, string modId, string name, byte[] data)
    {
        try
        {
            if (!ready || !PrisonLocal.Ready) return;
            if (modId != Plugin.PluginGuid || !PrisonStoreWire.IsBlob(name)) return;
            int bytes = data == null ? 0 : data.Length;
            Record rec = Parse(Text(data));
            if (rec == null || !PrisonStoreWire.IsBlobFor(name, rec.steamId)) return;
            if (Session.IsHost)
            {
                if (!RateOk(peer, Blob)) return;
                HostBlob(peer, rec, bytes);
            }
            else if (Session.IsGuest && peer == Session.HostId)
            {
                if (!RateOk(peer, Blob)) return;
                GuestBlob(rec, bytes);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonStore.OnBlob", e);
        }
    }

    private static bool RateOk(ulong peer, string kind)
    {
        float now = Time.unscaledTime;
        if (now >= nextPeerPrune) PrunePeers(now);
        string key = peer.ToString(Inv) + "|" + kind;
        float at;
        if (peerAt.TryGetValue(key, out at) && now - at < PeerCooldown) return false;
        peerAt[key] = now;
        return true;
    }

    private static void PrunePeers(float now)
    {
        nextPeerPrune = now + PeerPrune;
        if (peerAt.Count == 0) return;
        List<string> old = new List<string>();
        foreach (KeyValuePair<string, float> pair in peerAt)
        {
            if (now - pair.Value >= PeerCooldown) old.Add(pair.Key);
        }
        for (int i = 0; i < old.Count; i++) peerAt.Remove(old[i]);
    }

    private static void TellReleased(string id)
    {
        if (!Session.IsGuest || Session.HostId == 0UL) return;
        Session.Send(Session.HostId, PrisonStoreWire.Channel, PrisonStoreWire.Notice(PrisonStoreWire.Released, id), true);
    }

    private static void TellDeleted(string id)
    {
        if (!Session.IsGuest || Session.HostId == 0UL) return;
        Session.Send(Session.HostId, PrisonStoreWire.Channel, PrisonStoreWire.Notice(PrisonStoreWire.Deleted, id), true);
    }

    private static void Ship(Record rec, byte[] data)
    {
        if (!Session.IsGuest || data == null) return;
        ulong host = Session.HostId;
        if (host == 0UL)
        {
            CoopLog.Log.Error("coop: prison: the record of " + rec.steamId + " has no host to go to");
            return;
        }
        int id = NetBlob.Send(host, Plugin.PluginGuid, PrisonStoreWire.BlobName(rec.steamId), data);
        if (id < 0)
        {
            CoopLog.Log.Error("coop: prison: the record of " + rec.steamId + " was refused by the blob layer");
            return;
        }
        CoopLog.Log.Info("coop: prison: the record of " + rec.steamId + " was sent to the host (" + data.Length + " bytes)");
    }

    private static byte[] Bytes(Record rec)
    {
        try
        {
            byte[] data = Utf8.GetBytes(JsonConvert.SerializeObject(rec));
            return data.Length > MaxRecord ? null : data;
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonStore.Bytes", e);
            return null;
        }
    }

    private static string Text(byte[] data)
    {
        if (data == null || data.Length == 0 || data.Length > MaxRecord) return null;
        try
        {
            return Utf8.GetString(data);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Record Parse(string text)
    {
        if (text == null || text.Length == 0 || text.Length > MaxRecord) return null;
        try
        {
            Record rec = JsonConvert.DeserializeObject<Record>(text);
            if (rec == null || !PrisonStoreWire.Plain(rec.steamId) || string.IsNullOrEmpty(rec.state)) return null;
            return rec;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int Bytes(byte[] data)
    {
        return data == null ? 0 : data.Length;
    }
}
