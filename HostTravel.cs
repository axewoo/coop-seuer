using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ObenseuerCore;
using UnityEngine;

internal static class HostTravel
{
    private static bool installed;
    public static bool Ready { get; private set; }

    public static void Install()
    {
        if (installed) return;
        installed = true;
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".travel");
        try
        {
            MethodInfo original = AccessTools.Method(typeof(SaveController), "ChangeLevel", new Type[] { typeof(string), typeof(string) }, null);
            if (original == null) throw new MissingMethodException(typeof(SaveController).Name, "ChangeLevel");
            HarmonyMethod prefix = new HarmonyMethod(typeof(HostTravel), "ChangeLevelPrefix");
            prefix.priority = (int)Priority.First;
            harmony.Patch(original, prefix: prefix);
            SaveController.LoadingDone += OnLoadingDone;
            CoopLog.Log.Info("coop: host travel hook installed on SaveController.ChangeLevel");
            Ready = true;
        }
        catch (Exception e)
        {
            CoopLog.Once("HostTravel.Install", e);
            harmony.UnpatchSelf();
        }
    }

    private static float soloUntil;
    private static string soloReason = "";

    public static void SoloTravelNext(string reason)
    {
        soloReason = reason;
        soloUntil = Time.realtimeSinceStartup + 30f;
        if (CoopDebug.On("death")) CoopDebug.Event("death", "solotravel", "the next host travel is the host's own (" + reason + "), armed for 30 s and the stale door probe is ignored in that window");
    }

    public static bool ChangeLevelPrefix(string newlevel, string newLevelEntrypoint)
    {
        try
        {
            if (!Session.IsHost) return true;
            if (string.IsNullOrEmpty(newlevel))
            {
                Solo.NoteTravel();
                return true;
            }
            string here = GameRefs.AreaId;
            if (here == null || string.Equals(here, newlevel, StringComparison.Ordinal))
            {
                soloUntil = 0f;
                Solo.NoteTravel();
                return true;
            }
            bool armed = Time.realtimeSinceStartup < soloUntil;
            bool probe = DoorTravel();
            bool door = armed ? false : probe;
            if (armed) CoopLog.Log.Info("death: host solo travel armed (" + soloReason + ")" + (probe ? ", ignored the stale door probe" : ""));
            if (door && ContainerHost.GuestHoldsAny())
            {
                CoopNotice.Show("hold|container", "A guest is using a container", 5f);
                GuestSaveHooks.ReleaseDoorLock();
                return false;
            }
            bool solo = armed;
            if (solo) soloUntil = 0f;
            bool go = FreeRoam.HostChangeLevel(newlevel, newLevelEntrypoint, solo, door);
            CoopLog.Log.Info("coop: host travel to " + newlevel + " from " + here + (solo ? " (" + soloReason + ")" : "") + (go ? "" : ", held for the guest's live copy"));
            if (go) Solo.NoteTravel();
            return go;
        }
        catch (Exception e)
        {
            Solo.NoteTravel();
            CoopLog.Once("HostTravel.ChangeLevelPrefix", e);
            return true;
        }
    }

    private static bool DoorTravel()
    {
        InteractObjects objects = InteractObjects.instance;
        if (objects == null) return false;
        return objects.lastUsedInteractable is Changelevel;
    }

    public static void GuestLoaded(ulong guest, string guestScene)
    {
        try
        {
            if (!Session.IsHost || guestScene == null || guestScene.Length == 0) return;
            string here = GameRefs.AreaId;
            if (here == null)
            {
                CoopLog.Log.Info("coop: guest " + guest + " loaded " + guestScene + " while the host is not in a world");
                return;
            }
            if (string.Equals(guestScene, here, StringComparison.Ordinal))
            {
                CoopLog.Log.Info("coop: guest " + guest + " loaded the host scene " + here);
                return;
            }
            CoopLog.Log.Info("coop: guest " + guest + " is in " + guestScene + ", the host is in " + here);
        }
        catch (Exception e)
        {
            CoopLog.Once("HostTravel.GuestLoaded", e);
        }
    }

    public static void Tick()
    {
    }

    private static void OnLoadingDone()
    {
        soloUntil = 0f;
    }
}
