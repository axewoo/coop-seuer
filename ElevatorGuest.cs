using System;
using System.Collections.Generic;
using UnityEngine;

internal static class ElevatorGuest
{
    private const float SnapThreshold = 0.05f;
    private const float MaxAge = 0.3f;
    private const float StaleSeconds = 12f;
    private const float ResolveRetry = 1f;
    private const float SnapReportInterval = 60f;
    private const int MaxMaskFloors = 31;
    private static readonly Dictionary<string, Remote> remotes = new Dictionary<string, Remote>();
    private static readonly List<string> gone = new List<string>();
    private static ulong lastAuthority;
    internal static int Tracked { get { return remotes.Count; } }

    private sealed class Remote
    {
        public ElevMsg Last;
        public Elevator Car;
        public bool Seen;
        public float Pos;
        public bool Snap = true;
        public float RecvAt;
        public int AppliedFloor = -1;
        public int AppliedMask = int.MinValue;
        public string QueueRaw;
        public float NextResolve;
        public int Snaps;
        public float NextReport;
    }

    public static void Reset()
    {
        remotes.Clear();
        gone.Clear();
        lastAuthority = 0UL;
        ElevatorLog.Reset();
    }

    public static void OnSceneChanged()
    {
        remotes.Clear();
        gone.Clear();
        lastAuthority = 0UL;
        ElevatorRefs.Reset();
    }

    public static bool Following(Elevator car)
    {
        if (car == null) return false;
        if (!Session.IsInLobby || FreeRoam.IAmAuthority) return false;
        if (FreeRoam.Authority == 0UL) return false;
        string key = ElevatorRefs.KeyOf(car);
        if (key == null) return false;
        Remote remote;
        return remotes.TryGetValue(key, out remote) && remote.Seen;
    }

    public static void Request(Elevator car, string op, int arg)
    {
        if (car == null) return;
        string key = ElevatorRefs.KeyOf(car);
        if (key == null) return;
        ulong authority = FreeRoam.Authority;
        if (authority == 0UL || authority == Session.Me) return;
        string scene = GameRefs.AreaId;
        if (scene == null) return;
        Session.Send(authority, ElevatorWire.Channel, ElevatorWire.Request(scene, key, op, arg), true);
        ElevatorLog.Info("elev.req", "elevator request " + op + " for " + key + " was sent to " + authority);
    }

    public static void Handle(ulong from, ElevMsg m)
    {
        try
        {
            if (m == null) return;
            if (m.Kind == "ELE") OnState(m);
            else if (m.Kind == "ELA") OnAnswer(m);
        }
        catch (Exception e)
        {
            CoopLog.Once("ElevatorGuest.Handle", e);
        }
    }

    public static void Tick()
    {
        try
        {
            if (!Session.IsInLobby || FreeRoam.IAmAuthority)
            {
                if (remotes.Count > 0) Reset();
                return;
            }
            ulong authority = FreeRoam.Authority;
            if (authority == 0UL || authority != lastAuthority)
            {
                if (remotes.Count > 0) CoopLog.Log.Info("coop: the elevator authority changed, the mirrored cars are dropped");
                remotes.Clear();
                gone.Clear();
                lastAuthority = authority;
                if (authority == 0UL) return;
            }
            string scene = GameRefs.AreaId;
            if (scene == null) return;
            float now = Time.unscaledTime;
            gone.Clear();
            foreach (KeyValuePair<string, Remote> pair in remotes)
            {
                Remote remote = pair.Value;
                if (now - remote.RecvAt > StaleSeconds)
                {
                    gone.Add(pair.Key);
                    continue;
                }
                if (remote.Last == null || remote.Last.Scene != scene)
                {
                    gone.Add(pair.Key);
                    continue;
                }
                if (remote.Car == null && now >= remote.NextResolve)
                {
                    remote.NextResolve = now + ResolveRetry;
                    remote.Car = ElevatorRefs.CarOf(pair.Key);
                }
            }
            for (int i = 0; i < gone.Count; i++) remotes.Remove(gone[i]);
        }
        catch (Exception e)
        {
            CoopLog.Once("ElevatorGuest.Tick", e);
        }
    }

    public static void FixedStep(Elevator car)
    {
        try
        {
            if (car == null) return;
            string key = ElevatorRefs.KeyOf(car);
            if (key == null) return;
            Remote remote;
            if (!remotes.TryGetValue(key, out remote) || !remote.Seen || remote.Last == null) return;
            remote.Car = car;
            Steer(remote, car, Time.fixedDeltaTime);
        }
        catch (Exception e)
        {
            CoopLog.Once("ElevatorGuest.FixedStep", e);
        }
    }

    private static void OnState(ElevMsg m)
    {
        if (!Session.IsInLobby || FreeRoam.IAmAuthority) return;
        if (FreeRoam.Authority == 0UL) return;
        if (!ElevatorWire.InScene(m.Scene)) return;
        Remote remote;
        if (!remotes.TryGetValue(m.Key, out remote))
        {
            remote = new Remote();
            remote.Car = ElevatorRefs.CarOf(m.Key);
            remotes[m.Key] = remote;
            ElevatorLog.Info("elev.first", "elevator " + m.Key + " first state received in " + m.Scene);
        }
        if (m.Seq > 0 && remote.Last != null && remote.Last.Seq > 0 && m.Seq <= remote.Last.Seq) return;
        if (!remote.Seen)
        {
            remote.Pos = m.Position;
            remote.Snap = true;
            if (m.Moving == 0 && remote.Car != null && Mathf.Abs(remote.Car.Position - m.Position) <= 0.001f) remote.AppliedFloor = (int)m.Position;
        }
        remote.Seen = true;
        remote.RecvAt = Time.unscaledTime;
        remote.Last = m;
    }

