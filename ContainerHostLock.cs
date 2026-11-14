using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;

internal static partial class ContainerHost
{
    private const float NoisyQuiet = 2f;
    private static readonly Dictionary<ulong, float> refusedAt = new Dictionary<ulong, float>();
    private static readonly List<ulong> quieted = new List<ulong>();
    private static string HostHash(Storage storage, string key)
    {
        return ContainerWire.ContentHash(storage, key);
    }

    private static void Answer(Held held, bool applied, string reason)
    {
        if (held == null || held.Craft || held.Holder == 0UL || held.LockId == null) return;
        Send(held.Holder, applied ? ContainerWire.Applied(held.LockId) : ContainerWire.Refused(held.LockId, reason));
        if (CoopDebug.On("cont"))
        {
            CoopDebug.Event("cont", held.Key, (applied ? "applied" : "refused") + " lock " + held.LockId + " for " + held.Holder + (reason == null ? "" : ", " + reason));
        }
    }

    private static void RefuseClose(ulong peer, string lockId)
    {
        if (lockId == null) return;
        PruneQuieted();
        float at;
        if (refusedAt.TryGetValue(peer, out at) && Time.unscaledTime - at < NoisyQuiet) return;
        refusedAt[peer] = Time.unscaledTime;
        Send(peer, ContainerWire.Refused(lockId, "lock no longer held"));
        CoopLog.Log.Info("coop: a close named lock " + lockId + " which is no longer held, the state was refused back to " + peer);
    }

    private static void PruneQuieted()
    {
        if (refusedAt.Count == 0) return;
        quieted.Clear();
        foreach (KeyValuePair<ulong, float> pair in refusedAt)
        {
            if (!Session.Peers.Contains(pair.Key)) quieted.Add(pair.Key);
        }
        for (int i = 0; i < quieted.Count; i++) refusedAt.Remove(quieted[i]);
    }

    private static void Stale(ulong from, string name)
    {
        foreach (KeyValuePair<string, Held> pair in locks)
        {
            Held held = pair.Value;
            if (held.Craft || held.LockId == null || !ContainerWire.IsBlobForKey(name, held.Key)) continue;
            CoopLog.Log.Info("coop: container blob for stale lock dropped (" + name + ")");
            if (CoopDebug.On("cont")) CoopDebug.Event("cont", held.Key, "container blob for stale lock dropped");
            if (held.Holder == from) Send(from, ContainerWire.StaleNotice(held.LockId, "An older state for this container was dropped"));
            return;
        }
        CoopLog.Log.Info("coop: a container state for " + name + " arrived with no matching lock");
    }
}
