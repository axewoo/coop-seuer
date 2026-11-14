using System;
using System.Collections.Generic;
using System.Globalization;
using ObenseuerCore;
using UnityEngine;

internal static class BedVote
{
    private const string Channel = "world";
    private const string On = "BED|1";
    private const string Off = "BED|0";
    private const string Wake = "WAKE";
    private const string CastPrefix = "BEDS|";
    private const float Heartbeat = 5f;
    private const float CastInterval = 2f;
    private const float GuestStale = 30f;
    private const float WaitNoticeInterval = 15f;
    private const int MaxNames = 64;
    private const int MaxNameLength = 20;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Dictionary<ulong, float> beds = new Dictionary<ulong, float>();
    private static readonly List<ulong> gone = new List<ulong>();
    private static readonly List<string> missing = new List<string>();
    private static bool inBed;
    private static bool sleeping;
    private static bool voluntary;
    private static bool noRemotes = true;
    private static bool castDirty;
    private static string sentCast = "";
    private static float nextCast;
    private static float nextHeartbeat;
    private static int shownInBed;
    private static int shownTotal;
    private static string shownMissing = "";

    public static bool AllInBed
    {
        get { return noRemotes || (shownTotal > 0 && shownInBed == shownTotal); }
    }

    public static bool NoRemotePlayers
    {
        get { return noRemotes; }
    }

    public static void Tick(float now)
    {
        try
        {
            if (!WorldHooks.Timing) return;
            if (!Session.IsInLobby) return;
            if (Session.IsHost) HostTick(now);
            else GuestTick(now);
            WaitNotice();
        }
        catch (Exception e)
        {
            CoopLog.Once("BedVote.Tick", e);
        }
    }

    public static void Handle(ulong sender, string text)
    {
        try
        {
            if (text == On)
            {
                if (!Session.IsHost || !Session.Peers.Contains(sender)) return;
                if (!beds.ContainsKey(sender)) CoopLog.Log.Info("bed " + Session.DisplayName(sender) + " is in bed");
                beds[sender] = Time.unscaledTime;
                castDirty = true;
                return;
            }
            if (text == Off)
            {
                if (!Session.IsHost) return;
                if (!beds.Remove(sender)) return;
                castDirty = true;
                CoopLog.Log.Info("bed " + Session.DisplayName(sender) + " left the bed");
                return;
            }
            if (text == Wake)
            {
                if (Session.IsGuest && sender == Session.HostId) GuestWake();
                return;
            }
            if (text.StartsWith(CastPrefix, StringComparison.Ordinal) && Session.IsGuest && sender == Session.HostId) ReadCast(text);
        }
        catch (Exception e)
        {
            CoopLog.Once("BedVote.Handle", e);
        }
    }

    public static void BedUse()
    {
        WaitingUI ui = WaitingUI.instance;
        if (ui == null || !ui.waitingUIisVisible) return;
        voluntary = true;
        EnterBed();
    }

    public static void TimeSetClosed()
    {
        WaitingController controller = WaitingController.instance;
        if (controller != null && controller.IsSleeping) return;
        LeaveBed();
    }

    public static void SleepStarted()
    {
        sleeping = true;
        if (voluntary) EnterBed();
    }

    public static void SleepEnded(bool host)
    {
        bool was = sleeping;
        sleeping = false;
        voluntary = false;
        LeaveBed();
        TimeSpeed.ResetSleepWant();
        if (!was || !host) return;
        Session.SendAll(Channel, Wake, true);
        castDirty = true;
        CoopLog.Log.Info("bed the host woke, WAKE sent");
    }

    public static void Reset()
    {
        beds.Clear();
        inBed = false;
        sleeping = false;
        voluntary = false;
        noRemotes = true;
        castDirty = true;
        sentCast = "";
        nextCast = 0f;
        nextHeartbeat = 0f;
        shownInBed = 0;
        shownTotal = 0;
        shownMissing = "";
        CoopNotice.Reset();
    }

    public static void OnSceneChanged()
    {
        try
        {
            inBed = false;
            sleeping = false;
            voluntary = false;
            beds.Clear();
            castDirty = true;
            if (Session.IsGuest && Session.HostId != 0) Session.Send(Session.HostId, Channel, Off, true);
        }
        catch (Exception e)
        {
            CoopLog.Once("BedVote.OnSceneChanged", e);
        }
    }

