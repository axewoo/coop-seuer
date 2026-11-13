using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

internal static class GuestSaveHooks
{
    private const string Blocked = "blocked guest write";
    private static readonly FieldInfo CharacterNameField = typeof(SaveController).GetField("CharacterName", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly MethodInfo CharacterNameMethod = AccessTools.Method(typeof(SaveController), "GetCharacterName", null, null);
    private static readonly Regex Unsafe = new Regex("[^\\w]", RegexOptions.Compiled);
    private static bool installed;

    public static bool Ready { get; private set; }

    public static void Install()
    {
        if (installed) return;
        installed = true;
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".guest");
        try
        {
            Patch(harmony, "GetCharacterName", null, null, "CharacterNamePostfix", null);
            Patch(harmony, "SaveGame", new Type[] { typeof(string), typeof(string), typeof(string) }, "SaveGamePrefix", null, null);
            Patch(harmony, "WriteSaveFile", new Type[] { typeof(string), typeof(string) }, "WriteSaveFilePrefix", null, null);
            Patch(harmony, "ChangeLevel", new Type[] { typeof(string), typeof(string) }, "ChangeLevelPrefix", null, null);
            Ready = true;
            CoopLog.Log.Info("coop: guest save hooks installed");
        }
        catch (Exception e)
        {
            CoopLog.Once("GuestSaveHooks.Install", e);
            harmony.UnpatchSelf();
        }
    }

    private static bool Blocking
    {
        get { return GuestSession.InHostWorld; }
    }

    public static void CharacterNamePostfix()
    {
        if (!Blocking) return;
        if (CharacterNameField == null)
        {
            CoopLog.Log.Error("coop: SaveController.CharacterName is gone, the guest save redirect is off");
            return;
        }
        CharacterNameField.SetValue(null, GuestSession.RelativeName);
    }

    public static void RefreshCharacterName()
    {
        if (CharacterNameMethod == null) return;
        try
        {
            CharacterNameMethod.Invoke(null, null);
        }
        catch (Exception e)
        {
            CoopLog.Once("GuestSaveHooks.RefreshCharacterName", e);
        }
    }

    public static bool SaveGamePrefix(string savename)
    {
        if (!Blocking) return true;
        if (GuestSession.FollowPending)
        {
            CoopLog.Log.Error("coop: " + Blocked + ": the guest may not save while the host's follow ticket is pending");
            HideSavingUI();
            return false;
        }
        string name = string.IsNullOrEmpty(savename) ? GuestSession.AutosaveSlot : Unsafe.Replace(savename, "");
        if (name != GuestSession.AutosaveSlot && name != GuestSession.AutosaveSlot2)
        {
            CoopLog.Log.Error("coop: " + Blocked + ": the guest may not save to the slot '" + name + "'");
            HideSavingUI();
            return false;
        }
        string redirected = RedirectName();
        if (redirected == null)
        {
            HideSavingUI();
            return false;
        }
        string target = Application.persistentDataPath + "/Saves/" + redirected + "/" + name;
        string full;
        if (!GuestWorld.PathOk(target, out full))
        {
            CoopLog.Log.Error("coop: " + Blocked + ": the guest save path " + target + " is not under the coop root");
            HideSavingUI();
            return false;
        }
        return true;
    }

    private static void HideSavingUI()
    {
        SavingUI ui = SavingUI.instance;
        if (ui == null) return;
        try
        {
            ui.HideSavingUI();
        }
        catch (Exception e)
        {
            CoopLog.Once("GuestSaveHooks.HideSavingUI", e);
        }
    }

    public static bool WriteSaveFilePrefix(string path)
    {
        if (!Blocking) return true;
        if (!IsUnderSaves(path)) return true;
        CoopLog.Log.Error("coop: " + Blocked + ": " + path + " is under the single player saves");
        return false;
    }

    private static float deathBypassUntil;
    private static string deathBypassScene;

