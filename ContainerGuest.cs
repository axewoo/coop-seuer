using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;

internal static partial class ContainerGuest
{
    private const float StateTimeout = 5f;
    private const float TouchSeconds = 30f;
    private const float CloseBusySeconds = 30f;
    private const int MaxClosing = 4;
    private const float NoticeQuiet = 2f;
    private const int MaxReleased = 8;
    private static readonly Dictionary<string, string> holders = new Dictionary<string, string>();
    private static readonly List<string> released = new List<string>();
    private static readonly List<string> awaiting = new List<string>();
    private static int lastReqId;
    private static int pendingReq;
    private static string pendingKey;
    private static Storage pendingStorage;
    private static bool pendingForce;
    private static bool pendingOpen;
    private static string pendingJson;
    private static float pendingAt;
    private static Storage open;
    private static string openKey;
    private static Storage exiting;
    private static string exitingKey;
    private static readonly List<Closing> closing = new List<Closing>();
    private static float touchAt;
    private static bool reentry;
    private static float noticeAt;
    private static string noticeText = "";

    private sealed class Closing
    {
        public string Key;
        public string LockId;
        public Storage Storage;
        public Delta Delta;
        public float At;
    }

    public static void Init()
    {
    }

    public static void Reset()
    {
        Clear();
        if (!Session.IsInLobby) ClearOutcomes();
    }

    public static void OnSceneChanged()
    {
        if (openKey != null) SendFlag(openKey, false, openLockId);
        if (exitingKey != null) SendFlag(exitingKey, false, exitingLockId);
        if (pendingKey != null) SendFlag(pendingKey, false, pendingLockId);
        for (int i = 0; i < closing.Count; i++)
        {
            if (closing[i].Key != null) SendFlag(closing[i].Key, false, closing[i].LockId);
        }
        if (openSnap != null)
        {
            Storage seen = open != null ? open : exiting;
            Dictionary<int, int> snap = seen == null ? null : Snapshot(seen);
            if (snap != null) liveSnap = snap;
        }
        RevertOpen();
        for (int i = 0; i < closing.Count; i++) Revert(closing[i].Key, closing[i].Delta);
        Clear();
    }

    public static void OnSceneKept()
    {
        ClosePending();
        CloseRunning();
        List<Closing> waiting = new List<Closing>(closing);
        Clear();
        closing.AddRange(waiting);
    }

    public static bool OnEnter(Storage storage, bool forceOpen)
    {
        try
        {
            if (!GuestSession.InHostWorld || reentry || storage == null || FreeRoam.IAmAuthority) return true;
            if (PrisonLocal.IsCellStorage(storage))
            {
                if (PrisonLocal.OwnsCell(storage)) return true;
                Notice("That confiscated locker belongs to the other player.");
                return false;
            }
            if (FreeRoam.Authority == 0UL)
            {
                CoopNotice.Show("roam|owner", "Waiting to learn who runs this scene, try again in a moment", 3f);
                return false;
            }
            if (GuestGate.Block(GuestGate.KindContainer)) return false;
            if (storage == open) return false;
            if (pendingStorage == storage) return false;
            ClosePending();
            CloseRunning();
            string key = ContainerWire.KeyOf(storage);
            if (key == null)
            {
                Notice("This container is not shared in co-op.");
                return false;
            }
            int reqId = ++lastReqId;
            if (!Session.Send(FreeRoam.Authority, ContainerWire.Channel, ContainerWire.ReqOpen(reqId, GameRefs.AreaId, key), true))
            {
                Notice("The host could not be reached.");
                return false;
            }
            ClearPending();
            pendingReq = reqId;
            pendingKey = key;
            pendingStorage = storage;
            pendingForce = forceOpen;
            pendingAt = Time.unscaledTime;
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerGuest.OnEnter", e);
            return false;
        }
    }

    public static bool IsPending(Storage storage)
    {
        return storage != null && pendingStorage == storage;
    }

    public static Storage Pending()
    {
        return pendingKey == null ? null : pendingStorage;
    }

    public static bool IsOpen(string key)
    {
        return key != null && open != null && openKey == key;
    }

    public static bool TakeReleased(string key)
    {
        if (key == null) return false;
        return released.Remove(key);
    }

    private static void Await(string key)
    {
        if (key == null || awaiting.Contains(key)) return;
        if (awaiting.Count >= MaxReleased) awaiting.RemoveAt(0);
        awaiting.Add(key);
    }

    private static void MarkReleased(string key)
    {
        if (key == null || released.Contains(key)) return;
        if (released.Count >= MaxReleased)
        {
            CoopLog.Log.Error("coop: more than " + MaxReleased + " released container keys were unread, the oldest one was dropped");
            released.RemoveAt(0);
        }
        released.Add(key);
    }

