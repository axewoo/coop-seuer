using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;

internal static class SlotGuest
{
    private sealed class Pending
    {
        public int ReqId;
        public string Text;
        public SlotMachineGameplay Machine;
        public float SentAt;
        public int Attempts;
    }

    private const float AnswerTimeout = 10f;
    private const int MaxSends = 3;
    private const int MaxSpins = 32;
    private static readonly List<int> doneSpins = new List<int>();
    private static Pending pending;
    private static float noticeAt;
    private static string noticeText = "";

    public static void Init()
    {
    }

    public static void Reset()
    {
        pending = null;
        doneSpins.Clear();
    }

    public static void OnSceneChanged()
    {
        pending = null;
    }

    public static bool Waiting
    {
        get { return pending != null; }
    }

    public static bool RequestSpin(SlotMachineGameplay machine)
    {
        if (pending != null)
        {
            Notice("The slot machine is still waiting for the host.");
            return false;
        }
        if (!SlotRound.Ready)
        {
            Notice("The slot machine is not shared on this build.");
            return false;
        }
        int[] stakes = SlotRound.Stakes(machine);
        int index = SlotRound.StakesIndexOf(machine);
        if (stakes == null || index < 0 || index >= stakes.Length)
        {
            Notice("The slot machine is not shared on this build.");
            return false;
        }
        int stake = stakes[index];
        if (Money.instance == null || Money.instance.OCMoneyAmount < stake)
        {
            Notice("You cannot afford the " + stake + " coin stake.");
            return false;
        }
        string scene = GameRefs.AreaId;
        string key = SlotWire.KeyOf(machine);
        if (scene == null || key == null)
        {
            Notice("This slot machine is not shared in co-op.");
            return false;
        }
        int reqId = Shared.NextReqId();
        string text = SlotWire.Req(reqId, scene, key, index);
        if (!Session.Send(Session.HostId, SlotWire.Channel, text, true))
        {
            Notice("The host could not be reached.");
            return false;
        }
        Pending ask = new Pending();
        ask.ReqId = reqId;
        ask.Text = text;
        ask.Machine = machine;
        ask.SentAt = Time.unscaledTime;
        ask.Attempts = 1;
        pending = ask;
        Notice("Waiting for the host to spin.");
        return false;
    }

    public static bool RaiseStakes(SlotMachineGameplay machine)
    {
        if (!GuestSession.InHostWorld) return true;
        try
        {
            if (!SlotRound.Ready)
            {
                Notice("The slot machine is not shared on this build.");
                return false;
            }
            int[] stakes = SlotRound.Stakes(machine);
            int index = SlotRound.StakesIndexOf(machine);
            if (stakes == null || stakes.Length == 0) return false;
            int money = Money.instance == null ? 0 : Money.instance.OCMoneyAmount;
            int next = -1;
            for (int step = 1; step <= stakes.Length && next < 0; step++)
            {
                int candidate = (index + step) % stakes.Length;
                if (stakes[candidate] <= money) next = candidate;
            }
            if (next < 0)
            {
                Notice("You cannot afford any stake on this machine.");
                return false;
            }
            SlotRound.SetStakes(machine, next);
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("SlotGuest.RaiseStakes", e);
            return false;
        }
    }

    public static void RefuseUnshared()
    {
        Notice("The slot machine is not shared on this build.");
    }

    public static void RefuseDoubling()
    {
        Notice("Doubling is not shared in co-op, the stake and the payout are yours alone.");
    }

    public static void Handle(ulong from, SlotMsg m)
    {
        try
        {
            if (from != Session.HostId || !GuestSession.InHostWorld) return;
            if (m.Kind == SlotWire.SpinAck) OnAck(m);
            else if (m.Kind == SlotWire.SpinKind) OnSpin(m);
        }
        catch (Exception e)
        {
            CoopLog.Once("SlotGuest.Handle", e);
        }
    }

    public static void Tick()
    {
        try
        {
            if (pending == null) return;
            if (Time.unscaledTime - pending.SentAt < AnswerTimeout) return;
            if (pending.Attempts >= MaxSends)
            {
                pending = null;
                Notice("The host did not answer, nothing changed.");
                return;
            }
            pending.Attempts++;
            pending.SentAt = Time.unscaledTime;
            Session.Send(Session.HostId, SlotWire.Channel, pending.Text, true);
        }
        catch (Exception e)
        {
            CoopLog.Once("SlotGuest.Tick", e);
        }
    }

    private static void OnAck(SlotMsg m)
    {
        if (pending == null || pending.ReqId != m.ReqId) return;
        pending = null;
        if (!m.Ok) Notice("The host refused the spin: " + m.Reason);
    }

    private static void OnSpin(SlotMsg m)
    {
        if (Already(m.SpinId))
        {
            CoopLog.Log.Debug("slot.guest", "ignored a repeated spin " + m.SpinId);
            return;
        }
        Remember(m.SpinId);
        bool payer = m.Player == Session.Me;
        if (pending != null && payer) pending = null;
        SlotMachineGameplay machine = SlotWire.Find(m.Scene, m.Key);
        if (machine == null)
        {
            CoopLog.Once("SlotGuest.spin", "a spin for " + m.Key + " arrived with no slot machine in this scene");
            return;
        }
        if (!SlotRound.Ready) return;
        if (SlotRound.StateOf(machine) != "Standby")
        {
            CoopLog.Once("SlotGuest.busy", "a spin arrived while the local slot machine was busy, the values are dropped");
            return;
        }
        int[] stakes = SlotRound.Stakes(machine);
        if (stakes == null || m.StakeIndex < 0 || m.StakeIndex >= stakes.Length)
        {
            CoopLog.Once("SlotGuest.stake", "a spin named a stake this machine does not have");
            return;
        }
        SlotRound.Ctx ctx = SlotRound.Begin(machine, m.StakeIndex, payer, m.Line);
        if (ctx == null)
        {
            CoopLog.Once("SlotGuest.begin", "a spin arrived and the local machine did not take the round, nothing was charged");
            return;
        }
        SlotRound.SetValues(machine, m.R0, m.R1, m.R2);
        Shared.Apply(delegate { machine.Interact(); });
        if (SlotRound.IsRunningPayer(machine))
        {
            int stake = ctx.StakeValue;
            if (Money.instance != null)
            {
                Money.instance.RemoveMoney(stake, false);
                SlotRound.MarkCharged(machine);
                CoopLog.Log.Info("slot.guest: staked " + stake + " coins on the host spin, the win is paid from the vanilla result of the round");
                GuestProfile.AfterTransfer();
            }
        }
        SlotRound.Abort(machine);
        CoopLog.Log.Info("slot.guest: spin " + m.SpinId + " values " + m.R0 + " " + m.R1 + " " + m.R2 + " payer " + payer);
    }

    private static bool Already(int spinId)
    {
        return doneSpins.Contains(spinId);
    }

    private static void Remember(int spinId)
    {
        doneSpins.Add(spinId);
        while (doneSpins.Count > MaxSpins) doneSpins.RemoveAt(0);
    }

    private static void Notice(string text)
    {
        if (string.Equals(text, noticeText, StringComparison.Ordinal) && Time.unscaledTime - noticeAt < 2f) return;
        noticeText = text;
        noticeAt = Time.unscaledTime;
        CoopNotice.Show("slot", text, 3f);
    }
}
