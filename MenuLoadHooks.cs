using System;
using System.Reflection;
using HarmonyLib;

internal static class MenuLoadHooks
{
    private const string Notice = "Loading a save is not shared in co-op yet; return to the main menu and leave any co-op lobby first";
    private static bool installed;

    public static bool Install()
    {
        if (installed) return true;
        installed = true;
        try
        {
            Harmony harmony = new Harmony(Plugin.PluginGuid + ".menuload");
            MethodBase original = AccessTools.Method(typeof(LoadMenu), "LoadGameSlot", Type.EmptyTypes);
            if (original == null) throw new MissingMethodException(typeof(LoadMenu).Name, "LoadGameSlot");
            harmony.Patch(original, prefix: new HarmonyMethod(typeof(MenuLoadHooks), "LoadPrefix"));
            CoopLog.Log.Info("coop: menu load guest guard installed");
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("MenuLoadHooks.Install", e);
            return false;
        }
    }

    private static bool LoadPrefix()
    {
        bool guest;
        try
        {
            guest = Session.IsGuest || GuestSession.InHostWorld;
        }
        catch (Exception e)
        {
            CoopLog.Once("MenuLoadHooks.LoadPrefix", e);
            return true;
        }
        if (!guest) return true;
        try
        {
            GuestGate.Notice(GuestGate.KindUnshared, Notice);
            CoopLog.Log.Info("coop: blocked guest menu save load");
        }
        catch (Exception e)
        {
            CoopLog.Once("MenuLoadHooks.LoadPrefix", e);
        }
        return false;
    }
}