    public static bool OnExitStart(Storage storage)
    {
        try
        {
            if (!GuestSession.InHostWorld || storage == null || storage != open) return true;
            Dictionary<int, int> seen = Snapshot(storage);
            if (seen != null) liveSnap = seen;
            exiting = storage;
            exitingKey = openKey;
            exitingLockId = openLockId;
            open = null;
            openKey = null;
            openLockId = null;
            touchAt = 0f;
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerGuest.OnExitStart", e);
            return true;
        }
    }

    public static void OnExitEnd()
    {
        try
        {
            Storage storage = exiting;
            string key = exitingKey;
            string lockId = exitingLockId;
            exiting = null;
            exitingKey = null;
            exitingLockId = null;
            if (storage == null || key == null) return;
            SendClose(key, storage, lockId, FinishOpen(key, storage));
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerGuest.OnExitEnd", e);
        }
    }

    public static void Handle(ulong from, ContainerMsg m)
    {
        try
        {
            if (from != FreeRoam.Authority || !GuestSession.InHostWorld) return;
            if (m.Kind == "OPEN") OnOpen(m);
            else if (m.Kind == "DENY") OnDeny(m);
            else if (m.Kind == "LOCK") holders[m.Key] = Session.DisplayName(m.Holder);
            else if (m.Kind == "UNLOCK")
            {
                holders.Remove(m.Key);
                LockLost(m.Key);
                if (awaiting.Remove(m.Key)) MarkReleased(m.Key);
            }
            else if (m.Kind == "APPLIED") OutcomeApplied(m.LockId);
            else if (m.Kind == "REFUSED") OutcomeRefused(m.LockId, m.Reason);
            else if (m.Kind == "STALE") OutcomeStale(m);
            else if (m.Kind == "DIGEST") OnDigest(m);
            else if (m.Kind == "RELOCK") OnRelock(m);
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerGuest.Handle", e);
        }
    }

    public static void OnState(ulong from, string name, string json)
    {
        try
        {
            if (from != FreeRoam.Authority || !GuestSession.InHostWorld || json == null) return;
            if (pendingKey != null && Accepted(name))
            {
                pendingJson = json;
                TryOpen();
                return;
            }
            if (TrySyncState(name, json)) return;
            CoopLog.Log.Info("coop: a container state for " + name + " arrived with no matching request");
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerGuest.OnState", e);
        }
    }

    public static void Tick()
    {
        try
        {
            SyncTick();
            Touch();
            RetryClose();
            Live();
            Outcomes();
            RetryRemainders();
            if (pendingKey == null || Time.unscaledTime - pendingAt < StateTimeout) return;
            string key = pendingKey;
            string lockId = pendingLockId;
            Storage storage = pendingStorage;
            ClearPending();
            CraftGuest.OnStationUnavailable(storage);
            Notice("The container did not open.");
            SendClose(key, null, lockId, null);
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerGuest.Tick", e);
        }
    }

    private static void Touch()
    {
        if (open == null || openKey == null) return;
        if (Time.unscaledTime - touchAt < TouchSeconds) return;
        touchAt = Time.unscaledTime;
        Session.Send(FreeRoam.Authority, ContainerWire.Channel, ContainerWire.Touch(GameRefs.AreaId, openKey, openLockId), true);
    }

    private static void OnOpen(ContainerMsg m)
    {
        if (pendingKey != null && m.Key == pendingKey && m.ReqId != pendingReq) return;
        if (pendingKey == null || m.ReqId != pendingReq || m.Key != pendingKey)
        {
            SendClose(m.Key, null, m.LockId, null);
            return;
        }
        pendingLockId = m.LockId;
        pendingOpen = true;
        TryOpen();
    }

    private static void OnDeny(ContainerMsg m)
    {
        if (pendingKey == null || m.ReqId != pendingReq) return;
        string key = pendingKey;
        string reason = m.Reason;
        Storage storage = pendingStorage;
        ClearPending();
        CraftGuest.OnStationUnavailable(storage);
        if (reason == "in use")
        {
            string name;
            holders.TryGetValue(key, out name);
            Notice(string.IsNullOrEmpty(name) ? "Someone else is using that" : "Someone else is using that (" + name + ")");
            return;
        }
        Notice("The host refused: " + reason);
    }

    private static void TryOpen()
    {
        if (!pendingOpen || pendingJson == null) return;
        Storage storage = pendingStorage;
        string key = pendingKey;
        string lockId = pendingLockId;
        bool force = pendingForce;
        string json = pendingJson;
        ClearPending();
        if (storage == null) return;
        if (!ContainerWire.Apply(storage, key, json))
        {
            CoopLog.Log.Error("coop: the container state for " + key + " was refused");
            CraftGuest.OnStationUnavailable(storage);
            Notice("The container could not be opened.");
            SendClose(key, null, lockId, null);
            return;
        }
        Refresh(storage);
        open = storage;
        openKey = key;
        openLockId = lockId;
        OpenSnapshot(storage);
        if (CoopDebug.On("cont")) CoopDebug.Event("cont", key, "opened under lock " + lockId);
        touchAt = Time.unscaledTime;
        reentry = true;
        try
        {
            storage.EnterExternally(force);
        }
        catch (Exception e)
        {
            open = null;
            openKey = null;
            openLockId = null;
            openSnap = null;
            liveSnap = null;
            touchAt = 0f;
            CoopLog.Once("ContainerGuest.TryOpen", e);
            Notice("The container could not be opened.");
            SendClose(key, null, lockId, null);
        }
        finally
        {
            reentry = false;
        }
    }

