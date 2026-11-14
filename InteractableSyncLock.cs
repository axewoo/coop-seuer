using System;
using System.Collections.Generic;
using UnityEngine;

internal static partial class InteractableSync
{
    private const float LockEvery = 1f;
    private const float LockFindEvery = 5f;
    private static readonly Dictionary<string, int> lockSeen = new Dictionary<string, int>();
    private static readonly Dictionary<BreakableLock, TimeOfDayAzure.Timer> paused = new Dictionary<BreakableLock, TimeOfDayAzure.Timer>();
    private static readonly List<TimeOfDayAzure.Timer> saveUnstopped = new List<TimeOfDayAzure.Timer>();
    private static BreakableLock[] locks = new BreakableLock[0];
    private static float nextLockScan;
    private static float nextLockFind;

    private static void ScanLocks(float now)
    {
        nextLockScan = now + LockEvery;
        if (now >= nextLockFind)
        {
            nextLockFind = now + LockFindEvery;
            locks = UnityEngine.Object.FindObjectsByType<BreakableLock>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        }
        bool authority = FreeRoam.IAmAuthority;
        bool follower = FreeRoam.Authority != 0UL && !authority;
        if (authority) ResumeLocks();
        for (int i = 0; i < locks.Length; i++)
        {
            BreakableLock item = locks[i];
            if (item == null) continue;
            if (authority && item._Lock != null && item._Lock.timer != null) item._Lock.timer.stopped = false;
            string key = Key(item);
            if (key == null) continue;
            if (follower) PauseLock(item);
            int state = item.strength * 2 + (item.Locked ? 1 : 0);
            int was;
            bool known = lockSeen.TryGetValue(key, out was);
            lockSeen[key] = state;
            if (follower || !known || was == state) continue;
            toPush[key] = item;
            pushAt[key] = now;
        }
    }

    private static void PauseLock(BreakableLock item)
    {
        if (item._Lock == null || item._Lock.timer == null)
        {
            TimeOfDayAzure.Timer recorded;
            if (paused.TryGetValue(item, out recorded)) DropTimer(recorded);
            paused.Remove(item);
            return;
        }
        TimeOfDayAzure.Timer timer = item._Lock.timer;
        paused[item] = timer;
        timer.stopped = true;
    }

    private static void DropRelock(Interactable target, TimeOfDayAzure.Timer before)
    {
        BreakableLock item = target as BreakableLock;
        if (before != null && (item == null || item._Lock == null || before != item._Lock.timer)) DropTimer(before);
        if (item == null)
        {
            if (target is BreakableLock) paused.Remove(item);
            return;
        }
        PauseLock(item);
    }

    private static void DropTimer(TimeOfDayAzure.Timer timer)
    {
        timer.stopped = true;
        if (TimeOfDayAzure.instance != null) timer.Destroy();
    }

    private static void ResumeLocks()
    {
        foreach (KeyValuePair<BreakableLock, TimeOfDayAzure.Timer> pair in paused)
        {
            if (pair.Key == null) DropTimer(pair.Value);
            else pair.Value.stopped = false;
        }
        paused.Clear();
    }

    public static void AuthorityTaken()
    {
        ResumeLocks();
        lockSeen.Clear();
        unapplied.Clear();
        unappliedUntil.Clear();
    }

    internal static void SaveBegin()
    {
        try
        {
            saveUnstopped.Clear();
            foreach (KeyValuePair<BreakableLock, TimeOfDayAzure.Timer> pair in paused)
            {
                TimeOfDayAzure.Timer timer = pair.Value;
                if (timer == null || !timer.stopped) continue;
                timer.stopped = false;
                saveUnstopped.Add(timer);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.SaveBegin", e);
        }
    }

    internal static void SaveEnd()
    {
        try
        {
            for (int i = 0; i < saveUnstopped.Count; i++) saveUnstopped[i].stopped = true;
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.SaveEnd", e);
        }
        saveUnstopped.Clear();
    }
}
