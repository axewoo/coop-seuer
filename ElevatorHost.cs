using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

internal static class ElevatorHost
{
    private const float MovingInterval = 0.1f;
    private const float IdleInterval = 5f;
    private const int MaxCars = 64;
    private const int MaxRequests = 10;
    private const float RateWindow = 1f;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Dictionary<string, string> sent = new Dictionary<string, string>();
    private static readonly Dictionary<string, float> sentAt = new Dictionary<string, float>();
    private static readonly Dictionary<ulong, float> windowAt = new Dictionary<ulong, float>();
    private static readonly Dictionary<ulong, int> windowCount = new Dictionary<ulong, int>();
    private static readonly List<ulong> stale = new List<ulong>();
    internal static int Tracked { get { return sent.Count; } }
    private static readonly List<ulong> peersCache = new List<ulong>();
    private static ulong lastAuthority;
    private static float nextPrune;
    private static int seq;

    public static bool Active
    {
        get { return Session.IsInLobby && FreeRoam.IAmAuthority; }
    }

    public static void Reset()
    {
        sent.Clear();
        sentAt.Clear();
        windowAt.Clear();
        windowCount.Clear();
        stale.Clear();
        peersCache.Clear();
        lastAuthority = 0UL;
        nextPrune = 0f;
        ElevatorRefs.Reset();
        ElevatorLog.Reset();
    }

    public static void OnSceneChanged()
    {
        sent.Clear();
        sentAt.Clear();
        peersCache.Clear();
        ElevatorRefs.Reset();
    }

    public static void MarkDirty(Elevator car)
    {
        if (car == null || !Active) return;
        string key = ElevatorRefs.KeyOf(car);
        if (key == null) return;
        ElevatorRefs.Note(car);
        sent.Remove(key);
    }

    public static void Handle(ulong from, ElevMsg m)
    {
        try
        {
            if (!Active || m == null) return;
            if (m.Kind == "ELQ") Request(from, m);
        }
        catch (Exception e)
        {
            CoopLog.Once("ElevatorHost.Handle", e);
        }
    }

    public static void Tick()
    {
        try
        {
            if (!Active || !Session.LocalInWorld) return;
            float now = Time.unscaledTime;
            string scene = GameRefs.AreaId;
            if (scene == null) return;
            ulong authority = FreeRoam.Authority;
            if (authority != lastAuthority)
            {
                lastAuthority = authority;
                sent.Clear();
                sentAt.Clear();
                peersCache.Clear();
            }
            if (ElevatorRefs.Scan())
            {
                sent.Clear();
                sentAt.Clear();
            }
            PruneRates(now);
            List<ulong> peers = Session.PeersInArea(scene);
            bool joined = !SamePeers(peers);
            if (joined) RememberPeers(peers);
            if (peers.Count == 0) return;
            List<Elevator> cars = ElevatorRefs.Cars;
            int count = 0;
            int sentCount = 0;
            for (int i = 0; i < cars.Count; i++)
            {
                Elevator car = cars[i];
                if (car == null) continue;
                if (count >= MaxCars) break;
                string key = ElevatorRefs.KeyOf(car);
                if (key == null) continue;
                count++;
                string state = Signature(car);
                string known;
                bool changed = !sent.TryGetValue(key, out known) || known != state;
                float at;
                bool due = !sentAt.TryGetValue(key, out at) || now - at >= (ElevatorRefs.Moving(car) != 0 ? MovingInterval : IdleInterval);
                if (!joined && !changed && !due) continue;
                string payload = Build(scene, key, car);
                if (payload == null) continue;
                bool reliable = joined || changed;
                for (int p = 0; p < peers.Count; p++) Session.Send(peers[p], ElevatorWire.Channel, payload, reliable);
                sent[key] = state;
                sentAt[key] = now;
                sentCount++;
                if (changed) ElevatorLog.Info("elev.send", "elevator " + key + " state sent in " + scene);
            }
            if (sentCount > 0 && CoopDebug.On("elev")) CoopDebug.Event("elev", "send", sentCount + " of " + count + " car states sent to " + peers.Count + " peers in " + scene + (joined ? ", a new peer set forced the resend" : ""));
        }
        catch (Exception e)
        {
            CoopLog.Once("ElevatorHost.Tick", e);
        }
    }