    public static void ArmDeathBypass(string scene)
    {
        deathBypassScene = scene;
        deathBypassUntil = Time.realtimeSinceStartup + 5f;
    }

    public static bool ChangeLevelPrefix(string newlevel, string newLevelEntrypoint)
    {
        if (!Blocking) return true;
        if (deathBypassUntil > 0f && Time.realtimeSinceStartup <= deathBypassUntil && deathBypassScene != null && string.Equals(newlevel, deathBypassScene, StringComparison.Ordinal))
        {
            deathBypassUntil = 0f;
            deathBypassScene = null;
            CoopLog.Log.Info("death: guest home level change let through");
            return true;
        }
        if (string.IsNullOrEmpty(newlevel)) return true;
        string here = GameRefs.AreaId;
        if (here != null && string.Equals(newlevel, here, StringComparison.Ordinal)) return true;
        ReleaseDoorLock();
        return FreeRoam.GuestDoor(newlevel, newLevelEntrypoint);
    }

    internal static void ReleaseDoorLock()
    {
        Changelevel.changeLevelDelayActive = false;
        InteractObjects objects = InteractObjects.instance;
        GameController controller = GameController.instance;
        if (objects == null || controller == null) return;
        Interactable used = objects.lastUsedInteractable as Interactable;
        Changelevel door = used as Changelevel;
        if (door == null) return;
        controller.ControlsEnabled(door.gameObject);
        OpenSewerCharacterController body = OpenSewerCharacterController.instance;
        if (body != null) body.EnableControls(door.gameObject, false);
        CoopLog.Log.Info("coop: released the controls of door " + door.name + " after the travel refusal");
    }

    private static string RedirectName()
    {
        string relative = GuestSession.RelativeName;
        if (string.IsNullOrEmpty(relative))
        {
            CoopLog.Log.Error("coop: " + Blocked + ": the coop relative name is empty");
            return null;
        }
        if (CharacterNameMethod == null || CharacterNameField == null)
        {
            CoopLog.Log.Error("coop: " + Blocked + ": SaveController.GetCharacterName or SaveController.CharacterName is gone");
            return null;
        }
        try
        {
            CharacterNameMethod.Invoke(null, null);
        }
        catch (Exception e)
        {
            CoopLog.Log.Error("coop: " + Blocked + ": SaveController.GetCharacterName threw: " + e.Message);
            return null;
        }
        string name = CharacterNameField.GetValue(null) as string;
        if (name != relative)
        {
            CoopLog.Log.Error("coop: " + Blocked + ": SaveController.CharacterName is '" + (name == null ? "(null)" : name) + "', not the coop name");
            return null;
        }
        return name;
    }

    private static bool IsUnderSaves(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string full;
        try
        {
            full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return true;
        }
        string saves = Path.GetFullPath(Path.Combine(Application.persistentDataPath, "Saves")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(saves, StringComparison.OrdinalIgnoreCase);
    }

    private static void Patch(Harmony harmony, string name, Type[] args, string prefix, string postfix, string finalizer)
    {
        MethodInfo original = AccessTools.Method(typeof(SaveController), name, args, null);
        if (original == null) throw new MissingMethodException(typeof(SaveController).Name, name);
        HarmonyMethod prefixMethod = prefix == null ? null : new HarmonyMethod(typeof(GuestSaveHooks), prefix);
        HarmonyMethod postfixMethod = postfix == null ? null : new HarmonyMethod(typeof(GuestSaveHooks), postfix);
        HarmonyMethod finalizerMethod = finalizer == null ? null : new HarmonyMethod(typeof(GuestSaveHooks), finalizer);
        if (prefixMethod != null) prefixMethod.priority = (int)Priority.First;
        if (postfixMethod != null) postfixMethod.priority = (int)Priority.Last;
        if (finalizerMethod != null) finalizerMethod.priority = (int)Priority.First;
        harmony.Patch(original, prefix: prefixMethod, postfix: postfixMethod, finalizer: finalizerMethod);
    }
}
