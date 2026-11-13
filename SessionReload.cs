using System;
using UnityEngine;

internal static partial class Session
{
    private const float HostReloadTimeout = 300f;
    private const float ReloadJoinRetry = 2f;
    private const float ReloadLateWindow = 30f;
    internal const string HostReloadText = "Waiting for the host to load a world";
    private const string HostReloadTimeoutText = "The host did not load a world. Open the co-op menu to join.";
    private const string HostReloadCancelText = "The reload wait is cancelled. Open the co-op menu to join.";
    private static bool hostReload;
    private static float hostReloadAt;
    private static float reloadSeenAt = -1000f;
    private static float reloadJoinAt;
    private static float reloadHostGoneAt;
    private static float reloadHostWorldAt;
    private static float lastJoinAt = -1000f;
    private static bool waitingForHostLeave;
    private static bool waitingForHostWorld;

    public static bool HostReload
    {
        get { return hostReload; }
    }

    private static void ClearReloadState()
    {
        waitingForHostLeave = true;
        waitingForHostWorld = false;
        reloadHostGoneAt = 0f;
        reloadHostWorldAt = 0f;
        invited = false;
    }

    private static void HandleBye(string text)
    {
        if (CoopDebug.On("sess")) CoopDebug.Event("sess", "bye", "the host sent BYE '" + text + "', guest " + IsGuest + ", host reload " + hostReload);
        if (text == "BYE")
        {
            hostQuit = true;
            return;
        }
        if (text == "BYE|reload")
        {
            StartHostReload();
            return;
        }
        hostClosed = true;
    }

    internal static void StartHostReload()
    {
        if (!IsGuest) return;
        if (hostReload)
        {
            hostReloadAt = Time.unscaledTime;
            reloadJoinAt = 0f;
            ReloadWorldStage();
            CoopLog.Log.Info("coop: the reload announcement was seen again, the wait is refreshed");
            MainMenuButton.JoinStateChanged();
            return;
        }
        CoopLog.Log.Info("coop: the host is loading another save, the guest waits with the lobby kept");
        hostReload = true;
        hostReloadAt = Time.unscaledTime;
        if (CoopDebug.On("sess")) CoopDebug.Event("sess", "reload", "host reload wait started, timeout " + HostReloadTimeout + " s, stage 1 waits for the host to leave its world");
        reloadSeenAt = Time.unscaledTime;
        reloadJoinAt = 0f;
        ClearReloadState();
        RejoinAllowed();
        MainMenuButton.JoinStateChanged();
        ReturnToMenu(HostReloadText, GuestWorld.LoadRoutineActive || GuestSession.Active);
    }

    internal static void NoteReloadMessage()
    {
        if (!hostReload) return;
        ReloadWorldStage();
    }

    internal static void NoteHostPacket(bool inWorld)
    {
        if (!hostReload) return;
        if (!inWorld)
        {
            ReloadWorldStage();
            return;
        }
        if (!waitingForHostWorld) return;
        if (reloadHostWorldAt > reloadHostGoneAt) return;
        reloadHostWorldAt = hostSeenAt;
        CoopLog.Log.Info("coop: the host reported a world after leaving the old one, the new world is up");
        if (CoopDebug.On("sess")) CoopDebug.Event("sess", "reload", "reload stage 2 over, the host world is up after " + (Time.unscaledTime - reloadHostGoneAt).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s, the join is sent");
    }

    private static void ReloadWorldStage()
    {
        if (!waitingForHostLeave) return;
        waitingForHostLeave = false;
        waitingForHostWorld = true;
        reloadHostGoneAt = Time.unscaledTime;
        if (CoopDebug.On("sess")) CoopDebug.Event("sess", "reload", "reload stage 1 over, the host is proven to have left its old world, stage 2 waits for the new one");
    }

    internal static bool CancelHostReload()
    {
        if (!hostReload) return false;
        hostReload = false;
        ClearReloadState();
        notice = HostReloadCancelText;
        CoopLog.Log.Info("coop: the reload wait was cancelled by the guest");
        MainMenuButton.JoinStateChanged();
        return true;
    }

    internal static void ClearHostReload()
    {
        if (!hostReload) return;
        hostReload = false;
        ClearReloadState();
        MainMenuButton.JoinStateChanged();
        CoopLog.Log.Info("coop: the reload wait is over");
    }

    private static bool ReloadHostInWorld
    {
        get { return IsGuest && waitingForHostWorld && hostInWorld && reloadHostWorldAt > reloadHostGoneAt && Time.unscaledTime - hostSeenAt < HostStale; }
    }

    internal static bool ReloadMessageLate
    {
        get
        {
            if (!GuestSession.InHostWorld && !joinPending) return false;
            return lastJoinAt > reloadSeenAt && Time.unscaledTime - lastJoinAt < ReloadLateWindow;
        }
    }

    internal static void HostReloadTick()
    {
        if (!hostReload) return;
        if (!IsGuest)
        {
            ClearHostReload();
            return;
        }
        if (Time.unscaledTime - hostReloadAt > HostReloadTimeout)
        {
            hostReload = false;
            ClearReloadState();
            notice = HostReloadTimeoutText;
            CoopLog.Log.Info("coop: the host did not load a world within " + HostReloadTimeout + " s, the wait is over");
            MainMenuButton.JoinStateChanged();
            return;
        }
        if (MainMenu.Instance == null) return;
        if (!ReloadHostInWorld) return;
        if (Time.unscaledTime - reloadJoinAt < ReloadJoinRetry) return;
        reloadJoinAt = Time.unscaledTime;
        string block = JoinBlock();
        if (block.Length > 0)
        {
            CoopLog.Log.Info("coop: the reload join is blocked: " + block);
            return;
        }
        if (JoinWorld())
        {
            hostReload = false;
            ClearReloadState();
            CoopLog.Log.Info("coop: the reload wait is over, the host's new world was requested");
            MainMenuButton.JoinStateChanged();
        }
    }
}
