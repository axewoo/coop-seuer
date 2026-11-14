using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

internal static class TalkHooks
{
    private struct Target
    {
        public Type type;
        public string name;
        public Type[] args;
        public string prefix;
        public string postfix;
    }

    private const int MaxKeys = 256;
    private static readonly Dictionary<int, string> keys = new Dictionary<int, string>();
    private static bool installed;
    private static int patched;
    private static int missing;

    public static bool Ready { get; private set; }

    private static readonly Target[] Targets = new Target[]
    {
        Entry(typeof(InteractableTalk), "OnDialogueStart", new[] { typeof(string), typeof(bool) }, null, "StartPostfix"),
        Entry(typeof(InteractableTalk), "OnDialogueEnded", Type.EmptyTypes, null, "EndPostfix"),

        Entry(typeof(Crime), "PayFine", Type.EmptyTypes, "FinePrefix", null),
        Entry(typeof(Crime), "TryToBribe", Type.EmptyTypes, "BribePrefix", null),
        Entry(typeof(Bank), "CreateAccount", Type.EmptyTypes, "AccountPrefix", null),
        Entry(typeof(CharacterItemManager.GiveItemsToCharacter), "GiveItems", Type.EmptyTypes, "GivePrefix", null),
        Entry(typeof(InteractableItemGiver), "Interact", Type.EmptyTypes, "GiverPrefix", null),
        Entry(typeof(CharacterItemManager), "TakeItemsFromPlayerInventory", Type.EmptyTypes, "TakePrefix", null)
    };

    public static bool Install()
    {
        if (installed) return Ready;
        installed = true;
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".talk");
        for (int i = 0; i < Targets.Length; i++)
        {
            MethodBase original = AccessTools.Method(Targets[i].type, Targets[i].name, Targets[i].args);
            if (original == null)
            {
                missing++;
                CoopLog.Log.Info("coop: talk hook target missing: " + Targets[i].type.Name + "." + Targets[i].name);
                continue;
            }
            try
            {
                HarmonyMethod prefix = Targets[i].prefix == null ? null : new HarmonyMethod(typeof(TalkHooks), Targets[i].prefix);
                HarmonyMethod postfix = Targets[i].postfix == null ? null : new HarmonyMethod(typeof(TalkHooks), Targets[i].postfix);
                harmony.Patch(original, prefix: prefix, postfix: postfix);
                patched++;
            }
            catch (Exception e)
            {
                CoopLog.Once("TalkHooks.Install." + Targets[i].name, e);
            }
        }
        Ready = missing == 0 && patched == Targets.Length;
        CoopLog.Log.Info("coop: talk hooks installed on " + patched + " targets, " + missing + " missing");
        return Ready;
    }

    public static string SelfCheck()
    {
        for (int i = 0; i < Targets.Length; i++)
        {
            if (AccessTools.Method(Targets[i].type, Targets[i].name, Targets[i].args) == null) return Targets[i].type.Name + "." + Targets[i].name + " was not found";
        }
        return "talk hooks ok on " + Targets.Length + " targets";
    }

    public static void StartPostfix(InteractableTalk __instance)
    {
        try
        {
            string npc = TalkWire.NpcKey(__instance);
            if (npc == null) return;
            Remember(__instance, npc);
            if (GuestSession.InHostWorld) TalkGuest.Started(npc);
            else TalkHost.HostStarted(npc);
        }
        catch (Exception e)
        {
            CoopLog.Once("TalkHooks.StartPostfix", e);
        }
    }

    public static void EndPostfix(InteractableTalk __instance)
    {
        try
        {
            string npc = Recall(__instance);
            if (npc == null) npc = TalkWire.NpcKey(__instance);
            if (npc == null) return;
            if (GuestSession.InHostWorld) TalkGuest.Ended(npc);
            else TalkHost.HostEnded(npc);
        }
        catch (Exception e)
        {
            CoopLog.Once("TalkHooks.EndPostfix", e);
        }
    }

    public static bool FinePrefix(ref bool __result)
    {
        return Refuse("Crime.PayFine", ref __result);
    }

    public static bool BribePrefix(ref bool __result)
    {
        return Refuse("Crime.TryToBribe", ref __result);
    }

    public static bool GivePrefix(ref bool __result)
    {
        return Refuse("CharacterItemManager.GiveItems", ref __result);
    }

    public static bool AccountPrefix()
    {
        return !Refused("Bank.CreateAccount");
    }

    public static bool TakePrefix()
    {
        return !Refused("CharacterItemManager.TakeItemsFromPlayerInventory");
    }

    public static bool GiverPrefix()
    {
        return !Refused("InteractableItemGiver.Interact");
    }

    private static bool Refuse(string name, ref bool result)
    {
        if (!Refused(name)) return true;
        result = false;
        return false;
    }

    private static bool Refused(string name)
    {
        if (!GuestSession.InHostWorld) return false;
        CoopLog.Once("TalkHooks.Refuse." + name, "coop: refused " + name + " for the guest, it changes world or profile state");
        if (name.StartsWith("InteractableItemGiver", StringComparison.Ordinal))
        {
            GuestGate.Notice(GuestGate.KindItem, "That is not shared in co-op yet");
            return true;
        }
        GuestGate.Notice(GuestGate.KindDialogue, "Only the host can do that");
        return true;
    }

    private static void Remember(InteractableTalk talk, string key)
    {
        if (talk == null) return;
        if (keys.Count >= MaxKeys) keys.Clear();
        keys[talk.GetInstanceID()] = key;
    }

    private static string Recall(InteractableTalk talk)
    {
        if (talk == null) return null;
        string key;
        if (!keys.TryGetValue(talk.GetInstanceID(), out key)) return null;
        keys.Remove(talk.GetInstanceID());
        return key;
    }

    private static Target Entry(Type type, string name, Type[] args, string prefix, string postfix)
    {
        Target entry = new Target();
        entry.type = type;
        entry.name = name;
        entry.args = args;
        entry.prefix = prefix;
        entry.postfix = postfix;
        return entry;
    }
}
