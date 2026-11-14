using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

internal static class TrainGuest
{
    private const float ResolveRetry = 1f;
    private const float BlendSeconds = 0.2f;
    private const float SnapDistance = 2f;
    private const float LagAllowance = 0.35f;
    private const float CommandInterval = 0.1f;
    private const int HopLimit = 3;
    private const int ChairHops = 16;
    private const float RequestTimeout = 1.5f;
    private const int MaxAttempts = 3;
    private static readonly Dictionary<string, Remote> remotes = new Dictionary<string, Remote>();
    private static readonly List<string> gone = new List<string>();
    private static MethodInfo position;
    private static FieldInfo driving;
    private static MethodInfo keyNeutral;
    private static MethodInfo keyBrake;
    private static MethodInfo showSpeed;
    private static MethodInfo changeLever;
    private static FieldInfo needle;
    private static FieldInfo lever;
    private static readonly FieldInfo chairFlag = AccessTools.Field(typeof(InteractableChair), "disableStopSit");
    private static Pending drvPending;
    private static Pending startPending;
    private static Pending swtPending;
    private static Minitrain usingTrain;
    private static string leasedGuid;
    private static bool lastBrake = true;
    private static float nextCommand;
    private static float cmdSeconds;

    private sealed class Remote
    {
        public TrainMsg Last;
        public TrackTrain Train;
        public float TargetFront;
        public float TargetBack;
        public bool Snap;
        public float NextResolve;
        public float Applied = float.NaN;
        public float AppliedTarget = float.NaN;
        public float FrontLag;
        public float BackLag;
    }

    private sealed class Pending
    {
        public int ReqId;
        public string Kind;
        public string Guid;
        public string Text;
        public int Attempts;
        public float SentAt;
    }

    public static bool InSharedScene
    {
        get
        {
            if (!Session.IsInLobby || Session.IsHost || Session.HostId == 0 || !GuestSession.InHostWorld) return false;
            string area = GameRefs.AreaId;
            return area != null && Session.AreaOf(Session.HostId) == area;
        }
    }

    public static void Init()
    {
        position = AccessTools.Method(typeof(TrackTrain), "CalculatePositionAndRotation", new[] { typeof(bool) });
        driving = AccessTools.Field(typeof(TrackTrain.TrainData), "_drivingLocomotive");
        keyNeutral = AccessTools.Method(typeof(Minitrain), "GetNeutralKey");
        keyBrake = AccessTools.Method(typeof(Minitrain), "GetBrakeKey");
        showSpeed = AccessTools.Method(typeof(Minitrain), "ShowSpeed");
        changeLever = AccessTools.Method(typeof(Minitrain), "ChangeLever");
        needle = AccessTools.Field(typeof(Minitrain), "needle");
        lever = AccessTools.Field(typeof(Minitrain), "lever");
    }

    public static void Reset()
    {
        remotes.Clear();
        gone.Clear();
        drvPending = null;
        startPending = null;
        swtPending = null;
        usingTrain = null;
        leasedGuid = null;
        lastBrake = true;
        nextCommand = 0f;
        cmdSeconds = 0f;
    }

    public static void OnSceneChanged()
    {
        Reset();
        TrainRefs.Reset();
    }

    public static void Handle(ulong from, TrainMsg m)
    {
        try
        {
            if (m.Kind == "TRN") OnSnapshot(m);
            else if (m.Kind == "DRVACK") OnDrvAck(m);
            else if (m.Kind == "STARTACK") OnStartAck(m);
            else if (m.Kind == "SWT") OnSwitch(m);
            else if (m.Kind == "SWTACK") OnSwtAck(m);
            else if (m.Kind == "REL") OnRel(m);
        }
        catch (Exception e)
        {
            CoopLog.Once("TrainGuest.Handle", e);
        }
    }

    public static bool RefuseUse(Minitrain minitrain)
    {
        if (!InSharedScene || minitrain == null) return false;
        ulong driver = DriverOf(TrainRefs.TrainGuidOf(minitrain));
        return driver != 0 && driver != Session.Me;
    }

    public static void OnUse(Minitrain minitrain, bool newstate)
    {
        if (!InSharedScene || minitrain == null) return;
        if (newstate) RequestLease(minitrain);
        else Release(minitrain);
    }

