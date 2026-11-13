using System;
using System.Collections.Generic;
using System.Text;
using ObenseuerCore;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static partial class DoorSync
{
    private sealed class Scanned
    {
        public string Key;
        public Door Door;
        public LightTurnOffOn Light;
        public DoorChangelevel Changelevel;
    }

    private const float AuthorityIndexEvery = 30f;
    private static readonly List<Scanned> scanned = new List<Scanned>();
    private static readonly Dictionary<string, Scanned> scannedByKey = new Dictionary<string, Scanned>();
    private static readonly Dictionary<string, byte> sentCodes = new Dictionary<string, byte>();
    private static readonly HashSet<ulong> present = new HashSet<ulong>();
    private static readonly HashSet<ulong> previous = new HashSet<ulong>();
    private static readonly List<string> changes = new List<string>();
    private static readonly List<string> arrivalChanges = new List<string>();
    private static readonly List<ulong> arrived = new List<ulong>();
    private static readonly StringBuilder packet = new StringBuilder(1024);
    private static string scanScene;
    private static ulong scanAuthority;
    private static float nextScan;
    private static float nextIndex;
    private static float nextFull;

    public static void Tick()
    {
        try
        {
            if (!Session.IsInLobby || GameRefs.AreaId == null) return;
            float now = Time.unscaledTime;
            if (now < nextScan) return;
            nextScan = now + 1f;
            if (!string.Equals(scanScene, GameRefs.AreaId, StringComparison.Ordinal) || sceneHandle != SceneManager.GetActiveScene().handle)
            {
                ResetScan();
                scanScene = GameRefs.AreaId;
            }
            FlushDeferred();
            if (!FreeRoam.IAmAuthority) return;
            List<ulong> peers = Session.PeersInArea(scanScene);
            if (peers.Count == 0)
            {
                changes.Clear();
                arrivalChanges.Clear();
                arrived.Clear();
                return;
            }
            ulong owner = FreeRoam.Authority;
            if (owner != scanAuthority)
            {
                scanAuthority = owner;
                sentCodes.Clear();
                nextFull = 0f;
            }
            if (now >= nextIndex)
            {
                nextIndex = now + AuthorityIndexEvery;
                Index(true);
            }
            bool full = now >= nextFull;
            Arrivals();
            bool arrival = arrived.Count > 0;
            changes.Clear();
            arrivalChanges.Clear();
            for (int i = 0; i < scanned.Count; i++)
            {
                Scanned item = scanned[i];
                if (item.Door != null && item.Door.moving) continue;
                byte code = CodeOf(item);
                if (arrival) arrivalChanges.Add(EntryOf(item, code));
                byte old;
                if (!full && sentCodes.TryGetValue(item.Key, out old) && old == code) continue;
                sentCodes[item.Key] = code;
                changes.Add(EntryOf(item, code));
            }
            if (full) nextFull = now + 20f;
            if (full && changes.Count != 0)
            {
                CoopLog.Log.Info("coop: door sync full resend of " + changes.Count + " doors and switches to " + peers.Count + " peers");
            }
            SendChanges(peers, changes);
            if (arrival) SendChanges(arrived, arrivalChanges);
        }
        catch (Exception e)
        {
            CoopLog.Once("DoorSync.Tick", e);
        }
    }

    private static void Arrivals()
    {
        arrived.Clear();
        previous.Clear();
        foreach (ulong peer in present) previous.Add(peer);
        present.Clear();
        foreach (PresenceRecord record in Session.Remotes())
        {
            if (record.Area != scanScene || !Session.Peers.Contains(record.Peer)) continue;
            present.Add(record.Peer);
            if (!previous.Contains(record.Peer)) arrived.Add(record.Peer);
        }
    }

    private static byte CodeOf(Scanned item)
    {
        if (item.Light != null) return (byte)(item.Light.on ? 1 : 0);
        if (item.Changelevel != null) return (byte)((item.Changelevel._Lock != null && item.Changelevel._Lock.Locked) ? 1 : 0);
        if (item.Door == null) return 0;
        float p = PositionOf(item.Door);
        byte open = p <= 0.02f ? (byte)0 : (p >= 0.98f ? (byte)2 : (byte)1);
        return (byte)(open | (Reversed(item.Door) ? 4 : 0) | (item.Door.Locked ? 8 : 0));
    }

    private static string EntryOf(Scanned item, byte code)
    {
        if (item.Light != null) return "L" + Digit(code & 1) + "00" + item.Key;
        if (item.Changelevel != null) return "C00" + Digit(code & 1) + item.Key;
        return "D" + Digit(code & 3) + Digit((code >> 2) & 1) + Digit((code >> 3) & 1) + item.Key;
    }

    private static string Digit(int value)
    {
        return ((char)('0' + value)).ToString();
    }

    private static void SendChanges(List<ulong> peers, List<string> entries)
    {
        if (entries.Count == 0)
        {
            entries.Clear();
            return;
        }
        string header = GameRefs.AreaId + "|DB|";
        packet.Length = 0;
        packet.Append(header);
        int bytes = Encoding.UTF8.GetByteCount(header);
        for (int i = 0; i < entries.Count; i++)
        {
            string entry = entries[i];
            int size = Encoding.UTF8.GetByteCount(entry) + 1;
            if (bytes + size > 880 && packet.Length > header.Length)
            {
                string done = packet.ToString();
                for (int p = 0; p < peers.Count; p++) Session.Send(peers[p], Channel, done, true);
                packet.Length = 0;
                packet.Append(header);
                bytes = Encoding.UTF8.GetByteCount(header);
            }
            if (packet.Length > header.Length) packet.Append('|');
            packet.Append(entry);
            bytes += size;
        }
        if (packet.Length > header.Length)
        {
            string done = packet.ToString();
            for (int p = 0; p < peers.Count; p++) Session.Send(peers[p], Channel, done, true);
        }
        entries.Clear();
    }

    private static void ApplyBatch(string[] f)
    {
        try
        {
            int changed = 0;
            applying++;
            try
            {
                for (int i = 2; i < f.Length; i++)
                {
                    string entry = f[i];
                    if (entry.Length < 5) continue;
                    string key = entry.Substring(4);
                    Scanned item;
                    if (!scannedByKey.TryGetValue(key, out item) || item == null)
                    {
                        if (!IndexOnMiss()) continue;
                        if (!scannedByKey.TryGetValue(key, out item) || item == null) continue;
                    }
                    if (ApplyEntry(item, entry)) changed++;
                }
            }
            finally
            {
                applying--;
            }
            if (changed > 0) CoopLog.Log.Info("coop: door sync applied " + changed + " changes from the authority");
        }
        catch (Exception e)
        {
            CoopLog.Once("DoorSync.ApplyBatch", e);
        }
    }

    private static bool IndexOnMiss()
    {
        if (Time.unscaledTime < nextForce) return false;
        Index(true);
        return true;
    }

    private static bool ApplyEntry(Scanned item, string entry)
    {
        char kind = entry[0];
        if (item.Light != null)
        {
            if (kind != 'L') return false;
            bool on = entry[1] == '1';
            if (item.Light.on == on) return false;
            if (on) item.Light.turnLightOn();
            else item.Light.turnLightOff();
            return true;
        }
        if (item.Changelevel != null)
        {
            if (kind != 'C') return false;
            bool locked = entry[3] == '1';
            bool now = item.Changelevel._Lock != null && item.Changelevel._Lock.Locked;
            if (now == locked) return false;
            if (locked) item.Changelevel.Lock();
            else item.Changelevel.UnLock();
            return true;
        }
        if (item.Door == null || kind != 'D') return false;
        Door door = item.Door;
        bool changed = false;
        bool wantOpen = entry[1] != '0';
        if (!MovingTowards(door, wantOpen))
        {
            float position = PositionOf(door);
            if (!wantOpen)
            {
                if (position > 0.02f) { door.Close(); changed = true; }
            }
            else if (position < 0.98f)
            {
                if (entry[2] == '1') door.ForceOpenOpposite();
                else door.ForceOpen();
                changed = true;
            }
        }
        bool wantLock = entry[3] == '1';
        if (wantLock != door.Locked)
        {
            if (wantLock) door.Lock();
            else door.Unlock();
            changed = true;
        }
        return changed;
    }

    private static bool Syncable(MonoBehaviour m)
    {
        bool guid = HasGuid(m);
        if (m.GetComponentInParent<FurniturePlaceable>(true) != null) return false;
        if (!guid && m.GetComponentInParent<Spawner>(true) != null) return false;
        return true;
    }

    private static bool HasGuid(MonoBehaviour m)
    {
        try { return !string.IsNullOrEmpty(SaveController.GetObjectGUID(m)); }
        catch (Exception) { return false; }
    }

    private static void Track(string key, Door door, LightTurnOffOn light, DoorChangelevel changelevel)
    {
        Scanned item = new Scanned();
        item.Key = key;
        item.Door = door;
        item.Light = light;
        item.Changelevel = changelevel;
        scanned.Add(item);
        scannedByKey[key] = item;
    }

    private static void Count(Dictionary<string, int> counts, List<string> keys, string key)
    {
        int n;
        if (!counts.TryGetValue(key, out n))
        {
            counts[key] = 1;
            keys.Add(key);
            return;
        }
        counts[key] = n + 1;
    }

    private static void Drop(List<string> keys, Dictionary<string, int> counts)
    {
        for (int i = 0; i < keys.Count; i++)
        {
            string key = keys[i];
            int n;
            if (!counts.TryGetValue(key, out n) || n < 2) continue;
            doors.Remove(key);
            lights.Remove(key);
            changelevels.Remove(key);
            scannedByKey.Remove(key);
            for (int j = scanned.Count - 1; j >= 0; j--)
            {
                if (scanned[j].Key == key) scanned.RemoveAt(j);
            }
            if (collided.Add(key)) CoopLog.Log.Info("coop: door sync key clash, key excluded on both machines: " + key);
        }
    }

    private static void PruneSent()
    {
        if (sentCodes.Count == 0) return;
        List<string> stale = null;
        foreach (KeyValuePair<string, byte> pair in sentCodes)
        {
            if (scannedByKey.ContainsKey(pair.Key)) continue;
            if (stale == null) stale = new List<string>();
            stale.Add(pair.Key);
        }
        if (stale == null) return;
        for (int i = 0; i < stale.Count; i++) sentCodes.Remove(stale[i]);
    }

    private static void ResetScan()
    {
        scanned.Clear();
        scannedByKey.Clear();
        sentCodes.Clear();
        present.Clear();
        previous.Clear();
        changes.Clear();
        arrivalChanges.Clear();
        arrived.Clear();
        packet.Length = 0;
        scanScene = null;
        scanAuthority = 0UL;
        nextScan = 0f;
        nextIndex = 0f;
        nextFull = 0f;
    }
}
