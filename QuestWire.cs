using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

internal sealed class QuestMsg
{
    public string Kind;
    public string Sub;
    public int ReqId;
    public string TaskId;
    public string ObjectiveId;
    public int ObjectiveStatus = -1;
    public int TaskStatus = -1;
    public bool Optional;
    public bool CreateNewTask;
    public bool ObjectiveHasToExist;
    public bool TaskHasToBeActive;
    public int Amount;
    public bool SetAmount;
    public bool Ok;
    public string Reason;
    public string Title;
}

internal sealed class QuestObjectiveState
{
    public string Id;
    public string Path;
    public int Status;
    public bool Optional;
    public int CurrentAmount;
    public int CounterAmount;
}

internal sealed class QuestTaskState
{
    public string Id;
    public string Path;
    public int Status;
    public bool Renovation;
    public List<QuestObjectiveState> Objectives = new List<QuestObjectiveState>();
}

internal static class QuestWire
{
    public const string Channel = "quest";
    public const string ModId = "obenseuer.coop";
    public const string BlobName = "quest-state";
    public const string BlobHead = "QSTATE";
    public const string KindStartAttempt = "QTRY";
    public const string KindStart = "QSTART";
    public const string HostKey = "-";
    private const int MaxPacket = 900;
    private const int MaxTitle = 60;
    private const int MaxReqId = 999999999;
    private const int MaxText = 160;
    private const int MaxReason = 120;
    private const int MaxTasks = 4096;
    private const int MaxObjectives = 4096;
    private const int MaxBlob = 2097152;
    private const int MaxTaskStatus = 4;
    private const int MaxObjectiveStatus = 6;
    private const int MaxWorld = 64;
    private const int MaxRevision = 2147483646;
    private const int MinAmount = -1000000;
    private const int MaxAmount = 1000000;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly char[] Bad = { '|', '\r', '\n' };

    public static string ReqEdit(int reqId, string taskId, string objectiveId, int objectiveStatus, bool optional, bool createNewTask, int taskStatus, bool objectiveHasToExist, bool taskHasToBeActive)
    {
        return string.Format(Inv, "QREQ|{0}|EDIT|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}", reqId, Clean(taskId), Clean(objectiveId),
            objectiveStatus, Flag(optional), Flag(createNewTask), taskStatus, Flag(objectiveHasToExist), Flag(taskHasToBeActive));
    }

    public static string ReqStatus(int reqId, string taskId, int taskStatus)
    {
        return string.Format(Inv, "QREQ|{0}|TSTAT|{1}|{2}", reqId, Clean(taskId), taskStatus);
    }

    public static string ReqObjectiveDone(int reqId, string taskId, string objectiveId)
    {
        return string.Format(Inv, "QREQ|{0}|OBJDONE|{1}|{2}", reqId, Clean(taskId), Clean(objectiveId));
    }

    public static string ReqCounter(int reqId, string taskId, string objectiveId, int amount, bool setAmount)
    {
        return string.Format(Inv, "QREQ|{0}|COUNTER|{1}|{2}|{3}|{4}", reqId, Clean(taskId), Clean(objectiveId), amount, Flag(setAmount));
    }

    public static string ReqSync(int reqId)
    {
        return string.Format(Inv, "QREQ|{0}|SYNC", reqId);
    }

    public static string Retag(string payload, int reqId)
    {
        if (payload == null) return null;
        int first = payload.IndexOf('|');
        if (first < 0) return null;
        int second = payload.IndexOf('|', first + 1);
        if (second < 0) return null;
        int old;
        if (!int.TryParse(payload.Substring(first + 1, second - first - 1), NumberStyles.Integer, Inv, out old)) return null;
        return payload.Substring(0, first + 1) + reqId.ToString(Inv) + payload.Substring(second);
    }

    public static string StartAttempt(string taskId, string title)
    {
        return string.Format(Inv, "QTRY|{0}|{1}", Clean(taskId), TitleText(title));
    }

    public static string StartNotice(string taskId, string title)
    {
        return string.Format(Inv, "QSTART|{0}|{1}", Clean(taskId), TitleText(title));
    }

    public static string Ack(int reqId, bool ok, string reason)
    {
        return string.Format(Inv, "QACK|{0}|{1}|{2}", reqId, ok ? 1 : 0, Clean(reason, MaxReason));
    }

    public static bool TryParse(string payload, out QuestMsg msg)
    {
        msg = null;
        try
        {
            if (payload == null || Encoding.UTF8.GetByteCount(payload) > MaxPacket) return false;
            string[] f = payload.Split('|');
            QuestMsg m = new QuestMsg();
            m.Kind = f[0];
            if (!Fields(f, m)) return false;
            msg = m;
            return true;
        }
        catch (Exception)
        {
            msg = null;
            return false;
        }
    }

