using System;
using System.Reflection;
using HarmonyLib;
using ObenseuerCore;

internal static class VendHooks
{
    private static bool installed;
    public static bool Ready { get; private set; }

    public static bool Install()
    {
        if (installed) return true;
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".vend");
        try
        {
            Patch(harmony, "Buy", new[] { typeof(VendingMachine.VendingMachineItem) }, "BuyPrefix");
            Patch(harmony, "MinutePassed", new[] { typeof(int) }, "RestockPrefix");
            installed = true;
        }
        catch (Exception e)
        {
            CoopLog.Once("VendHooks.Install", e);
            harmony.UnpatchSelf();
            return false;
        }
        Lobby.MemberLeft += VendHost.OnMemberLeft;
        Ready = true;
        return true;
    }

    public static bool BuyPrefix(VendingMachine __instance, VendingMachine.VendingMachineItem item)
    {
        return !VendGuest.Request(__instance, item);
    }

    public static bool RestockPrefix(VendingMachine __instance)
    {
        return !(Session.IsGuest && GuestSession.InHostWorld);
    }

    public static string SelfCheck()
    {
        try
        {
            if (AccessTools.Method(typeof(VendingMachine), "Buy", new[] { typeof(VendingMachine.VendingMachineItem) }) == null) return "VendingMachine.Buy(VendingMachineItem) was not found";
            if (AccessTools.Method(typeof(VendingMachine), "MinutePassed", new[] { typeof(int) }) == null) return "VendingMachine.MinutePassed(int) was not found";
            if (AccessTools.Method(typeof(SaveController), "GetObjectGUID", new[] { typeof(UnityEngine.Object) }) == null) return "SaveController.GetObjectGUID was not found";
            if (AccessTools.Method(typeof(SaveController), "GetObjectByGUID", new[] { typeof(string), typeof(Type) }) == null) return "SaveController.GetObjectByGUID was not found";
        }
        catch (Exception e)
        {
            return "exception: " + e.Message;
        }
        return "vending hooks ok";
    }

    private static void Patch(Harmony harmony, string name, Type[] args, string prefix)
    {
        MethodBase original = AccessTools.Method(typeof(VendingMachine), name, args);
        if (original == null) throw new MissingMethodException(typeof(VendingMachine).Name, name);
        harmony.Patch(original, prefix: new HarmonyMethod(typeof(VendHooks), prefix));
    }
}
