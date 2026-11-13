using System;
using System.Reflection;
using HarmonyLib;

internal static class SlotHooks
{
    private static bool installed;

    public static bool Ready { get; private set; }

    public static bool Install()
    {
        if (installed) return true;
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".slot");
        try
        {
            Patch(harmony, "Interact", Type.EmptyTypes, "InteractPrefix", null);
            Patch(harmony, "StartRotatingNonDoublingSlots", Type.EmptyTypes, null, "RotatingPostfix");
            Patch(harmony, "HandleRoundEndSituation", new[] { typeof(bool) }, null, "RoundEndPostfix");
            Patch(harmony, "RaiseStakes", Type.EmptyTypes, "RaisePrefix", null);
            Patch(harmony, "DoubleButton", new[] { typeof(bool) }, "DoublePrefix", null);
            Patch(harmony, "StartDoubling", Type.EmptyTypes, "DoublePrefix", null);
            installed = true;
        }
        catch (Exception e)
        {
            CoopLog.Once("SlotHooks.Install", e);
            harmony.UnpatchSelf();
            return false;
        }
        if (!SlotRound.Init()) return false;
        Ready = true;
        return true;
    }

    public static string SelfCheck()
    {
        try
        {
            if (AccessTools.Method(typeof(SlotMachineGameplay), "Interact", Type.EmptyTypes) == null) return "SlotMachineGameplay.Interact was not found";
            if (AccessTools.Method(typeof(SlotMachineGameplay), "StartRotatingNonDoublingSlots", Type.EmptyTypes) == null) return "SlotMachineGameplay.StartRotatingNonDoublingSlots was not found";
            if (AccessTools.Method(typeof(SlotMachineGameplay), "HandleRoundEndSituation", new[] { typeof(bool) }) == null) return "SlotMachineGameplay.HandleRoundEndSituation was not found";
            if (AccessTools.Method(typeof(SlotMachineGameplay), "RaiseStakes", Type.EmptyTypes) == null) return "SlotMachineGameplay.RaiseStakes was not found";
            if (AccessTools.Method(typeof(SlotMachineGameplay), "DoubleButton", new[] { typeof(bool) }) == null) return "SlotMachineGameplay.DoubleButton was not found";
            if (AccessTools.Method(typeof(SlotMachineGameplay), "StartDoubling", Type.EmptyTypes) == null) return "SlotMachineGameplay.StartDoubling was not found";
            string fields = SlotRound.SelfCheck();
            if (fields != "slot machine round fields ok") return fields;
        }
        catch (Exception e)
        {
            return "exception: " + e.Message;
        }
        return "slot machine hooks ok";
    }

    public static bool InteractPrefix(SlotMachineGameplay __instance)
    {
        try
        {
            if (!GuestSession.InHostWorld) return true;
            if (Shared.Applying) return true;
            if (!SlotRound.Ready)
            {
                SlotGuest.RefuseUnshared();
                return false;
            }
            string state = SlotRound.StateOf(__instance);
            if (state == "Standby")
            {
                SlotGuest.RequestSpin(__instance);
                return false;
            }
            if (state == "Doubling") SlotGuest.RefuseDoubling();
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("SlotHooks.InteractPrefix", e);
            return false;
        }
    }

    public static void RotatingPostfix(SlotMachineGameplay __instance)
    {
        try
        {
            if (!SlotRound.Ready) return;
            if (Shared.Applying)
            {
                SlotRound.MarkRunning(__instance);
                if (Session.IsHost) SlotHost.OnRemoteSpinStarted(__instance);
                return;
            }
            if (Session.IsHost) SlotHost.OnOwnSpinStarted(__instance);
        }
        catch (Exception e)
        {
            CoopLog.Once("SlotHooks.RotatingPostfix", e);
        }
    }

    public static void RoundEndPostfix(SlotMachineGameplay __instance)
    {
        try
        {
            if (!SlotRound.Ready || !SlotRound.IsRunning(__instance)) return;
            if (SlotRound.PayerOf(__instance))
            {
                int earnings = SlotRound.EarningsOf(__instance);
                if (earnings > 0 && Money.instance != null)
                {
                    Money.instance.AddMoney(earnings, false);
                    GuestProfile.AfterTransfer();
                }
            }
            SlotRound.Finish(__instance);
        }
        catch (Exception e)
        {
            CoopLog.Once("SlotHooks.RoundEndPostfix", e);
        }
    }

    public static bool RaisePrefix(SlotMachineGameplay __instance)
    {
        try
        {
            return SlotGuest.RaiseStakes(__instance);
        }
        catch (Exception e)
        {
            CoopLog.Once("SlotHooks.RaisePrefix", e);
            return false;
        }
    }

    public static bool DoublePrefix()
    {
        try
        {
            if (!GuestSession.InHostWorld) return true;
            SlotGuest.RefuseDoubling();
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("SlotHooks.DoublePrefix", e);
            return false;
        }
    }

    private static void Patch(Harmony harmony, string name, Type[] args, string prefix, string postfix)
    {
        MethodBase original = AccessTools.Method(typeof(SlotMachineGameplay), name, args);
        if (original == null) throw new MissingMethodException(typeof(SlotMachineGameplay).Name, name);
        HarmonyMethod before = prefix == null ? null : new HarmonyMethod(typeof(SlotHooks), prefix);
        HarmonyMethod after = postfix == null ? null : new HarmonyMethod(typeof(SlotHooks), postfix);
        harmony.Patch(original, prefix: before, postfix: after);
    }
}
