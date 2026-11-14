using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;

internal static partial class ContainerGuest
{
    private const float AnswerSeconds = 150f;
    private const int MaxOutcomes = 8;
    private const int MaxSettled = 16;
    private static string pendingLockId;
    private static string openLockId;
    private static string exitingLockId;
    private static readonly List<Outcome> outcomes = new List<Outcome>();
    private static readonly List<Settled> settled = new List<Settled>();

    private sealed class Outcome
    {
        public string Key;
        public string LockId;
        public Delta Delta;
        public float At;
    }

    private sealed class Settled
    {
        public string LockId;
        public bool Applied;
        public float At;
    }

    private static bool Accepted(string name)
    {
        if (pendingLockId == null)
        {
            if (pendingKey == null) return false;
            if (syncPending.ContainsKey(pendingKey) && name == ContainerWire.BlobNameOf(pendingKey)) return false;
            return ContainerWire.IsBlobForKey(name, pendingKey);
        }
        return name == ContainerWire.BlobNameOf(pendingKey, pendingLockId);
    }

    private static void Arm(string key, string lockId, Delta delta)
    {
        if (lockId == null)
        {
            CoopLog.Log.Info("coop: the state of container " + key + " went out without a lock id, no answer is expected for it");
            return;
        }
        Settled done = SettledFor(lockId);
        if (done != null)
        {
            if (done.Applied)
            {
                CoopLog.Log.Info("coop: the host already applied lock " + lockId + " before this state of " + key + " was armed, no answer is awaited");
                return;
            }
            CoopLog.Log.Error("coop: the host refused lock " + lockId + " " + (int)(Time.unscaledTime - done.At) + " s before this state of " + key + " was armed, the local changes are undone");
            Revert(key, delta);
            return;
        }
        if (outcomes.Count >= MaxOutcomes)
        {
            CoopLog.Log.Error("coop: more than " + MaxOutcomes + " container answers are waiting, the oldest one for " + outcomes[0].Key + " was dropped");
            outcomes.RemoveAt(0);
        }
        Outcome entry = new Outcome();
        entry.Key = key;
        entry.LockId = lockId;
        entry.Delta = delta;
        entry.At = Time.unscaledTime;
        outcomes.Add(entry);
        if (CoopDebug.On("cont")) CoopDebug.Event("cont", key, "state handed to the send queue under lock " + lockId + ", waiting for the answer");
    }

    private static Outcome Find(string lockId)
    {
        if (lockId == null) return null;
        for (int i = 0; i < outcomes.Count; i++)
        {
            if (outcomes[i].LockId == lockId) return outcomes[i];
        }
        return null;
    }

    private static void Outcomes()
    {
        if (outcomes.Count == 0) return;
        for (int i = outcomes.Count - 1; i >= 0; i--)
        {
            Outcome entry = outcomes[i];
            if (Time.unscaledTime - entry.At < AnswerSeconds) continue;
            CoopLog.Log.Error("coop: no answer for container " + entry.Key + " under lock " + entry.LockId + " after " + AnswerSeconds + " s, the local state is kept");
            outcomes.RemoveAt(i);
        }
    }

    private static void OutcomeApplied(string lockId)
    {
        Outcome found = Find(lockId);
        if (found != null) outcomes.Remove(found);
        else Settle(lockId, true);
        if (CoopDebug.On("cont")) CoopDebug.Event("cont", found == null ? lockId : found.Key, found == null ? "an applied answer for a close that is not armed yet" : "the host applied the state");
    }

    private static void OutcomeRefused(string lockId, string reason)
    {
        Outcome found = Find(lockId);
        if (found == null)
        {
            if (lockId != null) Settle(lockId, false);
            CoopLog.Log.Info("coop: a container refusal for lock " + lockId + " (" + reason + ") is not waiting here");
            return;
        }
        outcomes.Remove(found);
        CoopLog.Log.Error("coop: the host refused the state of container " + found.Key + " (" + reason + "), the local changes are undone");
        Revert(found.Key, found.Delta);
        if (!string.IsNullOrEmpty(reason)) Notice(reason);
    }

    private static void OutcomeStale(ContainerMsg m)
    {
        CoopLog.Log.Info("coop: the host dropped a container state for an older lock (" + (m.Reason == null ? "no reason" : m.Reason) + ")");
        if (CoopDebug.On("cont")) CoopDebug.Event("cont", m.LockId, "the host dropped an older state for this container");
    }

    private static void LockLost(string key)
    {
        if (key == null || open == null || openKey != key) return;
        Storage running = open;
        string lockId = openLockId;
        CoopLog.Log.Info("coop: the host released container " + key + " while it was open here, the open is closed and its changes are undone");
        Delta delta = FinishOpen(key, running);
        SendFlag(key, false, lockId);
        open = null;
        openKey = null;
        openLockId = null;
        touchAt = 0f;
        ClosePanel(running);
        Revert(key, delta);
    }

    private static void ClosePanel(Storage running)
    {
        try
        {
            running.ExitExternally();
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerGuest.ClosePanel", e);
        }
    }

    private static void Settle(string lockId, bool applied)
    {
        if (lockId == null) return;
        for (int i = 0; i < settled.Count; i++)
        {
            if (settled[i].LockId != lockId) continue;
            settled[i].Applied = applied;
            settled[i].At = Time.unscaledTime;
            return;
        }
        if (settled.Count >= MaxSettled) settled.RemoveAt(0);
        Settled entry = new Settled();
        entry.LockId = lockId;
        entry.Applied = applied;
        entry.At = Time.unscaledTime;
        settled.Add(entry);
    }

    private static Settled SettledFor(string lockId)
    {
        if (lockId == null) return null;
        for (int i = 0; i < settled.Count; i++)
        {
            if (settled[i].LockId == lockId) return settled[i];
        }
        return null;
    }

    private static void ClearOutcomes()
    {
        outcomes.Clear();
        settled.Clear();
        pendingLockId = null;
        openLockId = null;
        exitingLockId = null;
    }
}