    public static string Encode(List<QuestTaskState> tasks, int rev, string world)
    {
        if (tasks == null) return null;
        if (tasks.Count > MaxTasks) return null;
        if (rev < 0 || rev > MaxRevision) return null;
        StringBuilder text = new StringBuilder();
        text.Append(BlobHead).Append("|R").Append(rev.ToString(Inv)).Append("|W").Append(WorldText(world)).Append('|').Append(tasks.Count.ToString(Inv));
        for (int i = 0; i < tasks.Count; i++)
        {
            QuestTaskState task = tasks[i];
            if (task == null || string.IsNullOrEmpty(task.Id) || Unfit(task.Id) || Unfit(task.Path)) return null;
            int count = task.Objectives == null ? 0 : task.Objectives.Count;
            if (count > MaxObjectives) return null;
            text.Append('|').Append(task.Id).Append('|').Append(Plain(task.Path)).Append('|').Append(task.Status.ToString(Inv));
            text.Append('|').Append(task.Renovation ? "1" : "0").Append('|').Append(count.ToString(Inv));
            for (int j = 0; j < count; j++)
            {
                QuestObjectiveState objective = task.Objectives[j];
                if (objective == null || string.IsNullOrEmpty(objective.Id) || Unfit(objective.Id) || Unfit(objective.Path)) return null;
                text.Append('|').Append(objective.Id).Append('|').Append(Plain(objective.Path)).Append('|').Append(objective.Status.ToString(Inv));
                text.Append('|').Append(objective.Optional ? "1" : "0").Append('|').Append(objective.CurrentAmount.ToString(Inv));
                text.Append('|').Append(objective.CounterAmount.ToString(Inv));
            }
            if (text.Length > MaxBlob) return null;
        }
        return text.ToString();
    }

    public static bool TryDecode(string payload, out List<QuestTaskState> tasks, out int rev, out string world)
    {
        tasks = null;
        rev = 0;
        world = "";
        try
        {
            if (payload == null || payload.Length > MaxBlob) return false;
            string[] f = payload.Split('|');
            if (f.Length < 2 || f[0] != BlobHead) return false;
            int at = 1;
            if (f.Length >= 4 && Mark(f[1], 'R', out rev))
            {
                if (!MarkText(f[2], 'W', out world)) return false;
                at = 3;
            }
            int count;
            if (!Int(f[at++], 0, MaxTasks, out count)) return false;
            List<QuestTaskState> list = new List<QuestTaskState>();
            for (int i = 0; i < count; i++)
            {
                QuestTaskState task = new QuestTaskState();
                if (!Text(f, ref at, out task.Id)) return false;
                if (!Path(f, ref at, out task.Path)) return false;
                if (!Int(f[at++], 0, MaxTaskStatus, out task.Status)) return false;
                if (!Flag(f[at++], out task.Renovation)) return false;
                int objectives;
                if (!Int(f[at++], 0, MaxObjectives, out objectives)) return false;
                for (int j = 0; j < objectives; j++)
                {
                    QuestObjectiveState objective = new QuestObjectiveState();
                    if (!Text(f, ref at, out objective.Id)) return false;
                    if (!Path(f, ref at, out objective.Path)) return false;
                    if (!Int(f[at++], 0, MaxObjectiveStatus, out objective.Status)) return false;
                    if (!Flag(f[at++], out objective.Optional)) return false;
                    if (!Int(f[at++], MinAmount, MaxAmount, out objective.CurrentAmount)) return false;
                    if (!Int(f[at++], 0, MaxAmount, out objective.CounterAmount)) return false;
                    task.Objectives.Add(objective);
                }
                list.Add(task);
            }
            if (at != f.Length) return false;
            tasks = list;
            return true;
        }
        catch (Exception)
        {
            tasks = null;
            return false;
        }
    }

    public static byte[] Bytes(string text)
    {
        return text == null ? null : Encoding.UTF8.GetBytes(text);
    }

    public static string Text(byte[] data)
    {
        if (data == null || data.Length == 0 || data.Length > MaxBlob) return null;
        return Encoding.UTF8.GetString(data);
    }

    public static bool HasBad(string text)
    {
        return text != null && text.IndexOfAny(Bad) >= 0;
    }

    public static bool TooLong(string text)
    {
        return text != null && text.Length > MaxText;
    }

    public static bool Unfit(string text)
    {
        return HasBad(text) || TooLong(text);
    }

    public static string Clean(string text)
    {
        return Clean(text, MaxText);
    }

