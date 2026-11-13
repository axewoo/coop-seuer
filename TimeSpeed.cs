using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

internal static class TimeSpeed
{
    public const float Normal = 1f;
    private const string Channel = "world";
    private const string Crafting = "crafting";
    private const string Waiting = "waiting";
    private const float MaxFactor = 1000f;
    private const float RefreshInterval = 2f;
    private const float StaleAfter = 10f;
    private const float StopAfter = 5f;
    private const float NoticeInterval = 10f;
    private const int MaxName = 24;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Dictionary<ulong, Request> requests = new Dictionary<ulong, Request>();
    private static readonly List<ulong> gone = new List<ulong>();
    private static bool applying;
    private static bool seeded;
    private static bool overridden;
    private static bool savedOverride;
    private static float localWant = Normal;
    private static float sleepWant = Normal;
    private static float guestMax = Normal;
    private static float notifiedLocal = Normal;
    private static bool sentLive;
    private static float sentFactor = Normal;
    private static string sentReason = "";
    private static float nextRefresh;
    private static float awaySince = -1f;
    private static TimeOfDayAzure bound;

    private struct Request
    {
        public float Factor;
        public string Reason;
        public float SeenAt;
    }

    public static void OnSpeedSet(float value)
    {
        try
        {
            if (!WorldHooks.Timing || applying || float.IsNaN(value) || float.IsInfinity(value) || value < 0f) return;
            WaitingController controller = WaitingController.instance;
            bool sleeping = controller != null && controller.IsSleeping;
            if (sleeping) sleepWant = value;
            else localWant = value;
            if (Session.IsGuest)
            {
                if (sleeping || value <= Normal) StopGuest();
                else StartGuest(value, Reason(controller));
                return;
            }
            if (!Session.IsHost || sleeping || value <= Normal)
            {
                notifiedLocal = Normal;
                ApplyHostScale();
                return;
            }
            if (value > notifiedLocal) HostRise(value, Reason(controller));
            ApplyHostScale();
        }
        catch (Exception e)
        {
            CoopLog.Once("TimeSpeed.OnSpeedSet", e);
        }
    }

    public static void Handle(ulong sender, string text)
    {
        try
        {
            if (!WorldHooks.Timing) return;
            if (text.StartsWith("SPEED|", StringComparison.Ordinal))
            {
                if (!Session.IsHost || !Session.Peers.Contains(sender)) return;
                ReadRequest(sender, text);
                return;
            }
            if (text.StartsWith("FAST|", StringComparison.Ordinal) && Session.IsGuest && sender == Session.HostId) ReadFast(text);
        }
        catch (Exception e)
        {
            CoopLog.Once("TimeSpeed.Handle", e);
        }
    }

    public static void Tick(float now)
    {
        try
        {
            if (!WorldHooks.Timing) return;
            if (!Session.IsInLobby) return;
            if (Session.IsHost) Expire(now);
            else GuestTick(now);
        }
        catch (Exception e)
        {
            CoopLog.Once("TimeSpeed.Tick", e);
        }
    }

    public static void ApplyHostScale()
    {
        try
        {
            if (!WorldHooks.Timing || !Session.IsInLobby || !Session.IsHost) return;
            TimeOfDayAzure time = TimeOfDayAzure.instance;
            if (time == null || time.TimeController == null || time.updateTimeDisabled) return;
            if (!object.ReferenceEquals(time, bound))
            {
                bound = time;
                seeded = false;
                localWant = Normal;
                sleepWant = Normal;
                notifiedLocal = Normal;
            }
            WaitingController controller = WaitingController.instance;
            bool sleeping = controller != null && controller.IsSleeping;
            if (!seeded)
            {
                seeded = true;
                localWant = time.timeSpeedOverrideFactor;
                sleepWant = Normal;
                if (sleeping)
                {
                    sleepWant = time.timeSpeedOverrideFactor;
                    localWant = Normal;
                }
            }
            float baseWant = BaseWant();
            float want;
            if (BedVote.NoRemotePlayers)
            {
                want = baseWant;
            }
            else
            {
                want = baseWant;
                if (sleeping) want = BedVote.AllInBed ? Mathf.Max(localWant, sleepWant) : Normal;
                if (guestMax > want) want = guestMax;
            }
            if (want < 0f) want = Normal;
            overridden = !Mathf.Approximately(want, baseWant);
            if (Mathf.Approximately(time.timeSpeedOverrideFactor, want)) return;
            Apply(time, want);
        }
        catch (Exception e)
        {
            applying = false;
            CoopLog.Once("TimeSpeed.ApplyHostScale", e);
        }
    }

