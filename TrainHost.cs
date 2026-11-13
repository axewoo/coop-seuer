using System;
using System.Collections.Generic;
using UnityEngine;

internal static class TrainHost
{
    private const float Interval = 0.2f;
    private const float PruneInterval = 1f;
    private const float KeepaliveInterval = 2f;
    private const int MaxTrains = 64;
    private const int MaxCommands = 20;
    private const float MaxSeconds = 1f;
    private static readonly Dictionary<string, string> sentFlags = new Dictionary<string, string>();
    private static readonly Dictionary<string, ulong> leases = new Dictionary<string, ulong>();
    private static readonly Dictionary<string, float> cmdWindow = new Dictionary<string, float>();
    private static readonly Dictionary<string, int> cmdCount = new Dictionary<string, int>();
    private static readonly Dictionary<string, float> cmdSpent = new Dictionary<string, float>();
    private static readonly Dictionary<string, float> keepalives = new Dictionary<string, float>();
    private static readonly List<string> done = new List<string>();
    private static string peersSignature = "";
    private static float next;
    private static float nextPrune;

    public static bool Active
    {
        get { return Session.IsInLobby && Session.IsHost; }
    }

    public static void Init()
    {
    }

    public static void Reset()
    {
        sentFlags.Clear();
        leases.Clear();
        cmdWindow.Clear();
        cmdCount.Clear();
        cmdSpent.Clear();
        keepalives.Clear();
        done.Clear();
        peersSignature = "";
        next = 0f;
        nextPrune = 0f;
        TrainRefs.Reset();
    }

    public static void OnSceneChanged()
    {
        sentFlags.Clear();
        leases.Clear();
        cmdWindow.Clear();
        cmdCount.Clear();
        cmdSpent.Clear();
        keepalives.Clear();
        peersSignature = "";
        TrainRefs.Reset();
    }

