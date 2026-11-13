using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;

internal static class QuestHost
{
    private sealed class Answer
    {
        public int ReqId;
        public string Text;
    }

    private static readonly Dictionary<ulong, List<Answer>> answers = new Dictionary<ulong, List<Answer>>();
    private static readonly List<ulong> stale = new List<ulong>();
    private static readonly HashSet<ulong> owed = new HashSet<ulong>();
    private static readonly Dictionary<ulong, int> transfers = new Dictionary<ulong, int>();
    private static readonly List<ulong> lost = new List<ulong>();
    private const int MaxAnswers = 64;
    private const float MinInterval = 0.5f;
    private const float RetryInterval = 2f;
    private const string OnlyHost = "only the host starts quests";
    private static bool dirty;
    private static bool subscribed;
    private static bool watched;
    private static bool refusedOnce;
    private static float nextSend;
    private static int revision;
    private static readonly System.Random RevRng = new System.Random();
    private static int revBase;

    public static void Init()
    {
        answers.Clear();
        owed.Clear();
        transfers.Clear();
        dirty = false;
        nextSend = 0f;
        revBase = RevRng.Next(0, 1000001);
        if (!watched)
        {
            watched = true;
            NetBlob.Finished += OnBlobFinished;
        }
        if (!subscribed)
        {
            subscribed = true;
            TaskController.TaskUpdated += OnTaskOrObjectiveUpdated;
            TaskController.ObjectiveUpdated += OnTaskOrObjectiveUpdated;
        }
    }

    private static void OnTaskOrObjectiveUpdated()
    {
        try
        {
            QuestStartHost.Observe();
            Touch();
        }
        catch (Exception e)
        {
            CoopLog.Once("QuestHost.OnTaskOrObjectiveUpdated", e);
        }
    }

    public static void Reset()
    {
        answers.Clear();
        stale.Clear();
        owed.Clear();
        transfers.Clear();
        dirty = false;
        nextSend = 0f;
        revision = 0;
        revBase = RevRng.Next(0, 1000001);
        QuestStartHost.Reset();
    }

    public static void Touch()
    {
        if (!Session.IsHost) return;
        dirty = true;
        Reowe();
    }

    private static void Reowe()
    {
        owed.Clear();
        foreach (ulong peer in Session.Peers) owed.Add(peer);
    }

    private static void OnBlobFinished(int id, bool ok)
    {
        try
        {
            if (ok || !Session.IsHost) return;
            lost.Clear();
            foreach (KeyValuePair<ulong, int> pair in transfers)
            {
                if (pair.Value != id) continue;
                lost.Add(pair.Key);
                owed.Add(pair.Key);
                dirty = true;
            }
            for (int i = 0; i < lost.Count; i++) transfers.Remove(lost[i]);
        }
        catch (Exception e)
        {
            CoopLog.Once("QuestHost.blobFinished", e);
        }
    }

    public static void Tick()
    {
        try
        {
            QuestStartHost.Tick();
            if (!Session.IsInLobby)
            {
                if (answers.Count > 0 || dirty) Reset();
                return;
            }
            if (!Session.IsHost || !dirty) return;
            float now = Time.unscaledTime;
            if (now < nextSend) return;
            TaskController ctl = TaskController.instance;
            if (ctl == null || ctl.allTasks == null || ctl.allTasks.Count == 0 || SaveData.Busy)
            {
                nextSend = now + RetryInterval;
                return;
            }
            if (Broadcast())
            {
                dirty = false;
                nextSend = now + MinInterval;
                return;
            }
            nextSend = now + RetryInterval;
        }
        catch (Exception e)
        {
            CoopLog.Once("QuestHost.Tick", e);
        }
    }

    public static void Handle(ulong from, QuestMsg m)
    {
        try
        {
            if (!Session.IsHost || !Session.Peers.Contains(from)) return;
            if (m.Kind == QuestWire.KindStartAttempt)
            {
                Tried(from, m);
                return;
            }
            if (m.Kind != "QREQ") return;
            string text = Stored(from, m.ReqId);
            if (text == null)
            {
                try
                {
                    text = (m.Sub == "SYNC") ? QuestWire.Ack(m.ReqId, true, "") : Apply(from, m);
                }
                catch (Exception e)
                {
                    CoopLog.Once("QuestHost.Apply", e);
                    text = QuestWire.Ack(m.ReqId, false, "the host could not apply that request");
                }
                if (string.IsNullOrEmpty(text)) text = QuestWire.Ack(m.ReqId, false, "the host could not apply that request");
                Remember(from, m.ReqId, text);
            }
            if (m.Sub == "SYNC") Touch();
            Session.Send(from, QuestWire.Channel, text, true);
        }
        catch (Exception e)
        {
            CoopLog.Once("QuestHost.Handle", e);
        }
    }

