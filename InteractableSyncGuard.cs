using System;
using System.Reflection;
using HarmonyLib;
using ObenseuerCore;

internal static partial class InteractableSync
{
    private const string GuestRefusal = "That cannot be used by a guest yet";
    private static Type buttonType;
    private static Type lanternType;
    private static Type relayType;
    private static FieldInfo buttonRequiredItemField;
    private static FieldInfo buttonRequiredTaskField;
    private static FieldInfo buttonAutoUnpressField;
    private static FieldInfo lanternCandleField;
    private static FieldInfo relayItemsField;
    private static FieldInfo relayTaskField;
    private static FieldInfo itemIdField;

    internal static void Guard()
    {
        buttonType = TypeOf("Button");
        lanternType = TypeOf("Lantern");
        relayType = TypeOf("InteractableRelay");
        buttonRequiredItemField = Field(buttonType, "Button", "requiredItem");
        buttonRequiredTaskField = Field(buttonType, "Button", "requiredTaskItem");
        buttonAutoUnpressField = Field(buttonType, "Button", "AutoUnpressTime");
        lanternCandleField = Field(lanternType, "Lantern", "candleItem");
        relayItemsField = Field(relayType, "InteractableRelay", "requiredItems");
        relayTaskField = Field(relayType, "InteractableRelay", "requiredTaskItem");
        Type referenceType = buttonRequiredItemField == null ? null : buttonRequiredItemField.FieldType;
        itemIdField = Field(referenceType, "ItemReference", "ID");
        int missing = 0;
        if (buttonRequiredItemField == null) missing++;
        if (buttonRequiredTaskField == null) missing++;
        if (buttonAutoUnpressField == null) missing++;
        if (lanternCandleField == null) missing++;
        if (relayItemsField == null) missing++;
        if (relayTaskField == null) missing++;
        if (itemIdField == null) missing++;
        CoopLog.Log.Info("coop: interactable guest checks resolved, " + missing + " of 7 fields could not be read");
    }

    private static Type TypeOf(string name)
    {
        Type type;
        return resolved.TryGetValue(name, out type) ? type : null;
    }

    private static FieldInfo Field(Type owner, string ownerName, string name)
    {
        string site = "InteractableSync.field." + ownerName + "." + name;
        if (owner == null)
        {
            CoopLog.Once(site, "coop: the type " + ownerName + " is not there, the check that reads " + name + " is off and such objects are refused for a guest");
            return null;
        }
        try
        {
            FieldInfo field = AccessTools.Field(owner, name);
            if (field == null) CoopLog.Once(site, "coop: " + ownerName + " has no field " + name + ", the check that reads it is off and such objects are refused for a guest");
            return field;
        }
        catch (Exception e)
        {
            CoopLog.Once(site, e);
            return null;
        }
    }

    private static object Read(FieldInfo field, object target)
    {
        return field == null || target == null ? null : field.GetValue(target);
    }

    private static bool IsOf(Type type, string name, Interactable target)
    {
        if (type != null) return type.IsInstanceOfType(target);
        return Of(target, new string[] { name });
    }

    internal static bool Shareable(Interactable target)
    {
        if (target == null) return false;
        if (!Of(target, Allow)) return false;
        try
        {
            return !target.IsInteractDisabled();
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.Shareable", e);
            return false;
        }
    }

    internal static string GuestReason(Interactable target)
    {
        if (target == null) return null;
        try
        {
            if (IsOf(relayType, "InteractableRelay", target))
            {
                if (Read(relayItemsField, target) != null || Read(relayTaskField, target) != null)
                {
                    CoopLog.Once("InteractableSync.relay.guest", "coop: an interactable relay that requires items was refused for a guest");
                }
                return GuestRefusal;
            }
            if (IsOf(lanternType, "Lantern", target))
            {
                if (lanternCandleField == null || Read(lanternCandleField, target) != null) return GuestRefusal;
                return null;
            }
            if (IsOf(buttonType, "Button", target))
            {
                if (buttonRequiredItemField == null) return GuestRefusal;
                object required = Read(buttonRequiredItemField, target);
                if (required != null)
                {
                    int id = IdOf(required);
                    if (id != -1 && id != 0) return GuestRefusal;
                }
                if (buttonRequiredTaskField == null || Read(buttonRequiredTaskField, target) != null) return GuestRefusal;
                return null;
            }
            BreakableLock breakable = target as BreakableLock;
            if (breakable != null && breakable.Locked) return GuestRefusal;
            return null;
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.GuestReason", e);
            return GuestRefusal;
        }
    }

    internal static int IdOf(object reference)
    {
        if (reference == null) return -1;
        if (itemIdField == null) return 1;
        try
        {
            object value = itemIdField.GetValue(reference);
            return value is int ? (int)value : 1;
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.ItemReference.ID", e);
            return 1;
        }
    }

    internal static float AutoUnpressOf(Interactable target)
    {
        if (target == null || buttonType == null || !buttonType.IsInstanceOfType(target)) return 0f;
        if (buttonAutoUnpressField == null) return 0f;
        try
        {
            object value = buttonAutoUnpressField.GetValue(target);
            return value is float ? (float)value : 0f;
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.AutoUnpressTime", e);
            return 0f;
        }
    }
}