    public static void RequestStart(Minitrain minitrain, bool on)
    {
        if (!InSharedScene || minitrain == null) return;
        string guid = TrainRefs.TrainGuidOf(minitrain);
        if (guid == null) return;
        int id = Shared.NextReqId();
        Pending pending = new Pending();
        pending.ReqId = id;
        pending.Kind = "REQSTART";
        pending.Guid = guid;
        pending.Text = TrainWire.ReqStart(id, GameRefs.AreaId, guid, on);
        pending.Attempts = 1;
        pending.SentAt = Time.unscaledTime;
        startPending = pending;
        Session.Send(Session.HostId, TrainWire.Channel, pending.Text, true);
    }

    public static void Tick()
    {
        try
        {
            if (!InSharedScene)
            {
                if (remotes.Count > 0 || leasedGuid != null || drvPending != null || usingTrain != null) DropAll();
                return;
            }
            float now = Time.unscaledTime;
            string scene = GameRefs.AreaId;
            gone.Clear();
            foreach (KeyValuePair<string, Remote> pair in remotes)
            {
                Remote remote = pair.Value;
                if (remote.Last.Scene != scene)
                {
                    gone.Add(pair.Key);
                    continue;
                }
                if (remote.Train == null && now >= remote.NextResolve)
                {
                    remote.NextResolve = now + ResolveRetry;
                    TrainController controller = TrainController.instance;
                    remote.Train = controller == null ? null : controller.GetTrackTrain(pair.Key);
                    SnapNow(remote);
                }
                if (remote.Train == null) continue;
                Steer(remote);
            }
            for (int i = 0; i < gone.Count; i++) remotes.Remove(gone[i]);
            Retry(drvPending, now);
            Retry(startPending, now);
            Retry(swtPending, now);
            Commands(now);
        }
        catch (Exception e)
        {
            CoopLog.Once("TrainGuest.Tick", e);
        }
    }

    private static void OnSnapshot(TrainMsg m)
    {
        if (!InSharedScene) return;
        if (!TrainWire.InScene(m.Scene)) return;
        Remote remote;
        if (!remotes.TryGetValue(m.Guid, out remote))
        {
            remote = new Remote();
            remote.Snap = true;
            TrainController controller = TrainController.instance;
            remote.Train = controller == null ? null : controller.GetTrackTrain(m.Guid);
            remotes[m.Guid] = remote;
        }
        remote.Last = m;
        remote.TargetFront = m.FrontPos;
        remote.TargetBack = m.BackPos;
        if (leasedGuid != null && m.Guid == leasedGuid && m.Driver != 0 && m.Driver != Session.Me)
        {
            DropLease("Someone else took the train lever.");
        }
    }

    private static void OnDrvAck(TrainMsg m)
    {
        if (drvPending == null || drvPending.ReqId != m.ReqId) return;
        Pending done = drvPending;
        drvPending = null;
        if (m.Ok)
        {
            leasedGuid = done.Guid;
            lastBrake = true;
            nextCommand = 0f;
            return;
        }
        leasedGuid = null;
        Leave(done.Guid);
        CoopNotice.Show("train|refused", "Train: " + (m.Reason == null || m.Reason.Length == 0 ? "refused" : m.Reason), 3f);
    }

    private static void OnStartAck(TrainMsg m)
    {
        if (startPending == null || startPending.ReqId != m.ReqId) return;
        startPending = null;
        if (m.Ok) return;
        CoopNotice.Show("train|start", "Train: " + (m.Reason == null || m.Reason.Length == 0 ? "refused" : m.Reason), 3f);
    }

    private static void OnRel(TrainMsg m)
    {
        if (m.Guid != leasedGuid) return;
        DropLease("The host took the train lever.");
    }

    private static void DropLease(string notice)
    {
        string guid = leasedGuid;
        leasedGuid = null;
        drvPending = null;
        lastBrake = true;
        Leave(guid);
        CoopNotice.Show("train|lease", notice, 3f);
    }

