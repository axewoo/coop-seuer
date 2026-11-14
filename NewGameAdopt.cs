using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using ObenseuerCore;
using UnityEngine;

internal static class NewGameAdopt
{
    private const string AutosaveSlot = "Autosave";
    private const string AutosaveSlot2 = "Autosave2";
    private const float RetrySeconds = 3f;
    private const int MaxTries = 3;
    private static readonly Regex Unsafe = new Regex("[^\\w]", RegexOptions.Compiled);
    private static bool installed;
    private static bool hosting;
    private static int tries;
    private static float nextTry;
    private static float nextFolderCheck;
    private static DateTime hostedAt;

    public static bool Ready { get; private set; }

    public static void Install()
    {
        if (installed) return;
        installed = true;
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".newgame");
        try
        {
            MethodBase original = AccessTools.Method(typeof(SaveController), "SaveGame", new[] { typeof(string), typeof(string), typeof(string) });
            if (original == null) throw new MissingMethodException("SaveController", "SaveGame");
            harmony.Patch(original, postfix: new HarmonyMethod(typeof(NewGameAdopt), "SaveGamePostfix"));
            Ready = true;
        }
        catch (Exception e)
        {
            CoopLog.Once("NewGameAdopt.Install", e);
            harmony.UnpatchSelf();
        }
    }

    public static void SaveGamePostfix(string savename)
    {
        try
        {
            if (!Session.IsHost || !Session.IsInLobby) return;
            if (!string.IsNullOrEmpty(SaveController.LastSaveFolderPath)) return;
            Adopt(SavedFolder(savename, DateTime.MinValue));
        }
        catch (Exception e)
        {
            CoopLog.Once("NewGameAdopt.SaveGamePostfix", e);
        }
    }

    public static void Tick()
    {
        if (!Ready) return;
        if (!Session.IsHost || !Session.IsInLobby)
        {
            hosting = false;
            return;
        }
        if (!hosting)
        {
            hosting = true;
            hostedAt = DateTime.UtcNow;
            tries = 0;
            nextTry = 0f;
            nextFolderCheck = 0f;
        }
        if (!string.IsNullOrEmpty(SaveController.LastSaveFolderPath)) return;
        if (Time.unscaledTime >= nextFolderCheck)
        {
            nextFolderCheck = Time.unscaledTime + RetrySeconds;
            string folder = SavedFolder(null, hostedAt);
            if (folder != null)
            {
                CoopLog.Log.Info("coop: the host has no save folder, the new game's " + folder + " is adopted");
                Adopt(folder);
                return;
            }
        }
        if (tries >= MaxTries || Time.unscaledTime < nextTry) return;
        if (SaveController.instance == null || MainMenu.Instance != null || SaveController.Loading) return;
        if (SaveData.Busy || string.IsNullOrEmpty(GameRefs.AreaId)) return;
        if (Session.LocalSavesDisabled()) return;
        tries++;
        nextTry = Time.unscaledTime + RetrySeconds;
        CoopLog.Log.Info("coop: the host has no save folder yet, the game is asked for an autosave (" + tries + " of " + MaxTries + ")");
        try
        {
            SaveController.Autosave(SaveController.instance);
        }
        catch (Exception e)
        {
            CoopLog.Once("NewGameAdopt.Autosave", e);
        }
    }

    private static void Adopt(string folder)
    {
        if (folder == null || folder == SaveController.LastSaveFolderPath) return;
        SaveController.LastSaveFolderPath = folder;
        CoopLog.Log.Info("coop: the host's save folder is now " + folder);
    }

    private static string SavedFolder(string savename, DateTime notBefore)
    {
        PlayerIdentity identity = PlayerIdentity.identity;
        if (identity == null) return null;
        string character = SaveController.SanitizeCharacterName(identity.firstName, identity.lastName);
        if (string.IsNullOrEmpty(character)) return null;
        string root = Path.Combine(Path.Combine(Application.persistentDataPath, "Saves"), character);
        string named = string.IsNullOrEmpty(savename) ? null : Unsafe.Replace(savename, "");
        string best = null;
        DateTime newest = DateTime.MinValue;
        foreach (string slot in new[] { named, AutosaveSlot, AutosaveSlot2 })
        {
            if (string.IsNullOrEmpty(slot)) continue;
            string folder = Path.Combine(root, slot);
            string globals = Path.Combine(folder, WorldShip.GlobalsFile);
            if (!File.Exists(globals)) continue;
            DateTime written = File.GetLastWriteTimeUtc(globals);
            if (written <= notBefore) continue;
            if (best != null && written <= newest) continue;
            best = folder;
            newest = written;
        }
        return best;
    }
}
