using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

internal static class GuestGateHooks
{
    private struct Target
    {
        public Type type;
        public string name;
        public Type[] args;
        public string prefix;
    }

    private static bool installed;
    private static readonly HashSet<string> hostSeen = new HashSet<string>();

    public static bool Ready { get; private set; }

    private static readonly Target[] Targets = new Target[]
    {
        Entry(typeof(Crime), "GoToPrison", new[] { typeof(double) }, "ArrestPrefix"),
        Entry(typeof(Crime), "TeleportToPrison", Type.EmptyTypes, "ArrestPrefix"),

        Entry(typeof(ShopController), "OpenMenu", new[] { typeof(BaseShop) }, "ShopOpenPrefix"),
        Entry(typeof(ShopBaseUI), "OpenMenu", new[] { typeof(BaseShop) }, "ShopOpenUiPrefix"),
        Entry(typeof(MarketShop), "Interact", Type.EmptyTypes, "ShopPrefix"),
        Entry(typeof(Trade), "Interact", Type.EmptyTypes, "ShopPrefix"),
        Entry(typeof(Trade), "StartTrade", Type.EmptyTypes, "ShopPrefix"),
        Entry(typeof(InteractableTicketMachine), "Interact", Type.EmptyTypes, "ShopPrefix"),
        Entry(typeof(BottleRecycling), "Interact", Type.EmptyTypes, "ShopPrefix"),
        Entry(typeof(InteractablePay), "Interact", Type.EmptyTypes, "ShopPrefix"),

        Entry(typeof(InteractableTalk), "CheckStartDialogueInterrupt", new[] { typeof(string), typeof(bool), typeof(bool) }, "DialogueFunnelPrefix"),
        Entry(typeof(InteractableTalk), "Interact", Type.EmptyTypes, "DialoguePrefix"),
        Entry(typeof(InteractableTalk), "StartDialogue", new[] { typeof(string) }, "DialogueQuietPrefix"),
        Entry(typeof(InteractableTalk), "ForseStartDialogue", new[] { typeof(string) }, "DialogueQuietPrefix"),

        Entry(typeof(Phone), "Interact", Type.EmptyTypes, "PhonePrefix"),
        Entry(typeof(Mailbox), "Interact", Type.EmptyTypes, "MailPrefix"),
        Entry(typeof(Bank), "EnterBank", Type.EmptyTypes, "BankPrefix"),

        Entry(typeof(TaskController), "EditTask", new[] { typeof(TaskInfo), typeof(TaskObjective), typeof(ObjectiveStatus), typeof(bool), typeof(bool), typeof(CurrentTaskStatus), typeof(bool), typeof(bool), typeof(bool) }, "QuestEditPrefix"),
        Entry(typeof(TaskController), "EditTaskStatus", new[] { typeof(TaskInfo), typeof(CurrentTaskStatus) }, "QuestTaskStatusPrefix"),
        Entry(typeof(TaskController), "EditTaskStatus", new[] { typeof(TaskStatusInfo), typeof(CurrentTaskStatus) }, "QuestStatusInfoPrefix"),
        Entry(typeof(TaskController), "SetObjectiveFinished", new[] { typeof(string), typeof(string) }, "QuestObjectiveDonePrefix"),
        Entry(typeof(TaskController), "EditTaskCounter", new[] { typeof(TaskInfo), typeof(TaskObjective), typeof(int), typeof(bool) }, "QuestCounterPrefix"),

        Entry(typeof(WaitingController), "PassOut", new[] { typeof(bool), typeof(WaitingController.Type) }, "PassoutPrefix")
    };

    public static int TargetCount
    {
        get { return Targets.Length; }
    }