    public static void SaveBegin()
    {
        try
        {
            if (!overridden) return;
            TimeOfDayAzure time = TimeOfDayAzure.instance;
            if (time == null) return;
            savedOverride = true;
            Apply(time, BaseWant());
        }
        catch (Exception e)
        {
            CoopLog.Once("TimeSpeed.SaveBegin", e);
        }
    }

    public static void SaveEnd()
    {
        try
        {
            if (!savedOverride) return;
            savedOverride = false;
            ApplyHostScale();
        }
        catch (Exception e)
        {
            CoopLog.Once("TimeSpeed.SaveEnd", e);
        }
    }

    public static void ResetSleepWant()
    {
        sleepWant = Normal;
    }

    public static void Reset()
    {
        if (overridden) ReleaseLocal();
        requests.Clear();
        gone.Clear();
        seeded = false;
        overridden = false;
        savedOverride = false;
        localWant = Normal;
        sleepWant = Normal;
        guestMax = Normal;
        notifiedLocal = Normal;
        bound = null;
        sentLive = false;
        sentFactor = Normal;
        sentReason = "";
        nextRefresh = 0f;
        awaySince = -1f;
    }

    public static void OnSceneChanged()
    {
        try
        {
            if (Session.IsGuest) StopGuest();
            requests.Clear();
            gone.Clear();
            seeded = false;
            guestMax = Normal;
            bound = null;
            sentLive = false;
            sentFactor = Normal;
            sentReason = "";
            nextRefresh = 0f;
            awaySince = -1f;
        }
        catch (Exception e)
        {
            CoopLog.Once("TimeSpeed.OnSceneChanged", e);
        }
    }

    private static void ReadRequest(ulong sender, string text)
    {
        string[] parts = text.Split('|');
        if (parts.Length < 2 || parts.Length > 3 || parts[0] != "SPEED") return;
        float factor;
        if (!ReadFloat(parts[1], out factor) || factor > MaxFactor) return;
        if (factor <= Normal)
        {
            if (!requests.Remove(sender)) return;
            Recompute();
            CoopLog.Log.Info("speed " + Session.DisplayName(sender) + " is done");
            return;
        }
        string reason = parts.Length == 3 && parts[2] == Waiting ? Waiting : Crafting;
        bool fresh = !requests.ContainsKey(sender);
        Request request = new Request();
        request.Factor = factor;
        request.Reason = reason;
        request.SeenAt = Time.unscaledTime;
        requests[sender] = request;
        Recompute();
        if (!fresh) return;
        CoopLog.Log.Info("speed " + Session.DisplayName(sender) + " requests " + factor.ToString("F2", Inv) + " (" + reason + ")");
        FastNotice(sender, reason);
    }

    private static void ReadFast(string text)
    {
        string[] parts = text.Split('|');
        if (parts.Length != 4 || parts[0] != "FAST") return;
        ulong cause;
        if (!ulong.TryParse(parts[1], NumberStyles.Integer, Inv, out cause)) return;
        if (cause == Session.Me || parts[2].Length > MaxName) return;
        string reason = parts[3] == Waiting ? Waiting : Crafting;
        CoopNotice.Show("fast|" + cause.ToString(Inv) + "|" + reason, FasterText(CleanName(parts[2]), reason), NoticeInterval);
    }

    private static void StartGuest(float factor, string reason)
    {
        sentLive = true;
        sentFactor = factor;
        sentReason = reason;
        nextRefresh = Time.unscaledTime + RefreshInterval;
        Session.Send(Session.HostId, Channel, "SPEED|" + factor.ToString("F3", Inv) + "|" + reason, true);
        CoopLog.Log.Info("speed local " + factor.ToString("F2", Inv) + " (" + reason + ") sent to the host");
    }

