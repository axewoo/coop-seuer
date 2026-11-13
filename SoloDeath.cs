using System;
using ObenseuerCore;
using UnityEngine;

internal static partial class Solo
{
    private const float HomeWait = 10f;
    private const float HomeRetry = 2f;
    private const int HomeWindows = 3;
    private const string HomeEntry = "PlayerTenement_Out";
    private static bool homePending;
    private static float homeUntil;
    private static float homeRetryAt;
    private static int homeTries;
    private static int homeLoads;
    private static int homeWindows;

    public static bool HomePending
    {
        get { return homePending; }
    }

    internal static void DeathHomeLoaded()
    {
        if (homePending) homeLoads++;
    }

    internal static void DeathHomeArm()
    {
        homePending = true;
        homeUntil = Time.realtimeSinceStartup + HomeWait;
        homeRetryAt = Time.realtimeSinceStartup + HomeRetry;
        homeTries = 0;
        homeLoads = 0;
        homeWindows = 0;
        homeArmedAt = Time.realtimeSinceStartup;
        CoopLog.Log.Info("death: home chosen");
    }

    internal static void DeathHomeClear()
    {
        homePending = false;
        homeTries = 0;
        homeWindows = 0;
        homeArmedAt = -1f;
    }

    internal static void DeathHomeTick(float now)
    {
        if (!homePending) return;
        if (MainMenu.Instance != null)
        {
            DeathHomeClear();
            return;
        }
        string here = GameRefs.AreaId;
        bool home = here != null && string.Equals(here, HomeScene, StringComparison.Ordinal);
        if (home && homeLoads > 0)
        {
            DeathHomeClear();
            CoopLog.Log.Info("death: a level change completed (" + (here == null ? "no scene" : here) + "), the home intent is done");
            return;
        }
        if (SaveController.Loading) return;
        if (deadAt > homeArmedAt && Death.instance != null && Death.instance.dead)
        {
            DeathHomeClear();
            CoopLog.Log.Info("death: the player died again inside the home window, the home intent is dropped");
            return;
        }
        if (now >= homeUntil)
        {
            if (Session.IsGuest && !home && homeWindows < HomeWindows)
            {
                homeWindows++;
                homeUntil = now + HomeWait;
                homeRetryAt = now;
                CoopLog.Log.Info("death: guest travel window " + homeWindows + " of " + HomeWindows);
            }
            else
            {
                DeathHomeSafety(here);
                return;
            }
        }
        if (!Session.IsGuest || !GuestSession.Loaded || GuestSession.Following || here == null || home) return;
        if (now < homeRetryAt) return;
        homeRetryAt = now + HomeRetry;
        homeTries++;
        CoopLog.Log.Info("death: guest travel retry " + homeTries);
        FreeRoam.GuestDoor(HomeScene, HomeEntry);
    }

    private static void ReleaseHeld()
    {
        TryRelease("ContainerGuest", ContainerGuest.OnSceneKept);
        TryRelease("ShopGuest", ShopGuest.OnSceneChanged);
        TryRelease("CraftGuest", CraftGuest.OnSceneChanged);
        TryRelease("TalkGuest", TalkGuest.OnSceneChanged);
        TryRelease("BedVote", BedVote.OnSceneChanged);
        TryRelease("SaunaGuest", SaunaGuest.OnSceneChanged);
        TryRelease("SlotGuest", SlotGuest.OnSceneChanged);
        if (Session.IsGuest) TryRelease("TimeSpeed", TimeSpeed.OnSceneChanged);
    }

    private static void TryRelease(string site, Action release)
    {
        try
        {
            release();
        }
        catch (Exception e)
        {
            CoopLog.Once("Solo.ReleaseHeld " + site, e);
        }
    }

    private static void DeathHomeSafety(string here)
    {
        DeathHomeClear();
        CoopLog.Log.Info("death: safety net fired (" + (here == null ? "no scene" : here) + ", no level change in " + (int)(HomeWait * (HomeWindows + 1)) + " s)");
        try
        {
            Revive();
            FreeRoam.GiveUpGoto();
            ReleaseHeld();
            if (GameController.instance != null && Death.instance != null) GameController.instance.ControlsEnabled(Death.instance.gameObject);
            if (PauseMenu.instance != null) PauseMenu.instance.Resume();
            Time.timeScale = 1f;
            if (GameController.instance != null)
            {
                GameController.instance.GameIsPaused = false;
                GameController.instance.ChangeCursorStatus(CursorLockMode.Locked, false);
            }
            CoopUi.DeathOpen = false;
            if (here != null && string.Equals(here, HomeScene, StringComparison.Ordinal))
            {
                TeleportHome(here);
            }
            else if (Session.IsGuest)
            {
                CoopLog.Log.Info("death: the guest never travelled home, it is revived where it stands");
                CoopNotice.Show("death|home", "Could not travel home, you were revived where you stand", 6f);
            }
            else
            {
                HostTravel.SoloTravelNext("respawn");
                SaveController.ChangeLevel(HomeScene, HomeEntry);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("Solo.DeathHomeSafety", e);
        }
    }

    private static void TeleportHome(string here)
    {
        if (PlayerLocator.instance == null) return;
        PlayerLevelEntrypoints points = PlayerLevelEntrypoints.instance;
        if (points == null || points.Entrypoints == null) return;
        PlayerLevelEntrypoints.Entrypoint entry = points.GetEntrypoint(HomeEntry);
        if (entry == null) entry = FirstEntry(points);
        if (entry == null || entry.Location == null) return;
        Teleport.TeleportPlayerToTargetStatic(entry.Location);
        CoopLog.Log.Info("death: revived in " + here + " at the home entrypoint " + entry.Name);
    }

    private static PlayerLevelEntrypoints.Entrypoint FirstEntry(PlayerLevelEntrypoints points)
    {
        for (int i = 0; i < points.Entrypoints.Count; i++)
        {
            PlayerLevelEntrypoints.Entrypoint entry = points.Entrypoints[i];
            if (entry != null && !entry.dontUseAsDefaultSpawn && entry.Location != null) return entry;
        }
        return null;
    }
}