    public static void OnMemberLeft(ulong peer)
    {
        try
        {
            if (!Session.IsInLobby || !Session.IsHost) return;
            answers.Remove(peer);
        }
        catch (Exception e)
        {
            CoopLog.Once("QuestHost.OnMemberLeft", e);
        }
    }

    private static void Tried(ulong from, QuestMsg m)
    {
        string name = string.IsNullOrEmpty(m.Title) ? m.TaskId : m.Title;
        CoopNotice.Show("queststart|" + from + "|" + m.TaskId, Session.DisplayName(from) + " tried to start " + name, 5f);
        CoopLog.Log.Info("coop: guest " + from + " tried to start the quest " + m.TaskId + " (" + name + ")");
    }

    private static string Apply(ulong from, QuestMsg m)
    {
        TaskController ctl = TaskController.instance;
        if (ctl == null) return QuestWire.Ack(m.ReqId, false, "the host has no task controller yet");
        if (m.Sub == "EDIT")
        {
            TaskInfo task = FindTask(m.TaskId);
            if (task == null) return QuestWire.Ack(m.ReqId, false, "the host does not know that task");
            TaskObjective objective = FindObjective(m.TaskId, m.ObjectiveId);
            if (objective == null) return QuestWire.Ack(m.ReqId, false, "the host does not know that objective");
            TaskStatusInfo held = FindStatus(ctl, m.TaskId);
            if (m.CreateNewTask && held == null) return QuestWire.Ack(m.ReqId, false, OnlyHost);
            if (!m.TaskHasToBeActive && m.TaskStatus == (int)CurrentTaskStatus.Active && (held == null || held.status != CurrentTaskStatus.Active)) return QuestWire.Ack(m.ReqId, false, OnlyHost);
            ObjectiveStatusInfo heldObjective = HeldObjective(held, m.ObjectiveId);
            if (m.ObjectiveStatus == (int)ObjectiveStatus.Active && heldObjective != null && heldObjective.status == ObjectiveStatus.Active) return QuestWire.Ack(m.ReqId, true, "");
            bool ok;
            try
            {
                ok = ctl.EditTask(task, objective, (ObjectiveStatus)m.ObjectiveStatus, m.Optional, m.CreateNewTask, (CurrentTaskStatus)m.TaskStatus, m.ObjectiveHasToExist, m.TaskHasToBeActive);
            }
            catch (Exception e)
            {
                CoopLog.Once("QuestHost.EditTask", e);
                return QuestWire.Ack(m.ReqId, false, "the host could not apply that step");
            }
            CoopLog.Log.Info("coop: applied quest step " + m.TaskId + "/" + m.ObjectiveId + " from " + from + ", ok " + ok);
            return QuestWire.Ack(m.ReqId, ok, ok ? "" : "that step is not open");
        }
        if (m.Sub == "TSTAT")
        {
            TaskInfo task = FindTask(m.TaskId);
            if (task == null) return QuestWire.Ack(m.ReqId, false, "the host does not know that task");
            TaskStatusInfo held = FindStatus(ctl, m.TaskId);
            if (held == null) return QuestWire.Ack(m.ReqId, false, "the host does not hold that task");
            if (m.TaskStatus == (int)CurrentTaskStatus.Active && held.status != CurrentTaskStatus.Active) return QuestWire.Ack(m.ReqId, false, OnlyHost);
            ctl.EditTaskStatus(task, (CurrentTaskStatus)m.TaskStatus);
            CoopLog.Log.Info("coop: applied quest status " + m.TaskId + " to " + m.TaskStatus + " from " + from);
            return QuestWire.Ack(m.ReqId, true, "");
        }
        if (m.Sub == "OBJDONE")
        {
            if (!ctl.ObjectiveActive(m.TaskId, m.ObjectiveId)) return QuestWire.Ack(m.ReqId, false, "that objective is not open");
            ctl.SetObjectiveFinished(m.TaskId, m.ObjectiveId);
            CoopLog.Log.Info("coop: applied objective done " + m.TaskId + "/" + m.ObjectiveId + " from " + from);
            return QuestWire.Ack(m.ReqId, true, "");
        }
        if (m.Sub == "COUNTER")
        {
            TaskInfo task = FindTask(m.TaskId);
            if (task == null) return QuestWire.Ack(m.ReqId, false, "the host does not know that task");
            TaskObjective objective = FindObjective(m.TaskId, m.ObjectiveId);
            if (objective == null) return QuestWire.Ack(m.ReqId, false, "the host does not know that objective");
            TaskStatusInfo held = FindStatus(ctl, m.TaskId);
            if (held == null) return QuestWire.Ack(m.ReqId, false, "the host does not hold that task");
            if (HeldObjective(held, m.ObjectiveId) == null) return QuestWire.Ack(m.ReqId, false, "the host does not hold that objective");
            ctl.EditTaskCounter(task, objective, m.Amount, m.SetAmount);
            CoopLog.Log.Info("coop: applied quest counter " + m.TaskId + "/" + m.ObjectiveId + " amount " + m.Amount + " set " + m.SetAmount + " from " + from);
            return QuestWire.Ack(m.ReqId, true, "");
        }
        return QuestWire.Ack(m.ReqId, false, "the host does not know that request");
    }

