using System;
using System.Collections.Generic;
using System.Text;
using ObenseuerCore;
using UnityEngine;

internal static partial class ContainerHost
{
    private const float DigestInterval = 20f;
    private const float DigestGrace = 10f;
    private const float SyncRefusedEvery = 30f;
    private const int MaxDigestPacket = 880;
    private static readonly HashSet<ulong> digestPresent = new HashSet<ulong>();
    private static readonly HashSet<ulong> digestPrevious = new HashSet<ulong>();
    private static readonly List<string> digestEntries = new List<string>();
    private static readonly List<string> relockKeys = new List<string>();
    private static readonly List<string> digestPackets = new List<string>();
    private static readonly StringBuilder digestBody = new StringBuilder(900);
    private static string digestScene;
    private static bool digestWasAuthority;
    private static float nextDigest;
    private static float digestNoSendBefore;
    private static float syncRefusedAt = -60f;

    public static void ScanTick()
    {
        try
        {
            if (!Session.IsInLobby)
            {
                digestPresent.Clear();
                digestPrevious.Clear();
                digestScene = null;
                digestWasAuthority = false;
                return;
            }
            bool authority = FreeRoam.IAmAuthority;
            if (digestWasAuthority != authority)
            {
                digestWasAuthority = authority;
                digestScene = null;
                digestPresent.Clear();
                digestPrevious.Clear();
                nextDigest = 0f;
                digestNoSendBefore = Time.unscaledTime + DigestGrace;
            }
            if (!authority) return;
            string scene = GameRefs.AreaId;
            if (scene == null) return;
            float now = Time.unscaledTime;
            if (digestScene != scene)
            {
                digestScene = scene;
                digestPresent.Clear();
                digestPrevious.Clear();
                nextDigest = now;
            }
            if (Session.PeersInArea(scene).Count == 0)
            {
                digestPresent.Clear();
                digestPrevious.Clear();
                return;
            }
            bool arrived = Arrivals(scene);
            if (arrived) nextDigest = now;
            if (now < digestNoSendBefore) return;
            if (SaveController.Loading || SaveData.Busy) return;
            if (!arrived && now < nextDigest) return;
            nextDigest = now + DigestInterval;
            SendDigest();
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerHost.ScanTick", e);
        }
    }

    private static bool Arrivals(string scene)
    {
        digestPrevious.Clear();
        foreach (ulong peer in digestPresent) digestPrevious.Add(peer);
        digestPresent.Clear();
        bool arrived = false;
        foreach (PresenceRecord record in Session.Remotes())
        {
            if (record.Area != scene || !Session.Peers.Contains(record.Peer)) continue;
            digestPresent.Add(record.Peer);
            if (!digestPrevious.Contains(record.Peer)) arrived = true;
        }
        return arrived;
    }

    private static void SendDigest()
    {
        string scene = digestScene;
        if (scene == null) return;
        List<ulong> peers = Session.PeersInArea(scene);
        if (peers.Count == 0) return;
        digestEntries.Clear();
        relockKeys.Clear();
        UnityEngine.Object[] found = UnityEngine.Object.FindObjectsByType(typeof(Storage), FindObjectsSortMode.None);
        for (int i = 0; i < found.Length; i++)
        {
            Storage storage = found[i] as Storage;
            if (storage == null) continue;
            string key = ContainerWire.KeyOf(storage);
            if (key == null) continue;
            if (locks.ContainsKey(key) || key == hostOpenKey) continue;
            CraftingBase craft = storage.GetComponent<CraftingBase>();
            if (craft == null) craft = storage.GetComponentInParent<CraftingBase>();
            if (craft != null && craft.InputOutputStorage == storage) continue;
            if (craft != null && craft.OutputStorage == storage) continue;
            if (storage._Lock != null && storage._Lock.Locked)
            {
                relockKeys.Add(key);
                continue;
            }
            Refresh(storage);
            digestEntries.Add(key + ":" + ContainerWire.ContentHash(storage, key));
        }
        for (int i = 0; i < relockKeys.Count; i++)
        {
            string text = ContainerWire.Relock(scene, relockKeys[i]);
            for (int p = 0; p < peers.Count; p++) Send(peers[p], text);
        }
        if (digestEntries.Count == 0) return;
        string head = "DIGEST|" + scene + "|";
        int headBytes = Encoding.UTF8.GetByteCount(head);
        digestPackets.Clear();
        digestBody.Length = 0;
        bool any = false;
        int bytes = headBytes;
        for (int i = 0; i < digestEntries.Count; i++)
        {
            string entry = digestEntries[i];
            int size = Encoding.UTF8.GetByteCount(entry) + 1;
            if (any && bytes + size > MaxDigestPacket)
            {
                digestPackets.Add(digestBody.ToString());
                digestBody.Length = 0;
                any = false;
                bytes = headBytes;
            }
            if (any) digestBody.Append(',');
            digestBody.Append(entry);
            bytes += size;
            any = true;
        }
        digestPackets.Add(digestBody.ToString());
        for (int i = 0; i < digestPackets.Count; i++)
        {
            string text = ContainerWire.Digest(scene, digestPackets[i]);
            for (int p = 0; p < peers.Count; p++) Send(peers[p], text);
        }
        if (CoopDebug.On("cont")) CoopDebug.Event("cont", "digest", digestEntries.Count + " containers in " + digestPackets.Count + " packets, " + peers.Count + " peers in " + scene);
    }

