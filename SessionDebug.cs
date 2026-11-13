using System;
using System.Globalization;
using ObenseuerCore;
using UnityEngine;

internal static partial class Session
{
    private static readonly CultureInfo DebugInv = CultureInfo.InvariantCulture;

    internal static void DebugInit()
    {
        try
        {
            Lobby.MemberJoined += DebugMemberJoined;
            Lobby.MemberLeft += DebugMemberLeft;
            Lobby.OwnerChanged += DebugOwnerChanged;
            CoopDebug.Log("sess", "session debug hooks installed on the lobby roster");
        }
        catch (Exception e)
        {
            CoopLog.Once("Session.DebugInit", e);
        }
    }

    private static void DebugMemberJoined(ulong id)
    {
        if (!CoopDebug.On("sess")) return;
        CoopDebug.Event("sess", "memberjoin", "lobby member joined: " + DisplayName(id) + " (" + id + "), " + CoopDebug.LobbyLine());
    }

    private static void DebugMemberLeft(ulong id)
    {
        if (!CoopDebug.On("sess")) return;
        bool listed = Lobby.Members != null && Lobby.Members.Contains(id);
        CoopDebug.Event("sess", "memberleft", "lobby member left: " + DisplayName(id) + " (" + id + "), still listed " + listed
            + ", the guest branch would mark the host left " + (IsGuest && id == knownHost)
            + ", host left flag " + hostLeft + ", " + CoopDebug.LobbyLine());
    }

    private static void DebugOwnerChanged(ulong previous, ulong owner)
    {
        if (!CoopDebug.On("sess")) return;
        CoopDebug.Event("sess", "owner", "lobby owner changed from " + previous + " to " + owner
            + ", the guest branch would mark the host left " + (IsGuest && previous != 0 && owner != 0)
            + ", host left flag " + hostLeft + ", " + CoopDebug.LobbyLine());
    }

    internal static void DebugLobbyGone(string where)
    {
        if (!CoopDebug.On("sess")) return;
        CoopDebug.Event("sess", "lobbygone", where + ": in lobby " + IsInLobby + ", guest " + IsGuest
            + ", host in world " + hostInWorld + ", host packet age " + CoopDebug.Age(hostSeenAt)
            + ", host left " + hostLeft + ", host quit " + hostQuit + ", host closed " + hostClosed + ", host reload " + hostReload
            + ", deliberate leave " + leftDeliberately + ", world received " + worldReceived + ", " + CoopDebug.LobbyLine());
    }

    internal static void DebugJoinPending(bool on)
    {
        if (!CoopDebug.On("sess")) return;
        if (on)
        {
            CoopDebug.Event("sess", "joinwait", "join pending set, timeout " + JoinAnswerTimeout.ToString("0.#", DebugInv)
                + " s, host " + DisplayName(HostId) + " (" + HostId + ")");
            return;
        }
        CoopDebug.Event("sess", "joinwait", "join pending cleared after " + (joinAskedAt <= 0f ? 0f : Time.unscaledTime - joinAskedAt).ToString("0.0", DebugInv)
            + " s, world received " + worldReceived + ", guard " + GuestSession.InHostWorld);
    }

    internal static void DebugAlert(string kind, string detail)
    {
        if (!CoopDebug.On("sess")) return;
        CoopDebug.Event("sess", "alert", "alert shown: " + kind + ", " + detail + ", " + CoopDebug.LobbyLine());
    }
}
