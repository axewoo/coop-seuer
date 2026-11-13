using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ObenseuerCore;

internal static partial class InteractableSync
{
    private static readonly string[] Phase3 = { "Radio", "TelevisionNoise", "InteractableShower" };

    static InteractableSync()
    {
        try
        {
            Allow = Union(Allow, Phase3);
            Stateful = Union(Stateful, Phase3);
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.phase3", e);
        }
    }

    private static string[] Union(string[] have, string[] add)
    {
        List<string> list = new List<string>(have);
        for (int i = 0; i < add.Length; i++)
        {
            if (!list.Contains(add[i])) list.Add(add[i]);
        }
        return list.ToArray();
    }

    private static bool radioProbed;
    private static FieldInfo radioState;
    private static FieldInfo radioOn;
    private static FieldInfo radioOff;
    private static FieldInfo radioStarted;
    private static MethodInfo radioSetState;
    private static Array noOutputs;

    private static bool ProbeRadio()
    {
        if (radioProbed) return radioSetState != null && radioState != null && radioOn != null && radioOff != null && noOutputs != null;
        radioProbed = true;
        try
        {
            Type type;
            if (!resolved.TryGetValue("Radio", out type) || type == null)
            {
                CoopLog.Once("InteractableSync.radio.type", "coop: the Radio type was not resolved, a radio state that arrives is saved but the look and the sound are not replayed");
                return false;
            }
            radioState = AccessTools.Field(type, "CurrentState");
            radioOn = AccessTools.Field(type, "OnTurnOn");
            radioOff = AccessTools.Field(type, "OnTurnOff");
            radioStarted = AccessTools.Field(type, "startDone");
            radioSetState = AccessTools.Method(type, "SetState", new Type[] { typeof(int) });
            if (radioOn != null && radioOn.FieldType.IsArray)
            {
                Type element = radioOn.FieldType.GetElementType();
                if (element != null) noOutputs = Array.CreateInstance(element, 0);
            }
            if (radioStarted == null) CoopLog.Once("InteractableSync.radio.startDone", "coop: the radio start flag was not found, its state is replayed even before it has started");
            if (radioSetState == null || radioState == null || radioOn == null || radioOff == null || noOutputs == null)
            {
                CoopLog.Once("InteractableSync.radio.fields", "coop: the radio save fields could not be read, a radio turned on by the other machine keeps its old look and sound on this one");
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.radio.probe", e);
            return false;
        }
    }

    internal static void RadioApplied(Interactable target, string key)
    {
        try
        {
            if (target == null || target.GetType().Name != "Radio") return;
            if (!ProbeRadio()) return;
            object started = radioStarted == null ? null : radioStarted.GetValue(target);
            if (started is bool && !(bool)started) return;
            object state = radioState.GetValue(target);
            if (!(state is int)) return;
            object on = radioOn.GetValue(target);
            object off = radioOff.GetValue(target);
            radioOn.SetValue(target, noOutputs);
            radioOff.SetValue(target, noOutputs);
            try
            {
                radioSetState.Invoke(target, new object[] { state });
            }
            finally
            {
                radioOn.SetValue(target, on);
                radioOff.SetValue(target, off);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.radio", e);
        }
    }

    public static void ApplyPostfix(bool __result, Interactable target, string key)
    {
        try
        {
            if (!__result) return;
            RadioApplied(target, key);
            if (!CoopDebug.On("inter")) return;
            CoopDebug.Event("inter", "apply", "applied the state of " + TypeName(target) + " key " + key + " on this machine");
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.ApplyPostfix", e);
        }
    }

    public static void RequestLog(ulong from, string text)
    {
        try
        {
            if (text == null || text.IndexOf("|IREQ|", StringComparison.Ordinal) < 0) return;
            if (!CoopDebug.On("inter")) return;
            CoopDebug.Event("inter", "request", "a guest request arrived from " + from + ": " + text);
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.RequestLog", e);
        }
    }

    public static void GrantLog(object __0)
    {
        try
        {
            if (reentry <= 0) return;
            if (!CoopDebug.On("inter")) return;
            Interactable target = __0 as Interactable;
            CoopDebug.Event("inter", "grant", "ran a guest request on " + TypeName(target) + " key " + Key(target));
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.GrantLog", e);
        }
    }

    public static void RejectLog(ulong from, string key, string why)
    {
        try
        {
            if (!CoopDebug.On("inter")) return;
            CoopDebug.Event("inter", "reject", "refused the request of " + from + " for " + key + ": " + why);
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.RejectLog", e);
        }
    }

    private static string TypeName(Interactable target)
    {
        return target == null ? "none" : target.GetType().Name;
    }

    internal static bool Patched(string typeName)
    {
        try
        {
            if (!installed || typeName == null) return false;
            Type type;
            if (!resolved.TryGetValue(typeName, out type) || type == null) return false;
            MethodInfo interact = AccessTools.Method(type, "Interact", Type.EmptyTypes);
            return interact != null && interact.DeclaringType == type;
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.Patched", e);
            return false;
        }
    }

    public static void SelfCheck()
    {
        try
        {
            Type[] types = AssemblyTypes();
            if (types == null) return;
            int placed = 0;
            for (int i = 0; i < Phase3.Length; i++)
            {
                Type type = Resolve(types, Phase3[i]);
                if (type == null)
                {
                    CoopLog.Log.Info("coop: phase 3a " + Phase3[i] + " is not in this build, its sharing is off");
                    continue;
                }
                MethodInfo interact = AccessTools.Method(type, "Interact", Type.EmptyTypes);
                if (interact == null)
                {
                    CoopLog.Log.Info("coop: phase 3a " + Phase3[i] + " resolved but declares no Interact of its own, its sharing is off");
                    continue;
                }
                placed++;
                CoopLog.Log.Info("coop: phase 3a " + Phase3[i] + " resolved, its Interact is declared by " + interact.DeclaringType.Name);
            }
            CoopLog.Log.Info("coop: phase 3a self check placed " + placed + " of " + Phase3.Length);
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.phase3.selfcheck", e);
        }
    }
}
