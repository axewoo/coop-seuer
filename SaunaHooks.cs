using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

internal static class SaunaHooks
{
    private static bool installed;
    private static bool loggedIgnition;

    public static bool Ready { get; private set; }

    public static bool Install()
    {
        if (installed) return true;
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".sauna");
        try
        {
            Patch(harmony, "TurnOn", Type.EmptyTypes, "TurnOnPrefix", "TurnOnPostfix");
            Patch(harmony, "TurnOff", Type.EmptyTypes, "TurnOffPrefix", "TurnOffPostfix");
            Patch(harmony, "ThrowWater", Type.EmptyTypes, "WaterPrefix", "WaterPostfix");
            installed = true;
        }
        catch (Exception e)
        {
            CoopLog.Once("SaunaHooks.Install", e);
            harmony.UnpatchSelf();
            return false;
        }
        Ready = true;
        return true;
    }

    public static string SelfCheck()
    {
        try
        {
            if (AccessTools.Method(typeof(Sauna), "TurnOn", Type.EmptyTypes) == null) return "Sauna.TurnOn was not found";
            if (AccessTools.Method(typeof(Sauna), "TurnOff", Type.EmptyTypes) == null) return "Sauna.TurnOff was not found";
            if (AccessTools.Method(typeof(Sauna), "ThrowWater", Type.EmptyTypes) == null) return "Sauna.ThrowWater was not found";
            if (typeof(Sauna).GetField("l\u00F6yly", BindingFlags.Instance | BindingFlags.Public) == null) return "the Sauna steam field was not found";
            if (typeof(Sauna).GetField("GUID", BindingFlags.Instance | BindingFlags.Public) == null) return "the Sauna GUID field was not found";
        }
        catch (Exception e)
        {
            return "exception: " + e.Message;
        }
        return "sauna hooks ok";
    }

    public static bool TurnOnPrefix(Sauna __instance)
    {
        try
        {
            if (Shared.Applying) return true;
            LogIgnition(__instance, "TurnOn", true);
            return SaunaGuest.Request(__instance, SaunaWire.OpOn);
        }
        catch (Exception e)
        {
            CoopLog.Once("SaunaHooks.TurnOnPrefix", e);
            return false;
        }
    }

    public static void TurnOnPostfix(Sauna __instance)
    {
        try
        {
            SaunaHost.OnLocalChange(__instance);
        }
        catch (Exception e)
        {
            CoopLog.Once("SaunaHooks.TurnOnPostfix", e);
        }
    }

    public static bool TurnOffPrefix(Sauna __instance)
    {
        try
        {
            if (Shared.Applying) return true;
            LogIgnition(__instance, "TurnOff", false);
            return SaunaGuest.Request(__instance, SaunaWire.OpOff);
        }
        catch (Exception e)
        {
            CoopLog.Once("SaunaHooks.TurnOffPrefix", e);
            return false;
        }
    }

    public static void TurnOffPostfix(Sauna __instance)
    {
        try
        {
            SaunaHost.OnLocalChange(__instance);
        }
        catch (Exception e)
        {
            CoopLog.Once("SaunaHooks.TurnOffPostfix", e);
        }
    }

    public static bool WaterPrefix(Sauna __instance)
    {
        try
        {
            if (Shared.Applying) return true;
            return SaunaGuest.RequestWater(__instance);
        }
        catch (Exception e)
        {
            CoopLog.Once("SaunaHooks.WaterPrefix", e);
            return false;
        }
    }

    public static void WaterPostfix(Sauna __instance)
    {
        try
        {
            if (!Session.IsHost || Shared.Applying) return;
            SaunaHost.OnLocalChange(__instance);
        }
        catch (Exception e)
        {
            CoopLog.Once("SaunaHooks.WaterPostfix", e);
        }
    }

    private static void LogIgnition(Sauna sauna, string method, bool on)
    {
        if (loggedIgnition || sauna == null) return;
        if (Shared.Applying) return;
        loggedIgnition = true;
        CoopLog.Log.Info("coop: the sauna ignition is wired through Sauna." + method + " on " + PathOf(sauna.transform) + ", On=" + on + ", so the prefab seam is confirmed");
    }

    private static string PathOf(Transform step)
    {
        return step.parent == null ? step.name : PathOf(step.parent) + "/" + step.name;
    }

    private static void Patch(Harmony harmony, string name, Type[] args, string prefix, string postfix)
    {
        MethodBase original = AccessTools.Method(typeof(Sauna), name, args);
        if (original == null) throw new MissingMethodException(typeof(Sauna).Name, name);
        HarmonyMethod before = prefix == null ? null : new HarmonyMethod(typeof(SaunaHooks), prefix);
        HarmonyMethod after = postfix == null ? null : new HarmonyMethod(typeof(SaunaHooks), postfix);
        harmony.Patch(original, prefix: before, postfix: after);
    }
}