    private static void SyncRequest(ulong from, ContainerMsg m)
    {
        try
        {
            if (!Active || !Session.Peers.Contains(from)) return;
            string scene = GameRefs.AreaId;
            if (scene == null || m.Scene != scene || Session.AreaOf(from) != scene) return;
            if (locks.ContainsKey(m.Key) || m.Key == hostOpenKey) return;
            if (SaveData.Busy || SaveController.Loading) return;
            Storage storage = Resolve(m.Key);
            if (storage == null) return;
            if (PrisonLocal.IsCellStorage(storage)) return;
            if (storage._Lock != null && storage._Lock.Locked) return;
            if (StationState.StationOf(storage) != null) return;
            string json = ContainerWire.Capture(storage, m.Key);
            if (json == null) return;
            if (NetBlob.Send(from, ContainerWire.ModId, ContainerWire.BlobNameOf(m.Key), ContainerWire.Bytes(json)) < 0)
            {
                float now = Time.unscaledTime;
                if (now - syncRefusedAt >= SyncRefusedEvery)
                {
                    syncRefusedAt = now;
                    CoopLog.Log.Debug("ContainerHost.SyncRequest.blob", "coop: the state of container " + m.Key + " could not be sent to " + from + ", too many blobs are in flight");
                }
                return;
            }
            if (CoopDebug.On("cont")) CoopDebug.Event("cont", m.Key, "current state sent to " + from);
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerHost.SyncRequest", e);
        }
    }
}

internal static partial class ContainerGuest
{
    private const float SyncRetry = 60f;
    private const float SyncTimeout = 10f;
    private const float RequestRound = 5f;
    private const int MaxSync = 256;
    private const int MaxRequests = 8;
    private static readonly Dictionary<string, float> syncAt = new Dictionary<string, float>();
    private static readonly Dictionary<string, float> syncPending = new Dictionary<string, float>();
    private static readonly List<string> syncStale = new List<string>();
    private static readonly Dictionary<string, Storage> digestIndex = new Dictionary<string, Storage>();
    private static float digestIndexAt = -1f;
    private static float askedRoundAt = -1f;
    private static int askedRound;

    private static void SyncTick()
    {
        try
        {
            if (!Session.IsInLobby)
            {
                syncAt.Clear();
                syncPending.Clear();
                return;
            }
            if (syncPending.Count == 0 && syncAt.Count == 0) return;
            float now = Time.unscaledTime;
            syncStale.Clear();
            foreach (KeyValuePair<string, float> pair in syncPending)
            {
                if (now - pair.Value >= SyncTimeout) syncStale.Add(pair.Key);
            }
            for (int i = 0; i < syncStale.Count; i++) syncPending.Remove(syncStale[i]);
            if (syncAt.Count <= MaxSync) return;
            syncStale.Clear();
            foreach (KeyValuePair<string, float> pair in syncAt)
            {
                if (now - pair.Value >= SyncRetry) syncStale.Add(pair.Key);
            }
            for (int i = 0; i < syncStale.Count; i++) syncAt.Remove(syncStale[i]);
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerGuest.SyncTick", e);
        }
    }

