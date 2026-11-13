using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;

internal static partial class Session
{
    private static void StartJoinPending()
    {
        joinPending = true;
        joinAskedAt = Time.unscaledTime;
        MainMenuButton.JoinStateChanged();
        DebugJoinPending(true);
    }
    private static void JoinPendingTick()
    {
        if (!joinPending) return;
        if (worldReceived || GuestSession.InHostWorld)
        {
            ClearJoinPending();
            return;
        }
        if (Time.unscaledTime - joinAskedAt < JoinAnswerTimeout) return;
        notice = "The host did not answer the join request.";
        CoopLog.Log.Info("coop: no answer to the join request within " + JoinAnswerTimeout + " s");
        ClearJoinPending();
    }

    private static void ClearJoinPending()
    {
        if (!joinPending) return;
        joinPending = false;
        DebugJoinPending(false);
        MainMenuButton.JoinStateChanged();
    }

    private static void SendHost()
    {
        string scene = GameRefs.AreaId;
        bool inWorld = LocalInWorld;
        string text = "HST|" + (inWorld ? "1" : "0") + "|" + (inWorld ? scene : "") + "|" + Core.GameBuild + "|" + (LocalSavesDisabled() ? "1" : "0") + "|" + TimerScale.Wire();
        if (text == sentHost && Time.unscaledTime < nextHostSend) return;
        sentHost = text;
        nextHostSend = Time.unscaledTime + HostInterval;
        SendAll(WorldChannel, text, true);
    }

    private static void HandleHost(string text)
    {
        string[] parts = text.Split('|');
        bool five = parts.Length == 5;
        bool six = parts.Length == 6;
        if ((!five && !six && parts.Length != 4) || (parts[1] != "0" && parts[1] != "1") || ((five || six) && parts[4] != "0" && parts[4] != "1") || parts[2].Length > MaxTextLength || parts[3].Length > MaxTextLength)
        {
            CoopLog.Log.Debug("world.host", "ignored malformed HST");
            return;
        }
        bool inWorld = parts[1] == "1" && parts[2].Length > 0;
        bool savesOff = (five || six) && parts[4] == "1";
        if (inWorld != hostInWorld || parts[2] != hostScene)
        {
            CoopLog.Log.Info("host " + (inWorld ? "is in " + parts[2] : "is not in a world") + ", build " + parts[3] + ", saves off " + savesOff);
        }
        hostInWorld = inWorld;
        hostSavesOff = savesOff;
        hostScene = parts[2];
        hostBuild = parts[3];
        if (six) TimerScale.FromHost(parts[5]);
        hostSeenAt = Time.unscaledTime;
        NoteHostPacket(inWorld);
    }

    private static void HandleAck(string text)
    {
        string[] parts = text.Split(new char[] { '|' }, 3);
        if (parts.Length != 3 || (parts[1] != "0" && parts[1] != "1"))
        {
            CoopLog.Log.Debug("world.ack", "ignored malformed WACK");
            return;
        }
        if (parts[1] == "1")
        {
            notice = "";
            CoopLog.Log.Info("host accepted the join request " + parts[2]);
            return;
        }
        notice = "Join refused: " + parts[2];
        ClearJoinPending();
        CoopLog.Log.Info("host refused the join request: " + parts[2]);
    }

    private static void OnWorldReceived(WorldShip.WorldTicket ticket)
    {
        if (GuestSession.Following && GuestSession.Loaded && !FreeRoam.TicketMatches(ticket))
        {
            CoopLog.Log.Info("coop: a travel ticket for " + ticket.Manifest.hostScene + " was ignored, it is not the trip the guest is waiting for");
            return;
        }
        if (GuestSession.Following && GuestSession.Loaded)
        {
            CoopLog.Log.Info("coop: follow ticket received from the host");
            ClearJoinPending();
            GuestWorld.BeginFollow(ticket);
            return;
        }
        if (worldReceived || GuestSession.Active || GuestSession.Guard)
        {
            CoopLog.Log.Info("coop: a second world ticket was refused, a guest session is already running");
            return;
        }
        if (MainMenu.Instance == null)
        {
            CoopLog.Log.Info("coop: a world ticket was refused, the game is not on the main menu");
            return;
        }
        if (!GuestWorld.Begin(ticket))
        {
            worldReceived = false;
            CoopLog.Log.Info("coop: the guest load did not start, the join is allowed again");
            return;
        }
        worldReceived = true;
        ClearJoinPending();
        MainMenuButton.LobbyLoadStarted();
        CoopLog.Log.Info("coop: world received from the host, starting the guest load");
    }

    private static bool StartJoin(ulong guest)
    {
        if (GameRefs.LocalPlayer == null || GameRefs.AreaId == null)
        {
            Ack(guest, false, "the host is not in a world");
            return false;
        }
        if (WorldShip.SendTo(guest))
        {
            Ack(guest, true, "");
            return true;
        }
        Ack(guest, false, WorldShip.Refusal);
        return false;
    }

    private static void StartNextJoin()
    {
        while (joinQueue.Count > 0)
        {
            ulong next = joinQueue.Dequeue();
            if (IsHost && Peers.Contains(next) && StartJoin(next)) return;
        }
    }

    private static void Ack(ulong guest, bool accepted, string reason)
    {
        Send(guest, WorldChannel, "WACK|" + (accepted ? "1" : "0") + "|" + reason, true);
    }

    private static void ResetHost()
    {
        ClearJoinPending();
        ClearHostReload();
        hostInWorld = false;
        hostSavesOff = false;
        hostSeenAt = -1000f;
        sentHost = "";
        worldReceived = false;
        ClearWorldId();
    }
}