    public static void Handle(ulong from, TrainMsg m)
    {
        try
        {
            if (!Active || m == null) return;
            if (m.Kind == "REQDRV") RequestDrive(from, m);
            else if (m.Kind == "DRV") Command(from, m);
            else if (m.Kind == "REL") Released(from, m);
            else if (m.Kind == "REQSTART") RequestStart(from, m);
            else if (m.Kind == "REQSWT") RequestSwitch(from, m);
        }
        catch (Exception e)
        {
            CoopLog.Once("TrainHost.Handle", e);
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
            PruneLeases(scene, now);
            if (now < next) return;
            next = now + Interval;
            List<ulong> peers = Session.PeersInArea(scene);
            if (peers.Count == 0)
            {
                peersSignature = "";
                return;
            }
            TrainController controller = TrainController.instance;
            if (controller == null) return;
            Dictionary<string, TrackTrain> trains = controller.TrackTrains;
            if (trains == null) return;
            string signature = Peers(peers);
            bool joined = signature != peersSignature;
            peersSignature = signature;
            int count = 0;
            foreach (KeyValuePair<string, TrackTrain> pair in trains)
            {
                if (count >= MaxTrains) break;
                count++;
                TrackTrain train = pair.Value;
                if (train == null || string.IsNullOrEmpty(train.GUID)) continue;
                string guid = train.GUID;
                ulong driver = DriverOf(guid, train);
                bool moving = IsMoving(train);
                string flags = Flags(train, driver);
                string known;
                bool seen = sentFlags.TryGetValue(guid, out known);
                bool changed = !seen || flags != known;
                if (!moving && driver == 0 && !changed)
                {
                    if (!joined)
                    {
                        float due;
                        if (!keepalives.TryGetValue(guid, out due) || now < due) continue;
                    }
                    string keep = Build(scene, train, driver);
                    if (keep == null) continue;
                    for (int i = 0; i < peers.Count; i++) Session.Send(peers[i], TrainWire.Channel, keep, joined);
                    keepalives[guid] = now + KeepaliveInterval;
                    continue;
                }
                string payload = Build(scene, train, driver);
                if (payload == null) continue;
                bool reliable = joined || changed;
                for (int i = 0; i < peers.Count; i++) Session.Send(peers[i], TrainWire.Channel, payload, reliable);
                sentFlags[guid] = flags;
                if (moving || driver != 0) keepalives.Remove(guid);
                else keepalives[guid] = now + KeepaliveInterval;
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("TrainHost.Tick", e);
        }
    }

    public static void HostTook(string guid)
    {
        if (guid == null) return;
        ulong driver;
        if (!leases.TryGetValue(guid, out driver)) return;
        leases.Remove(guid);
        sentFlags.Remove(guid);
        Forget(guid);
        Session.Send(driver, TrainWire.Channel, TrainWire.Rel(GameRefs.AreaId, guid), true);
        CoopLog.Log.Info("coop: the host took the lever of train " + guid + ", guest " + driver + " lost the lease");
    }

    private static void RequestDrive(ulong from, TrainMsg m)
    {
        if (!Session.Peers.Contains(from)) return;
        if (!Shared.Admit(from, TrainWire.Channel, m.ReqId, m.Kind)) return;
        if (!Session.LocalInWorld || m.Scene != GameRefs.AreaId)
        {
            Shared.Answer(from, TrainWire.Channel, m.ReqId, m.Kind, TrainWire.DrvAck(m.ReqId, false, "Not in co-op yet"));
            return;
        }
        if (TrainController.instance == null || TrainController.instance.GetTrackTrain(m.Guid) == null)
        {
            Shared.Answer(from, TrainWire.Channel, m.ReqId, m.Kind, TrainWire.DrvAck(m.ReqId, false, "not found"));
            return;
        }
        ulong holder;
        if (leases.TryGetValue(m.Guid, out holder) && holder != from)
        {
            Shared.Answer(from, TrainWire.Channel, m.ReqId, m.Kind, TrainWire.DrvAck(m.ReqId, false, "Someone else is driving"));
            return;
        }
        leases[m.Guid] = from;
        sentFlags.Remove(m.Guid);
        Forget(m.Guid);
        Shared.Answer(from, TrainWire.Channel, m.ReqId, m.Kind, TrainWire.DrvAck(m.ReqId, true, ""));
        CoopLog.Log.Info("coop: guest " + from + " holds the lever of train " + m.Guid);
    }

    private static void Command(ulong from, TrainMsg m)
    {
        ulong holder;
        if (!leases.TryGetValue(m.Guid, out holder) || holder != from) return;
        if (m.Scene != GameRefs.AreaId) return;
        TrainController controller = TrainController.instance;
        TrackTrain train = controller == null ? null : controller.GetTrackTrain(m.Guid);
        if (train == null) return;
        float allowed;
        if (!Admit(m.Guid, m.Seconds, out allowed)) return;
        ApplyCmd(train, m.Cmd, allowed);
    }

    private static bool Admit(string guid, float seconds, out float allowed)
    {
        allowed = 0f;
        float start;
        float now = Time.unscaledTime;
        if (!cmdWindow.TryGetValue(guid, out start) || now - start >= 1f)
        {
            cmdWindow[guid] = now;
            cmdCount[guid] = 1;
            cmdSpent[guid] = 0f;
        }
        int count;
        if (!cmdCount.TryGetValue(guid, out count)) count = 0;
        if (count >= MaxCommands) return false;
        float spent;
        if (!cmdSpent.TryGetValue(guid, out spent)) spent = 0f;
        float cost = seconds > 0.001f ? seconds : Time.deltaTime;
        float left = MaxSeconds - spent;
        if (left <= 0.001f) return false;
        allowed = Mathf.Min(cost, left);
        cmdSpent[guid] = spent + allowed;
        cmdCount[guid] = count + 1;
        return true;
    }

    private static void Forget(string guid)
    {
        cmdWindow.Remove(guid);
        cmdCount.Remove(guid);
        cmdSpent.Remove(guid);
    }

    public static void ApplyCmd(TrackTrain train, string cmd, float seconds)
    {
        Minitrain minitrain = TrainRefs.MinitrainOf(train.GUID);
        float change = minitrain != null ? minitrain.changeTargetSpeed : 1f;
        float neutral = minitrain != null ? minitrain.changeNeutralSpeed : 10f;
        float dt = seconds > 0.001f ? seconds : Time.deltaTime;
        if (cmd == "F") train.SetSpeed(train.targetSpeed + change * dt);
        else if (cmd == "B") train.SetSpeed(train.targetSpeed - change * dt);
        else if (cmd == "N")
        {
            if (train.targetSpeed == 0f) return;
            float speed = Mathf.Lerp(train.targetSpeed, 0f, neutral * dt);
            if (speed > -0.01f && speed < 0.01f) speed = 0f;
            train.SetSpeed(speed);
        }
        else if (cmd == "K") train.ApplyBrakes(true);
        else if (cmd == "R") train.ApplyBrakes(false);
    }

    private static void Released(ulong from, TrainMsg m)
    {
        ulong holder;
        if (!leases.TryGetValue(m.Guid, out holder) || holder != from) return;
        leases.Remove(m.Guid);
        sentFlags.Remove(m.Guid);
        Forget(m.Guid);
        CoopLog.Log.Info("coop: guest " + from + " released the lever of train " + m.Guid);
    }

    private static void RequestStart(ulong from, TrainMsg m)
    {
        if (!Session.Peers.Contains(from)) return;
        if (!Shared.Admit(from, TrainWire.Channel, m.ReqId, m.Kind)) return;
        if (!Session.LocalInWorld || m.Scene != GameRefs.AreaId)
        {
            Shared.Answer(from, TrainWire.Channel, m.ReqId, m.Kind, TrainWire.StartAck(m.ReqId, false, "Not in co-op yet"));
            return;
        }
        Minitrain minitrain = TrainRefs.MinitrainOf(m.Guid);
        if (minitrain == null)
        {
            Shared.Answer(from, TrainWire.Channel, m.ReqId, m.Kind, TrainWire.StartAck(m.ReqId, false, "not found"));
            return;
        }
        Shared.Apply(() =>
        {
            if (m.On) minitrain.StartMinitrain();
            else minitrain.StopMinitrain();
        });
        sentFlags.Remove(m.Guid);
        Shared.Answer(from, TrainWire.Channel, m.ReqId, m.Kind, TrainWire.StartAck(m.ReqId, true, ""));
    }

    public static void SwitchChanged(MinitrainSwitch entry, int index)
    {
        try
        {
            if (!Active || Shared.Applying || entry == null) return;
            if (Occupied(entry)) return;
            string scene = GameRefs.AreaId;
            if (scene == null || string.IsNullOrEmpty(entry.GUID)) return;
            BroadcastSwitch(scene, entry.GUID, index);
        }
        catch (Exception e)
        {
            CoopLog.Once("TrainHost.SwitchChanged", e);
        }
    }

    private static void BroadcastSwitch(string scene, string guid, int index)
    {
        string payload = TrainWire.Swt(scene, guid, index);
        List<ulong> peers = Session.PeersInArea(scene);
        for (int i = 0; i < peers.Count; i++) Session.Send(peers[i], TrainWire.Channel, payload, true);
    }

    private static bool Occupied(MinitrainSwitch entry)
    {
        Track_Segment segment = entry.trackSegment;
        if (segment == null) return false;
        if (!entry.isReversed) return segment.occupied;
        return segment.previousSegment != null && segment.previousSegment.occupied;
    }

    private static void RequestSwitch(ulong from, TrainMsg m)
    {
        if (!Session.Peers.Contains(from)) return;
        if (!Shared.Admit(from, TrainWire.Channel, m.ReqId, m.Kind)) return;
        if (!Session.LocalInWorld || m.Scene != GameRefs.AreaId)
        {
            Shared.Answer(from, TrainWire.Channel, m.ReqId, m.Kind, TrainWire.SwtAck(m.ReqId, false, "Not in co-op yet"));
            return;
        }
        MinitrainSwitch entry = TrainRefs.SwitchOf(m.Guid);
        if (entry == null)
        {
            Shared.Answer(from, TrainWire.Channel, m.ReqId, m.Kind, TrainWire.SwtAck(m.ReqId, false, "not found"));
            return;
        }
        int branches = TrainRefs.BranchCount(entry);
        if (m.Index < 0 || (branches > 0 && m.Index >= branches))
        {
            Shared.Answer(from, TrainWire.Channel, m.ReqId, m.Kind, TrainWire.SwtAck(m.ReqId, false, "bad switch index"));
            return;
        }
        if (Occupied(entry))
        {
            Shared.Answer(from, TrainWire.Channel, m.ReqId, m.Kind, TrainWire.SwtAck(m.ReqId, false, "the switch is in use"));
            return;
        }
        if (entry.currentBranchIndex != m.Index)
        {
            Shared.Apply(() => entry.ChangeTrack(m.Index));
            BroadcastSwitch(GameRefs.AreaId, entry.GUID, m.Index);
        }
        Shared.Answer(from, TrainWire.Channel, m.ReqId, m.Kind, TrainWire.SwtAck(m.ReqId, true, ""));
    }
    private static void PruneLeases(string scene, float now)
    {
        if (now < nextPrune) return;
        nextPrune = now + PruneInterval;
        done.Clear();
        foreach (KeyValuePair<string, ulong> pair in leases)
        {
            if (!Session.Peers.Contains(pair.Value) || Session.AreaOf(pair.Value) != scene) done.Add(pair.Key);
        }
        for (int i = 0; i < done.Count; i++)
        {
            string guid = done[i];
            ulong driver;
            if (!leases.TryGetValue(guid, out driver)) continue;
            leases.Remove(guid);
            sentFlags.Remove(guid);
            Forget(guid);
            Session.SendAll(TrainWire.Channel, TrainWire.Rel(scene, guid), true);
            CoopLog.Log.Info("coop: the train lease of " + driver + " on " + guid + " was cleared, the driver left the scene or the lobby");
        }
    }

    private static ulong DriverOf(string guid, TrackTrain train)
    {
        ulong holder;
        if (leases.TryGetValue(guid, out holder)) return holder;
        Minitrain minitrain = TrainRefs.MinitrainOf(guid);
        return minitrain != null && minitrain.playerUsing ? Session.Me : 0UL;
    }

    private static bool IsMoving(TrackTrain train)
    {
        return train.active || train.currentSpeed != 0f || On(train);
    }

    private static string Flags(TrackTrain train, ulong driver)
    {
        return (On(train) ? "1" : "0") + (train.braking ? "1" : "0") + driver.ToString();
    }

    private static bool On(TrackTrain train)
    {
        Minitrain minitrain = TrainRefs.MinitrainOf(train.GUID);
        return minitrain != null ? minitrain.isOn : train.active;
    }

    private static string Build(string scene, TrackTrain train, ulong driver)
    {
        TrackTrain.TrainAxle front = train.frontAxle;
        TrackTrain.TrainAxle back = train.backAxle;
        if (front == null || back == null) return null;
        string frontGuid = SegmentGuid(front);
        string backGuid = SegmentGuid(back);
        if (frontGuid == null || backGuid == null) return null;
        bool on = On(train);
        return TrainWire.Snapshot(scene, train.GUID, on, train.braking, train.currentSpeed, train.targetSpeed,
            frontGuid, front.positionOnSegment, Dir(front),
            backGuid, back.positionOnSegment, Dir(back), driver);
    }

    private static string SegmentGuid(TrackTrain.TrainAxle axle)
    {
        Track_Segment.TrackSegmentReference reference = axle.currentSegment;
        if (reference == null) return null;
        Track_Segment segment = reference.segment;
        return segment == null || string.IsNullOrEmpty(segment.GUID) ? null : segment.GUID;
    }

    private static int Dir(TrackTrain.TrainAxle axle)
    {
        return axle.directionOnSegment == Track_Segment.Direction.Backward ? 1 : 0;
    }

    private static string Peers(List<ulong> peers)
    {
        string text = "";
        for (int i = 0; i < peers.Count; i++) text += peers[i].ToString() + ",";
        return text;
    }
}