    private static void OnDigest(ContainerMsg m)
    {
        try
        {
            if (FreeRoam.IAmAuthority || !GuestSession.InHostWorld) return;
            if (m == null || m.Keys == null || m.Hashes == null) return;
            if (m.Scene != GameRefs.AreaId) return;
            if (SaveController.Loading || SaveData.Busy) return;
            float now = Time.unscaledTime;
            if (now - askedRoundAt >= RequestRound)
            {
                askedRoundAt = now;
                askedRound = 0;
            }
            if (digestIndexAt != now)
            {
                digestIndexAt = now;
                digestIndex.Clear();
                UnityEngine.Object[] found = UnityEngine.Object.FindObjectsByType(typeof(Storage), FindObjectsSortMode.None);
                for (int i = 0; i < found.Length; i++)
                {
                    Storage storage = found[i] as Storage;
                    if (storage == null) continue;
                    string key = ContainerWire.KeyOf(storage);
                    if (key != null && !digestIndex.ContainsKey(key)) digestIndex[key] = storage;
                }
            }
            int asked = 0;
            for (int i = 0; i < m.Keys.Count && askedRound < MaxRequests; i++)
            {
                string key = m.Keys[i];
                string hash = m.Hashes[i];
                if (key == null || hash == null || HeldHere(key)) continue;
                Storage storage;
                if (!digestIndex.TryGetValue(key, out storage) || storage == null) continue;
                float sent;
                if (syncAt.TryGetValue(key, out sent) && now - sent < SyncRetry) continue;
                if (syncPending.ContainsKey(key)) continue;
                if (syncAt.Count >= MaxSync) continue;
                Refresh(storage);
                if (ContainerWire.ContentHash(storage, key) == hash) continue;
                int reqId = ++lastReqId;
                if (!Session.Send(FreeRoam.Authority, ContainerWire.Channel, ContainerWire.ReqSync(reqId, GameRefs.AreaId, key), true)) continue;
                syncAt[key] = now;
                syncPending[key] = now;
                asked++;
                askedRound++;
            }
            if (asked > 0) CoopLog.Log.Info("coop: " + asked + " container states were asked for after the authority digest");
            if (CoopDebug.On("cont")) CoopDebug.Event("cont", "digest", m.Keys.Count + " containers compared, " + asked + " asked for in " + m.Scene);
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerGuest.OnDigest", e);
        }
    }

    private static void OnRelock(ContainerMsg m)
    {
        if (SaveController.Loading || SaveData.Busy) return;
        if (m.Scene != GameRefs.AreaId || HeldHere(m.Key)) return;
        Storage storage = ResolveGuest(m.Key);
        if (storage == null || storage._Lock == null || storage._Lock.Locked) return;
        storage._Lock._Lock();
        CoopLog.Log.Info("coop: container " + m.Key + " was locked by the authority");
        if (CoopDebug.On("cont")) CoopDebug.Event("cont", m.Key, "locked by the authority");
    }

    internal static bool HeldHere(string key)
    {
        return HeldLocally(key) || (key != null && holders.ContainsKey(key));
    }

    internal static bool HeldLocally(string key)
    {
        if (key == null) return true;
        if (key == openKey || key == pendingKey || key == exitingKey) return true;
        if (awaiting.Contains(key)) return true;
        for (int i = 0; i < closing.Count; i++)
        {
            if (closing[i].Key == key) return true;
        }
        return false;
    }

    internal static bool TrySyncState(string name, string json)
    {
        if (name == null || json == null || syncPending.Count == 0) return false;
        string key = null;
        foreach (string candidate in syncPending.Keys)
        {
            if (name != ContainerWire.BlobNameOf(candidate)) continue;
            key = candidate;
            break;
        }
        if (key == null) return false;
        syncPending.Remove(key);
        Storage storage = ResolveGuest(key);
        if (storage == null)
        {
            CoopLog.Log.Info("coop: the state of container " + key + " arrived but the container is not here");
            if (CoopDebug.On("cont")) CoopDebug.Event("cont", key, "a digest state arrived for a container that is not here");
            return true;
        }
        if (HeldHere(key))
        {
            if (CoopDebug.On("cont")) CoopDebug.Event("cont", key, "a digest state arrived while the container is held here");
            return true;
        }
        if (ContainerWire.Picking(storage))
        {
            syncAt.Remove(key);
            CoopLog.Log.Info("coop: the state of container " + key + " arrived while its lock is being picked, it is asked for again with the next digest");
            if (CoopDebug.On("cont")) CoopDebug.Event("cont", key, "a digest state was dropped while its lock is being picked");
            return true;
        }
        syncAt[key] = Time.unscaledTime;
        bool ok = ContainerWire.Apply(storage, key, json);
        if (ok) Refresh(storage);
        if (ok) CoopLog.Log.Info("coop: container " + key + " was brought back to the state of the authority");
        else CoopLog.Log.Error("coop: the state of container " + key + " could not be applied");
        if (CoopDebug.On("cont")) CoopDebug.Event("cont", key, ok ? "a digest mismatch was repaired" : "a digest state was refused");
        return true;
    }

    private static Storage ResolveGuest(string key)
    {
        if (key == null) return null;
        UnityEngine.Object[] found = UnityEngine.Object.FindObjectsByType(typeof(Storage), FindObjectsSortMode.None);
        for (int i = 0; i < found.Length; i++)
        {
            Storage storage = found[i] as Storage;
            if (storage != null && ContainerWire.KeyOf(storage) == key) return storage;
        }
        return null;
    }
}
