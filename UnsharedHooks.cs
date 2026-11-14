using System;
using System.Reflection;
using HarmonyLib;

internal static class UnsharedHooks
{
    private const string NotShared = "That is not shared in co-op yet";
    private static bool installed;
    public static bool Ready { get; private set; }

    public static bool Install()
    {
        if (installed) return true;
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".unshared");
        try
        {
            Patch(harmony, typeof(LiquidStorage), "EnterExternally", new[] { typeof(bool) }, "LiquidPrefix");
            Patch(harmony, typeof(LiquidStorage), "UseStorage", Type.EmptyTypes, "LiquidPrefix");
            Patch(harmony, typeof(LiquidStorage), "LockUse", Type.EmptyTypes, "LiquidPrefix");
            installed = true;
        }
        catch (Exception e)
        {
            CoopLog.Once("UnsharedHooks.Install", e);
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
            if (AccessTools.Method(typeof(LiquidStorage), "EnterExternally", new[] { typeof(bool) }) == null) return "LiquidStorage.EnterExternally(bool) was not found";
            if (AccessTools.Method(typeof(LiquidStorage), "UseStorage", Type.EmptyTypes) == null) return "LiquidStorage.UseStorage was not found";
            if (AccessTools.Method(typeof(LiquidStorage), "LockUse", Type.EmptyTypes) == null) return "LiquidStorage.LockUse was not found";
        }
        catch (Exception e)
        {
            return "exception: " + e.Message;
        }
        return "unshared storage hooks ok";
    }

    public static bool LiquidPrefix(LiquidStorage __instance)
    {
        return LiquidSync.OpenPrefix(__instance);
    }

    private static void Patch(Harmony harmony, Type type, string name, Type[] args, string prefix)
    {
        MethodBase original = AccessTools.Method(type, name, args);
        if (original == null) throw new MissingMethodException(type.Name, name);
        harmony.Patch(original, prefix: new HarmonyMethod(typeof(UnsharedHooks), prefix));
    }
}
