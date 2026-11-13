using System;
using System.Collections.Generic;
using OS.Items;
using UnityEngine;

internal static class VendGuest
{
    private sealed class Pending
    {
        public int ReqId;
        public string Text;
        public int Attempts;
        public float SentAt;
        public VendingMachine Machine;
        public int Index;
        public string Guid;
        public Money.money Money;
        public bool Nack;
        public float ExpiredAt;
    }

    private const float RetryAfter = 3f;
    private const float ExpireAfter = 15f;
    private const float ExpiredGrace = 60f;
    private const int MaxAttempts = 3;
    private const int MaxPending = 16;
    private const int MaxExpired = 16;
    private const string TimeoutText = "The host did not answer, nothing was bought";
    private const string NotShared = "That vending machine is not shared in co-op yet";
    private const string StolenText = "Stolen vending items are not shared yet";

    private static readonly List<Pending> outgoing = new List<Pending>();
    private static readonly List<Pending> expired = new List<Pending>();
    private static readonly List<Pending> finished = new List<Pending>();
    private static int lastReqId;
    private static bool timeoutOnce;

    public static bool Forwarding
    {
        get { return Session.IsGuest && GuestSession.InHostWorld && GuestSession.Loaded; }
    }

    public static void Init()
    {
        Reset();
    }

    public static void Reset()
    {
        outgoing.Clear();
        expired.Clear();
        finished.Clear();
        timeoutOnce = false;
    }

    public static bool Request(VendingMachine machine, VendingMachine.VendingMachineItem item)
    {
        if (!Forwarding || machine == null || item == null) return false;
        if (Money.instance == null) return false;
        if (item.currentStock == 0) return false;
        if (Busy(machine))
        {
            CoopNotice.Show("vend|busy", "That purchase is still waiting for the host", 5f);
            return true;
        }
        Money.money price = Price(item);
        if (!Money.instance.CheckMoney(price)) return false;
        if (!HasSpace(item))
        {
            CoopNotice.Show("vend|full", "Inventory full, nothing was bought", 5f);
            return true;
        }
        string guid = SaveController.GetObjectGUID(machine);
        if (string.IsNullOrEmpty(guid))
        {
            CoopNotice.Show("vend|noshare", NotShared, 5f);
            return true;
        }
        if (item.stolen && machine.Trader != null && machine.Trader.ID > 0)
        {
            CoopNotice.Show("vend|stolen", StolenText, 5f);
            return true;
        }
        int index = VendWire.IndexOf(machine, item);
        if (index < 0)
        {
            CoopNotice.Show("vend|slot", NotShared, 5f);
            return true;
        }
        int reqId = Next();
        if (!Send(reqId, VendWire.Req(reqId, guid, index), guid, machine, index, price))
        {
            CoopNotice.Show("vend|send", "The host could not be asked, nothing was bought", 5f);
        }
        return true;
    }

    public static void Handle(ulong from, VendMsg m)
    {
        try
        {
            if (Session.IsHost || from != Session.HostId) return;
            if (m.Kind != "VACK") return;
            Pending pending = Take(m.ReqId);
            if (pending == null)
            {
                Pending late = TakeExpired(m.ReqId);
                if (late != null && m.Ok)
                {
                    CoopLog.Log.Info("coop: a vending answer arrived after the request gave up, the stock is given back");
                    Nack(late);
                    return;
                }
                CoopLog.Log.Debug("vend.ack", "an answer to request " + m.ReqId + " arrived with nothing waiting for it");
                return;
            }
            Apply(pending, m);
        }
        catch (Exception e)
        {
            CoopLog.Once("VendGuest.Handle", e);
        }
    }

    public static void Tick()
    {
        try
        {
            if (!Session.IsInLobby)
            {
                if (outgoing.Count > 0 || expired.Count > 0) Reset();
                return;
            }
            if (Session.IsHost || !Forwarding) return;
            Retry(Time.unscaledTime);
        }
        catch (Exception e)
        {
            CoopLog.Once("VendGuest.Tick", e);
        }
    }