    private static void RequestLease(Minitrain minitrain)
    {
        string guid = TrainRefs.TrainGuidOf(minitrain);
        if (guid == null) return;
        usingTrain = minitrain;
        if (leasedGuid == guid) return;
        if (drvPending != null && drvPending.Guid == guid) return;
        int id = Shared.NextReqId();
        Pending pending = new Pending();
        pending.ReqId = id;
        pending.Kind = "REQDRV";
        pending.Guid = guid;
        pending.Text = TrainWire.ReqDrv(id, GameRefs.AreaId, guid);
        pending.Attempts = 1;
        pending.SentAt = Time.unscaledTime;
        drvPending = pending;
        Session.Send(Session.HostId, TrainWire.Channel, pending.Text, true);
    }

    private static void Release(Minitrain minitrain)
    {
        usingTrain = null;
        if (drvPending != null) drvPending = null;
        string guid = leasedGuid;
        if (guid == null) return;
        leasedGuid = null;
        lastBrake = true;
        Session.Send(Session.HostId, TrainWire.Channel, TrainWire.Rel(GameRefs.AreaId, guid), true);
    }

    private static void DropAll()
    {
        string guid = leasedGuid;
        if (guid == null && usingTrain != null) guid = TrainRefs.TrainGuidOf(usingTrain);
        bool leased = leasedGuid != null;
        Reset();
        if (guid == null) return;
        Leave(guid);
        if (!leased || Session.HostId == 0 || GameRefs.AreaId == null) return;
        Session.Send(Session.HostId, TrainWire.Channel, TrainWire.Rel(GameRefs.AreaId, guid), true);
    }

    private static void Leave(string guid)
    {
        Minitrain minitrain = TrainRefs.MinitrainOf(guid);
        if (minitrain == null || !minitrain.playerUsing) return;
        minitrain.playerUsing = false;
        TrainController controller = TrainController.instance;
        if (controller != null) controller.SetCurrentMinitrain(null);
        StandUp();
    }

    private static void StandUp()
    {
        try
        {
            if (!InteractableChair.PlayerIsSitting) return;
            InteractObjects objects = InteractObjects.instance;
            InteractableChair chair = objects != null ? objects.savedInteractable as InteractableChair : null;
            if (chair == null) chair = ChairUnderPlayer();
            if (chair == null)
            {
                CoopLog.Once("TrainGuest.StandUp", "coop: no seat chair found while standing the guest up, the seat was left as it is");
                return;
            }
            LeaveSit(chair);
        }
        catch (Exception e)
        {
            CoopLog.Once("TrainGuest.StandUp", e);
        }
    }

    private static InteractableChair ChairUnderPlayer()
    {
        PlayerLocator locator = PlayerLocator.instance;
        if (locator == null || locator.transform == null) return null;
        Transform parent = locator.transform.parent;
        for (int i = 0; i < ChairHops && parent != null; i++)
        {
            InteractableChair chair = parent.GetComponent<InteractableChair>();
            if (chair != null) return chair;
            parent = parent.parent;
        }
        return null;
    }

    private static void LeaveSit(InteractableChair chair)
    {
        object old = ChairFlag(chair);
        if (old is bool && !(bool)old) SetChairFlag(chair, false);
        try
        {
            chair.StopSit();
        }
        finally
        {
            if (old is bool) SetChairFlag(chair, (bool)old);
        }
    }

    private static object ChairFlag(InteractableChair chair)
    {
        return chairFlag != null ? chairFlag.GetValue(chair) : null;
    }

    private static void SetChairFlag(InteractableChair chair, bool value)
    {
        if (chairFlag != null) chairFlag.SetValue(chair, value);
    }

    public static void RequestSwitch(MinitrainSwitch entry, int index)
    {
        if (!InSharedScene || entry == null || string.IsNullOrEmpty(entry.GUID)) return;
        int branches = TrainRefs.BranchCount(entry);
        if (index < 0 || (branches > 0 && index >= branches)) return;
        if (swtPending != null && swtPending.Guid == entry.GUID && swtPending.Kind == "REQSWT") return;
        int id = Shared.NextReqId();
        Pending pending = new Pending();
        pending.ReqId = id;
        pending.Kind = "REQSWT";
        pending.Guid = entry.GUID;
        pending.Text = TrainWire.ReqSwt(id, GameRefs.AreaId, entry.GUID, index);
        pending.Attempts = 1;
        pending.SentAt = Time.unscaledTime;
        swtPending = pending;
        Session.Send(Session.HostId, TrainWire.Channel, pending.Text, true);
    }

