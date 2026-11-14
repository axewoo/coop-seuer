using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using ObenseuerCore;

// Game 0.5.19 changed SaveController.ReplaceWithSavedata: a loaded entry is now set to
// null in tempSavedata_* (and found through a GUID lookup) instead of being removed.
// The framework's SaveData.RunLoad (verified on 0.5.08) only accepts a load when the
// list ends up empty, so every SaveData.Apply was refused and rolled back on 0.5.19:
// station states, interactable states and anything else applied from a peer.
// This prefix runs the same load and accepts a list whose entries were all consumed.
internal static class SaveDataCompat
{
    private static readonly FieldInfo GlobalList = AccessTools.Field(typeof(SaveController), "tempSavedata_Global");
    private static readonly FieldInfo LevelList = AccessTools.Field(typeof(SaveController), "tempSavedata_Level");
    private static bool installed;

    public static void Install()
    {
        if (installed) return;
        installed = true;
        try
        {
            MethodInfo runLoad = AccessTools.Method(typeof(SaveData), "RunLoad");
            if (runLoad == null || GlobalList == null || LevelList == null)
            {
                CoopLog.Log.Error("coop: SaveData.RunLoad or the game save lists were not found, the 0.5.19 save data fix is off");
                return;
            }
            Harmony harmony = new Harmony(Plugin.PluginGuid + ".savedatacompat");
            harmony.Patch(runLoad, prefix: new HarmonyMethod(typeof(SaveDataCompat), "RunLoadPrefix"));
            CoopLog.Log.Info("coop: save data apply accepts consumed entries (game 0.5.19 load lookup)");
        }
        catch (Exception e)
        {
            CoopLog.Once("SaveDataCompat.Install", e);
        }
    }

    private static bool RunLoadPrefix(SavableScript savable, object header, ref bool __result)
    {
        IList list = (IList)Activator.CreateInstance(LevelList.FieldType);
        list.Add(header);
        object oldGlobal = GlobalList.GetValue(null);
        object oldLevel = LevelList.GetValue(null);
        bool loaded = false;
        try
        {
            GlobalList.SetValue(null, list);
            LevelList.SetValue(null, list);
            loaded = savable.OnLoadingGame();
        }
        finally
        {
            GlobalList.SetValue(null, oldGlobal);
            LevelList.SetValue(null, oldLevel);
        }
        __result = loaded && Consumed(list);
        return false;
    }

    private static bool Consumed(IList list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] != null) return false;
        }
        return true;
    }
}