    private static string WorldId()
    {
        try
        {
            string id = Session.WorldIdFor(Session.WorldSlot);
            return id == null ? "" : id;
        }
        catch (Exception e)
        {
            CoopLog.Once("QuestHost.WorldId", e);
            return "";
        }
    }

    private static bool Broadcast()
    {
        List<QuestTaskState> state = Snapshot();
        int rev = revBase + revision + 1;
        string world = WorldId();
        string payload = QuestWire.Encode(state, rev, world);
        if (payload == null)
        {
            if (!refusedOnce)
            {
                refusedOnce = true;
                CoopLog.Log.Error("coop: the quest state is too large or holds an id that cannot travel, no quest state is sent");
            }
            return false;
        }
        revision++;
        byte[] data = QuestWire.Bytes(payload);
        bool sent = true;
        int took = 0;
        foreach (ulong peer in Session.Peers)
        {
            if (!owed.Contains(peer)) continue;
            try
            {
                int id = NetBlob.Send(peer, QuestWire.ModId, QuestWire.BlobName, data);
                if (id < 0)
                {
                    sent = false;
                    CoopLog.Once("QuestHost.blob", "the quest state blob was refused for peer " + peer);
                    continue;
                }
                owed.Remove(peer);
                transfers[peer] = id;
                took++;
            }
            catch (Exception e)
            {
                sent = false;
                CoopLog.Once("QuestHost.blobSend", e);
            }
        }
        if (CoopDebug.On("quest")) CoopDebug.Event("quest", "blob sent", "the quest state rev " + rev + " with " + state.Count + " task(s) went to " + took + " peer(s) in world " + (world.Length == 0 ? "?" : world) + ", ok " + sent);
        return sent;
    }

    private static List<QuestTaskState> Snapshot()
    {
        List<QuestTaskState> list = new List<QuestTaskState>();
        TaskController ctl = TaskController.instance;
        if (ctl == null || ctl.allTasks == null) return list;
        foreach (TaskStatusInfo status in ctl.allTasks)
        {
            if (status == null || status.task == null) continue;
            string taskId = status.task.Id;
            if (string.IsNullOrEmpty(taskId) || QuestWire.Unfit(taskId) || QuestWire.Unfit(status.task.addressableAssetPath))
            {
                CoopLog.Once("QuestHost.snapshotTask", "a quest state entry with an id or a path that cannot travel was skipped: " + taskId);
                continue;
            }
            QuestTaskState task = new QuestTaskState();
            task.Id = taskId;
            task.Path = status.task.addressableAssetPath;
            task.Status = (int)status.status;
            task.Renovation = status.renovation;
            if (status.objectives != null)
            {
                foreach (ObjectiveStatusInfo objective in status.objectives)
                {
                    if (objective == null || objective.objective == null) continue;
                    string objectiveId = objective.objective.Id;
                    if (string.IsNullOrEmpty(objectiveId) || QuestWire.Unfit(objectiveId) || QuestWire.Unfit(objective.objective.addressableAssetPath))
                    {
                        CoopLog.Once("QuestHost.snapshotObjective", "a quest objective with an id or a path that cannot travel was skipped: " + objectiveId);
                        continue;
                    }
                    QuestObjectiveState entry = new QuestObjectiveState();
                    entry.Id = objectiveId;
                    entry.Path = objective.objective.addressableAssetPath;
                    entry.Status = (int)objective.status;
                    entry.Optional = objective.optional;
                    entry.CurrentAmount = objective.currentAmount;
                    entry.CounterAmount = objective.counterAmount;
                    task.Objectives.Add(entry);
                }
            }
            list.Add(task);
        }
        return list;
    }

