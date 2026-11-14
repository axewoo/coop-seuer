using System;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using ObenseuerCore;
using UnityEngine;

internal static class TimerScale
{
    public const string Channel = "tcfg";
    private const string Group = "obenseuer.coop.timerscale";
    private const string Mark = "TS|";
    private const float MinFactor = 1f;
    private const float MaxFactor = 1000f;
    private const float Off = 1f;
    private const float Epsilon = 1.0001f;
    private const int MaxScaled = 1 << 28;
    private const float HostInterval = 30f;
    private const float PresenceInterval = 5f;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static ConfigEntry<float> speed;
    private static Harmony harmony;
    private static bool installed;
    private static float factor = Off;
    private static float nextSend;
    private static float nextPresence;
    private static int knownPeers = -1;
    private static float lastSent = -1f;
    private static bool sentOnce;
    private static bool sawTenement;
    private static bool sawCraft;
    private static bool sawFurniture;
    private static bool sawGrow;
    private static int loadFrame = -1;

    public static bool Ready { get { return installed; } }

    public static float Factor { get { return factor; } }

    public static bool Install(ConfigFile config)
    {
        if (installed) return true;
        try
        {
            speed = config.Bind("Co-op", "TimerSpeed", 1f, new ConfigDescription(
                "Scales the timed gameplay processes (tenement and apartment upgrades, station crafting, furniture blueprints and plant growth). The host value is authoritative and is sent to the guests, so the whole lobby runs the same factor. 1 turns the scaling off.",
                new AcceptableValueRange<float>(MinFactor, MaxFactor)));
            if (!Targets()) return false;
            Harmony h = new Harmony(Group);
            harmony = h;
            Patch(h, typeof(TenementController), "UpdateProgesses", new[] { typeof(int) }, "TenementPrefix", null);
            Patch(h, typeof(TenementUpgradeProgress), "GetCurrentTime", new[] { typeof(bool) }, "UpgradeTimePrefix", null);
            Patch(h, typeof(DifficultyInfo), "GetItemCraftingTime", new[] { typeof(float) }, null, "ItemCraftingPostfix");
            Patch(h, typeof(DifficultyInfo), "GetFurnitureCraftingTime", new[] { typeof(float) }, null, "FurniturePostfix");
            Patch(h, typeof(grow), "DeltaSeconds", new[] { typeof(int) }, "GrowPrefix", null);
            Patch(h, typeof(TimeOfDayAzure.Timer), "OnLoading", new[] { typeof(int), typeof(bool) }, null, "TimerLoadPostfix");
            installed = true;
            CoopLog.Log.Info("coop: timer scale ready on 6 targets, the host factor is " + HostFactor().ToString("0.###", Inv));
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("TimerScale.Install", e);
            if (harmony != null)
            {
                harmony.UnpatchSelf();
                harmony = null;
            }
            installed = false;
            return false;
        }
    }

    public static void Tick(float now)
    {
        try
        {
            if (!Session.IsInLobby)
            {
                Reset();
                return;
            }
            if (!Session.IsHost) return;
            float want = HostFactor();
            if (want != factor)
            {
                SetFactor(want, "the host config");
                Send(now);
                return;
            }
            if (now < nextPresence) return;
            nextPresence = now + PresenceInterval;
            int peers = Session.Peers.Count;
            if (peers != knownPeers)
            {
                knownPeers = peers;
                Send(now);
                return;
            }
            if (now < nextSend) return;
            Send(now);
        }
        catch (Exception e)
        {
            CoopLog.Once("TimerScale.Tick", e);
        }
    }

    public static void Handle(ulong from, string text)
    {
        try
        {
            if (!Session.IsInLobby || Session.IsHost) return;
            if (from != Session.HostId) return;
            Apply(text, "the host");
        }
        catch (Exception e)
        {
            CoopLog.Once("TimerScale.Handle", e);
        }
    }

    public static void FromHost(string text)
    {
        try
        {
            if (!Session.IsInLobby || Session.IsHost) return;
            Apply(text, "the host info line");
        }
        catch (Exception e)
        {
            CoopLog.Once("TimerScale.FromHost", e);
        }
    }

    public static string Wire()
    {
        return HostFactor().ToString("F3", Inv);
    }

    public static void Reset()
    {
        if (factor != Off) CoopLog.Log.Info("timer scale: the factor went back to 1");
        factor = Off;
        nextSend = 0f;
        nextPresence = 0f;
        knownPeers = -1;
        lastSent = -1f;
        sentOnce = false;
        sawTenement = false;
        sawCraft = false;
        sawFurniture = false;
        sawGrow = false;
    }

    public static bool TenementPrefix(ref int sec)
    {
        try
        {
            if (factor <= Epsilon || sec <= 0) return true;
            sec = Scale(sec);
            First(ref sawTenement, "timer scale: tenement upgrade tick x");
        }
        catch (Exception e)
        {
            CoopLog.Once("TimerScale.TenementPrefix", e);
        }
        return true;
    }

    public static bool UpgradeTimePrefix(TenementUpgradeProgress __instance, bool simple, ref string __result)
    {
        try
        {
            if (__instance == null || factor <= Epsilon) return true;
            __result = TimeOfDayAzure.GetCurrentTime((float)__instance.TimeSecondsLeft / 3600f / factor, simple);
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("TimerScale.UpgradeTimePrefix", e);
            return true;
        }
    }

