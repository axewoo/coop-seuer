using System;
using System.Reflection;
using HarmonyLib;

internal static class TenementRentHooks
{
    private static bool installed;

    public static bool Install()
    {
        if (installed) return true;
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".tenement");
        try
        {
            Patch(harmony, typeof(TenementController), "UpdateRentPayment", new[] { typeof(int) }, "RentPrefix");
            installed = true;
        }
        catch (Exception e)
        {
            CoopLog.Once("TenementRentHooks.Install", e);
            harmony.UnpatchSelf();
            return false;
        }
        return true;
    }

    public static bool RentPrefix()
    {
        try
        {
            return !(Session.IsGuest || GuestSession.InHostWorld);
        }
        catch (Exception e)
        {
            CoopLog.Once("TenementRentHooks.RentPrefix", e);
            return true;
        }
    }

    private static void Patch(Harmony harmony, Type type, string name, Type[] args, string prefix)
    {
        MethodBase original = AccessTools.Method(type, name, args);
        if (original == null) throw new MissingMethodException(type.Name, name);
        harmony.Patch(original, prefix: new HarmonyMethod(typeof(TenementRentHooks), prefix));
    }
}
