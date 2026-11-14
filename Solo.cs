using System;
using System.IO;
using HarmonyLib;
using ObenseuerCore;
using UnityEngine;

internal static partial class Solo
{
    private const string Channel = "world";
    private const string HomeScene = "Open Sewer Tenement";
    private const float PassoutSeconds = 10f;
    private const float RespawnDelay = 4f;
    private const float RespawnHealth = 35f;
    private static float passoutAt = -1f;
    private static bool arrest;
    private const float ArrestSafety = 60f;
    private static float deadAt = -1f;
    private static float homeArmedAt = -1f;
    private static bool travelExpected;
    private static bool reloadIntent;
    private static bool rejoin;
    private static float rejoinUntil;
    public static bool Ready;

    public static void Install()
    {
        try
        {
            Harmony harmony = new Harmony(Plugin.PluginGuid + ".solo");
            harmony.Patch(AccessTools.Method(typeof(WaitingController), "PassOut", new[] { typeof(bool), typeof(WaitingController.Type) }), postfix: new HarmonyMethod(typeof(Solo), "PassOutPostfix"));
            harmony.Patch(AccessTools.Method(typeof(TimeOfDayAzure), "SetSpeedFactor", new[] { typeof(float) }), prefix: new HarmonyMethod(typeof(Solo), "SpeedPrefix"));
            harmony.Patch(AccessTools.Method(typeof(Death), "PlayerDeath"), postfix: new HarmonyMethod(typeof(Solo), "DeathPostfix"));
            harmony.Patch(AccessTools.Method(typeof(PauseMenu), "PausemenuDeath"), prefix: new HarmonyMethod(typeof(Solo), "DeathMenuPrefix"));
            harmony.Patch(AccessTools.Method(typeof(WaitingController), "StartSleep"), postfix: new HarmonyMethod(typeof(Solo), "SleepPostfix"));
            harmony.Patch(AccessTools.Method(typeof(SaveController), "LoadGameWithMigration", new[] { typeof(string), typeof(string), typeof(bool) }), prefix: new HarmonyMethod(typeof(Solo), "LoadPrefix"));
            SaveController.LoadingDone += OnLoadingDone;
            SaveController.LoadingDone += DeathHomeLoaded;
            Ready = true;
            CoopLog.Log.Info("coop: solo passout, death and reload hooks installed");
        }
        catch (Exception e)
        {
            CoopLog.Once("Solo.Install", e);
        }
    }

    private static bool Shared
    {
        get { return Session.IsInLobby && Session.Peers.Count > 0 && Session.LocalInWorld; }
    }

    public static void PassOutPostfix()
    {
        WaitingController w = WaitingController.instance;
        if (!Session.IsInLobby || w == null || (!w.IsSleeping && !w.IsWaiting)) return;
        passoutAt = Time.realtimeSinceStartup;
        arrest = !w.IsSleeping;
        CoopLog.Log.Info("coop: " + (arrest ? "arrested" : "passed out") + ", only this player is affected; the shared clock keeps its speed");
    }

    public static void SpeedPrefix(ref float newValue)
    {
        if (newValue <= 1f || !Session.IsInLobby) return;
        newValue = 1f;
    }

    private const float SleepSeconds = 15f;
    private const int MaxSleep = 12 * 3600;
    private static float sleepAt = -1f;
    private static readonly System.Reflection.FieldInfo SetTimeField = AccessTools.Field(typeof(WaitingController), "currentSleepSetTime");
    private static readonly System.Reflection.FieldInfo AlarmField = AccessTools.Field(typeof(WaitingController), "alarmEnabled");

    public static void SleepPostfix(WaitingController __instance)
    {
        if (!Session.IsInLobby || __instance == null || !__instance.IsSleeping) return;
        sleepAt = Time.realtimeSinceStartup;
        CoopLog.Log.Info("coop: sleeping on the real clock, waking in " + SleepSeconds + " s with the rest applied");
    }

    private static void FinishSleep(WaitingController w)
    {
        sleepAt = -1f;
        passoutAt = -1f;
        try
        {
            int seconds = MaxSleep;
            object alarm = AlarmField == null ? null : AlarmField.GetValue(w);
            object set = SetTimeField == null ? null : SetTimeField.GetValue(w);
            if (alarm is bool && (bool)alarm && set is int && (int)set > 0) seconds = Math.Min((int)set, MaxSleep);
            PlayerStatsTiredness tiredness = PlayerStats.instance != null ? PlayerStats.instance.tiredness : null;
            int slept = 0;
            while (tiredness != null && slept < seconds && w.IsSleeping && (tiredness.sleepdebt > 0f || tiredness.currentStatus > 0f))
            {
                tiredness.UpdateStats(60);
                slept += 60;
            }
            CoopLog.Log.Info("coop: woke after " + (slept / 60) + " slept minutes on the real clock");
        }
        catch (Exception e)
        {
            CoopLog.Once("Solo.FinishSleep", e);
        }
        if (w.IsSleeping) w.Wakeup(false, false);
    }