    private static void HostTick(float now)
    {
        Expire(now);
        int total = 0;
        int count = 0;
        missing.Clear();
        noRemotes = true;
        WaitingController controller = WaitingController.instance;
        bool sleepingLocal = controller != null && controller.IsSleeping;
        if (Session.LocalInWorld)
        {
            total = 1;
            if (inBed || sleepingLocal) count++;
            else missing.Add(Session.DisplayName(Session.Me));
        }
        foreach (PresenceRecord record in Session.Remotes())
        {
            if (!Session.Peers.Contains(record.Peer)) continue;
            noRemotes = false;
            total++;
            if (beds.ContainsKey(record.Peer)) count++;
            else missing.Add(Session.DisplayName(record.Peer));
        }
        shownInBed = count;
        shownTotal = total;
        shownMissing = JoinNames();
        if (total < 1) return;
        string cast = CastPrefix + count.ToString(Inv) + "|" + total.ToString(Inv) + "|" + shownMissing;
        bool changed = cast != sentCast;
        if (!changed && !castDirty && (count < 1 || now < nextCast)) return;
        if (changed) CoopLog.Log.Info("bed " + count.ToString(Inv) + "/" + total.ToString(Inv));
        sentCast = cast;
        castDirty = false;
        nextCast = now + CastInterval;
        Session.SendAll(Channel, cast, true);
    }

    private static void GuestTick(float now)
    {
        if (!inBed || now < nextHeartbeat) return;
        nextHeartbeat = now + Heartbeat;
        Session.Send(Session.HostId, Channel, On, true);
    }

    private static void WaitNotice()
    {
        if (!inBed || shownTotal < 1 || shownInBed >= shownTotal) return;
        string text = shownMissing.Length > 0 ? "Waiting for " + shownMissing : "Waiting for the other players";
        CoopNotice.Show("bed|" + shownInBed.ToString(Inv) + "|" + shownTotal.ToString(Inv), text, WaitNoticeInterval);
    }

    private static void Expire(float now)
    {
        if (beds.Count == 0) return;
        gone.Clear();
        foreach (KeyValuePair<ulong, float> entry in beds)
        {
            if (now - entry.Value > GuestStale || !Session.Peers.Contains(entry.Key)) gone.Add(entry.Key);
        }
        foreach (ulong id in gone)
        {
            beds.Remove(id);
            castDirty = true;
        }
    }

    private static void EnterBed()
    {
        if (inBed) return;
        inBed = true;
        castDirty = true;
        CoopLog.Log.Info("bed the local player is in bed");
        if (!Session.IsGuest) return;
        nextHeartbeat = Time.unscaledTime + Heartbeat;
        Session.Send(Session.HostId, Channel, On, true);
    }

    private static void LeaveBed()
    {
        voluntary = false;
        if (!inBed) return;
        inBed = false;
        castDirty = true;
        CoopLog.Log.Info("bed the local player left the bed");
        if (Session.IsGuest) Session.Send(Session.HostId, Channel, Off, true);
    }

    private static void GuestWake()
    {
        WaitingController controller = WaitingController.instance;
        if (controller == null || !controller.IsSleeping) return;
        CoopLog.Log.Info("bed the host woke, ending the local sleep");
        Solo.WakeNow();
    }

    private static void ReadCast(string text)
    {
        string[] parts = text.Split('|');
        if (parts.Length != 4 || parts[0] != "BEDS") return;
        int count, total;
        if (!int.TryParse(parts[1], NumberStyles.Integer, Inv, out count) || !int.TryParse(parts[2], NumberStyles.Integer, Inv, out total)) return;
        if (count < 0 || total < 1 || total > Session.MaxMembers || count > total) return;
        if (parts[3].Length > MaxNames) return;
        shownInBed = count;
        shownTotal = total;
        shownMissing = CleanNames(parts[3]);
    }

    private static string JoinNames()
    {
        if (missing.Count == 0) return "";
        string text = "";
        foreach (string name in missing)
        {
            string clean = CleanName(name);
            if (clean.Length == 0) continue;
            if (text.Length > 0 && text.Length + 2 + clean.Length > MaxNames) break;
            if (text.Length > 0) text += ", ";
            text += clean;
        }
        return text;
    }

    private static string CleanNames(string text)
    {
        string clean = "";
        foreach (char c in text)
        {
            if (clean.Length >= MaxNames) break;
            clean += Allowed(c) || c == ',' ? c : '_';
        }
        return clean.Trim();
    }

    private static string CleanName(string name)
    {
        if (name == null) return "";
        string text = "";
        foreach (char c in name)
        {
            if (text.Length >= MaxNameLength) break;
            text += Allowed(c) ? c : '_';
        }
        return text.Trim();
    }

    private static bool Allowed(char c)
    {
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
            || c == ' ' || c == '-' || c == '_' || c == '.' || c == '\'';
    }
}
