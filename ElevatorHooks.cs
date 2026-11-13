using System;
using System.Reflection;
using HarmonyLib;

internal static class ElevatorHooks
{
    private const int Targets = 7;
    private static bool installed;

    public static bool Ready { get; private set; }

    public static bool Install()
    {
        if (installed) return true;
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".elevator");
        try
        {
            Patch(harmony, "FixedUpdate", Type.EmptyTypes, "FixedUpdatePrefix", null);
            Patch(harmony, "Call", new[] { typeof(int) }, "CallPrefix", "DirtyPostfix");
            Patch(harmony, "MoveToOneFloorUp", new[] { typeof(bool) }, "UpPrefix", "DirtyPostfix");
            Patch(harmony, "MoveToOneFloorDown", new[] { typeof(bool) }, "DownPrefix", "DirtyPostfix");
            Patch(harmony, "SetFloorEnabled", new[] { typeof(int) }, "EnablePrefix", "DirtyPostfix");
            Patch(harmony, "SetFloorDisabled", new[] { typeof(int) }, "DisablePrefix", "DirtyPostfix");
            Patch(harmony, "TogglePower", new[] { typeof(bool) }, "PowerPrefix", "DirtyPostfix");
            installed = true;
            Ready = true;
            CoopLog.Log.Info("coop: elevator hooks installed, " + Targets + " targets");
        }
        catch (Exception e)
        {
            CoopLog.Once("ElevatorHooks.Install", e);
            CoopLog.Log.Error("the elevator hooks did not install, elevator sync is off");
            harmony.UnpatchSelf();
            return false;
        }
        return true;
    }

    public static string SelfCheck()
    {
        try
        {
            if (AccessTools.Method(typeof(Elevator), "FixedUpdate", Type.EmptyTypes) == null) return "Elevator.FixedUpdate was not found";
            if (AccessTools.Method(typeof(Elevator), "Call", new[] { typeof(int) }) == null) return "Elevator.Call(int) was not found";
            if (AccessTools.Method(typeof(Elevator), "MoveToOneFloorUp", new[] { typeof(bool) }) == null) return "Elevator.MoveToOneFloorUp(bool) was not found";
            if (AccessTools.Method(typeof(Elevator), "MoveToOneFloorDown", new[] { typeof(bool) }) == null) return "Elevator.MoveToOneFloorDown(bool) was not found";
            if (AccessTools.Method(typeof(Elevator), "SetFloorEnabled", new[] { typeof(int) }) == null) return "Elevator.SetFloorEnabled(int) was not found";
            if (AccessTools.Method(typeof(Elevator), "SetFloorDisabled", new[] { typeof(int) }) == null) return "Elevator.SetFloorDisabled(int) was not found";
            if (AccessTools.Method(typeof(Elevator), "TogglePower", new[] { typeof(bool) }) == null) return "Elevator.TogglePower(bool) was not found";
            if (AccessTools.Method(typeof(Elevator), "SetPosition", new[] { typeof(float) }) == null) return "Elevator.SetPosition(float) was not found";
            if (AccessTools.Method(typeof(Elevator), "CounterWeightMovement", Type.EmptyTypes) == null) return "Elevator.CounterWeightMovement was not found";
            if (AccessTools.Field(typeof(Elevator), "moving") == null) return "Elevator.moving was not found";
            if (AccessTools.Field(typeof(Elevator), "direction") == null) return "Elevator.direction was not found";
            if (AccessTools.Field(typeof(Elevator), "doorsclosed") == null) return "Elevator.doorsclosed was not found";
            if (AccessTools.Field(typeof(Elevator), "Queue") == null) return "Elevator.Queue was not found";
            if (AccessTools.Field(typeof(Elevator), "DistanceBetweenFloors") == null) return "Elevator.DistanceBetweenFloors was not found";
            if (AccessTools.Field(typeof(Elevator), "DirectionBetweenFloors") == null) return "Elevator.DirectionBetweenFloors was not found";
            if (AccessTools.Field(typeof(Elevator.FloorData), "enabled") == null) return "Elevator.FloorData.enabled was not found";
        }
        catch (Exception e)
        {
            return "exception: " + e.Message;
        }
        return "elevator hooks ok";
    }

    public static bool FixedUpdatePrefix(Elevator __instance)
    {
        if (!ElevatorGuest.Following(__instance)) return true;
        ElevatorGuest.FixedStep(__instance);
        return false;
    }

    public static bool CallPrefix(Elevator __instance, int floor)
    {
        if (!ElevatorGuest.Following(__instance)) return true;
        if (floor < 0 || floor >= Floors(__instance)) return false;
        ElevatorGuest.Request(__instance, "call", floor);
        return false;
    }

    public static bool UpPrefix(Elevator __instance, bool dontTriggerOnMove)
    {
        if (!ElevatorGuest.Following(__instance)) return true;
        if (dontTriggerOnMove && ElevatorRefs.Moving(__instance) != 0) return false;
        int target = (int)__instance.Position + 1;
        if (target < 0 || target >= Floors(__instance)) return false;
        ElevatorGuest.Request(__instance, "up", dontTriggerOnMove ? 1 : 0);
        return false;
    }

    public static bool DownPrefix(Elevator __instance, bool dontTriggerOnMove)
    {
        if (!ElevatorGuest.Following(__instance)) return true;
        if (dontTriggerOnMove && ElevatorRefs.Moving(__instance) != 0) return false;
        int target = (int)__instance.Position - 1;
        if (target < 0 || target >= Floors(__instance)) return false;
        ElevatorGuest.Request(__instance, "down", dontTriggerOnMove ? 1 : 0);
        return false;
    }

    public static bool EnablePrefix(Elevator __instance, int floor)
    {
        if (!ElevatorGuest.Following(__instance)) return true;
        if (floor < 0 || floor >= Floors(__instance)) return false;
        ElevatorGuest.Request(__instance, "enable", floor);
        return false;
    }

    public static bool DisablePrefix(Elevator __instance, int floor)
    {
        if (!ElevatorGuest.Following(__instance)) return true;
        if (floor < 0 || floor >= Floors(__instance)) return false;
        ElevatorGuest.Request(__instance, "disable", floor);
        return false;
    }

    public static bool PowerPrefix(Elevator __instance, bool power)
    {
        if (!ElevatorGuest.Following(__instance)) return true;
        ElevatorGuest.Request(__instance, "power", power ? 1 : 0);
        return false;
    }

    public static void DirtyPostfix(Elevator __instance)
    {
        if (__instance == null || Shared.Applying) return;
        ElevatorHost.MarkDirty(__instance);
    }

    private static int Floors(Elevator car)
    {
        return car == null || car.Floors == null ? 0 : car.Floors.Length;
    }

    private static void Patch(Harmony harmony, string name, Type[] args, string prefix, string postfix)
    {
        MethodBase original = AccessTools.Method(typeof(Elevator), name, args);
        if (original == null) throw new MissingMethodException("Elevator", name);
        MethodInfo pre = prefix == null ? null : AccessTools.Method(typeof(ElevatorHooks), prefix);
        MethodInfo post = postfix == null ? null : AccessTools.Method(typeof(ElevatorHooks), postfix);
        if (prefix != null && pre == null) throw new MissingMethodException("ElevatorHooks", prefix);
        if (postfix != null && post == null) throw new MissingMethodException("ElevatorHooks", postfix);
        harmony.Patch(original,
            prefix: pre == null ? null : new HarmonyMethod(pre),
            postfix: post == null ? null : new HarmonyMethod(post));
    }
}
