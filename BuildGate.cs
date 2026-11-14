using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

internal static class BuildGate
{
    private const float Quiet = 30f;
    private static readonly Dictionary<string, float> seen = new Dictionary<string, float>();
    private static bool installed;

    public static bool Install()
    {
        if (installed) return true;
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".buildgate");
        try
        {
            MethodBase original = AccessTools.Method(typeof(BuildingArea), "Enter", Type.EmptyTypes);
            if (original == null) throw new MissingMethodException(typeof(BuildingArea).Name, "Enter");
            HarmonyMethod postfix = new HarmonyMethod(typeof(BuildGate), "EnterPostfix");
            postfix.priority = Priority.Normal;
            harmony.Patch(original, postfix: postfix);
            installed = true;
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("BuildGate.Install", e);
            harmony.UnpatchSelf();
            return false;
        }
    }

    public static void EnterPostfix(BuildingArea __instance)
    {
        try
        {
            if (!Session.IsInLobby || FreeRoam.IAmAuthority) return;
            FurnitureManager manager = __instance == null ? null : __instance.furnitureManager;
            if (manager == null || string.IsNullOrEmpty(manager.GUID)) return;
            float now = Time.unscaledTime;
            float at;
            if (seen.TryGetValue(manager.GUID, out at) && now < at) return;
            seen[manager.GUID] = now + Quiet;
            bool has = true;
            if (manager.requiredTaskItem != null)
            {
                has = TaskItemsManager.instance != null && TaskItemsManager.instance.CheckIfPlayerHasTaskItem(manager.requiredTaskItem);
            }
            bool inArea = BuildingSystem.instance != null && BuildingSystem.instance.inBuildingArea;
            CoopLog.Log.Info("coop build gate " + manager.GUID + " " + manager.gameObject.name
                + ": BuildMenuDisabled=" + manager.BuildMenuDisabled + " managerDisabled=" + manager.managerDisabled
                + " notFullyExamined=" + manager.notFullyExamined
                + " requiredTaskItem=" + (manager.requiredTaskItem == null ? "none" : manager.requiredTaskItem.name)
                + " hasTaskItem=" + has + " inBuildingArea=" + inArea);
        }
        catch (Exception e)
        {
            CoopLog.Once("BuildGate.Enter", e);
        }
    }
}