    private static void Apply(Pending pending, VendMsg m)
    {
        VendingMachine machine = pending.Machine;
        if (!m.Ok)
        {
            CoopNotice.Show("vend|refused", "That item was not bought" + (string.IsNullOrEmpty(m.Reason) ? "" : ": " + m.Reason), 5f);
            CoopLog.Log.Info("coop: the host refused a vending buy: " + m.Reason);
            return;
        }
        if (machine == null)
        {
            CoopLog.Once("VendGuest.noMachine", "the host sold the item and this copy no longer names the machine, the host stock is given back");
            Nack(pending);
            return;
        }
        VendingMachine.VendingMachineItem entry = VendWire.Item(machine, pending.Index);
        if (entry == null)
        {
            CoopLog.Once("VendGuest.slot", "the host sold a vending slot this machine copy does not have");
            Nack(pending);
            return;
        }
        if (entry.item == null || entry.item.Item == null)
        {
            CoopLog.Once("VendGuest.item", "the machine sold an item this build does not have");
            Nack(pending);
            return;
        }
        if (Money.instance == null || !Money.instance.Buy(pending.Money))
        {
            CoopLog.Once("VendGuest.pay", "the host sold the item and this machine could not pay for it");
            CoopNotice.Show("vend|pay", "The purchase could not be paid for", 5f);
            Nack(pending);
            return;
        }
        if (m.Stock >= 0) entry.currentStock = m.Stock;
        int remaining = Give(entry);
        if (remaining > 0)
        {
            Refund(pending);
            Nack(pending);
            if (entry.currentStock >= 0) entry.currentStock = entry.currentStock + 1;
            try
            {
                machine.UpdateMoney();
            }
            catch (Exception e)
            {
                CoopLog.Once("VendGuest.UpdateMoney", e);
            }
            CoopNotice.Show("vend|full", "Inventory full, nothing was bought", 5f);
            return;
        }
        if (machine.soundDispense != null) machine.soundDispense.PlayMisc(machine.gameObject);
        try
        {
            machine.UpdateMoney();
        }
        catch (Exception e)
        {
            CoopLog.Once("VendGuest.UpdateMoney", e);
        }
        CoopLog.Log.Info("coop: bought vending slot " + pending.Index + ", stock now " + entry.currentStock);
        GuestProfile.AfterTransfer();
    }

    private static int Give(VendingMachine.VendingMachineItem entry)
    {
        Item item = entry.item == null ? null : entry.item.Item;
        if (item == null) return 1;
        ItemOperations.ItemOperationSettings settings = new ItemOperations.ItemOperationSettings();
        settings.disableQualityMix = true;
        settings.ignoreBackpack = true;
        try
        {
            return ItemOperations.AddItemAndReturnRemaining(item, -1, null, entry.GetMeta(), 1, 0, settings);
        }
        catch (Exception e)
        {
            CoopLog.Once("VendGuest.give", e);
            return 1;
        }
    }

    private static void Refund(Pending pending)
    {
        try
        {
            if (Money.instance != null) Money.instance.AddMoney(pending.Money, false);
        }
        catch (Exception e)
        {
            CoopLog.Once("VendGuest.refund", e);
        }
    }

    private static void Nack(Pending pending)
    {
        ulong host = Session.HostId;
        if (host == 0 || !Session.Peers.Contains(host)) return;
        string guid = pending.Guid;
        if (string.IsNullOrEmpty(guid) && pending.Machine != null) guid = SaveController.GetObjectGUID(pending.Machine);
        if (string.IsNullOrEmpty(guid))
        {
            CoopLog.Once("VendGuest.nack", "a buy could not be completed and the machine cannot be named, the host stock stays taken");
            return;
        }
        string text = VendWire.Nack(pending.ReqId, guid, pending.Index);
        bool full = outgoing.Count >= MaxPending;
        if (full) CoopLog.Once("VendGuest.nackfull", "the vending queue is full, a stock restore was sent without a retry entry");
        if (!Session.Send(host, VendWire.Channel, text, true))
        {
            CoopLog.Once("VendGuest.nacksend", "a stock restore could not be sent, the host stock stays taken");
            return;
        }
        if (full) return;
        Pending again = new Pending();
        again.ReqId = pending.ReqId;
        again.Text = text;
        again.Attempts = 1;
        again.SentAt = Time.unscaledTime;
        again.Machine = pending.Machine;
        again.Index = pending.Index;
        again.Guid = guid;
        again.Money = pending.Money;
        again.Nack = true;
        outgoing.Add(again);
    }

