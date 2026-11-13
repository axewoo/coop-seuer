using System;
using System.Reflection;
using HarmonyLib;

internal static class DeveloperConsoleHooks
{
    private const string Notice = "The developer console is not shared in co-op yet";
    private static bool installed;

    public static bool Install()
    {
        if (installed) return true;
        installed = true;
        try
        {
            Harmony harmony = new Harmony(Plugin.PluginGuid + ".console");
            MethodBase original = AccessTools.Method(typeof(DeveloperConsole), "HandleInput", new[] { typeof(string) });
            if (original == null) throw new MissingMethodException(typeof(DeveloperConsole).Name, "HandleInput");
            harmony.Patch(original, prefix: new HarmonyMethod(typeof(DeveloperConsoleHooks), "CommandPrefix"));
            CoopLog.Log.Info("coop: developer console guest guard installed");
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("DeveloperConsoleHooks.Install", e);
            return false;
        }
    }

    private static bool CommandPrefix()
    {
        bool guest;
        try
        {
            guest = Session.IsGuest || GuestSession.InHostWorld;
        }
        catch (Exception e)
        {
            CoopLog.Once("DeveloperConsoleHooks.CommandPrefix", e);
            return true;
        }
        if (!guest) return true;
        try
        {
            GuestGate.Notice(GuestGate.KindUnshared, Notice);
            CoopLog.Log.Info("coop: blocked guest developer console command");
        }
        catch (Exception e)
        {
            CoopLog.Once("DeveloperConsoleHooks.CommandPrefix", e);
        }
        return false;
    }
}