    public static void ItemCraftingPostfix(ref float __result)
    {
        try
        {
            if (factor <= Epsilon || __result <= 0f) return;
            if (SaveController.Loading || SaveData.Busy || Time.frameCount == loadFrame) return;
            __result = Mathf.Max(1f, __result / factor);
            First(ref sawCraft, "timer scale: item crafting time x");
        }
        catch (Exception e)
        {
            CoopLog.Once("TimerScale.ItemCraftingPostfix", e);
        }
    }

    public static void FurniturePostfix(ref float __result)
    {
        try
        {
            if (factor <= Epsilon || __result <= 0f) return;
            __result = Mathf.Max(1f, __result / factor);
            First(ref sawFurniture, "timer scale: furniture crafting time x");
        }
        catch (Exception e)
        {
            CoopLog.Once("TimerScale.FurniturePostfix", e);
        }
    }

    public static bool GrowPrefix(ref int sec)
    {
        try
        {
            if (factor <= Epsilon || sec <= 0) return true;
            sec = Scale(sec);
            First(ref sawGrow, "timer scale: plant growth tick x");
        }
        catch (Exception e)
        {
            CoopLog.Once("TimerScale.GrowPrefix", e);
        }
        return true;
    }

    private static int Scale(int sec)
    {
        long value = (long)Math.Round((double)sec * (double)factor, MidpointRounding.AwayFromZero);
        if (value < 1L) return 1;
        if (value > MaxScaled) return MaxScaled;
        return (int)value;
    }

    private static void Apply(string text, string why)
    {
        if (!installed) return;
        if (text == null) return;
        if (text.StartsWith(Mark, StringComparison.Ordinal)) text = text.Substring(Mark.Length);
        float value;
        if (!float.TryParse(text, NumberStyles.Float, Inv, out value)) return;
        if (float.IsNaN(value) || float.IsInfinity(value) || value < MinFactor || value > MaxFactor)
        {
            CoopLog.Once("TimerScale.range", "coop: a timer factor outside 1 to 1000 was sent and was ignored");
            return;
        }
        SetFactor(Quantize(value), why);
    }

    private static void SetFactor(float value, string why)
    {
        if (value == factor) return;
        factor = value;
        sawTenement = false;
        sawCraft = false;
        sawFurniture = false;
        sawGrow = false;
        CoopLog.Log.Info("timer scale: the factor is now x" + FactorText() + " (" + why + ")");
    }

    private static void Send(float now)
    {
        nextSend = now + HostInterval;
        if (!sentOnce || factor != lastSent)
        {
            sentOnce = true;
            lastSent = factor;
            CoopLog.Log.Info("timer scale: host factor x" + FactorText() + " sent to the lobby");
        }
        Session.SendAll(Channel, Mark + factor.ToString("F3", Inv), true);
    }

    private static float HostFactor()
    {
        if (!installed || speed == null) return Off;
        return Quantize(speed.Value);
    }

    private static float Quantize(float value)
    {
        if (float.IsNaN(value) || value < MinFactor) return MinFactor;
        if (value > MaxFactor) return MaxFactor;
        return Mathf.Round(value * 1000f) / 1000f;
    }

    private static void First(ref bool flag, string text)
    {
        if (flag) return;
        flag = true;
        CoopLog.Log.Info(text + FactorText());
    }

    private static string FactorText()
    {
        return factor.ToString("0.###", Inv);
    }

    public static void TimerLoadPostfix(TimeOfDayAzure.Timer __instance)
    {
        try
        {
            if (__instance == null || __instance.Callback == null) return;
            if (!(__instance.Callback.Target is CraftingBase)) return;
            loadFrame = Time.frameCount;
        }
        catch (Exception e)
        {
            CoopLog.Once("TimerScale.TimerLoadPostfix", e);
        }
    }

    private static bool Targets()
    {
        bool ok = true;
        ok &= Has(typeof(TenementController), "UpdateProgesses", new[] { typeof(int) });
        ok &= Has(typeof(TenementUpgradeProgress), "GetCurrentTime", new[] { typeof(bool) });
        ok &= Has(typeof(DifficultyInfo), "GetItemCraftingTime", new[] { typeof(float) });
        ok &= Has(typeof(DifficultyInfo), "GetFurnitureCraftingTime", new[] { typeof(float) });
        ok &= Has(typeof(grow), "DeltaSeconds", new[] { typeof(int) });
        ok &= Has(typeof(TimeOfDayAzure.Timer), "OnLoading", new[] { typeof(int), typeof(bool) });
        if (!ok)
        {
            CoopLog.Log.Error("coop: a timer scale target was not found, every timed process keeps the game speed");
            installed = false;
        }
        return ok;
    }

    private static bool Has(Type type, string name, Type[] args)
    {
        if (AccessTools.Method(type, name, args) != null) return true;
        CoopLog.Once("TimerScale." + type.Name + "." + name, "coop: " + type.Name + "." + name + " was not found, timer scaling is off");
        return false;
    }

    private static void Patch(Harmony harmony, Type type, string name, Type[] args, string prefix, string postfix)
    {
        MethodBase original = AccessTools.Method(type, name, args);
        if (original == null) throw new MissingMethodException(type.Name, name);
        harmony.Patch(original,
            prefix: prefix == null ? null : new HarmonyMethod(typeof(TimerScale), prefix),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(TimerScale), postfix));
    }
}