    private static void Request(ulong from, ElevMsg m)
    {
        if (!Session.Peers.Contains(from)) return;
        if (!Session.LocalInWorld || m.Scene != GameRefs.AreaId)
        {
            Answer(from, m, false, "not in co-op yet");
            return;
        }
        if (!Admit(from))
        {
            ElevatorLog.Info("elev.rate", "elevator requests from " + from + " were over the rate limit and were dropped");
            return;
        }
        Elevator car = ElevatorRefs.CarOf(m.Key);
        if (car == null)
        {
            Answer(from, m, false, "the elevator is not here");
            ElevatorLog.Info("elev.refuse", "elevator request " + m.Op + " for " + m.Key + " in " + m.Scene + " was refused, the car was not found");
            return;
        }
        if (!Apply(car, m))
        {
            ElevatorLog.Info("elev.ignore", "elevator request " + m.Op + " for " + m.Key + " carried an out of range argument and was ignored");
            return;
        }
        sent.Remove(m.Key);
        Answer(from, m, true, "");
        ElevatorLog.Info("elev.apply", "elevator request " + m.Op + " for " + m.Key + " from " + from + " was applied");
    }

    private static bool Apply(Elevator car, ElevMsg m)
    {
        int floors = car.Floors == null ? 0 : car.Floors.Length;
        if (m.Op == "call" || m.Op == "enable" || m.Op == "disable")
        {
            if (m.Arg < 0 || m.Arg >= floors) return false;
        }
        else if (m.Arg != 0 && m.Arg != 1)
        {
            return false;
        }
        Shared.Apply(() =>
        {
            if (m.Op == "call") car.Call(m.Arg);
            else if (m.Op == "up") car.MoveToOneFloorUp(m.Arg == 1);
            else if (m.Op == "down") car.MoveToOneFloorDown(m.Arg == 1);
            else if (m.Op == "enable") car.SetFloorEnabled(m.Arg);
            else if (m.Op == "disable") car.SetFloorDisabled(m.Arg);
            else if (m.Op == "power") car.TogglePower(m.Arg == 1);
        });
        return true;
    }

    private static void Answer(ulong from, ElevMsg m, bool ok, string reason)
    {
        string scene = m.Scene == null ? GameRefs.AreaId : m.Scene;
        Session.Send(from, ElevatorWire.Channel, ElevatorWire.Answer(scene, m.Key, ok, reason), true);
    }

    private static string Signature(Elevator car)
    {
        string text = ElevatorRefs.Moving(car).ToString(Inv) + (ElevatorRefs.Direction(car) ? "1" : "0")
            + (ElevatorRefs.DoorsClosed(car) ? "1" : "0") + (car.powered ? "1" : "0") + Mask(car).ToString(Inv)
            + "|" + ElevatorRefs.QueueText(car);
        if (ElevatorRefs.Moving(car) == 0) text += "|" + car.Position.ToString("R", Inv);
        return text;
    }

    private static int Mask(Elevator car)
    {
        int mask = 0;
        int floors = car.Floors == null ? 0 : car.Floors.Length;
        for (int i = 0; i < floors && i < 31; i++)
        {
            if (car.Floors[i] != null && car.Floors[i].enabled) mask |= 1 << i;
        }
        return mask;
    }

    private static string Build(string scene, string key, Elevator car)
    {
        if (car.Floors == null || car.Floors.Length == 0) return null;
        return ElevatorWire.State(scene, key, car.Position, ElevatorRefs.Moving(car), ElevatorRefs.Direction(car),
            ElevatorRefs.DoorsClosed(car), car.powered, Mask(car), ElevatorRefs.QueueText(car), NextSeq());
    }

    private static int NextSeq()
    {
        seq++;
        if (seq < 1) seq = 1;
        return seq;
    }

    private static bool Admit(ulong peer)
    {
        float now = Time.unscaledTime;
        float start;
        if (!windowAt.TryGetValue(peer, out start) || now - start >= RateWindow)
        {
            windowAt[peer] = now;
            windowCount[peer] = 1;
            return true;
        }
        int count;
        if (!windowCount.TryGetValue(peer, out count)) count = 0;
        if (count >= MaxRequests) return false;
        windowCount[peer] = count + 1;
        return true;
    }

    private static void PruneRates(float now)
    {
        if (now < nextPrune) return;
        nextPrune = now + RateWindow;
        stale.Clear();
        foreach (KeyValuePair<ulong, float> pair in windowAt)
        {
            if (!Session.Peers.Contains(pair.Key)) stale.Add(pair.Key);
        }
        for (int i = 0; i < stale.Count; i++)
        {
            windowAt.Remove(stale[i]);
            windowCount.Remove(stale[i]);
        }
    }

    private static bool SamePeers(List<ulong> peers)
    {
        if (peers.Count != peersCache.Count) return false;
        for (int i = 0; i < peers.Count; i++)
        {
            if (peers[i] != peersCache[i]) return false;
        }
        return true;
    }

    private static void RememberPeers(List<ulong> peers)
    {
        peersCache.Clear();
        peersCache.AddRange(peers);
    }
}