    private static void OnSwitch(TrainMsg m)
    {
        if (!InSharedScene || !TrainWire.InScene(m.Scene)) return;
        MinitrainSwitch entry = TrainRefs.SwitchOf(m.Guid);
        if (entry == null || entry.currentBranchIndex == m.Index) return;
        int branches = TrainRefs.BranchCount(entry);
        if (m.Index < 0 || (branches > 0 && m.Index >= branches)) return;
        Shared.Apply(() => entry.ChangeTrackForce(m.Index));
    }

    private static void OnSwtAck(TrainMsg m)
    {
        if (swtPending == null || swtPending.ReqId != m.ReqId) return;
        swtPending = null;
        if (m.Ok) return;
        CoopNotice.Show("train|switch", "Switch: " + (m.Reason == null || m.Reason.Length == 0 ? "refused" : m.Reason), 3f);
    }
    private static void Retry(Pending pending, float now)
    {
        if (pending == null || now - pending.SentAt < RequestTimeout) return;
        if (pending.Attempts >= MaxAttempts)
        {
            if (pending == drvPending)
            {
                drvPending = null;
                Leave(pending.Guid);
            }
            if (pending == startPending) startPending = null;
            if (pending == swtPending) swtPending = null;
            CoopNotice.Show("train|timeout", "Train: the host did not answer.", 3f);
            return;
        }
        pending.Attempts++;
        pending.SentAt = now;
        Session.Send(Session.HostId, TrainWire.Channel, pending.Text, true);
    }

    private static void Commands(float now)
    {
        if (leasedGuid == null || usingTrain == null)
        {
            cmdSeconds = 0f;
            return;
        }
        if (!usingTrain.playerUsing)
        {
            Release(usingTrain);
            return;
        }
        cmdSeconds += Time.deltaTime;
        if (now < nextCommand) return;
        nextCommand = now + CommandInterval;
        bool brake = Held(usingTrain, keyBrake);
        if (brake != lastBrake)
        {
            SendCmd(brake ? "K" : "R", false);
            lastBrake = brake;
        }
        string dir = Wanted(usingTrain);
        if (dir.Length == 0)
        {
            cmdSeconds = 0f;
            return;
        }
        SendCmd(dir, true);
    }

    private static string Wanted(Minitrain minitrain)
    {
        TrackTrain train = minitrain.GetTrain();
        if (Held(minitrain, keyNeutral) && train != null && train.targetSpeed != 0f) return "N";
        if (Forward()) return "F";
        if (Backward()) return "B";
        return "";
    }

    private static bool Forward()
    {
        return Action("Forward") || Input.GetAxis("JoystickAxis7") > 0f;
    }

    private static bool Backward()
    {
        return Action("Backward") || Input.GetAxis("JoystickAxis7") < 0f;
    }

    private static void SendCmd(string cmd, bool consume)
    {
        float seconds = cmdSeconds;
        if (seconds < 0.001f) seconds = CommandInterval;
        if (seconds > TrainWire.MaxCommandSeconds) seconds = TrainWire.MaxCommandSeconds;
        if (consume) cmdSeconds = 0f;
        Session.Send(Session.HostId, TrainWire.Channel, TrainWire.Drv(GameRefs.AreaId, leasedGuid, cmd, seconds), false);
    }

    private static bool Held(Minitrain minitrain, MethodInfo getter)
    {
        if (getter == null || minitrain == null) return false;
        return Action(getter.Invoke(minitrain, null) as string);
    }

    private static bool Action(string name)
    {
        if (name == null || InputManager.instance == null) return false;
        return InputManager.instance.GetKey(name);
    }

    public static ulong DriverOf(string guid)
    {
        Remote remote;
        if (guid == null || !remotes.TryGetValue(guid, out remote) || remote.Last == null) return 0;
        return remote.Last.Driver;
    }