    private static void OnAnswer(ElevMsg m)
    {
        if (!Session.IsInLobby || FreeRoam.IAmAuthority) return;
        if (!ElevatorWire.InScene(m.Scene)) return;
        if (m.Ok)
        {
            ElevatorLog.Info("elev.ack", "elevator request for " + m.Key + " was answered");
            return;
        }
        string reason = string.IsNullOrEmpty(m.Reason) ? "refused" : m.Reason;
        CoopNotice.Show("elevator|refused", "Elevator: " + reason, 3f);
        ElevatorLog.Info("elev.refused", "elevator request for " + m.Key + " was refused: " + reason);
    }

    private static void Steer(Remote remote, Elevator car, float dt)
    {
        ElevMsg m = remote.Last;
        if (m == null) return;
        if (remote.AppliedMask != m.Mask)
        {
            Mask(car, m.Mask);
            remote.AppliedMask = m.Mask;
        }
        if (remote.QueueRaw == null || remote.QueueRaw != m.QueueRaw)
        {
            ElevatorRefs.SetQueue(car, m.Queue);
            remote.QueueRaw = m.QueueRaw;
        }
        if (car.powered != m.Powered) car.powered = m.Powered;
        if (m.Moving == 0) Stop(remote, car, m);
        else Move(remote, car, m, dt);
        ElevatorRefs.SetMoving(car, m.Moving);
        ElevatorRefs.SetDirection(car, m.Direction);
        ElevatorRefs.SetDoorsClosed(car, m.DoorsClosed);
        Report(remote);
    }

    private static void Stop(Remote remote, Elevator car, ElevMsg m)
    {
        bool moved = remote.AppliedFloor != (int)m.Position || Mathf.Abs(car.Position - m.Position) > 0.001f;
        if (remote.Snap || moved)
        {
            float floor = m.Position;
            Shared.Apply(() =>
            {
                ElevatorRefs.SetMoving(car, 0);
                car.SetPosition(floor);
            });
            remote.Snaps++;
        }
        remote.AppliedFloor = (int)m.Position;
        remote.Pos = m.Position;
        remote.Snap = false;
    }

    private static void Move(Remote remote, Elevator car, ElevMsg m, float dt)
    {
        float[] distances = ElevatorRefs.Distances(car);
        Vector3[] directions = ElevatorRefs.Directions(car);
        if (car.Floors == null || car.Floors.Length < 2 || distances == null || directions == null || distances.Length == 0)
        {
            Shared.Apply(() => car.SetPosition(m.Position));
            remote.Pos = m.Position;
            remote.Snap = false;
            return;
        }
        float target = Target(remote, car, m, distances);
        if (remote.Snap || Mathf.Abs(target - remote.Pos) > SnapThreshold)
        {
            float place = target;
            Shared.Apply(() =>
            {
                ElevatorRefs.SetMoving(car, m.Moving);
                car.SetPosition(place);
            });
            remote.Pos = target;
            remote.Snaps++;
        }
        else
        {
            float step = Mathf.MoveTowards(remote.Pos, target, Speed(car) * dt);
            remote.Pos = step;
            car.Position = step;
            ElevatorRefs.Move(car, step, distances, directions);
        }
        remote.Snap = false;
    }

    private static float Target(Remote remote, Elevator car, ElevMsg m, float[] distances)
    {
        float age = Time.unscaledTime - remote.RecvAt;
        if (age > MaxAge) age = MaxAge;
        if (age < 0f) age = 0f;
        float target = m.Position;
        int segment = ElevatorRefs.Segment(m.Position, car.Floors.Length, distances.Length);
        float length = distances[segment];
        float speed = Speed(car);
        if (speed > 0f && length > 0.0001f) target += (m.Moving > 0 ? 1f : -1f) * speed / length * age;
        float top = car.Floors.Length - 1;
        if (target > top) target = top;
        if (target < 0f) target = 0f;
        return target;
    }

    private static float Speed(Elevator car)
    {
        return car.Speed > 0f ? car.Speed : 0f;
    }

    private static void Mask(Elevator car, int mask)
    {
        if (car.Floors == null) return;
        for (int i = 0; i < car.Floors.Length && i < MaxMaskFloors; i++)
        {
            if (car.Floors[i] == null) continue;
            bool on = (mask & (1 << i)) != 0;
            if (car.Floors[i].enabled != on) car.Floors[i].enabled = on;
        }
    }

    private static void Report(Remote remote)
    {
        if (remote.Snaps == 0) return;
        float now = Time.unscaledTime;
        if (remote.NextReport == 0f) remote.NextReport = now + SnapReportInterval;
        if (now < remote.NextReport) return;
        string key = remote.Last == null ? "?" : remote.Last.Key;
        ElevatorLog.Info("elev.snap", "elevator " + key + " was corrected " + remote.Snaps + " time(s) in the last minute");
        remote.Snaps = 0;
        remote.NextReport = now + SnapReportInterval;
    }
}
