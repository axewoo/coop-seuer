using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;

internal static partial class LiquidSync
{
    private static readonly Dictionary<string, string> leaseIds = new Dictionary<string, string>();
    private static readonly Dictionary<string, string> leaseNames = new Dictionary<string, string>();
    private static readonly List<ulong> onePeer = new List<ulong> { 0UL };

    private const float ResendEvery = 20f;
    private const float ResendOffset = 10f;
    private const float PresenceEvery = 1f;
    private const int ResendShare = 2;
    private static readonly HashSet<ulong> resendPresent = new HashSet<ulong>();
    private static readonly HashSet<ulong> resendPrevious = new HashSet<ulong>();
    private static readonly List<ulong> resendArrived = new List<ulong>();
    private static readonly List<TankJob> resendPending = new List<TankJob>();
    private static readonly Dictionary<string, TankJob> resendWaiting = new Dictionary<string, TankJob>();
    private static string resendScene;
    private static ulong resendAuthority;
    private static float resendAt;
    private static float presenceAt;

    private static string LeaseId(string key)
    {
        string id;
        return key != null && leaseIds.TryGetValue(key, out id) ? id : null;
    }

    private static string LeaseName(string key)
    {
        string head = BlobName(key);
        string id = LeaseId(key);
        return string.IsNullOrEmpty(id) ? head : head + "-" + ContainerWire.LockTag(id);
    }

    private static string HeldName(string key)
    {
        string name;
        return key != null && leaseNames.TryGetValue(key, out name) ? name : BlobName(key);
    }

    private static bool IsLeaseNameOf(string name, string key)
    {
        string head = BlobName(key);
        if (name == head) return true;
        return name != null && name.Length == head.Length + 5 && name[head.Length] == '-' && name.StartsWith(head, StringComparison.Ordinal);
    }

    private static void StaleLease(ulong peer, string name)
    {
        foreach (KeyValuePair<string, ulong> pair in held)
        {
            if (pair.Value != peer || !IsLeaseNameOf(name, pair.Key)) continue;
            if (CoopDebug.On("cont")) CoopDebug.Event("cont", pair.Key, "liquid blob for stale lease dropped");
            return;
        }
    }

    private static void EnsureIndex()
    {
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle == sceneHandle && Time.unscaledTime < nextReindex) return;
        Reindex();
    }

    private static void ResendTick()
    {
        try
        {
            if (!Session.IsInLobby) return;
            ulong owner = FreeRoam.Authority;
            if (owner != resendAuthority)
            {
                resendAuthority = owner;
                resendPending.Clear();
                resendWaiting.Clear();
                resendAt = Time.unscaledTime + ResendOffset;
            }
            if (!FreeRoam.IAmAuthority) return;
            string scene = GameRefs.AreaId;
            if (scene == null) return;
            float now = Time.unscaledTime;
            if (!string.Equals(resendScene, scene, StringComparison.Ordinal))
            {
                resendScene = scene;
                resendPresent.Clear();
                resendArrived.Clear();
                resendPending.Clear();
                resendWaiting.Clear();
                resendAt = now + ResendOffset;
                presenceAt = 0f;
            }
            DrainTanks();
            if (now >= presenceAt)
            {
                presenceAt = now + PresenceEvery;
                ResendArrivals(scene);
            }
            bool full = now >= resendAt;
            if (!full && resendArrived.Count == 0) return;
            if (full) resendAt = now + ResendEvery;
            EnsureIndex();
            List<ulong> peers = full ? Session.PeersInArea(scene) : new List<ulong>(resendArrived);
            resendArrived.Clear();
            if (peers.Count == 0) return;
            int sent = 0;
            foreach (KeyValuePair<string, LiquidStorage> pair in tanks)
            {
                LiquidStorage tank = pair.Value;
                if (tank == null) continue;
                ulong holder;
                if (Holds(pair.Key, out holder)) continue;
                TankJob waitingJob;
                if (resendWaiting.TryGetValue(pair.Key, out waitingJob))
                {
                    waitingJob.Tank = tank;
                    for (int i = 0; i < peers.Count; i++)
                    {
                        if (!waitingJob.Peers.Contains(peers[i])) waitingJob.Peers.Add(peers[i]);
                    }
                    continue;
                }
                EnqueueTank(tank, pair.Key, peers);
                sent++;
            }
            if (CoopDebug.On("inter")) CoopDebug.Event("inter", "liquid-resend", sent + " tanks to " + peers.Count + " peers");
        }
        catch (Exception e)
        {
            CoopLog.Once("LiquidSync.ResendTick", e);
        }
    }

    private static void EnqueueTank(LiquidStorage tank, string key, List<ulong> peers)
    {
        TankJob job = new TankJob();
        job.Tank = tank;
        job.Key = key;
        job.Peers = new List<ulong>(peers);
        resendWaiting[key] = job;
        resendPending.Add(job);
    }

    private static void DrainTanks()
    {
        if (resendPending.Count == 0 || BlobBudget.Blocked) return;
        int share = ResendShare;
        List<ulong> live = Session.Peers;
        while (share > 0 && resendPending.Count > 0)
        {
            TankJob job = resendPending[0];
            if (job.Tank == null || job.Index >= job.Peers.Count)
            {
                resendPending.RemoveAt(0);
                resendWaiting.Remove(job.Key);
                continue;
            }
            ulong holder;
            if (Holds(job.Key, out holder))
            {
                resendPending.RemoveAt(0);
                resendWaiting.Remove(job.Key);
                continue;
            }
            if (!BlobBudget.Take()) return;
            ulong peer = job.Peers[job.Index];
            if (!live.Contains(peer))
            {
                job.Peers.RemoveAt(job.Index);
                continue;
            }
            onePeer[0] = peer;
            bool refused;
            if (!PushState(job.Tank, job.Key, onePeer, false, out refused))
            {
                if (!refused || ++job.Refusals >= BlobBudget.MaxRefusals)
                {
                    CoopLog.Once("LiquidSync.DrainTanks.state", "coop: the state of tank " + job.Key + " could not be sent");
                    resendPending.RemoveAt(0);
                    resendWaiting.Remove(job.Key);
                    continue;
                }
                BlobBudget.Block();
                return;
            }
            job.Refusals = 0;
            job.Index++;
            share--;
        }
    }

    private sealed class TankJob
    {
        public LiquidStorage Tank;
        public string Key;
        public List<ulong> Peers;
        public int Index;
        public int Refusals;
    }

    private static void ResendArrivals(string scene)
    {
        resendArrived.Clear();
        resendPrevious.Clear();
        foreach (ulong peer in resendPresent) resendPrevious.Add(peer);
        resendPresent.Clear();
        foreach (PresenceRecord record in Session.Remotes())
        {
            if (record.Area != scene || !Session.Peers.Contains(record.Peer)) continue;
            resendPresent.Add(record.Peer);
            if (!resendPrevious.Contains(record.Peer)) resendArrived.Add(record.Peer);
        }
    }
}
