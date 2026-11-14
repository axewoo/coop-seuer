using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine.AzureSky;

internal static class WorldHooks
{
    private static bool installed;

    public static bool Ready { get; private set; }

    public static bool Timing { get; private set; }

    public static void Install()
    {
        if (installed) return;
        installed = true;
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".world");
        try
        {
            Patch(harmony, typeof(AzureTimeController), "Update", null, null, "ClockPostfix", null);
            Patch(harmony, typeof(WeatherManager), "SetRandomWeather", null, "RandomWeatherPrefix", null, null);
            Patch(harmony, typeof(WeatherManager), "SetWeather", new[] { typeof(int), typeof(bool), typeof(float) }, null, "WeatherSetPostfix", null);
            Patch(harmony, typeof(WeatherManager), "OnSavingGame", null, "WeatherSavePrefix", null, null);
            Patch(harmony, typeof(WeatherManager), "OnLoadingGame", null, null, "WeatherLoadPostfix", null);
            Patch(harmony, typeof(TimeOfDayAzure), "OnSavingGame", null, "ClockSavePrefix", null, null);
            Patch(harmony, typeof(TimeOfDayAzure), "OnLoadingGamePrimary", null, null, "ClockLoadPostfix", null);
            Patch(harmony, typeof(WaitingController), "Sleep", null, "SleepPrefix", null, null);
            Patch(harmony, typeof(WaitingController), "PassOut", null, "PassOutPrefix", null, null);
            Patch(harmony, typeof(WaitingController), "Wait", null, "WaitPrefix", null, null);
            Patch(harmony, typeof(SaveController), "SaveGame", null, "SavePrefix", null, "SaveFinalizer");
            Ready = true;
        }
        catch (Exception e)
        {
            CoopLog.Once("WorldHooks.Install", e);
            harmony.UnpatchSelf();
        }
        InstallTiming();
    }

    private static void InstallTiming()
    {
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".timing");
        try
        {
            Patch(harmony, typeof(WaitingController), "StartSleep", new[] { typeof(float), typeof(bool), typeof(SleepEvent[]), typeof(bool), typeof(float) }, null, "SleepStartPostfix", null);
            Patch(harmony, typeof(WaitingController), "Wakeup", new[] { typeof(bool), typeof(bool) }, "WakeupPrefix", "WakeupPostfix", null);
            Patch(harmony, typeof(TimeOfDayAzure), "SetSpeedFactor", new[] { typeof(float) }, null, "SpeedFactorPostfix", null);
            Patch(harmony, typeof(Sleep), "Use", Type.EmptyTypes, null, "BedUsePostfix", null);
            Patch(harmony, typeof(Sleep), "Wakeup", Type.EmptyTypes, null, "BedWakePostfix", null);
            Patch(harmony, typeof(WaitingUI), "CloseTimeSetUI", Type.EmptyTypes, null, "CloseTimeSetPostfix", null);
            Timing = true;
        }
        catch (Exception e)
        {
            CoopLog.Once("WorldHooks.InstallTiming", e);
            harmony.UnpatchSelf();
        }
    }

    public static void ClockPostfix(AzureTimeController __instance)
    {
        WorldSync.OverrideClock(__instance);
    }

    public static bool RandomWeatherPrefix()
    {
        return !WorldSync.Following;
    }

    public static void WeatherSetPostfix(float transitionTime)
    {
        WorldSync.HostWeatherSet(transitionTime);
    }

    public static bool WeatherSavePrefix(WeatherManager __instance, ref bool __result)
    {
        if (!WorldSync.Saving) return true;
        __instance.savedWeather = WorldSync.OwnWeather;
        __result = SaveController.SerializeData(__instance, true);
        return false;
    }

    public static void WeatherLoadPostfix(WeatherManager __instance, bool __result)
    {
        WorldSync.LoadedWeather(__result ? __instance.ActiveWeatherIndex() : -1);
    }

    public static bool ClockSavePrefix(TimeOfDayAzure __instance, TimeAndDay ___savedTimeAndDay, ref bool __result)
    {
        if (!WorldSync.Saving) return true;
        ___savedTimeAndDay.CopyValues(WorldSync.OwnClock);
        __result = SaveController.SerializeData(__instance, true);
        return false;
    }

    public static void ClockLoadPostfix(TimeAndDay ___savedTimeAndDay)
    {
        WorldSync.LoadedClock(___savedTimeAndDay);
    }

    public static bool SleepPrefix()
    {
        return true;
    }

    public static bool PassOutPrefix()
    {
        return true;
    }

    public static bool WaitPrefix()
    {
        return true;
    }

    public static void SpeedFactorPostfix(float newValue)
    {
        TimeSpeed.OnSpeedSet(newValue);
    }

    public static void SleepStartPostfix()
    {
        BedVote.SleepStarted();
    }

    public static void WakeupPrefix(WaitingController __instance, out bool __state)
    {
        __state = __instance.IsSleeping || __instance.IsWaiting;
    }

    public static void WakeupPostfix(WaitingController __instance, bool __state)
    {
        if (__state && !(__instance.IsSleeping || __instance.IsWaiting)) BedVote.SleepEnded(Session.IsHost);
    }

    public static void BedUsePostfix()
    {
        BedVote.BedUse();
    }

    public static void BedWakePostfix()
    {
        BedVote.TimeSetClosed();
    }

    public static void CloseTimeSetPostfix()
    {
        BedVote.TimeSetClosed();
    }

    public static void SavePrefix(out bool __state)
    {
        __state = WorldSync.BeginSave();
        TimeSpeed.SaveBegin();
        InteractableSync.SaveBegin();
    }

    public static Exception SaveFinalizer(bool __state, Exception __exception)
    {
        if (__state) WorldSync.EndSave();
        TimeSpeed.SaveEnd();
        InteractableSync.SaveEnd();
        return __exception;
    }

    private static void Patch(Harmony harmony, Type type, string name, Type[] args, string prefix, string postfix, string finalizer)
    {
        MethodBase original = AccessTools.Method(type, name, args);
        if (original == null) throw new MissingMethodException(type.Name, name);
        harmony.Patch(original,
            prefix: prefix == null ? null : new HarmonyMethod(typeof(WorldHooks), prefix),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(WorldHooks), postfix),
            finalizer: finalizer == null ? null : new HarmonyMethod(typeof(WorldHooks), finalizer));
    }

    private static bool Refused(string key)
    {
        if (!WorldSync.Following) return false;
        CoopLog.Log.Debug(key, "refused: this guest follows the host clock");
        return true;
    }
}
