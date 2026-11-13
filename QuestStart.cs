using System;
using System.Collections.Generic;
using UnityEngine;

internal static class QuestStart
{
    private const float RepeatAfter = 10f;
    private const int MaxTracked = 128;
    private const int MaxName = 60;
    private static readonly Dictionary<string, float> shownAt = new Dictionary<string, float>();
    private static readonly List<string> expired = new List<string>();

    public static bool EditStarts(TaskInfo task, bool createNewTask, CurrentTaskStatus newTaskStatus, bool taskHasToBeActive)
    {
        if (task == null) return false;
        TaskStatusInfo known = Find(task);
        if (known != null) return !taskHasToBeActive && newTaskStatus == CurrentTaskStatus.Active && known.status != CurrentTaskStatus.Active;
        return createNewTask && Meets(task);
    }

    public static bool EditQuiet(TaskInfo task, bool createNewTask)
    {
        if (task == null || !createNewTask) return false;
        return Find(task) == null && !Meets(task);
    }

    public static bool StatusStarts(TaskInfo task, CurrentTaskStatus status)
    {
        if (task == null || status != CurrentTaskStatus.Active) return false;
        TaskStatusInfo known = Find(task);
        return known != null && known.status != CurrentTaskStatus.Active;
    }

    public static bool StatusStarts(TaskStatusInfo task, CurrentTaskStatus status)
    {
        if (task == null || task.task == null || status != CurrentTaskStatus.Active) return false;
        return task.status != CurrentTaskStatus.Active;
    }

    public static void Refuse(string taskId, string title)
    {
        try
        {
            string id = string.IsNullOrEmpty(taskId) ? title : taskId;
            if (string.IsNullOrEmpty(id)) return;
            string name = string.IsNullOrEmpty(title) ? id : title;
            float now = Time.unscaledTime;
            float at;
            if (shownAt.TryGetValue(id, out at) && now < at) return;
            shownAt[id] = now + RepeatAfter;
            Prune(now);
            CoopPopup.Show(id, "Quest", "Only " + Trim(Session.DisplayName(Session.HostId)) + " can start quests. Ask them to start " + Trim(name) + ".");
            QuestGuest.ReportStart(id, name);
            CoopLog.Log.Info("coop: refused a guest quest start on " + id + " (" + name + ")");
        }
        catch (Exception e)
        {
            CoopLog.Once("QuestStart.Refuse", e);
        }
    }

    public static void Reset()
    {
        shownAt.Clear();
    }

    private static TaskStatusInfo Find(TaskInfo task)
    {
        TaskController ctl = TaskController.instance;
        if (ctl == null || ctl.allTasks == null) return null;
        for (int i = 0; i < ctl.allTasks.Count; i++)
        {
            TaskStatusInfo status = ctl.allTasks[i];
            if (status == null || status.task == null) continue;
            if (status.task == task || status.task.Compare(task)) return status;
        }
        return null;
    }

    private static bool Meets(TaskInfo task)
    {
        try
        {
            TaskController ctl = TaskController.instance;
            if (ctl == null || ctl.allTasks == null) return false;
            return task.CheckActivateRequirements(ctl.allTasks);
        }
        catch (Exception e)
        {
            CoopLog.Once("QuestStart.requirements", e);
            return false;
        }
    }

    private static string Trim(string text)
    {
        string plain = text == null ? "" : text.Replace("|", "/");
        return plain.Length > MaxName ? plain.Substring(0, MaxName) : plain;
    }

    private static void Prune(float now)
    {
        if (shownAt.Count <= MaxTracked) return;
        expired.Clear();
        foreach (KeyValuePair<string, float> pair in shownAt)
        {
            if (pair.Value < now) expired.Add(pair.Key);
        }
        for (int i = 0; i < expired.Count; i++) shownAt.Remove(expired[i]);
    }
}