    private static void ClosePending()
    {
        string key = pendingKey;
        string lockId = pendingLockId;
        if (key == null) return;
        ClearPending();
        SendClose(key, null, lockId, null);
    }

    internal static void CloseRunning()
    {
        Storage running = open;
        if (running == null) return;
        running.ExitExternally();
    }

    private static void SendClose(string key, Storage storage, string lockId, Delta delta)
    {
        if (key == null) return;
        Await(key);
        if (storage == null)
        {
            SendFlag(key, false, lockId);
            Revert(key, delta);
            return;
        }
        string json = ContainerWire.Capture(storage, key);
        if (json == null && SaveData.Busy)
        {
            Remember(key, storage, lockId, delta);
            SendFlag(key, true, lockId);
            return;
        }
        SendFlag(key, json != null, lockId);
        if (json == null)
        {
            CoopLog.Log.Error("coop: the container state for " + key + " could not be read on the way out");
            Revert(key, delta);
            return;
        }
        SendState(key, json, lockId, delta);
        GuestProfile.AfterTransfer();
    }

    private static void SendFlag(string key, bool hasState, string lockId)
    {
        Session.Send(FreeRoam.Authority, ContainerWire.Channel, ContainerWire.Close(GameRefs.AreaId, key, hasState, lockId), true);
    }

    private static void Remember(string key, Storage storage, string lockId, Delta delta)
    {
        if (closing.Count >= MaxClosing)
        {
            CoopLog.Log.Error("coop: more than " + MaxClosing + " closes are waiting on the game, the oldest state for " + closing[0].Key + " was dropped");
            Revert(closing[0].Key, closing[0].Delta);
            closing.RemoveAt(0);
        }
        Closing entry = new Closing();
        entry.Key = key;
        entry.LockId = lockId;
        entry.Storage = storage;
        entry.Delta = delta;
        entry.At = Time.unscaledTime;
        closing.Add(entry);
    }

    private static void SendState(string key, string json, string lockId, Delta delta)
    {
        if (NetBlob.Send(FreeRoam.Authority, ContainerWire.ModId, ContainerWire.BlobNameOf(key, lockId), ContainerWire.Bytes(json)) < 0)
        {
            CoopLog.Log.Error("coop: the final state for " + key + " could not be sent to the host");
            SendFlag(key, false, lockId);
            Revert(key, delta);
            return;
        }
        Arm(key, lockId, delta);
    }

    private static void RetryClose()
    {
        if (closing.Count == 0) return;
        for (int i = closing.Count - 1; i >= 0; i--)
        {
            Closing entry = closing[i];
            if (SaveData.Busy)
            {
                if (Time.unscaledTime - entry.At < CloseBusySeconds) continue;
                closing.RemoveAt(i);
                CoopLog.Log.Error("coop: the game stayed busy, the final state for " + entry.Key + " was dropped");
                SendFlag(entry.Key, false, entry.LockId);
                Revert(entry.Key, entry.Delta);
                continue;
            }
            closing.RemoveAt(i);
            string json = ContainerWire.Capture(entry.Storage, entry.Key);
            if (json == null)
            {
                CoopLog.Log.Error("coop: the container state for " + entry.Key + " could not be read on the way out");
                SendFlag(entry.Key, false, entry.LockId);
                Revert(entry.Key, entry.Delta);
                continue;
            }
            SendState(entry.Key, json, entry.LockId, entry.Delta);
        }
    }

    private static void Refresh(Storage storage)
    {
        try
        {
            storage.UpdateSlots();
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerGuest.Refresh", e);
        }
    }

    private static void Clear()
    {
        holders.Clear();
        released.Clear();
        awaiting.Clear();
        ClearPending();
        open = null;
        openKey = null;
        openLockId = null;
        exiting = null;
        exitingKey = null;
        exitingLockId = null;
        closing.Clear();
        openSnap = null;
        liveSnap = null;
        touchAt = 0f;
        reentry = false;
    }

    private static void ClearPending()
    {
        pendingReq = 0;
        pendingKey = null;
        pendingLockId = null;
        pendingStorage = null;
        pendingForce = false;
        pendingOpen = false;
        pendingJson = null;
        pendingAt = 0f;
    }

    public static bool AnyOpen()
    {
        return open != null;
    }

    private static void Notice(string text)
    {
        CoopLog.Log.Info("coop: " + text);
        if (string.Equals(text, noticeText, StringComparison.Ordinal) && Time.unscaledTime - noticeAt < NoticeQuiet) return;
        noticeText = text;
        noticeAt = Time.unscaledTime;
        if (Notifications.instance != null) Notifications.instance.CreateNotification("Co-op", text);
    }
}
