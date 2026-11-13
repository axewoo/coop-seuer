using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;

internal static class QuestStartHost
{
    private const float PrimeInterval = 0.25f;
    private static readonly HashSet<string> active = new HashSet<string>();
    private static bool primed;
    private static bool wasLive;
    private static float nextPrime;
    private static float nextLive;

    public static void Reset()
    {
        active.Clear();
        primed = false;
        wasLive = false;
        nextPrime = 0f;
        nextLive = 0f;
    }

    public static void Observe()
    {
        try
        {
            if (!Session.IsHost || !wasLive || !Live()) return;
            TaskController ctl = TaskController.instance;
            if (ctl == null || ctl.allTasks == null) return;
            Diff(ctl);
            Prime();
        }
        catch (Exception e)
        {
            CoopLog.Once("QuestStartHost.Observe", e);
        }
    }

    public static void Tick()
    {
        try
        {
            if (!Session.IsHost)
            {
                if (primed || active.Count > 0) Reset();
                return;
            }
            if (!Live())
            {
                wasLive = false;
                float now = Time.unscaledTime;
                if (now < nextPrime) return;
                nextPrime = now + PrimeInterval;
                Prime();
                return;
            }
            if (!wasLive)
            {
                wasLive = true;
                Prime();
                return;
            }
            float clock = Time.unscaledTime;
            if (clock < nextLive) return;
            nextLive = clock + PrimeInterval;
            TaskController ctl = TaskController.instance;
            if (ctl == null || ctl.allTasks == null || ctl.allTasks.Count == 0) return;
            Diff(ctl);
            Prime();
        }
        catch (Exception e)
        {
            CoopLog.Once("QuestStartHost.Tick", e);
        }
    }

    private static bool Live()
    {
        if (!Session.IsInLobby || !Session.IsHost) return false;
        if (SaveData.Busy || SaveController.Loading) return false;
        if (GuestSession.InHostWorld) return false;
        return GameRefs.LocalPlayer != null && GameRefs.AreaId != null;
    }

    private static void Prime()
    {
        TaskController ctl = TaskController.instance;
        if (ctl == null || ctl.allTasks == null) return;
        active.Clear();
        for (int i = 0; i < ctl.allTasks.Count; i++)
        {
            TaskStatusInfo status = ctl.allTasks[i];
            if (status == null || status.task == null) continue;
            if (status.renovation) continue;
            if (status.status != CurrentTaskStatus.Active) continue;
            string id = status.task.Id;
            if (!string.IsNullOrEmpty(id)) active.Add(id);
        }
        primed = true;
    }

    private static void Diff(TaskController ctl)
    {
        if (!primed) return;
        for (int i = 0; i < ctl.allTasks.Count; i++)
        {
            TaskStatusInfo status = ctl.allTasks[i];
            if (status == null || status.task == null) continue;
            if (status.renovation) continue;
            if (status.status != CurrentTaskStatus.Active) continue;
            string id = status.task.Id;
            if (string.IsNullOrEmpty(id) || active.Contains(id)) continue;
            Announce(id, status.task.title);
        }
    }

    private static void Announce(string taskId, string title)
    {
        string name = string.IsNullOrEmpty(title) ? taskId : title;
        string payload = QuestWire.StartNotice(taskId, name);
        if (payload == null) return;
        int sent = 0;
        foreach (ulong peer in Session.Peers)
        {
            if (Session.Send(peer, QuestWire.Channel, payload, true)) sent++;
        }
        CoopLog.Log.Info("coop: quest " + taskId + " started on the host (" + name + "), sent to " + sent + " peer(s)");
    }
}
