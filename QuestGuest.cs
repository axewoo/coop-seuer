using System;
using System.Collections.Generic;
using UnityEngine;

internal static partial class QuestGuest
{
    private sealed class Pending
    {
        public int ReqId;
        public string Text;
        public string TaskId;
        public string ObjectiveId;
        public string Sub;
        public int Wanted;
        public int Attempts;
        public float SentAt;
    }

    private const float RetryAfter = 3f;
    private const float ExpireAfter = 15f;
    private const int MaxAttempts = 3;
    private const int MaxPending = 64;
    private const string TimeoutText = "The host did not answer, that quest step was not applied";

    private static readonly List<Pending> outgoing = new List<Pending>();
    private static readonly List<Pending> finished = new List<Pending>();
    private static int lastReqId;
    private static bool syncSent;
    private static bool quiet;
    private static bool timeoutOnce;

    public static bool Forwarding
    {
        get { return Session.IsGuest && GuestSession.InHostWorld && GuestSession.Loaded; }
    }

    public static bool HasState
    {
        get { return !quiet; }
    }

    public static void Resync()
    {
        if (Session.IsHost) return;
        syncSent = false;
        quiet = true;
        ResetFlow();
    }

    public static void Init()
    {
        Reset();
    }

    public static void Reset()
    {
        outgoing.Clear();
        finished.Clear();
        syncSent = false;
        quiet = true;
        timeoutOnce = false;
        ResetFlow();
    }

    public static bool ForwardEdit(TaskInfo task, TaskObjective objective, ObjectiveStatus objectiveStatus, bool optional, bool createNewTask, CurrentTaskStatus taskStatus, bool objectiveHasToExist, bool taskHasToBeActive)
    {
        if (task == null || objective == null) return false;
        if (string.IsNullOrEmpty(task.Id) || string.IsNullOrEmpty(objective.Id))
        {
            EmptyId("ForwardEdit");
            return false;
        }
        int reqId = Next();
        return Send(reqId, QuestWire.ReqEdit(reqId, task.Id, objective.Id, (int)objectiveStatus, optional, createNewTask, (int)taskStatus, objectiveHasToExist, taskHasToBeActive), task.Id, objective.Id, "EDIT", (int)objectiveStatus);
    }

    public static bool ForwardStatus(string taskId, CurrentTaskStatus taskStatus)
    {
        if (string.IsNullOrEmpty(taskId))
        {
            EmptyId("ForwardStatus");
            return false;
        }
        int reqId = Next();
        return Send(reqId, QuestWire.ReqStatus(reqId, taskId, (int)taskStatus), taskId, null, "TSTAT", -1);
    }