    private static void StopGuest()
    {
        if (!sentLive) return;
        sentLive = false;
        sentFactor = Normal;
        sentReason = "";
        Session.Send(Session.HostId, Channel, "SPEED|1", true);
        CoopLog.Log.Info("speed local speed-up ended, SPEED|1 sent");
    }

    private static void GuestTick(float now)
    {
        if (!sentLive) return;
        if (!Session.LocalInWorld)
        {
            if (awaySince < 0f) awaySince = now;
            else if (now - awaySince > StopAfter) StopGuest();
            return;
        }
        awaySince = -1f;
        if (now < nextRefresh) return;
        nextRefresh = now + RefreshInterval;
        Session.Send(Session.HostId, Channel, "SPEED|" + sentFactor.ToString("F3", Inv) + "|" + sentReason, true);
    }

    private static void Expire(float now)
    {
        if (requests.Count == 0) return;
        gone.Clear();
        foreach (KeyValuePair<ulong, Request> entry in requests)
        {
            if (now - entry.Value.SeenAt > StaleAfter || !Session.Peers.Contains(entry.Key)) gone.Add(entry.Key);
        }
        if (gone.Count == 0) return;
        foreach (ulong id in gone)
        {
            requests.Remove(id);
            CoopLog.Log.Info("speed dropped the request from " + Session.DisplayName(id));
        }
        Recompute();
    }

    private static void Recompute()
    {
        float max = Normal;
        foreach (Request request in requests.Values)
        {
            if (request.Factor > max) max = request.Factor;
        }
        guestMax = max;
    }

    private static void FastNotice(ulong cause, string reason)
    {
        string name = Session.DisplayName(cause);
        CoopNotice.Show("fast|" + cause.ToString(Inv) + "|" + reason, FasterText(name, reason), NoticeInterval);
        Session.SendAll(Channel, "FAST|" + cause.ToString(Inv) + "|" + CleanName(name) + "|" + reason, true);
    }

    private static void HostRise(float value, string reason)
    {
        notifiedLocal = value;
        Session.SendAll(Channel, "FAST|" + Session.Me.ToString(Inv) + "|" + CleanName(Session.DisplayName(Session.Me)) + "|" + reason, true);
        CoopLog.Log.Info("speed the local clock runs at " + value.ToString("F2", Inv) + " (" + reason + "), FAST sent");
    }

    private static void ReleaseLocal()
    {
        overridden = false;
        TimeOfDayAzure time = TimeOfDayAzure.instance;
        if (time == null || time.TimeController == null || time.updateTimeDisabled) return;
        float want = BaseWant();
        if (Mathf.Approximately(time.timeSpeedOverrideFactor, want)) return;
        Apply(time, want);
        CoopLog.Log.Info("speed shared scale released, local scale " + want.ToString("F2", Inv));
    }

    private static float BaseWant()
    {
        WaitingController controller = WaitingController.instance;
        if (controller != null && controller.IsSleeping) return sleepWant;
        return localWant;
    }

    private static void Apply(TimeOfDayAzure time, float value)
    {
        applying = true;
        try
        {
            time.SetSpeedFactor(value);
        }
        finally
        {
            applying = false;
        }
    }

    private static string Reason(WaitingController controller)
    {
        return controller != null && controller.IsWaiting ? Waiting : Crafting;
    }

    private static string FasterText(string name, string reason)
    {
        return "Time is passing faster (" + name + " is " + reason + ")";
    }

    private static string CleanName(string name)
    {
        if (name == null) return "";
        string text = "";
        foreach (char c in name)
        {
            if (text.Length >= MaxName) break;
            text += Allowed(c) ? c : '_';
        }
        return text.Trim();
    }

    private static bool Allowed(char c)
    {
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
            || c == ' ' || c == '-' || c == '_' || c == '.' || c == '\'';
    }

    private static bool ReadFloat(string text, out float value)
    {
        return float.TryParse(text, NumberStyles.Float, Inv, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
