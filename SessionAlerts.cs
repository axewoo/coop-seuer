using System;
using ObenseuerCore;
using UnityEngine;

internal static partial class Session
{
    private const float ConnectionStale = 15f;
    private const float HostGoneGrace = 0.5f;
    private const float ReloadHostGoneGrace = 15f;
    private static bool hostClosed;
    private static float hostGoneGraceAt;
    private static float hostLeftAt;
    private static bool connectionLostShown;
    private const float LocalFreezeThreshold = 1f;
    private static float lastConnectionTickAt;
    private static bool leftDeliberately;

    internal static bool LeftDeliberately
    {
        get { return leftDeliberately; }
    }

    private static void HostGoneTick()
    {
        if (hostReload)
        {
            ReloadGoneTick();
            return;
        }
        if (hostQuit)
        {
            hostQuit = false;
            hostInWorld = false;
            DebugAlert("host quit", "the quit BYE arrived");
            ReturnToMenu("The host quit the game. You are back on the main menu.", false);
            CoopAlert.Show(CoopAlert.Kind.HostQuit);
            return;
        }
        if (hostClosed)
        {
            hostClosed = false;
            hostInWorld = false;
            DebugAlert("host closed", "the closed lobby BYE arrived");
            ReturnToMenu("The host closed the lobby. You are back on the main menu.", false);
            LeaveOnHostGone();
            CoopAlert.Show(CoopAlert.Kind.HostClosed);
            return;
        }
        if (!hostLeft) return;
        if (Time.unscaledTime - hostLeftAt < HostGoneGrace) return;
        hostLeft = false;
        hostQuit = false;
        hostClosed = false;
        CoopLog.Log.Info("host left the lobby");
        DebugAlert("host left", "the host was off the roster for the whole grace of " + HostGoneGrace + " s, last host packet " + CoopDebug.Age(hostSeenAt) + " ago");
        ReturnToMenu("The host quit the game. You are back on the main menu.", false);
        LeaveOnHostGone();
        CoopAlert.Show(CoopAlert.Kind.HostLeft);
    }

    private static void ReloadGoneTick()
    {
        if (hostQuit)
        {
            hostQuit = false;
            hostInWorld = false;
            hostReload = false;
            DebugAlert("host quit during the reload wait", "the quit BYE arrived while the reload wait was open");
            ReturnToMenu("The host quit the game. You are back on the main menu.", false);
            LeaveOnHostGone();
            CoopAlert.Show(CoopAlert.Kind.HostQuit);
            return;
        }
        if (hostClosed)
        {
            hostClosed = false;
            hostInWorld = false;
            hostReload = false;
            DebugAlert("host closed during the reload wait", "the closed lobby BYE arrived while the reload wait was open");
            ReturnToMenu("The host closed the lobby. You are back on the main menu.", false);
            LeaveOnHostGone();
            CoopAlert.Show(CoopAlert.Kind.HostClosed);
            return;
        }
        if (HostStillMember())
        {
            if (hostLeft) DebugAlert("roster refresh", "a Steam lobby refresh dropped the host from the roster while the lobby still lists it as owner and member");
            hostLeft = false;
            hostGoneGraceAt = 0f;
            return;
        }
        if (hostGoneGraceAt <= 0f)
        {
            hostGoneGraceAt = Time.unscaledTime;
            return;
        }
        if (Time.unscaledTime - hostGoneGraceAt < ReloadHostGoneGrace) return;
        hostGoneGraceAt = 0f;
        hostLeft = false;
        hostReload = false;
        CoopLog.Log.Info("the host left the lobby during the reload wait");
        DebugAlert("host left during the reload wait", "off the roster for the whole reload grace of " + ReloadHostGoneGrace + " s, last host packet " + CoopDebug.Age(hostSeenAt) + " ago");
        ReturnToMenu("The host quit the game. You are back on the main menu.", false);
        LeaveOnHostGone();
        CoopAlert.Show(CoopAlert.Kind.HostLeft);
    }

    private static bool HostStillMember()
    {
        return Lobby.Owner != 0 && Lobby.Members.Contains(Lobby.Owner);
    }

    private static void MarkHostLeft()
    {
        if (hostLeft) return;
        hostLeft = true;
        hostLeftAt = Time.unscaledTime;
    }

    private static void ConnectionTick()
    {
        float now = Time.unscaledTime;
        float gap = now - lastConnectionTickAt;
        lastConnectionTickAt = now;
        if (!IsGuest || !GuestSession.InHostWorld)
        {
            connectionLostShown = false;
            return;
        }
        // While the guest places and loads the host's world, its own long frames
        // stop it from reading any packet, so silence there says nothing about the host.
        if (!GuestSession.Loaded)
        {
            hostSeenAt = Mathf.Max(hostSeenAt, now);
            connectionLostShown = false;
            return;
        }
        // A local freeze (a scene load, a save, a slow machine) also stops packet reads.
        // Do not count that frozen time as host silence.
        if (gap > LocalFreezeThreshold && hostSeenAt > -999f)
            hostSeenAt = Mathf.Min(now, hostSeenAt + gap);
        if (now - hostSeenAt < ConnectionStale)
        {
            connectionLostShown = false;
            return;
        }
        if (connectionLostShown) return;
        if (Lobby.Owner == 0 || !Lobby.Members.Contains(Lobby.Owner)) return;
        connectionLostShown = true;
        CoopLog.Log.Info("coop: no host packet for " + ConnectionStale + " s while Steam still lists the host");
        DebugAlert("connection lost", "no host packet for " + ConnectionStale + " s while the host is still a member, last packet " + CoopDebug.Age(hostSeenAt) + " ago");
        CoopAlert.Show(CoopAlert.Kind.ConnectionLost);
    }

    private static void LeaveOnHostGone()
    {
        leftDeliberately = false;
        invited = false;
        ResetHost();
        menuPending = false;
        Lobby.Leave();
    }
}
