using System;
using System.Reflection;
using HarmonyLib;

internal static class TrainHooks
{
    private static bool installed;

    public static bool Install()
    {
        if (installed) return true;
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".train");
        try
        {
            Patch(harmony, typeof(TrackTrain), "CalculateSpeed", Type.EmptyTypes, "CalculateSpeedPrefix", null);
            Patch(harmony, typeof(Minitrain), "FixedUpdate", Type.EmptyTypes, "MinitrainPrefix", null);
            Patch(harmony, typeof(Minitrain), "ChangePlayerUsingState", new[] { typeof(bool) }, "ChangeUsePrefix", "ChangeUsePostfix");
            Patch(harmony, typeof(Minitrain), "StartMinitrain", Type.EmptyTypes, "StartPrefix", null);
            Patch(harmony, typeof(Minitrain), "StopMinitrain", Type.EmptyTypes, "StopPrefix", null);
            Patch(harmony, typeof(MinitrainSwitch), "ChangeTrack", new[] { typeof(int) }, "SwitchPrefix", "SwitchPostfix");
            Patch(harmony, typeof(MinitrainSwitch), "ChangeTrackForce", new[] { typeof(int) }, null, "SwitchPostfix");
            installed = true;
        }
        catch (Exception e)
        {
            CoopLog.Once("TrainHooks.Install", e);
            harmony.UnpatchSelf();
            return false;
        }
        return true;
    }

    public static string SelfCheck()
    {
        try
        {
            if (AccessTools.Method(typeof(TrackTrain), "CalculateSpeed", Type.EmptyTypes) == null) return "TrackTrain.CalculateSpeed was not found";
            if (AccessTools.Method(typeof(Minitrain), "FixedUpdate", Type.EmptyTypes) == null) return "Minitrain.FixedUpdate was not found";
            if (AccessTools.Method(typeof(Minitrain), "ChangePlayerUsingState", new[] { typeof(bool) }) == null) return "Minitrain.ChangePlayerUsingState(bool) was not found";
            if (AccessTools.Method(typeof(Minitrain), "StartMinitrain", Type.EmptyTypes) == null) return "Minitrain.StartMinitrain was not found";
            if (AccessTools.Method(typeof(Minitrain), "StopMinitrain", Type.EmptyTypes) == null) return "Minitrain.StopMinitrain was not found";
            if (AccessTools.Method(typeof(TrackTrain), "CalculatePositionAndRotation", new[] { typeof(bool) }) == null) return "TrackTrain.CalculatePositionAndRotation(bool) was not found";
            if (AccessTools.Field(typeof(TrackTrain.TrainData), "_drivingLocomotive") == null) return "TrainData._drivingLocomotive was not found";
            if (AccessTools.Method(typeof(Minitrain), "GetNeutralKey", Type.EmptyTypes) == null) return "Minitrain.GetNeutralKey was not found";
            if (AccessTools.Method(typeof(Minitrain), "GetBrakeKey", Type.EmptyTypes) == null) return "Minitrain.GetBrakeKey was not found";
            if (AccessTools.Method(typeof(Minitrain), "ShowSpeed", new[] { typeof(float), typeof(float), typeof(float) }) == null) return "Minitrain.ShowSpeed was not found";
            if (AccessTools.Method(typeof(Minitrain), "ChangeLever", new[] { typeof(float), typeof(float), typeof(float) }) == null) return "Minitrain.ChangeLever was not found";
            if (AccessTools.Field(typeof(MinitrainSwitch), "branchRotations") == null) return "MinitrainSwitch.branchRotations was not found";
            if (AccessTools.Method(typeof(MinitrainSwitch), "ChangeTrack", new[] { typeof(int) }) == null) return "MinitrainSwitch.ChangeTrack(int) was not found";
            if (AccessTools.Method(typeof(MinitrainSwitch), "ChangeTrackForce", new[] { typeof(int) }) == null) return "MinitrainSwitch.ChangeTrackForce(int) was not found";
        }
        catch (Exception e)
        {
            return "exception: " + e.Message;
        }
        return "train hooks ok";
    }

    public static bool CalculateSpeedPrefix(TrackTrain __instance)
    {
        if (__instance == null) return true;
        return !TrainGuest.InSharedScene;
    }

    public static bool MinitrainPrefix()
    {
        return !TrainGuest.InSharedScene;
    }

    public static bool ChangeUsePrefix(Minitrain __instance, bool newstate)
    {
        if (!newstate || __instance == null) return true;
        if (Session.IsHost) return true;
        if (!TrainGuest.InSharedScene) return true;
        if (!TrainGuest.RefuseUse(__instance)) return true;
        CoopNotice.Show("train|busy", "Someone else is driving the train.", 3f);
        return false;
    }

    public static void ChangeUsePostfix(Minitrain __instance, bool newstate)
    {
        if (__instance == null) return;
        if (Session.IsHost)
        {
            if (newstate && TrainHost.Active) TrainHost.HostTook(TrainRefs.TrainGuidOf(__instance));
            return;
        }
        TrainGuest.OnUse(__instance, newstate);
    }

    public static bool StartPrefix(Minitrain __instance)
    {
        if (Shared.Applying || __instance == null) return true;
        if (!TrainGuest.InSharedScene) return true;
        TrainGuest.RequestStart(__instance, true);
        return false;
    }

    public static bool StopPrefix(Minitrain __instance)
    {
        if (Shared.Applying || __instance == null) return true;
        if (!TrainGuest.InSharedScene) return true;
        TrainGuest.RequestStart(__instance, false);
        return false;
    }

    public static bool SwitchPrefix(MinitrainSwitch __instance, int num)
    {
        if (Shared.Applying || __instance == null) return true;
        if (!TrainGuest.InSharedScene) return true;
        TrainGuest.RequestSwitch(__instance, num);
        return false;
    }

    public static void SwitchPostfix(MinitrainSwitch __instance, int num)
    {
        if (__instance == null) return;
        TrainHost.SwitchChanged(__instance, num);
    }
    private static void Patch(Harmony harmony, Type type, string name, Type[] args, string prefix, string postfix)
    {
        MethodBase original = AccessTools.Method(type, name, args);
        if (original == null) throw new MissingMethodException(type.Name, name);
        MethodInfo pre = prefix == null ? null : AccessTools.Method(typeof(TrainHooks), prefix);
        MethodInfo post = postfix == null ? null : AccessTools.Method(typeof(TrainHooks), postfix);
        if (prefix != null && pre == null) throw new MissingMethodException("TrainHooks", prefix);
        if (postfix != null && post == null) throw new MissingMethodException("TrainHooks", postfix);
        harmony.Patch(original,
            prefix: pre == null ? null : new HarmonyMethod(pre),
            postfix: post == null ? null : new HarmonyMethod(post));
    }
}
