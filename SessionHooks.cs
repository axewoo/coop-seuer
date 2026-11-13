using System;
using HarmonyLib;

internal static class SessionHooks
{
    public static void Install()
    {
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".session");
        try
        {
            harmony.Patch(AccessTools.Method(typeof(PauseMenu), "QuitMainMenu"), prefix: new HarmonyMethod(typeof(SessionHooks), "BeforeQuitMainMenu"));
            harmony.Patch(AccessTools.Method(typeof(PauseMenu), "QuitDesktop"), prefix: new HarmonyMethod(typeof(SessionHooks), "BeforeQuitDesktop"));
            harmony.Patch(AccessTools.Method(typeof(Session), "Leave"), prefix: new HarmonyMethod(typeof(SessionHooks), "LeavePrefix"));
        }
        catch (Exception e)
        {
            CoopLog.Once("SessionHooks.Install", e);
            harmony.UnpatchSelf();
        }
    }

    public static void BeforeQuitMainMenu()
    {
        try
        {
            FreeRoam.BeginQuitCollect("reload", 8f);
        }
        catch (Exception e)
        {
            CoopLog.Once("SessionHooks.BeforeQuitMainMenu", e);
        }
    }

    public static void BeforeQuitDesktop()
    {
        try
        {
            FreeRoam.QuitNow();
        }
        catch (Exception e)
        {
            CoopLog.Once("SessionHooks.BeforeQuitDesktop", e);
        }
    }

    public static bool LeavePrefix()
    {
        try
        {
            if (FreeRoam.LeaveBypass) return true;
            return !FreeRoam.LeaveCollect();
        }
        catch (Exception e)
        {
            CoopLog.Once("SessionHooks.LeavePrefix", e);
            return true;
        }
    }
}