    private static string Clean(string text, int max)
    {
        string plain = (text ?? "").Replace("|", "").Replace("\r", "").Replace("\n", "");
        return plain.Length > max ? plain.Substring(0, max) : plain;
    }

    private static string TitleText(string title)
    {
        string plain = (title ?? "").Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
        return plain.Length > MaxTitle ? plain.Substring(0, MaxTitle) : plain;
    }

    private static bool Mark(string token, char mark, out int value)
    {
        value = 0;
        if (token == null || token.Length < 2 || token[0] != mark) return false;
        return int.TryParse(token.Substring(1), NumberStyles.Integer, Inv, out value) && value >= 0 && value <= MaxRevision;
    }

    private static bool MarkText(string token, char mark, out string value)
    {
        value = "";
        if (token == null || token.Length < 1 || token[0] != mark) return false;
        string plain = token.Substring(1);
        if (plain.Length > MaxWorld || HasBad(plain)) return false;
        value = plain;
        return true;
    }

    private static string WorldText(string world)
    {
        if (string.IsNullOrEmpty(world)) return "";
        StringBuilder text = new StringBuilder(world.Length);
        for (int i = 0; i < world.Length && text.Length < MaxWorld; i++)
        {
            char c = world[i];
            if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) text.Append(c);
        }
        return text.ToString();
    }

    private static string Plain(string text)
    {
        return text ?? "";
    }

    private static string Flag(bool value)
    {
        return value ? "1" : "0";
    }

    private static bool Fields(string[] f, QuestMsg m)
    {
        switch (m.Kind)
        {
            case "QREQ":
                m.Sub = f.Length > 2 ? f[2] : "";
                return Req(f, m);
            case "QACK":
                return f.Length == 4 && Int(f[1], 1, MaxReqId, out m.ReqId) && Flag(f[2], out m.Ok) && Note(f[3], out m.Reason);
            case "QTRY":
            case "QSTART":
                return f.Length == 3 && Text(f, 1, out m.TaskId) && Title(f[2], out m.Title);
            default:
                return false;
        }
    }

    private static bool Req(string[] f, QuestMsg m)
    {
        if (m.Sub == "EDIT")
        {
            return f.Length == 11 && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f, 3, out m.TaskId) && Text(f, 4, out m.ObjectiveId)
                && Int(f[5], 0, MaxObjectiveStatus, out m.ObjectiveStatus) && Flag(f[6], out m.Optional) && Flag(f[7], out m.CreateNewTask)
                && Int(f[8], 0, MaxTaskStatus, out m.TaskStatus) && Flag(f[9], out m.ObjectiveHasToExist) && Flag(f[10], out m.TaskHasToBeActive);
        }
        if (m.Sub == "TSTAT")
        {
            return f.Length == 5 && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f, 3, out m.TaskId) && Int(f[4], 0, MaxTaskStatus, out m.TaskStatus);
        }
        if (m.Sub == "OBJDONE")
        {
            return f.Length == 5 && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f, 3, out m.TaskId) && Text(f, 4, out m.ObjectiveId);
        }
        if (m.Sub == "COUNTER")
        {
            return f.Length == 7 && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f, 3, out m.TaskId) && Text(f, 4, out m.ObjectiveId)
                && Int(f[5], MinAmount, MaxAmount, out m.Amount) && Flag(f[6], out m.SetAmount);
        }
        if (m.Sub == "SYNC")
        {
            return f.Length == 3 && Int(f[1], 1, MaxReqId, out m.ReqId);
        }
        return false;
    }

    private static bool Int(string text, int min, int max, out int value)
    {
        return int.TryParse(text, NumberStyles.Integer, Inv, out value) && value >= min && value <= max;
    }

    private static bool Flag(string text, out bool value)
    {
        value = text == "1";
        return text == "1" || text == "0";
    }

    private static bool Text(string[] f, int at, out string value)
    {
        value = f[at];
        return !string.IsNullOrEmpty(value) && value.Length <= MaxText && !HasBad(value);
    }

    private static bool Text(string[] f, ref int at, out string value)
    {
        if (at >= f.Length) { value = null; return false; }
        value = f[at++];
        return !string.IsNullOrEmpty(value) && value.Length <= MaxText && !HasBad(value);
    }

    private static bool Path(string[] f, ref int at, out string value)
    {
        if (at >= f.Length) { value = null; return false; }
        value = f[at++];
        return value.Length <= MaxText && !HasBad(value);
    }

    private static bool Title(string text, out string value)
    {
        value = text;
        return !string.IsNullOrEmpty(text) && text.Length <= MaxTitle && !HasBad(text);
    }

    private static bool Note(string text, out string value)
    {
        value = text;
        return text != null && !HasBad(text);
    }
}