    public static TaskInfo FindTask(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        TaskController ctl = TaskController.instance;
        if (ctl != null && ctl.allTasks != null)
        {
            foreach (TaskStatusInfo status in ctl.allTasks)
            {
                if (status != null && status.task != null && status.task.Id == id) return status.task;
            }
        }
        return Scan(typeof(TaskInfo), id) as TaskInfo;
    }

    public static TaskObjective FindObjective(string taskId, string objectiveId)
    {
        if (string.IsNullOrEmpty(objectiveId)) return null;
        TaskController ctl = TaskController.instance;
        if (ctl != null && ctl.allTasks != null)
        {
            foreach (TaskStatusInfo status in ctl.allTasks)
            {
                if (status == null || status.task == null || status.task.Id != taskId || status.objectives == null) continue;
                foreach (ObjectiveStatusInfo entry in status.objectives)
                {
                    if (entry != null && entry.objective != null && entry.objective.Id == objectiveId) return entry.objective;
                }
            }
        }
        TaskInfo task = FindTask(taskId);
        if (task != null && task.firstObjectives != null)
        {
            foreach (TaskObjectiveReference reference in task.firstObjectives)
            {
                if (reference != null && reference.objective != null && reference.objective.Id == objectiveId) return reference.objective;
            }
        }
        return Scan(typeof(TaskObjective), objectiveId) as TaskObjective;
    }

    private static ScriptableObject Scan(Type type, string id)
    {
        try
        {
            UnityEngine.Object[] found = Resources.FindObjectsOfTypeAll(type);
            for (int i = 0; i < found.Length; i++)
            {
                TaskInfo task = found[i] as TaskInfo;
                if (task != null && task.Id == id) return task;
                TaskObjective objective = found[i] as TaskObjective;
                if (objective != null && objective.Id == id) return objective;
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("QuestHost.Scan", e);
        }
        return null;
    }

    private static string Stored(ulong peer, int reqId)
    {
        List<Answer> list;
        if (!answers.TryGetValue(peer, out list)) return null;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].ReqId == reqId) return list[i].Text;
        }
        return null;
    }

    private static void Remember(ulong peer, int reqId, string text)
    {
        List<Answer> list;
        if (!answers.TryGetValue(peer, out list))
        {
            list = new List<Answer>();
            answers[peer] = list;
        }
        Answer answer = new Answer();
        answer.ReqId = reqId;
        answer.Text = text;
        list.Add(answer);
        while (list.Count > MaxAnswers) list.RemoveAt(0);
        Prune();
    }

    private static void Prune()
    {
        stale.Clear();
        foreach (KeyValuePair<ulong, List<Answer>> pair in answers)
        {
            if (!Session.Peers.Contains(pair.Key)) stale.Add(pair.Key);
        }
        for (int i = 0; i < stale.Count; i++) answers.Remove(stale[i]);
    }

    private static TaskStatusInfo FindStatus(TaskController ctl, string id)
    {
        if (ctl == null || ctl.allTasks == null || string.IsNullOrEmpty(id)) return null;
        for (int i = 0; i < ctl.allTasks.Count; i++)
        {
            TaskStatusInfo status = ctl.allTasks[i];
            if (status != null && status.task != null && status.task.Id == id) return status;
        }
        return null;
    }

    private static ObjectiveStatusInfo HeldObjective(TaskStatusInfo held, string objectiveId)
    {
        if (held == null || held.objectives == null || string.IsNullOrEmpty(objectiveId)) return null;
        for (int i = 0; i < held.objectives.Count; i++)
        {
            ObjectiveStatusInfo objective = held.objectives[i];
            if (objective != null && objective.objective != null && objective.objective.Id == objectiveId) return objective;
        }
        return null;
    }
}