    private static bool Busy(VendingMachine machine)
    {
        for (int i = 0; i < outgoing.Count; i++)
        {
            if (!outgoing[i].Nack && outgoing[i].Machine == machine) return true;
        }
        return false;
    }

    private static bool HasSpace(VendingMachine.VendingMachineItem entry)
    {
        try
        {
            Item item = entry.item == null ? null : entry.item.Item;
            if (item == null) return true;
            Inventory inventory = Inventory.instance;
            if (inventory == null || inventory.Slots == null) return true;
            for (int i = 0; i < inventory.Slots.Length; i++)
            {
                SlotController slot = inventory.Slots[i];
                if (slot == null) continue;
                if (slot.IsEmpty()) return true;
                int remaining;
                if (slot.SlotHasSpaceForItem(item, out remaining, 0, 1, 0, entry.GetMeta(), true) && remaining <= 0) return true;
            }
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("VendGuest.space", e);
            return true;
        }
    }

    private static Money.money Price(VendingMachine.VendingMachineItem item)
    {
        if (item.currency != Money.Currency.OC) return new Money.money(0, item.GetPrice());
        return new Money.money(Mathf.RoundToInt(item.GetPrice()), 0f);
    }

    private static int Next()
    {
        lastReqId++;
        if (lastReqId > 999999999) lastReqId = 1;
        return lastReqId;
    }

    private static bool Send(int reqId, string text, string guid, VendingMachine machine, int index, Money.money price)
    {
        ulong host = Session.HostId;
        if (host == 0 || !Session.Peers.Contains(host)) return false;
        if (outgoing.Count >= MaxPending)
        {
            CoopLog.Once("VendGuest.full", "the vending queue is full, a buy was dropped");
            return false;
        }
        if (!Session.Send(host, VendWire.Channel, text, true)) return false;
        Pending pending = new Pending();
        pending.ReqId = reqId;
        pending.Text = text;
        pending.Attempts = 1;
        pending.SentAt = Time.unscaledTime;
        pending.Machine = machine;
        pending.Index = index;
        pending.Guid = guid;
        pending.Money = price;
        outgoing.Add(pending);
        return true;
    }

    private static void Retry(float now)
    {
        finished.Clear();
        for (int i = 0; i < outgoing.Count; i++)
        {
            Pending pending = outgoing[i];
            float age = now - pending.SentAt;
            if (age > ExpireAfter)
            {
                finished.Add(pending);
                continue;
            }
            if (pending.Attempts < MaxAttempts && age > RetryAfter * pending.Attempts)
            {
                ulong host = Session.HostId;
                if (host != 0 && Session.Peers.Contains(host) && Session.Send(host, VendWire.Channel, pending.Text, true)) pending.Attempts++;
            }
        }
        for (int i = 0; i < finished.Count; i++)
        {
            Pending pending = finished[i];
            outgoing.Remove(pending);
            Expire(pending, now);
        }
        PruneExpired(now);
    }

    private static void Expire(Pending pending, float now)
    {
        pending.ExpiredAt = now;
        expired.Add(pending);
        while (expired.Count > MaxExpired) expired.RemoveAt(0);
        if (!pending.Nack && !timeoutOnce)
        {
            timeoutOnce = true;
            CoopNotice.Show("vend|timeout", TimeoutText, 5f);
            CoopLog.Log.Info("coop: " + TimeoutText);
        }
    }

    private static void PruneExpired(float now)
    {
        while (expired.Count > 0 && now - expired[0].ExpiredAt > ExpiredGrace) expired.RemoveAt(0);
    }

    private static Pending Take(int reqId)
    {
        for (int i = 0; i < outgoing.Count; i++)
        {
            if (outgoing[i].Nack) continue;
            if (outgoing[i].ReqId != reqId) continue;
            Pending pending = outgoing[i];
            outgoing.RemoveAt(i);
            return pending;
        }
        return null;
    }

    private static Pending TakeExpired(int reqId)
    {
        for (int i = 0; i < expired.Count; i++)
        {
            if (expired[i].ReqId != reqId) continue;
            Pending pending = expired[i];
            expired.RemoveAt(i);
            return pending;
        }
        return null;
    }
}
