using System;
using System.Reflection;
using HarmonyLib;

internal static class ProfileHooks
{
    private static bool installed;

    public static bool Ready { get; private set; }

    public static void Install()
    {
        if (installed) return;
        installed = true;
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".profile");
        try
        {
            Patch(harmony, "QuitMainMenu", "QuitMainMenuPrefix");
            Patch(harmony, "QuitDesktop", "QuitDesktopPrefix");
            CoopLog.Log.Info("coop: profile hooks installed");
            Ready = true;
        }
        catch (Exception e)
        {
            CoopLog.Once("ProfileHooks.Install", e);
            harmony.UnpatchSelf();
        }
    }

    public static void QuitMainMenuPrefix()
    {
        try
        {
            if (!GuestSession.Loaded) return;
            GuestProfile.OnLeave();
        }
        catch (Exception e)
        {
            CoopLog.Once("ProfileHooks.QuitMainMenuPrefix", e);
        }
    }

    public static void QuitDesktopPrefix()
    {
        try
        {
            if (!GuestSession.Loaded) return;
            GuestProfile.OnQuit();
        }
        catch (Exception e)
        {
            CoopLog.Once("ProfileHooks.QuitDesktopPrefix", e);
        }
    }

    private static void Patch(Harmony harmony, string name, string prefix)
    {
        MethodInfo original = AccessTools.Method(typeof(PauseMenu), name, new Type[0], null);
        if (original == null) throw new MissingMethodException(typeof(PauseMenu).Name, name);
        HarmonyMethod prefixMethod = new HarmonyMethod(typeof(ProfileHooks), prefix);
        prefixMethod.priority = (int)Priority.First;
        harmony.Patch(original, prefix: prefixMethod, postfix: null, transpiler: null);
    }
}