    private static void Steer(Remote remote)
    {
        TrackTrain train = remote.Train;
        TrainMsg m = remote.Last;
        TrainController controller = TrainController.instance;
        Track_Segment front = controller == null ? null : controller.GetSegment(m.FrontSeg);
        Track_Segment back = controller == null ? null : controller.GetSegment(m.BackSeg);
        Shared.Apply(() =>
        {
            if (train.active != m.On) train.Toggle(m.On);
            if (train.braking != m.Braking) train.ApplyBrakes(m.Braking);
            if (remote.Applied != m.Speed)
            {
                train.SetSpeedInstantly(m.Speed);
                remote.Applied = m.Speed;
            }
            if (remote.AppliedTarget != m.Target)
            {
                train.SetSpeed(m.Target);
                remote.AppliedTarget = m.Target;
            }
            if (front != null) SetAxle(train, train.frontAxle, front, m.FrontDir, remote, true);
            if (back != null) SetAxle(train, train.backAxle, back, m.BackDir, remote, false);
            Lights(train, m);
            if (m.On) Gauges(train);
            if (position != null && !Drives(train)) position.Invoke(train, new object[] { false });
            remote.Snap = false;
        });
    }

    private static bool Drives(TrackTrain train)
    {
        if (driving == null || train.train == null) return true;
        object value = driving.GetValue(train.train);
        return value != null && object.ReferenceEquals(value, train);
    }

    private static void SetAxle(TrackTrain train, TrackTrain.TrainAxle axle, Track_Segment segment, int dir, Remote remote, bool front)
    {
        if (axle == null) return;
        Track_Segment.TrackSegmentReference reference = axle.currentSegment;
        Track_Segment current = reference == null ? null : reference.segment;
        bool changed = !object.ReferenceEquals(current, segment);
        bool joint = changed && Adjacent(current, segment);
        if (changed)
        {
            axle.currentSegment = new Track_Segment.TrackSegmentReference(segment);
            if (!joint) SnapNow(remote);
        }
        Track_Segment.Direction wanted = dir == 1 ? Track_Segment.Direction.Backward : Track_Segment.Direction.Forward;
        if (axle.directionOnSegment != wanted)
        {
            axle.directionOnSegment = wanted;
            if (!joint) SnapNow(remote);
        }
        float target = front ? remote.TargetFront : remote.TargetBack;
        float limit = SnapDistance + Mathf.Abs(remote.Last.Speed) * LagAllowance;
        if (joint)
        {
            float lag = front ? remote.FrontLag : remote.BackLag;
            axle.positionOnSegment = Mathf.Max(0f, target - lag);
        }
        float error = target - axle.positionOnSegment;
        bool snapped = remote.Snap || Mathf.Abs(error) > limit || Still(remote);
        if (snapped) axle.positionOnSegment = target;
        else axle.positionOnSegment += error * Mathf.Clamp01(Time.deltaTime / BlendSeconds);
        float owed = snapped ? 0f : Mathf.Clamp(error, -limit, limit);
        if (front) remote.FrontLag = owed;
        else remote.BackLag = owed;
    }

    private static void SnapNow(Remote remote)
    {
        remote.Snap = true;
        remote.FrontLag = 0f;
        remote.BackLag = 0f;
    }

    private static bool Adjacent(Track_Segment from, Track_Segment to)
    {
        if (from == null || to == null) return false;
        Track_Segment segment = from;
        for (int i = 0; i < HopLimit && segment != null; i++)
        {
            Track_Segment.SegmentBranch branch = segment.activeBranch;
            segment = branch == null ? null : branch.nextSegment;
            if (object.ReferenceEquals(segment, to)) return true;
        }
        segment = from;
        for (int i = 0; i < HopLimit && segment != null; i++)
        {
            segment = segment.previousSegment;
            if (object.ReferenceEquals(segment, to)) return true;
        }
        return false;
    }

    private static bool Still(Remote remote)
    {
        return remote.Last.Speed == 0f;
    }

    private static void Gauges(TrackTrain train)
    {
        Minitrain minitrain = TrainRefs.MinitrainOf(train.GUID);
        if (minitrain == null) return;
        if (showSpeed != null && needle != null && needle.GetValue(minitrain) != null) showSpeed.Invoke(minitrain, new object[] { train.currentSpeed, 0f, 40f });
        if (changeLever != null && lever != null && lever.GetValue(minitrain) != null) changeLever.Invoke(minitrain, new object[] { train.targetSpeed, train.maxReverseSpeed, train.maxSpeed });
    }
    private static void Lights(TrackTrain train, TrainMsg m)
    {
        Minitrain minitrain = TrainRefs.MinitrainOf(train.GUID);
        if (minitrain == null || minitrain.isOn == m.On) return;
        minitrain.isOn = m.On;
        minitrain.ToggleLightsExternal(m.On);
    }
}
