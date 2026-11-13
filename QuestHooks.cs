using System;
using System.Reflection;
using HarmonyLib;
using ObenseuerCore;

internal static class QuestHooks
{
    private static bool installed;
    public static bool Ready { get; private set; }

    private static readonly Type[] EditArgs = new[]
    {
        typeof(TaskInfo), typeof(TaskObjective), typeof(ObjectiveStatus), typeof(bool), typeof(bool), typeof(CurrentTaskStatus), typeof(bool), typeof(bool), typeof(bool)
    };

    public static bool Install()
    {
        if (installed) return true;
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".quest");
        try
        {
            Patch(harmony, "EditTask", EditArgs, "TouchPostfix");
            Patch(harmony, "EditTaskStatus", new[] { typeof(TaskInfo), typeof(CurrentTaskStatus) }, "TouchPostfix");
            Patch(harmony, "EditTaskStatus", new[] { typeof(TaskStatusInfo), typeof(CurrentTaskStatus) }, "TouchPostfix");
            Patch(harmony, "SetObjectiveFinished", new[] { typeof(string), typeof(string) }, "TouchPostfix");
            Patch(harmony, "EditTaskObjective", new[] { typeof(TaskStatusInfo), typeof(ObjectiveStatusInfo), typeof(ObjectiveStatus) }, "TouchPostfix");
            Patch(harmony, "EditTaskCounter", new[] { typeof(TaskInfo), typeof(TaskObjective), typeof(int), typeof(bool) }, "TouchPostfix");
            installed = true;
        }
        catch (Exception e)
        {
            CoopLog.Once("QuestHooks.Install", e);
            harmony.UnpatchSelf();
            return false;
        }
        NetBlob.Received += OnBlob;
        Lobby.MemberLeft += QuestHost.OnMemberLeft;
        Ready = true;
        return true;
    }

    public static void TouchPostfix()
    {
        QuestHost.Touch();
    }

    public static void OnBlob(ulong peer, string modId, string name, byte[] data)
    {
        try
        {
            if (modId != QuestWire.ModId || name != QuestWire.BlobName) return;
            if (Session.IsHost || peer != Session.HostId) return;
            string payload = QuestWire.Text(data);
            if (payload == null) return;
            QuestGuest.OnState(payload);
        }
        catch (Exception e)
        {
            CoopLog.Once("QuestHooks.OnBlob", e);
        }
    }

    public static string SelfCheck()
    {
        try
        {
            if (AccessTools.Method(typeof(TaskController), "EditTask", EditArgs) == null) return "TaskController.EditTask was not found";
            if (AccessTools.Method(typeof(TaskController), "EditTaskStatus", new[] { typeof(TaskInfo), typeof(CurrentTaskStatus) }) == null) return "TaskController.EditTaskStatus(TaskInfo) was not found";
            if (AccessTools.Method(typeof(TaskController), "EditTaskStatus", new[] { typeof(TaskStatusInfo), typeof(CurrentTaskStatus) }) == null) return "TaskController.EditTaskStatus(TaskStatusInfo) was not found";
            if (AccessTools.Method(typeof(TaskController), "SetObjectiveFinished", new[] { typeof(string), typeof(string) }) == null) return "TaskController.SetObjectiveFinished was not found";
            if (AccessTools.Method(typeof(TaskController), "EditTaskObjective", new[] { typeof(TaskStatusInfo), typeof(ObjectiveStatusInfo), typeof(ObjectiveStatus) }) == null) return "TaskController.EditTaskObjective was not found";
            if (AccessTools.Method(typeof(TaskController), "EditTaskCounter", new[] { typeof(TaskInfo), typeof(TaskObjective), typeof(int), typeof(bool) }) == null) return "TaskController.EditTaskCounter was not found";
            if (AccessTools.Method(typeof(NetBlob), "Send", new[] { typeof(ulong), typeof(string), typeof(string), typeof(byte[]) }) == null) return "NetBlob.Send was not found";
        }
        catch (Exception e)
        {
            return "exception: " + e.Message;
        }
        return "quest hooks ok";
    }

    private static void Patch(Harmony harmony, string name, Type[] args, string postfix)
    {
        MethodBase original = AccessTools.Method(typeof(TaskController), name, args);
        if (original == null) throw new MissingMethodException(typeof(TaskController).Name, name);
        harmony.Patch(original, postfix: new HarmonyMethod(typeof(QuestHooks), postfix));
    }
}
