using System;
using System.Collections.Generic;
using UnityEngine;

internal static partial class QuestGuest
{
    private sealed class Attempt
    {
        public string Text;
        public string TaskId;
        public string ObjectiveId;
        public string Sub;
        public int Wanted;
        public string Reason;
        public int Attempts;
        public float At;
    }

    private const float StepDelay = 5f;
    private const int MaxRetries = 3;
    private const int MaxAttemptsHeld = 32;
    private static readonly List<Attempt> retries = new List<Attempt>();
    private static readonly List<Attempt> spent = new List<Attempt>();
    private static int lastAppliedRev;
    private static string lastWorld = "";
    private static ulong lastHost;

    private static void ResetFlow()
    {
        retries.Clear();
        lastAppliedRev = 0;
        lastWorld = "";
        lastHost = 0UL;
    }

    private static bool AcceptState(int rev, string world)
    {
        ulong host = Session.HostId;
        string mine = GuestSession.WorldId;
        if (host != lastHost)
        {
            lastHost = host;
            lastAppliedRev = 0;
        }
        if (!string.Equals(mine, lastWorld, StringComparison.Ordinal))
        {
            lastWorld = mine;
            lastAppliedRev = 0;
        }
        if (!string.IsNullOrEmpty(world) && !string.IsNullOrEmpty(mine) && !string.Equals(world, mine, StringComparison.Ordinal))
        {
            CoopLog.Log.Info("quest state ignored (other world)");
            if (CoopDebug.On("quest")) CoopDebug.Event("quest", "state ignored", "the host quest state of world " + world + " is not this world (" + mine + ")");
            return false;
        }
        if (rev > 0 && rev <= lastAppliedRev)
        {
            CoopLog.Log.Info("quest state ignored (older revision " + rev + ", have " + lastAppliedRev + ")");
            if (CoopDebug.On("quest")) CoopDebug.Event("quest", "state ignored", "the host quest state rev " + rev + " is not newer than the applied rev " + lastAppliedRev);
            return false;
        }
        return true;
    }

    private static void MarkApplied(int rev)
    {
        if (rev > lastAppliedRev) lastAppliedRev = rev;
    }

    private static void Refused(Pending pending, string reason)
    {
        string label = Label(pending);
        Attempt entry = Find(pending.TaskId, pending.ObjectiveId, pending.Sub);
        if (!Retryable(reason))
        {
            if (entry != null) retries.Remove(entry);
            CoopNotice.Show("quest|refused|" + label, "The host refused that quest step" + (string.IsNullOrEmpty(reason) ? "" : ": " + reason), 5f);
            CoopLog.Log.Info("coop: the host refused quest step " + label + " (request " + pending.ReqId + ")" + (string.IsNullOrEmpty(reason) ? "" : ": " + reason));
            return;
        }
        if (entry == null)
        {
            if (retries.Count >= MaxAttemptsHeld) retries.RemoveAt(0);
            entry = new Attempt();
            entry.TaskId = pending.TaskId;
            entry.ObjectiveId = pending.ObjectiveId;
            entry.Sub = pending.Sub;
            retries.Add(entry);
        }
        entry.Text = pending.Text;
        entry.Wanted = pending.Wanted;
        entry.Reason = reason;
        entry.At = Time.unscaledTime + StepDelay;
        CoopLog.Log.Info("coop: the host refused quest step " + label + " (request " + pending.ReqId + ")" + (string.IsNullOrEmpty(reason) ? "" : ": " + reason) + ", it is retried in " + (int)StepDelay + " s");
    }

    private static void Succeeded(Pending pending)
    {
        Attempt entry = Find(pending.TaskId, pending.ObjectiveId, pending.Sub);
        if (entry != null) retries.Remove(entry);
    }

    private static void TickRetries(float now)
    {
        spent.Clear();
        for (int i = 0; i < retries.Count; i++)
        {
            Attempt entry = retries[i];
            if (now < entry.At) continue;
            string label = Label(entry.TaskId, entry.ObjectiveId);
            if (!StillNeeded(entry.TaskId, entry.ObjectiveId, entry.Wanted))
            {
                spent.Add(entry);
                CoopLog.Log.Info("coop: the retry of quest step " + label + " was dropped, the host state already satisfies it");
                continue;
            }
            if (entry.Attempts >= MaxRetries)
            {
                spent.Add(entry);
                CoopNotice.Show("quest|refused|" + label, "The host refused that quest step" + (string.IsNullOrEmpty(entry.Reason) ? "" : ": " + entry.Reason), 5f);
                CoopLog.Log.Info("coop: the host kept refusing quest step " + label + " after " + MaxRetries + " retries");
                continue;
            }
            int reqId = Next();
            string text = QuestWire.Retag(entry.Text, reqId);
            if (text == null || !Send(reqId, text, entry.TaskId, entry.ObjectiveId, entry.Sub, entry.Wanted))
            {
                entry.At = now + StepDelay;
                continue;
            }
            entry.Text = text;
            entry.Attempts++;
            entry.At = now + StepDelay;
            CoopLog.Log.Info("coop: re-sent quest step " + label + ", retry " + entry.Attempts + " of " + MaxRetries);
            if (CoopDebug.On("quest")) CoopDebug.Event("quest", "retry " + entry.Attempts, "re-sent quest step " + label + " as request " + reqId + ", retry " + entry.Attempts + " of " + MaxRetries);
        }
        for (int i = 0; i < spent.Count; i++) retries.Remove(spent[i]);
    }

    private static bool Retryable(string reason)
    {
        if (string.IsNullOrEmpty(reason)) return false;
        if (reason.IndexOf("not open", StringComparison.Ordinal) < 0) return false;
        return reason.IndexOf("only the host", StringComparison.Ordinal) < 0;
    }

    private static bool StillNeeded(string taskId, string objectiveId, int wanted)
    {
        if (string.IsNullOrEmpty(taskId)) return false;
        TaskController ctl = TaskController.instance;
        if (ctl == null || ctl.allTasks == null) return false;
        TaskStatusInfo status = null;
        for (int i = 0; i < ctl.allTasks.Count; i++)
        {
            TaskStatusInfo candidate = ctl.allTasks[i];
            if (candidate != null && candidate.task != null && candidate.task.Id == taskId)
            {
                status = candidate;
                break;
            }
        }
        if (status == null) return false;
        if (status.status == CurrentTaskStatus.Finished || status.status == CurrentTaskStatus.Abandoned) return false;
        if (string.IsNullOrEmpty(objectiveId)) return true;
        if (status.objectives == null) return false;
        for (int i = 0; i < status.objectives.Count; i++)
        {
            ObjectiveStatusInfo objective = status.objectives[i];
            if (objective == null || objective.objective == null || objective.objective.Id != objectiveId) continue;
            if (wanted == (int)ObjectiveStatus.Finished) return objective.status != ObjectiveStatus.Finished;
            return objective.status != ObjectiveStatus.Active && objective.status != ObjectiveStatus.Finished;
        }
        return false;
    }

    private static Attempt Find(string taskId, string objectiveId, string sub)
    {
        for (int i = 0; i < retries.Count; i++)
        {
            Attempt entry = retries[i];
            if (entry.TaskId != taskId || entry.ObjectiveId != objectiveId || entry.Sub != sub) continue;
            return entry;
        }
        return null;
    }

    private static string Label(string taskId, string objectiveId)
    {
        if (string.IsNullOrEmpty(taskId)) return "step";
        return string.IsNullOrEmpty(objectiveId) ? taskId : taskId + "/" + objectiveId;
    }
}