    public static void Install()
    {
        if (installed) return;
        installed = true;
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".gate");
        try
        {
            for (int i = 0; i < Targets.Length; i++)
            {
                MethodBase original = AccessTools.Method(Targets[i].type, Targets[i].name, Targets[i].args);
                if (original == null) throw new MissingMethodException(Targets[i].type.Name, Targets[i].name);
                HarmonyMethod prefixMethod = new HarmonyMethod(typeof(GuestGateHooks), Targets[i].prefix);
                prefixMethod.priority = (int)Priority.First;
                harmony.Patch(original, prefix: prefixMethod);
            }
            Ready = true;
            CoopLog.Log.Info("coop: guest gate hooks installed on " + Targets.Length + " targets");
        }
        catch (Exception e)
        {
            CoopLog.Once("GuestGateHooks.Install", e);
            harmony.UnpatchSelf();
        }
    }

    public static string Resolve()
    {
        for (int i = 0; i < Targets.Length; i++)
        {
            if (AccessTools.Method(Targets[i].type, Targets[i].name, Targets[i].args) == null)
            {
                return Targets[i].type.Name + "." + Targets[i].name;
            }
        }
        return "";
    }

    public static void SelfCheck()
    {
        string missing = Resolve();
        if (missing.Length == 0)
        {
            CoopLog.Log.Info("coop: guest gate targets resolved " + Targets.Length + " of " + Targets.Length);
            return;
        }
        CoopLog.Log.Error("coop: guest gate target missing: " + missing);
    }

    private static bool Pass(string kind, bool quiet)
    {
        bool host = false;
        try
        {
            host = !GuestSession.InHostWorld;
            if (host)
            {
                HostPass(kind);
                return true;
            }
            return quiet ? GuestGate.Allowed(kind) : !GuestGate.Block(kind);
        }
        catch (Exception e)
        {
            CoopLog.Once("GuestGateHooks." + kind, e);
            return host;
        }
    }

    private static void HostPass(string kind)
    {
        if (!hostSeen.Add(kind)) return;
        CoopLog.Log.Info("coop: host " + kind + " passed through");
    }

    public static bool ArrestPrefix()
    {
        if (!GuestSession.InHostWorld) HostTravel.SoloTravelNext("arrest");
        return true;
    }

    public static bool ShopPrefix()
    {
        return Pass(GuestGate.KindShop, false);
    }

    public static bool ShopOpenPrefix(BaseShop shop)
    {
        return Ask(shop);
    }

    public static bool ShopOpenUiPrefix(BaseShop baseShop)
    {
        return Ask(baseShop);
    }

    private static bool Ask(BaseShop shop)
    {
        try
        {
            if (!GuestSession.InHostWorld)
            {
                HostPass(GuestGate.KindShop);
                return true;
            }
            if (GuestGate.Frozen(GuestGate.KindShop))
            {
                GuestGate.Notice(GuestGate.KindShop, GuestGate.Text(GuestGate.KindShop));
                return false;
            }
            string key = ShopWire.KeyOf(shop);
            if (key == null)
            {
                GuestGate.Notice(GuestGate.KindShop, "That shop cannot be shared");
                return false;
            }
            if (GuestGate.Granted(key)) return true;
            return ShopGuest.RequestOpen(shop, key);
        }
        catch (Exception e)
        {
            CoopLog.Once("GuestGateHooks.ShopOpen", e);
            return false;
        }
    }

    public static bool DialoguePrefix(InteractableTalk __instance)
    {
        if (!GuestSession.InHostWorld) return Pass(GuestGate.KindDialogue, false);
        try
        {
            return TalkGuest.Start(__instance);
        }
        catch (Exception e)
        {
            CoopLog.Once("GuestGateHooks.TalkStart", e);
            return false;
        }
    }

    public static bool DialogueQuietPrefix(InteractableTalk __instance)
    {
        if (!GuestSession.InHostWorld) return Pass(GuestGate.KindDialogue, true);
        try
        {
            return TalkGuest.Allows(TalkWire.NpcKey(__instance));
        }
        catch (Exception e)
        {
            CoopLog.Once("GuestGateHooks.TalkAllows", e);
            return false;
        }
    }

    public static bool DialogueFunnelPrefix(InteractableTalk __instance, ref bool __result)
    {
        bool host = false;
        try
        {
            host = !GuestSession.InHostWorld;
            if (host)
            {
                HostPass(GuestGate.KindDialogue);
                return true;
            }
            if (TalkGuest.Allows(TalkWire.NpcKey(__instance))) return true;
            __result = false;
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("GuestGateHooks.DialogueFunnelPrefix", e);
            if (host) return true;
            __result = false;
            return false;
        }
    }

    public static bool PhonePrefix()
    {
        return Pass(GuestGate.KindPhone, false);
    }

    public static bool MailPrefix()
    {
        return Pass(GuestGate.KindMail, false);
    }

    public static bool BankPrefix()
    {
        return Pass(GuestGate.KindBank, false);
    }

    public static bool QuestEditPrefix(TaskInfo task, TaskObjective objective, ObjectiveStatus newObjectiveStatus, bool optional, bool createNewTask, CurrentTaskStatus newTaskStatus, bool objectiveHasToExist, bool taskHasToBeActive, bool renovation, ref bool __result)
    {
        bool host = false;
        try
        {
            host = !GuestSession.InHostWorld;
            if (host)
            {
                HostPass(GuestGate.KindQuest);
                return true;
            }
            if (renovation)
            {
                if (GuestGate.Allowed(GuestGate.KindQuest)) return true;
                __result = false;
                return false;
            }
            __result = false;
            if (QuestWaiting()) return false;
            if (QuestGuest.Forwarding)
            {
                if (QuestGuest.HasState && QuestStart.EditStarts(task, createNewTask, newTaskStatus, taskHasToBeActive))
                {
                    QuestStart.Refuse(task == null ? null : task.Id, task == null ? null : task.title);
                    return false;
                }
                if (QuestGuest.HasState && QuestStart.EditQuiet(task, createNewTask)) return false;
                if (QuestGuest.ForwardEdit(task, objective, newObjectiveStatus, optional, createNewTask, newTaskStatus, objectiveHasToExist, taskHasToBeActive)) return false;
            }
            QuestRefused();
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("GuestGateHooks.QuestPrefix", e);
            if (host) return true;
            __result = false;
            return false;
        }
    }

    public static bool QuestTaskStatusPrefix(TaskInfo task, CurrentTaskStatus status)
    {
        bool host = false;
        try
        {
            host = !GuestSession.InHostWorld;
            if (host)
            {
                HostPass(GuestGate.KindQuest);
                return true;
            }
            if (!QuestForwarding()) return true;
            if (QuestWaiting()) return false;
            if (QuestGuest.Forwarding)
            {
                if (QuestGuest.HasState && QuestStart.StatusStarts(task, status))
                {
                    QuestStart.Refuse(task == null ? null : task.Id, task == null ? null : task.title);
                    return false;
                }
                if (task != null && QuestGuest.ForwardStatus(task.Id, status)) return false;
            }
            QuestRefused();
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("GuestGateHooks.QuestTaskStatus", e);
            if (host) return true;
            return false;
        }
    }

    public static bool QuestStatusInfoPrefix(TaskStatusInfo task, CurrentTaskStatus status)
    {
        bool host = false;
        try
        {
            host = !GuestSession.InHostWorld;
            if (host)
            {
                HostPass(GuestGate.KindQuest);
                return true;
            }
            if (!QuestForwarding()) return true;
            if (QuestWaiting()) return false;
            string id = task == null || task.task == null ? null : task.Id;
            if (QuestGuest.Forwarding)
            {
                if (QuestGuest.HasState && QuestStart.StatusStarts(task, status))
                {
                    QuestStart.Refuse(id, task.task.title);
                    return false;
                }
                if (id != null && QuestGuest.ForwardStatus(id, status)) return false;
            }
            QuestRefused();
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("GuestGateHooks.QuestStatusInfo", e);
            if (host) return true;
            return false;
        }
    }

    public static bool QuestObjectiveDonePrefix(string taskId, string objectiveId)
    {
        bool host = false;
        try
        {
            host = !GuestSession.InHostWorld;
            if (host)
            {
                HostPass(GuestGate.KindQuest);
                return true;
            }
            if (!QuestForwarding()) return true;
            if (QuestWaiting()) return false;
            if (QuestGuest.Forwarding && QuestGuest.ForwardObjectiveDone(taskId, objectiveId)) return false;
            QuestRefused();
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("GuestGateHooks.QuestObjectiveDone", e);
            if (host) return true;
            return false;
        }
    }

    public static bool QuestCounterPrefix(TaskInfo task, TaskObjective objective, int amount, bool setamount)
    {
        bool host = false;
        try
        {
            host = !GuestSession.InHostWorld;
            if (host)
            {
                HostPass(GuestGate.KindQuest);
                return true;
            }
            if (!QuestForwarding()) return true;
            if (QuestWaiting()) return false;
            if (QuestGuest.Forwarding && QuestGuest.ForwardCounter(task, objective, amount, setamount)) return false;
            QuestRefused();
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("GuestGateHooks.QuestCounter", e);
            if (host) return true;
            return false;
        }
    }

    private static bool QuestForwarding()
    {
        return Session.IsGuest && GuestSession.InHostWorld;
    }

    private static bool QuestWaiting()
    {
        if (!GuestGate.Frozen(GuestGate.KindQuest)) return false;
        GuestGate.Notice(GuestGate.KindQuest, GuestGate.Text(GuestGate.KindQuest));
        return true;
    }

    private static void QuestRefused()
    {
        GuestGate.Notice(GuestGate.KindQuest, GuestGate.Text(GuestGate.KindQuest));
    }

    public static bool PassoutPrefix()
    {
        if (!GuestSession.InHostWorld) HostTravel.SoloTravelNext("passout");
        return true;
    }

    private static Target Entry(Type type, string name, Type[] args, string prefix)
    {
        Target entry = new Target();
        entry.type = type;
        entry.name = name;
        entry.args = args;
        entry.prefix = prefix;
        return entry;
    }
}