    public static void WakeNow()
    {
        WaitingController w = WaitingController.instance;
        if (w == null || !w.IsSleeping) return;
        if (sleepAt >= 0f) FinishSleep(w);
        else w.Wakeup(false, false);
    }

    private const float WaitSeconds = 15f;
    private static float waitAt = -1f;

    private static void WaitTick(float now)
    {
        WaitingController w = WaitingController.instance;
        if (!Session.IsInLobby || w == null || !w.IsWaiting || w.IsSleeping || passoutAt >= 0f)
        {
            waitAt = -1f;
            return;
        }
        if (waitAt < 0f) waitAt = now;
        if (now - waitAt < WaitSeconds) return;
        waitAt = -1f;
        CoopNotice.Show("solo|wait", "Co-op runs on a real clock, the wait ends here", 4f);
        w.Wakeup(false, false);
    }

    public static void DeathPostfix()
    {
        if (!Shared) return;
        deadAt = Time.realtimeSinceStartup;
        DeathHomeClear();
        CoopUi.DeathOpen = true;
        try
        {
            if (GameController.instance != null && Death.instance != null)
            {
                GameController.instance.ControlsDisabled(Death.instance.gameObject);
                GameController.instance.ChangeCursorStatus(CursorLockMode.None, true);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("Solo.DeathPostfix", e);
        }
        CoopLog.Log.Info("coop: the local player died, the co-op death screen asks: home or reload");
    }

    private static bool vanillaDeathMenu;
    private static bool hostLoading;

    public static bool DeathMenuPrefix()
    {
        if (vanillaDeathMenu || !Shared) return true;
        return false;
    }

    public static void ChooseHome()
    {
        if (!CoopUi.DeathOpen) return;
        CoopLog.Log.Info("coop: death choice: wake up at home");
        Respawn();
    }

    public static void ChooseReload()
    {
        float now = Time.realtimeSinceStartup;
        CoopUi.DeathOpen = false;
        if (Session.IsHost)
        {
            CoopLog.Log.Info("coop: death choice: load a save");
            hostLoading = true;
            vanillaDeathMenu = true;
            try
            {
                if (PauseMenu.instance != null)
                {
                    PauseMenu.instance.PausemenuDeath();
                    PauseMenu.instance.LoadGame();
                }
            }
            finally
            {
                vanillaDeathMenu = false;
            }
            return;
        }
        CoopLog.Log.Info("coop: death choice: rejoin the host's world");
        deadAt = -1f;
        DeathHomeClear();
        Revive();
        rejoin = true;
        rejoinUntil = now + 180f;
        Session.ReturnToMenu("You died. Rejoining the host's world...", true);
    }

    private const float DeathChoice = 60f;

    private static void DeathTick(float now)
    {
        if (now - deadAt < RespawnDelay) return;
        if (hostLoading)
        {
            if (Death.instance == null || !Death.instance.dead)
            {
                hostLoading = false;
                deadAt = -1f;
                CoopUi.DeathOpen = false;
                return;
            }
            if (GameController.instance != null && GameController.instance.GameIsPaused) return;
            hostLoading = false;
            CoopUi.DeathOpen = true;
            deadAt = now - RespawnDelay;
            return;
        }
        if (Death.instance != null && !Death.instance.dead)
        {
            deadAt = -1f;
            return;
        }
        if (Input.GetKeyDown(KeyCode.F8))
        {
            ChooseReload();
            return;
        }
        if (Input.GetKeyDown(KeyCode.F7) || now - deadAt > DeathChoice) Respawn();
    }

    private static void Revive()
    {
        try
        {
            if (Death.instance != null)
            {
                Death.instance.dead = false;
                Death.instance.ToggleDeadthMessage(false);
            }
            if (PlayerStats.instance != null && PlayerStats.instance.health != null)
            {
                PlayerStats.instance.health.bleeding = 0f;
                if (PlayerStats.instance.health.currentStatus < RespawnHealth) PlayerStats.instance.health.currentStatus = RespawnHealth;
                if (PlayerStats.instance.health.HealthToRegenerate < 0f) PlayerStats.instance.health.HealthToRegenerate = 0f;
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("Solo.Revive", e);
        }
    }

    private static void Respawn()
    {
        deadAt = -1f;
        hostLoading = false;
        CoopUi.DeathOpen = false;
        try
        {
            Revive();
            if (GameController.instance != null && Death.instance != null) GameController.instance.ControlsEnabled(Death.instance.gameObject);
            if (PauseMenu.instance != null) PauseMenu.instance.Resume();
            Time.timeScale = 1f;
            if (GameController.instance != null)
            {
                GameController.instance.GameIsPaused = false;
                GameController.instance.ChangeCursorStatus(CursorLockMode.Locked, false);
            }
            string here = GameRefs.AreaId;
            CoopLog.Log.Info("coop: respawned, going home from " + here);
            DeathHomeArm();
            if (Session.IsGuest && here != HomeScene)
            {
                CoopLog.Log.Info("death: guest travel request sent");
                FreeRoam.GuestDoor(HomeScene, HomeEntry);
            }
            else if (Session.IsGuest)
            {
                GuestSaveHooks.ArmDeathBypass(HomeScene);
                SaveController.ChangeLevel(HomeScene, HomeEntry);
            }
            else
            {
                HostTravel.SoloTravelNext("respawn");
                SaveController.ChangeLevel(HomeScene, HomeEntry);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("Solo.Respawn", e);
        }
    }

    public static void NoteTravel()
    {
        travelExpected = true;
    }

    public static void LoadPrefix(string characterName, string saveName)
    {
        try
        {
            if (!Session.IsHost || !Session.IsInLobby) return;
            if (travelExpected) return;
            reloadIntent = true;
            Session.NoteSaveSlot(SaveSlot(characterName, saveName));
        }
        catch (Exception e)
        {
            CoopLog.Once("Solo.LoadPrefix", e);
        }
    }

    private static string SaveSlot(string characterName, string saveName)
    {
        if (string.IsNullOrEmpty(characterName) || string.IsNullOrEmpty(saveName)) return "";
        string root = Path.Combine(Application.persistentDataPath, "Saves");
        return Path.Combine(Path.Combine(root, characterName), saveName);
    }

    private static void OnLoadingDone()
    {
        try
        {
            bool travel = travelExpected;
            travelExpected = false;
            bool reload = reloadIntent;
            reloadIntent = false;
            if (!Session.IsHost || !Session.IsInLobby) return;
            if (travel || !reload) return;
            Session.SendAll(Channel, "RELOAD", true);
            CoopLog.Log.Info("coop: the host loaded another save, the guests are asked to rejoin");
        }
        catch (Exception e)
        {
            CoopLog.Once("Solo.OnLoadingDone", e);
        }
    }

    public static bool HandleWorld(ulong from, string text)
    {
        if (text != "RELOAD") return false;
        if (!Session.IsGuest || from != Session.HostId) return true;
        if (Session.ReloadMessageLate)
        {
            CoopLog.Log.Info("coop: the reload announcement is late, the guest is already on its way into the host's new world");
            return true;
        }
        Session.StartHostReload();
        Session.NoteReloadMessage();
        CoopLog.Log.Info("coop: the host loaded another save, the guest waits for its world");
        return true;
    }

    private static void Rejoin(float now)
    {
        if (!rejoin) return;
        if (now > rejoinUntil || !Session.IsGuest)
        {
            rejoin = false;
            return;
        }
        if (MainMenu.Instance == null || !Session.HostInWorld || Session.JoinBlock().Length > 0) return;
        rejoin = false;
        CoopLog.Log.Info("coop: rejoining the host's new world");
        Session.JoinWorld();
    }

    public static void Tick()
    {
        try
        {
            float now = Time.realtimeSinceStartup;
            Rejoin(now);
            if (passoutAt >= 0f)
            {
                WaitingController w = WaitingController.instance;
                if (w == null || (!w.IsSleeping && !w.IsWaiting)) passoutAt = -1f;
                else if (!arrest) passoutAt = -1f;
                else if (now - passoutAt > ArrestSafety)
                {
                    passoutAt = -1f;
                    CoopLog.Log.Info("coop: waking from the passout");
                    w.Wakeup(false, false);
                }
            }
            WaitTick(now);
            if (sleepAt >= 0f)
            {
                WaitingController sw = WaitingController.instance;
                if (sw == null || !sw.IsSleeping) sleepAt = -1f;
                else if (now - sleepAt > SleepSeconds) FinishSleep(sw);
            }
            if (deadAt >= 0f) DeathTick(now);
            DeathHomeTick(now);
        }
        catch (Exception e)
        {
            CoopLog.Once("Solo.Tick", e);
        }
    }

    public static void Reset()
    {
        passoutAt = -1f;
        deadAt = -1f;
        DeathHomeClear();
    }
}
