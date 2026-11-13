using System;
using System.Reflection;
using HarmonyLib;
using ObenseuerCore;

internal static class InteractableStateHooks
{
    private const int Expected = 4;
    private static bool installed;

    public static bool Install()
    {
        if (installed) return true;
        installed = true;
        Harmony harmony;
        try
        {
            harmony = new Harmony(Plugin.PluginGuid + ".interactstate");
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableStateHooks.harmony", e);
            return false;
        }
        bool ok = true;
        ok = Patch(harmony, "Apply", new Type[] { typeof(Interactable), typeof(string), typeof(string) }, null, "ApplyPostfix", "apply") && ok;
        ok = Patch(harmony, "Handle", new Type[] { typeof(ulong), typeof(string) }, "RequestLog", null, "request") && ok;
        ok = Patch(harmony, "Reject", new Type[] { typeof(ulong), typeof(string), typeof(string) }, "RejectLog", null, "reject") && ok;
        ok = Patch(harmony, "InteractPostfix", new Type[] { typeof(object), typeof(bool) }, "GrantLog", null, "grant") && ok;
        try
        {
            InteractableSync.SelfCheck();
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableStateHooks.selfcheck", e);
        }
        CoopLog.Log.Info("coop: phase 3a hooks " + (ok ? "installed" : "incomplete") + ", " + Expected + " methods are wanted and the radio replay needs the apply hook");
        return ok;
    }

    private static bool Patch(Harmony harmony, string name, Type[] args, string prefix, string postfix, string site)
    {
        string key = "InteractableStateHooks." + site;
        try
        {
            MethodInfo original = AccessTools.Method(typeof(InteractableSync), name, args);
            if (original == null)
            {
                CoopLog.Once(key, "coop: the method " + name + " of the interactable sync was not found, the " + site + " hook is off");
                return false;
            }
            HarmonyMethod pre = prefix == null ? null : new HarmonyMethod(typeof(InteractableSync), prefix);
            HarmonyMethod post = postfix == null ? null : new HarmonyMethod(typeof(InteractableSync), postfix);
            harmony.Patch(original, prefix: pre, postfix: post);
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once(key, e);
            return false;
        }
    }
}
