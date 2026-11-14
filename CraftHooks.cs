using System;
using System.Reflection;
using HarmonyLib;
using ObenseuerCore;

internal static class CraftHooks
{
    private static bool installed;
    public static bool Ready { get; private set; }
    private static int autoCraft;

    public static bool Install()
    {
        if (installed) return true;
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".craft");
        try
        {
            Patch(harmony, typeof(manufacturing), "Craft", Type.EmptyTypes, "CraftPrefix", null, null);
            Patch(harmony, typeof(manufacturing), "OnCraftDone", null, null, "DonePostfix", null);
            Patch(harmony, typeof(manufacturingProcess), "StartProcess", Type.EmptyTypes, "StationPrefix", "AuthorityPush", null);
            Patch(harmony, typeof(manufacturingProcess), "OpenLid", Type.EmptyTypes, "StationPrefix", "AuthorityPush", null);
            Patch(harmony, typeof(Growing), "StartGrow", new[] { typeof(int) }, "StationPrefix", "AuthorityPush", null);
            Patch(harmony, typeof(manufacturingProcess), "EndProcessing", null, null, "AuthorityPush", null);
            Patch(harmony, typeof(Growing), "Harvest", new[] { typeof(UnityEngine.GameObject) }, "StationPrefix", "AuthorityPush", null);
            Patch(harmony, typeof(Growing), "Harvest", Type.EmptyTypes, "StationPrefix", "AuthorityPush", null);
            Patch(harmony, typeof(CraftingBase), "Exit", Type.EmptyTypes, null, "AuthorityPush", null);
            Patch(harmony, typeof(manufacturingProcess), "ProcessStarted", null, null, "PrunePostfix", null);
            Patch(harmony, typeof(manufacturing), "CheckItems", new[] { typeof(bool) }, "AutoPrefix", null, "AutoFinally");
            Type skillType = typeof(PlayerStats.PlayerSkillType);
            Patch(harmony, typeof(PlayerStats), "GetSkill", new[] { skillType }, "SkillPrefix", null, null);
            Patch(harmony, typeof(PlayerStats), "GetSkillWithEffects", new[] { skillType }, "SkillEffectsPrefix", null, null);
            Patch(harmony, typeof(PlayerStats), "IncreaseSkill", new[] { skillType, typeof(float), typeof(bool) }, "SkillGainPrefix", null, null);
            Patch(harmony, typeof(CraftingBase), "CreateItems", null, "CreateItemsPrefix", null, "CreateItemsFinally");
            SoftPatch(harmony, typeof(CraftingBase), "Interact", Type.EmptyTypes, "InteractPrefix", "the host station refusal");
            SoftPatch(harmony, typeof(SlotController), "DropItem", new[] { typeof(ItemData), typeof(int).MakeByRefType(), typeof(bool), typeof(bool), typeof(bool) }, "StationSlotPrefix", "the guest station slot guard");
            SoftPatch(harmony, typeof(SlotController), "TakeItem", new[] { typeof(ItemData) }, "StationSlotPrefix", "the guest station slot guard");
            installed = true;
        }
        catch (Exception e)
        {
            CoopLog.Once("CraftHooks.Install", e);
            harmony.UnpatchSelf();
            return false;
        }
        NetBlob.Received += OnBlob;
        Lobby.MemberLeft += CraftHost.OnMemberLeft;
        Ready = true;
        return true;
    }

    public static string SelfCheck()
    {
        try
        {
            if (AccessTools.Method(typeof(manufacturing), "Craft", Type.EmptyTypes) == null) return "manufacturing.Craft was not found";
            if (AccessTools.Method(typeof(manufacturing), "OnCraftDone") == null) return "manufacturing.OnCraftDone was not found";
            if (AccessTools.Method(typeof(manufacturingProcess), "StartProcess", Type.EmptyTypes) == null) return "manufacturingProcess.StartProcess was not found";
            if (AccessTools.Method(typeof(manufacturingProcess), "OpenLid", Type.EmptyTypes) == null) return "manufacturingProcess.OpenLid was not found";
            if (AccessTools.Method(typeof(Growing), "StartGrow", new[] { typeof(int) }) == null) return "Growing.StartGrow(int) was not found";
            if (AccessTools.Method(typeof(manufacturing), "CheckItems", new[] { typeof(bool) }) == null) return "manufacturing.CheckItems(bool) was not found";
            if (AccessTools.Field(typeof(CraftingBase), "AllRecipes") == null) return "CraftingBase.AllRecipes was not found";
            if (AccessTools.Field(typeof(CraftingBase), "selectedRecipe") == null) return "CraftingBase.selectedRecipe was not found";
            if (AccessTools.Method(typeof(CraftingBase), "SetSelectedRecipe", new[] { typeof(Recipe) }) == null) return "CraftingBase.SetSelectedRecipe(Recipe) was not found";
            if (AccessTools.Method(typeof(CraftingBase), "CompareRecipeToItems") == null) return "CraftingBase.CompareRecipeToItems was not found";
            if (AccessTools.Method(typeof(PlayerStats), "GetSkill") == null) return "PlayerStats.GetSkill was not found";
            if (AccessTools.Method(typeof(PlayerStats), "IncreaseSkill") == null) return "PlayerStats.IncreaseSkill was not found";
            if (AccessTools.Method(typeof(CraftingBase), "CreateItems") == null) return "CraftingBase.CreateItems was not found";
            if (AccessTools.Method(typeof(CraftingBase), "Interact", Type.EmptyTypes) == null) return "crafting hooks ok (CraftingBase.Interact is optional and was not found)";
        }
        catch (Exception e)
        {
            return "exception: " + e.Message;
        }
        return "crafting hooks ok";
    }

    public static bool CraftPrefix(manufacturing __instance)
    {
        if (!CraftHost.InHostRun && Session.IsHost && __instance != null && ContainerHost.GuestHolds(__instance.InputOutputStorage)) return false;
        if (!GuestSession.InHostWorld || FreeRoam.IAmAuthority) return true;
        if (CraftHost.InHostRun) return true;
        if (autoCraft > 0) return false;
        return CraftGuest.Request(__instance);
    }

    public static bool StationPrefix(CraftingBase __instance)
    {
        if (!GuestSession.InHostWorld || FreeRoam.IAmAuthority) return true;
        if (autoCraft > 0) return false;
        string key = __instance == null ? null : ContainerWire.KeyOf(__instance.InputOutputStorage);
        if (key != null && ContainerGuest.IsOpen(key)) return true;
        GuestGate.Notice(GuestGate.KindCraftStation, "Open the station first");
        return false;
    }

    public static void PrunePostfix(manufacturingProcess __instance)
    {
        if (__instance != null && Session.IsInLobby) StationState.PruneTimers(__instance);
    }

    public static void AuthorityPush(CraftingBase __instance)
    {
        try
        {
            if (__instance == null || !FreeRoam.IAmAuthority || Session.Peers.Count == 0) return;
            string key = ContainerWire.KeyOf(__instance.InputOutputStorage);
            ulong holder;
            if (key == null || ContainerHost.HeldBy(key, out holder)) return;
            CraftHost.PushAll(__instance);
        }
        catch (Exception e)
        {
            CoopLog.Once("CraftHooks.AuthorityPush", e);
        }
    }

    public static bool InteractPrefix(CraftingBase __instance)
    {
        if (__instance == null || !Session.IsInLobby || !Session.IsHost) return true;
        string key = ContainerWire.KeyOf(__instance.InputOutputStorage);
        ulong holder;
        if (key == null || !ContainerHost.HeldBy(key, out holder)) return true;
        if (holder == 0 || holder == Session.Me) return true;
        CraftHost.Notice("A guest is using that station");
        return false;
    }

    public static bool StationSlotPrefix(SlotController __instance)
    {
        if (Session.IsHost || !GuestSession.InHostWorld || __instance == null) return true;
        Storage pending = ContainerGuest.Pending();
        if (pending == null || !Owns(pending, __instance)) return true;
        GuestGate.Notice(GuestGate.KindCraftStation, "Waiting for the host");
        return false;
    }

    private static bool Owns(Storage storage, SlotController controller)
    {
        SlotController[] slots = storage.Slots;
        if (slots == null) return false;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] == controller) return true;
        }
        return false;
    }

    public static void DonePostfix(manufacturing __instance)
    {
        CraftHost.OnCraftDone(__instance);
    }

    public static void CreateItemsPrefix(CraftingBase __instance, out bool __state)
    {
        __state = false;
        if (__instance == null) return;
        __state = CraftHost.BeginGuestCraft(ContainerWire.KeyOf(__instance.InputOutputStorage));
    }

    public static Exception CreateItemsFinally(bool __state, Exception __exception)
    {
        if (__state) CraftHost.EndGuestCraft();
        return __exception;
    }

    public static bool SkillPrefix(PlayerStats.PlayerSkillType Type, ref int __result)
    {
        if (!CraftHost.InGuestCraft || (int)Type != CraftHost.GuestSkillType) return true;
        __result = CraftHost.GuestSkill;
        return false;
    }

    public static bool SkillEffectsPrefix(PlayerStats.PlayerSkillType Type, ref float __result)
    {
        if (!CraftHost.InGuestCraft || (int)Type != CraftHost.GuestSkillType) return true;
        __result = CraftHost.GuestEffect;
        return false;
    }

    public static bool SkillGainPrefix(PlayerStats.PlayerSkillType Type, float amount)
    {
        if (!CraftHost.InGuestCraft || (int)Type != CraftHost.GuestSkillType) return true;
        CraftHost.NoteGain(amount);
        return false;
    }

    public static void OnBlob(ulong peer, string modId, string name, byte[] data)
    {
        try
        {
            if (modId != CraftWire.ModId || !CraftWire.IsBlobName(name)) return;
            string json = ContainerWire.Text(data);
            if (json == null) return;
            if (Session.IsHost)
            {
                CoopLog.Once("CraftHooks.OnBlob", "coop: a station state blob arrived on the host, which is the side that sends them");
                return;
            }
            CraftGuest.State(peer, name, json);
        }
        catch (Exception e)
        {
            CoopLog.Once("CraftHooks.OnBlob", e);
        }
    }

    public static void AutoPrefix()
    {
        autoCraft++;
    }

    public static void AutoFinally()
    {
        if (autoCraft > 0) autoCraft--;
    }

    private static void Patch(Harmony harmony, Type type, string name, Type[] args, string prefix, string postfix, string finalizer)
    {
        MethodBase original = AccessTools.Method(type, name, args);
        if (original == null) throw new MissingMethodException(type.Name, name);
        harmony.Patch(original,
            prefix: prefix == null ? null : new HarmonyMethod(typeof(CraftHooks), prefix),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(CraftHooks), postfix),
            finalizer: finalizer == null ? null : new HarmonyMethod(typeof(CraftHooks), finalizer));
    }

    private static void SoftPatch(Harmony harmony, Type type, string name, Type[] args, string prefix, string what)
    {
        try
        {
            MethodBase original = AccessTools.Method(type, name, args);
            if (original == null)
            {
                CoopLog.Once("CraftHooks." + prefix, "coop: " + type.Name + "." + name + " was not found, " + what + " is off");
                return;
            }
            harmony.Patch(original, prefix: new HarmonyMethod(typeof(CraftHooks), prefix));
        }
        catch (Exception e)
        {
            CoopLog.Once("CraftHooks.Interact", e);
        }
    }
}