    public static bool ForwardObjectiveDone(string taskId, string objectiveId)
    {
        if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(objectiveId))
        {
            EmptyId("ForwardObjectiveDone");
            return false;
        }
        int reqId = Next();
        return Send(reqId, QuestWire.ReqObjectiveDone(reqId, taskId, objectiveId), taskId, objectiveId, "OBJDONE", (int)ObjectiveStatus.Finished);
    }

    public static bool ForwardCounter(TaskInfo task, TaskObjective objective, int amount, bool setAmount)
    {
        if (task == null || objective == null) return false;
        if (string.IsNullOrEmpty(task.Id) || string.IsNullOrEmpty(objective.Id))
        {
            EmptyId("ForwardCounter");
            return false;
        }
        int reqId = Next();
        return Send(reqId, QuestWire.ReqCounter(reqId, task.Id, objective.Id, amount, setAmount), task.Id, objective.Id, "COUNTER", -1);
    }

    private static void EmptyId(string method)
    {
        CoopLog.Once("QuestGuest.ids", "a quest step with an empty id was refused locally: " + method);
    }

    public static void Handle(ulong from, QuestMsg m)
    {
        try
        {
            if (Session.IsHost || from != Session.HostId) return;
            if (m.Kind == QuestWire.KindStart)
            {
                HostStart(m);
                return;
            }
            if (m.Kind != "QACK") return;
            Pending pending = Take(m.ReqId);
            if (pending == null)
            {
                CoopLog.Log.Debug("quest.ack", "an answer to request " + m.ReqId + " arrived with nothing waiting for it");
                return;
            }
            if (!m.Ok)
            {
                Refused(pending, m.Reason);
                return;
            }
            Succeeded(pending);
        }
        catch (Exception e)
        {
            CoopLog.Once("QuestGuest.Handle", e);
        }
    }

    private static void HostStart(QuestMsg m)
    {
        string name = string.IsNullOrEmpty(m.Title) ? m.TaskId : m.Title;
        CoopPopup.Show(m.TaskId, "Quest", Session.DisplayName(Session.HostId) + " is starting the quest: " + name);
        CoopLog.Log.Info("coop: the host started the quest " + m.TaskId + " (" + name + ")");
    }

    private static string Label(Pending pending)
    {
        return Label(pending.TaskId, pending.ObjectiveId);
    }

    public static void ReportStart(string taskId, string title)
    {
        try
        {
            if (!Session.IsGuest) return;
            ulong host = Session.HostId;
            if (host == 0 || !Session.Peers.Contains(host)) return;
            if (!Session.Send(host, QuestWire.Channel, QuestWire.StartAttempt(taskId, title), true))
            {
                CoopLog.Once("QuestGuest.start", "a guest start attempt could not be reported to the host");
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("QuestGuest.ReportStart", e);
        }
    }

    public static void OnState(string payload)
    {
        try
        {
            if (Session.IsHost) return;
            List<QuestTaskState> tasks;
            int rev;
            string world;
            if (!QuestWire.TryDecode(payload, out tasks, out rev, out world))
            {
                CoopLog.Once("QuestGuest.state", "dropped a quest state that this build cannot read");
                return;
            }
            if (!AcceptState(rev, world)) return;
            Apply(tasks);
            MarkApplied(rev);
        }
        catch (Exception e)
        {
            CoopLog.Once("QuestGuest.OnState", e);
        }
    }

    public static void Tick()
    {
        try
        {
            if (!Session.IsInLobby)
            {
                if (outgoing.Count > 0 || syncSent) Reset();
                return;
            }
            if (Session.IsHost || !Forwarding) return;
            float now = Time.unscaledTime;
            Sync();
            Retry(now);
            TickRetries(now);
        }
        catch (Exception e)
        {
            CoopLog.Once("QuestGuest.Tick", e);
        }
    }

    private static void Sync()
    {
        if (syncSent) return;
        int reqId = Next();
        if (Send(reqId, QuestWire.ReqSync(reqId), null, null, "SYNC", -1)) syncSent = true;
    }

    private static void Retry(float now)
    {
        finished.Clear();
        for (int i = 0; i < outgoing.Count; i++)
        {
            Pending pending = outgoing[i];
            float age = now - pending.SentAt;
            if (age > ExpireAfter)
            {
                finished.Add(pending);
                continue;
            }
            if (pending.Attempts < MaxAttempts && age > RetryAfter * pending.Attempts)
            {
                if (Resend(pending)) pending.Attempts++;
            }
        }
        for (int i = 0; i < finished.Count; i++)
        {
            outgoing.Remove(finished[i]);
            if (!timeoutOnce)
            {
                timeoutOnce = true;
                CoopNotice.Show("quest|timeout", TimeoutText, 5f);
                CoopLog.Log.Info("coop: " + TimeoutText);
            }
        }
    }

    private static int Next()
    {
        lastReqId++;
        if (lastReqId > 999999999) lastReqId = 1;
        return lastReqId;
    }

    private static bool Send(int reqId, string text, string taskId, string objectiveId, string sub, int wanted)
    {
        ulong host = Session.HostId;
        if (host == 0 || !Session.Peers.Contains(host)) return false;
        if (outgoing.Count >= MaxPending)
        {
            CoopLog.Once("QuestGuest.full", "the quest queue is full, a step was dropped");
            return false;
        }
        if (!Session.Send(host, QuestWire.Channel, text, true)) return false;
        Pending pending = new Pending();
        pending.ReqId = reqId;
        pending.Text = text;
        pending.TaskId = taskId;
        pending.ObjectiveId = objectiveId;
        pending.Sub = sub;
        pending.Wanted = wanted;
        pending.Attempts = 1;
        pending.SentAt = Time.unscaledTime;
        outgoing.Add(pending);
        return true;
    }

    private static bool Resend(Pending pending)
    {
        ulong host = Session.HostId;
        if (host == 0 || !Session.Peers.Contains(host)) return false;
        return Session.Send(host, QuestWire.Channel, pending.Text, true);
    }

    private static Pending Take(int reqId)
    {
        for (int i = 0; i < outgoing.Count; i++)
        {
            if (outgoing[i].ReqId != reqId) continue;
            Pending pending = outgoing[i];
            outgoing.RemoveAt(i);
            return pending;
        }
        return null;
    }

    private static readonly HashSet<string> unknownTasks = new HashSet<string>();

    private static void Apply(List<QuestTaskState> tasks)
    {
        TaskController ctl = TaskController.instance;
        if (ctl == null) return;
        if (tasks == null || tasks.Count == 0)
        {
            CoopLog.Log.Info("quest.state: refused an empty host quest state, the guest task list is kept");
            if (CoopDebug.On("quest")) CoopDebug.Event("quest", "state ignored", "the host quest state was empty, the guest task list is kept");
            return;
        }
        bool suppress = quiet;
        quiet = false;
        Dictionary<string, int> before = new Dictionary<string, int>();
        if (ctl.allTasks != null)
        {
            foreach (TaskStatusInfo status in ctl.allTasks)
            {
                if (status != null && status.task != null) before[status.task.Id] = (int)status.status;
            }
        }
        List<TaskStatusInfo> next = new List<TaskStatusInfo>();
        int dropped = 0;
        for (int i = 0; i < tasks.Count; i++)
        {
            QuestTaskState entry = tasks[i];
            TaskInfo info = ResolveTask(entry);
            if (info == null)
            {
                dropped++;
                if (unknownTasks.Count > 256) unknownTasks.Clear();
                if (unknownTasks.Add(entry.Id)) CoopLog.Log.Info("quest.state: a quest state names a task this build does not have: " + entry.Id);
                continue;
            }
            TaskStatusInfo status = FindStatus(ctl, info.Id);
            bool fresh = status == null;
            if (fresh) status = new TaskStatusInfo(info, (CurrentTaskStatus)entry.Status);
            status.task = info;
            status.status = (CurrentTaskStatus)entry.Status;
            status.renovation = entry.Renovation;
            if (status.objectives == null) status.objectives = new List<ObjectiveStatusInfo>();
            List<ObjectiveStatusInfo> created = new List<ObjectiveStatusInfo>();
            List<ObjectiveStatusInfo> kept = ApplyObjectives(status, entry, created, ref dropped);
            dropped += LogStaleObjectives(status, kept);
            status.objectives = kept;
            next.Add(status);
            Draw(fresh, status, before, created, suppress);
        }
        ctl.allTasks.Clear();
        ctl.allTasks.AddRange(next);
        dropped += LogStaleTasks(before, next);
        Lists(ctl, next);
        if (TaskPanel.instance != null) TaskPanel.instance.UpdateCurrentTaskInfo();
        CoopLog.Log.Info("quest.state: applied the host quest state, " + next.Count + " of " + tasks.Count + " task(s), dropped " + dropped);
        if (CoopDebug.On("quest")) CoopDebug.Event("quest", "state applied", "applied the host quest state rev " + lastAppliedRev + ", " + next.Count + " of " + tasks.Count + " task(s), dropped " + dropped);
    }

    private static List<ObjectiveStatusInfo> ApplyObjectives(TaskStatusInfo status, QuestTaskState entry, List<ObjectiveStatusInfo> created, ref int dropped)
    {
        List<ObjectiveStatusInfo> next = new List<ObjectiveStatusInfo>();
        for (int i = 0; i < entry.Objectives.Count; i++)
        {
            QuestObjectiveState item = entry.Objectives[i];
            TaskObjective objective = ResolveObjective(status, entry.Id, item);
            if (objective == null)
            {
                dropped++;
                CoopLog.Once("QuestGuest.objective", "a quest state names an objective this build does not have: " + item.Id);
                continue;
            }
            ObjectiveStatusInfo known = FindObjective(status, item.Id);
            if (known == null)
            {
                known = new ObjectiveStatusInfo(objective, (ObjectiveStatus)item.Status, item.Optional, item.CounterAmount);
                created.Add(known);
            }
            known.objective = objective;
            known.status = (ObjectiveStatus)item.Status;
            known.optional = item.Optional;
            known.currentAmount = item.CurrentAmount;
            known.counterAmount = item.CounterAmount;
            next.Add(known);
        }
        return next;
    }

    private static ObjectiveStatusInfo FindObjective(TaskStatusInfo status, string objectiveId)
    {
        for (int i = 0; i < status.objectives.Count; i++)
        {
            ObjectiveStatusInfo candidate = status.objectives[i];
            if (candidate != null && candidate.objective != null && candidate.objective.Id == objectiveId) return candidate;
        }
        return null;
    }

    private static int LogStaleObjectives(TaskStatusInfo status, List<ObjectiveStatusInfo> kept)
    {
        string gone = "";
        int count = 0;
        for (int i = 0; i < status.objectives.Count; i++)
        {
            ObjectiveStatusInfo candidate = status.objectives[i];
            if (candidate == null || candidate.objective == null) continue;
            if (HoldsObjective(kept, candidate.objective.Id)) continue;
            if (count < 5) gone = gone.Length == 0 ? candidate.objective.Id : gone + ", " + candidate.objective.Id;
            count++;
        }
        if (count > 0) CoopLog.Log.Info("quest.state: dropped " + count + " objective(s) the host no longer has: " + gone);
        return count;
    }

    private static bool HoldsObjective(List<ObjectiveStatusInfo> kept, string objectiveId)
    {
        for (int i = 0; i < kept.Count; i++)
        {
            ObjectiveStatusInfo item = kept[i];
            if (item != null && item.objective != null && item.objective.Id == objectiveId) return true;
        }
        return false;
    }

    private static int LogStaleTasks(Dictionary<string, int> before, List<TaskStatusInfo> applied)
    {
        string gone = "";
        int count = 0;
        foreach (KeyValuePair<string, int> pair in before)
        {
            if (HoldsTask(applied, pair.Key)) continue;
            if (count < 5) gone = gone.Length == 0 ? pair.Key : gone + ", " + pair.Key;
            count++;
        }
        if (count > 0) CoopLog.Log.Info("quest.state: dropped " + count + " task(s) the host no longer has: " + gone);
        return count;
    }

    private static bool HoldsTask(List<TaskStatusInfo> applied, string taskId)
    {
        for (int i = 0; i < applied.Count; i++)
        {
            TaskStatusInfo status = applied[i];
            if (status != null && status.task != null && status.task.Id == taskId) return true;
        }
        return false;
    }

    private static TaskInfo ResolveTask(QuestTaskState entry)
    {
        TaskInfo task = QuestHost.FindTask(entry.Id);
        if (task != null) return task;
        if (string.IsNullOrEmpty(entry.Path)) return null;
        try
        {
            return SavableScriptableObject.LoadFromPath(entry.Path) as TaskInfo;
        }
        catch (Exception e)
        {
            CoopLog.Once("QuestGuest.load", e);
            return null;
        }
    }

    private static TaskObjective ResolveObjective(TaskStatusInfo status, string taskId, QuestObjectiveState item)
    {
        for (int i = 0; i < status.objectives.Count; i++)
        {
            ObjectiveStatusInfo candidate = status.objectives[i];
            if (candidate != null && candidate.objective != null && candidate.objective.Id == item.Id) return candidate.objective;
        }
        TaskObjective objective = QuestHost.FindObjective(taskId, item.Id);
        if (objective != null) return objective;
        if (string.IsNullOrEmpty(item.Path)) return null;
        try
        {
            return SavableScriptableObject.LoadFromPath(item.Path) as TaskObjective;
        }
        catch (Exception e)
        {
            CoopLog.Once("QuestGuest.load", e);
            return null;
        }
    }

    private static TaskStatusInfo FindStatus(TaskController ctl, string id)
    {
        if (ctl.allTasks == null) return null;
        foreach (TaskStatusInfo status in ctl.allTasks)
        {
            if (status != null && status.task != null && status.task.Id == id) return status;
        }
        return null;
    }

    private static void Panel(bool fresh, TaskStatusInfo status, Dictionary<string, int> before)
    {
        TaskPanel panel = TaskPanel.instance;
        if (panel == null) return;
        bool active = status.status == CurrentTaskStatus.Active;
        if (fresh)
        {
            panel.AddActiveTask(status);
            if (!active) panel.MoveToFinishedTasks(status);
            return;
        }
        int old;
        if (before.TryGetValue(status.task.Id, out old) && old == (int)CurrentTaskStatus.Active && !active) panel.MoveToFinishedTasks(status);
    }

    private static void Draw(bool fresh, TaskStatusInfo status, Dictionary<string, int> before, List<ObjectiveStatusInfo> created, bool suppress)
    {
        try
        {
            Panel(fresh, status, before);
        }
        catch (Exception e)
        {
            CoopLog.Once("QuestGuest.draw", e);
        }
        try
        {
            Notify(fresh, status, before, created, suppress);
        }
        catch (Exception e)
        {
            CoopLog.Once("QuestGuest.notify", e);
        }
    }

    private static void Notify(bool fresh, TaskStatusInfo status, Dictionary<string, int> before, List<ObjectiveStatusInfo> created, bool suppress)
    {
        if (status == null || status.task == null) return;
        if (suppress) return;
        if (fresh)
        {
            if (status.status == CurrentTaskStatus.Active) Toast("New Task", status.task.title);
            return;
        }
        if (status.status == CurrentTaskStatus.Active)
        {
            int old;
            if (before.TryGetValue(status.task.Id, out old) && old != (int)CurrentTaskStatus.Active) Toast("New Task", status.task.title);
        }
        for (int i = 0; i < created.Count; i++) NotifyObjective(created[i]);
    }

    private static void NotifyObjective(ObjectiveStatusInfo objective)
    {
        if (objective == null || objective.objective == null) return;
        if (objective.status != ObjectiveStatus.Active) return;
        string text = objective.objective.title;
        if (objective.counterAmount > 0) text += " 0/" + objective.counterAmount;
        if (objective.optional) text += " (Optional)";
        Toast("New Objective", text);
    }

    private static void Toast(string type, string text)
    {
        try
        {
            if (!GuestSession.Loaded) return;
            if (Notifications.instance == null) return;
            Notifications.instance.CreateNotification(type, text);
        }
        catch (Exception e)
        {
            CoopLog.Once("QuestGuest.Toast", e);
        }
    }

    private static void Lists(TaskController ctl, List<TaskStatusInfo> tasks)
    {
        ctl.anyStatusTasksIds.Clear();
        ctl.activeTasksIds.Clear();
        ctl.failedTasksIds.Clear();
        ctl.abandonedTasksIds.Clear();
        ctl.finishedTasksIds.Clear();
        ctl.anyStatusObjectives.Clear();
        ctl.activeObjectives.Clear();
        ctl.failedObjectives.Clear();
        ctl.abandonedObjectives.Clear();
        ctl.completedObjectives.Clear();
        for (int i = 0; i < tasks.Count; i++)
        {
            TaskStatusInfo status = tasks[i];
            if (status == null || status.task == null) continue;
            string id = status.task.Id;
            ctl.anyStatusTasksIds.Add(id);
            if (status.status == CurrentTaskStatus.Active) ctl.activeTasksIds.Add(id);
            else if (status.status == CurrentTaskStatus.Failed) ctl.failedTasksIds.Add(id);
            else if (status.status == CurrentTaskStatus.Finished) ctl.finishedTasksIds.Add(id);
            else if (status.status == CurrentTaskStatus.Abandoned) ctl.abandonedTasksIds.Add(id);
            if (status.objectives == null) continue;
            for (int j = 0; j < status.objectives.Count; j++)
            {
                ObjectiveStatusInfo objective = status.objectives[j];
                if (objective == null || objective.objective == null) continue;
                string key = id + "_" + objective.objective.Id;
                ctl.anyStatusObjectives.Add(key);
                if (objective.status == ObjectiveStatus.Active) ctl.activeObjectives.Add(key);
                else if (objective.status == ObjectiveStatus.Failed) ctl.failedObjectives.Add(key);
                else if (objective.status == ObjectiveStatus.Finished) ctl.completedObjectives.Add(key);
                else if (objective.status == ObjectiveStatus.Abandoned) ctl.abandonedObjectives.Add(key);
            }
        }
    }
}
