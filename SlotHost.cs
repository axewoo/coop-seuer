using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;

internal static class SlotHost
{
    private sealed class Req
    {
        public SlotMachineGameplay Machine;
        public int StakeIndex;
        public ulong Requester;
        public int ReqId;
    }

    private static Req pending;
    private static int spins;

    public static void Init()
    {
    }

    public static void Reset()
    {
        pending = null;
    }

    public static bool Busy
    {
        get { return pending != null; }
    }

    public static void Handle(ulong from, SlotMsg m)
    {
        Req req = null;
        try
        {
            if (m.Kind != SlotWire.ReqSpin) return;
            if (!Session.Peers.Contains(from)) return;
            if (!Shared.Admit(from, SlotWire.Channel, m.ReqId, SlotWire.ReqSpin)) return;
            string reason = Refuse(m);
            if (reason.Length > 0)
            {
                Shared.Answer(from, SlotWire.Channel, m.ReqId, SlotWire.ReqSpin, SlotWire.Ack(m.ReqId, false, reason));
                CoopLog.Log.Debug("slot.host", "refused the spin of " + Session.DisplayName(from) + ": " + reason);
                return;
            }
            SlotMachineGameplay machine = SlotWire.Find(m.Scene, m.Key);
            if (machine == null)
            {
                Shared.Answer(from, SlotWire.Channel, m.ReqId, SlotWire.ReqSpin, SlotWire.Ack(m.ReqId, false, "not found"));
                return;
            }
            req = new Req();
            req.Machine = machine;
            req.StakeIndex = m.StakeIndex;
            req.Requester = from;
            req.ReqId = m.ReqId;
            pending = req;
            if (SlotRound.Begin(machine, m.StakeIndex, false, SlotRound.LineOf(machine)) == null)
            {
                pending = null;
                req = null;
                Shared.Answer(from, SlotWire.Channel, m.ReqId, SlotWire.ReqSpin, SlotWire.Ack(m.ReqId, false, "bad stake"));
                return;
            }
            Shared.Apply(delegate { machine.Interact(); });
        }
        catch (Exception e)
        {
            CoopLog.Once("SlotHost.Handle", e);
        }
        finally
        {
            if (req != null && ReferenceEquals(pending, req)) RefuseUnstarted(req);
        }
    }

    private static void RefuseUnstarted(Req req)
    {
        pending = null;
        try
        {
            SlotRound.Abort(req.Machine);
        }
        catch (Exception e)
        {
            CoopLog.Once("SlotHost.Abort", e);
        }
        try
        {
            Shared.Answer(req.Requester, SlotWire.Channel, req.ReqId, SlotWire.ReqSpin, SlotWire.Ack(req.ReqId, false, "the machine did not start"));
            CoopLog.Log.Info("slot.host: the spin of " + Session.DisplayName(req.Requester) + " did not start, the machine was put back and the request was refused");
        }
        catch (Exception e)
        {
            CoopLog.Once("SlotHost.Refusal", e);
        }
    }

    private static string Refuse(SlotMsg m)
    {
        if (pending != null) return "busy";
        if (m.Scene != GameRefs.AreaId) return "not in the host world";
        if (!SlotRound.Ready) return "the slot machine is not shared on this build";
        SlotMachineGameplay machine = SlotWire.Find(m.Scene, m.Key);
        if (machine == null) return "not found";
        if (SlotRound.OtherRoundInProgress(machine)) return "another slot machine in this world is still mid-round";
        if (SlotRound.StateOf(machine) != "Standby") return "not standby";
        if (SlotRound.EarningsOf(machine) > 0) return "the host has an unredeemed win on this machine";
        if (SlotRound.CanDoubleOf(machine)) return "the host can still double the win";
        if (SlotRound.LockingInPlay(machine)) return "the host has a lock on this machine";
        int[] stakes = SlotRound.Stakes(machine);
        if (stakes == null || m.StakeIndex < 0 || m.StakeIndex >= stakes.Length) return "bad stake";
        return "";
    }

    public static void OnOwnSpinStarted(SlotMachineGameplay machine)
    {
        int r0;
        int r1;
        int r2;
        WarnIfLocked(SlotRound.ReadPredetermined(machine, out r0, out r1, out r2));
        Broadcast(machine, r0, r1, r2, Session.Me, SlotRound.StakesIndexOf(machine));
    }

    public static void OnRemoteSpinStarted(SlotMachineGameplay machine)
    {
        Req req = pending;
        if (req == null || req.Machine != machine) return;
        pending = null;
        int r0;
        int r1;
        int r2;
        WarnIfLocked(SlotRound.ReadPredetermined(machine, out r0, out r1, out r2));
        Broadcast(machine, r0, r1, r2, req.Requester, req.StakeIndex);
        Shared.Answer(req.Requester, SlotWire.Channel, req.ReqId, SlotWire.ReqSpin, SlotWire.Ack(req.ReqId, true, ""));
    }

    private static void WarnIfLocked(bool exact)
    {
        if (!exact) CoopLog.Once("SlotHost.locked", "a spin with a locked cylinder was shared, the locked cylinder may land on a different symbol on the other machine");
    }

    private static void Broadcast(SlotMachineGameplay machine, int r0, int r1, int r2, ulong player, int stakeIndex)
    {
        string scene = GameRefs.AreaId;
        string key = SlotWire.KeyOf(machine);
        if (scene == null || key == null)
        {
            CoopLog.Once("SlotHost.key", "a slot machine could not be named for the wire, the spin stays local");
            return;
        }
        spins++;
        string text = SlotWire.Spin(scene, key, spins, player, stakeIndex, SlotRound.LineOf(machine), r0, r1, r2);
        List<ulong> peers = Session.PeersInArea(scene);
        for (int i = 0; i < peers.Count; i++) Session.Send(peers[i], SlotWire.Channel, text, true);
        CoopLog.Log.Info("slot.host: spin " + spins + " on " + key + " shared with " + peers.Count + " peer(s), values " + r0 + " " + r1 + " " + r2);
    }
}
