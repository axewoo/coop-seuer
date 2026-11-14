using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;

internal static class ShopGuestTrade
{
    private const float TradeTimeout = 10f;
    private const int TradeAttempts = 2;
    private const int MaxSettled = 8;
    private const int MaxOrphans = 8;
    private const int TradeAmount = 1;
    private const float MaxTrackAge = 300f;
    private const float SweepSeconds = 5f;
    private const int MaxEpoch = 1000000000;
    private static readonly TransferLedger ledger = new TransferLedger();
    private static int epoch = 1;
    private static string epochWorld;
    private static float nextSweep;
    private static Trade pendingTrade;
    private static readonly List<Trade> orphans = new List<Trade>();
    private static readonly List<int> settled = new List<int>();

    private sealed class Trade
    {
        public int ReqId;
        public TransferKey Transfer;
        public bool Buying;
        public string Key;
        public BaseShop Shop;
        public ShopBaseUI Ui;
        public BuildingSystem.FurnitureInfo Info;
        public Furniture Furniture;
        public BuildingSystem.FurnitureInfo.Meta Meta;
        public string ItemId;
        public int Amount;
        public string Text;
        public int Attempts;
        public float SentAt;
    }

    private static int Epoch
    {
        get
        {
            string world = GuestSession.WorldId;
            if (string.Equals(world, epochWorld, StringComparison.Ordinal)) return epoch;
            epochWorld = world;
            epoch++;
            if (epoch > MaxEpoch) epoch = 1;
            return epoch;
        }
    }

    internal static void Reset()
    {
        if (pendingTrade != null)
        {
            Orphan(pendingTrade);
            pendingTrade = null;
        }
        PruneOrphans(Time.unscaledTime);
    }

    internal static void Tick(float now)
    {
        try
        {
            if (now >= nextSweep)
            {
                nextSweep = now + SweepSeconds;
                ledger.Now = now;
                ledger.Expire(now, MaxTrackAge);
            }
            if (pendingTrade != null && now - pendingTrade.SentAt > TradeTimeout) RetryTrade(now);
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopGuestTrade.Tick", e);
        }
    }

    internal static bool Request(ShopBaseUI ui, BaseShop shop, string key, bool buying)
    {
        try
        {
            if (shop is TrainShop)
            {
                GuestGate.Notice(GuestGate.KindShop, "Train shops are not shared yet");
                return false;
            }
            if (pendingTrade != null)
            {
                GuestGate.Notice(GuestGate.KindShop, "The host has not answered the last trade");
                return false;
            }
            BuildingSystemShopSlot slot = ui == null ? null : ui.currentSelection;
            BuildingSystem.FurnitureInfo info = slot == null ? null : slot.buildObject;
            if (info == null || info.furniture == null || info.amount < TradeAmount)
            {
                GuestGate.Notice(GuestGate.KindShop, "That item cannot travel");
                return false;
            }
            string itemId = ShopWire.ItemKey(info.furniture);
            if (itemId == null)
            {
                GuestGate.Notice(GuestGate.KindShop, "That item cannot travel");
                return false;
            }
            int oc;
            int rm;
            if (buying)
            {
                oc = shop.GetOcBuyPrice(info);
                rm = shop.GetRmBuyPrice(info);
                if (!Pay(oc, rm)) return false;
                if (BuildingSystem.instance == null)
                {
                    GuestGate.Notice(GuestGate.KindShop, "Your inventory is not ready");
                    return false;
                }
            }
            else
            {
                if (info.gameObject != null)
                {
                    GuestGate.Notice(GuestGate.KindShop, "That piece holds items, empty it first");
                    return false;
                }
                if (info.furniture.IsFree())
                {
                    GuestGate.Notice(GuestGate.KindShop, "That shop does not buy that");
                    return false;
                }
                if (info.meta != null && info.meta.HasCustomImages)
                {
                    GuestGate.Notice(GuestGate.KindShop, "That item cannot travel");
                    return false;
                }
                if (!ShopStock.Carried(itemId, TradeAmount))
                {
                    GuestGate.Notice(GuestGate.KindShop, "That piece is no longer in your inventory");
                    return false;
                }
                oc = shop.GetOcSellPrice(info);
                rm = shop.GetRmSellPrice(info);
            }
            int reqId = ShopGuest.NextReqId();
            TransferKey transfer = new TransferKey(Session.Me, Epoch, reqId);
            ledger.Now = Time.unscaledTime;
            if (ledger.Open(transfer, buying ? "buy" : "sell", "") == null)
            {
                GuestGate.Notice(GuestGate.KindShop, "Too many trades are open, the trade was not sent");
                return false;
            }
            string text = buying ? ShopWire.ReqBuy(reqId, transfer.JoinEpoch, key, itemId, TradeAmount) : ShopWire.ReqSell(reqId, transfer.JoinEpoch, key, itemId, TradeAmount, oc, rm, ShopWire.MetaText(info.meta));
            if (!Session.Send(Session.HostId, ShopWire.Channel, text, true))
            {
                ledger.Advance(transfer, TransferState.Refused);
                GuestGate.Notice(GuestGate.KindShop, "The host could not be reached, the trade was not sent");
                return false;
            }
            Trade trade = new Trade();
            trade.ReqId = reqId;
            trade.Transfer = transfer;
            trade.Buying = buying;
            trade.Key = key;
            trade.Shop = shop;
            trade.Ui = ui;
            trade.Info = info;
            trade.Furniture = info.furniture;
            trade.Meta = info.meta;
            trade.ItemId = itemId;
            trade.Amount = TradeAmount;
            trade.Text = text;
            trade.Attempts = 1;
            trade.SentAt = Time.unscaledTime;
            pendingTrade = trade;
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopGuest.RequestTrade", e);
            return false;
        }
    }

    private static bool Pay(int oc, int rm)
    {
        Money money = Money.instance;
        if (money == null) return false;
        if (!money.CheckMoney(oc, Money.Currency.OC, true)) return false;
        if (!money.CheckMoney(rm, Money.Currency.RM, true)) return false;
        return true;
    }

    private static void RetryTrade(float now)
    {
        Trade lost = pendingTrade;
        if (lost.Attempts < TradeAttempts && ShopGuest.Shared && Session.Send(Session.HostId, ShopWire.Channel, lost.Text, true))
        {
            lost.Attempts++;
            lost.SentAt = now;
            CoopLog.Log.Debug("shop.retry", "no answer to trade " + lost.ReqId + ", attempt " + lost.Attempts);
            return;
        }
        pendingTrade = null;
        Orphan(lost);
        GuestGate.Notice(GuestGate.KindShop, "The host has not answered the trade yet, it will apply if the answer arrives");
    }

    private static void Orphan(Trade trade)
    {
        for (int i = 0; i < orphans.Count; i++)
        {
            if (orphans[i].ReqId == trade.ReqId && orphans[i].Transfer.JoinEpoch == trade.Transfer.JoinEpoch) return;
        }
        trade.Attempts = TradeAttempts;
        orphans.Add(trade);
        while (orphans.Count > MaxOrphans) orphans.RemoveAt(0);
        CoopLog.Log.Debug("shop.orphan", "trade " + trade.ReqId + " waits for a late answer");
    }

    private static void PruneOrphans(float now)
    {
        for (int i = orphans.Count - 1; i >= 0; i--)
        {
            if (now - orphans[i].SentAt <= MaxTrackAge) continue;
            orphans.RemoveAt(i);
        }
    }

    private static Trade Take(int reqId)
    {
        int epoch = Epoch;
        for (int i = 0; i < orphans.Count; i++)
        {
            if (orphans[i].ReqId != reqId || orphans[i].Transfer.JoinEpoch != epoch) continue;
            Trade found = orphans[i];
            orphans.RemoveAt(i);
            return found;
        }
        return null;
    }

    internal static void OnAck(ShopMsg m)
    {
        if (settled.Contains(m.ReqId))
        {
            CoopLog.Log.Debug("shop.ack", "ignored a repeated answer to request " + m.ReqId);
            return;
        }
        Trade done = null;
        if (pendingTrade != null && pendingTrade.ReqId == m.ReqId)
        {
            done = pendingTrade;
            pendingTrade = null;
        }
        else
        {
            done = Take(m.ReqId);
        }
        if (done == null)
        {
            CoopLog.Log.Debug("shop.ack", "ack " + m.ReqId + " has no request");
            if (m.Ok)
            {
                Remember(m.ReqId);
                SendNack(m.ReqId, Epoch, null);
            }
            return;
        }
        Remember(m.ReqId);
        ledger.Now = Time.unscaledTime;
        if (!m.Ok)
        {
            ledger.Advance(done.Transfer, TransferState.Refused);
            GuestGate.Notice(GuestGate.KindShop, m.Reason);
            if (done.Buying && m.Reason == "That item is sold out") DropLocalStock(done);
            return;
        }
        if (done.Shop == null)
        {
            CoopLog.Once("ShopGuest.OnAck", "the shop of the answered trade is gone, the trade was handed back to the host");
            ledger.Advance(done.Transfer, TransferState.RolledBack);
            SendNack(done);
            return;
        }
        if (!ledger.Advance(done.Transfer, TransferState.Reserved) || !ledger.Advance(done.Transfer, TransferState.Granted)) return;
        bool applied = done.Buying ? ApplyBuy(done, m.Oc, m.Rm) : ApplySell(done, m.Oc, m.Rm);
        if (!applied)
        {
            ledger.Advance(done.Transfer, TransferState.RolledBack);
            SendNack(done);
            return;
        }
        ledger.Advance(done.Transfer, TransferState.Applied);
        SendDone(done);
        ledger.Advance(done.Transfer, TransferState.Closed);
    }

    private static bool ApplyBuy(Trade trade, int oc, int rm)
    {
        Money money = Money.instance;
        if (money == null)
        {
            CoopLog.Once("ShopGuest.ApplyBuy", "the bought item was not paid for: no Money instance");
            return false;
        }
        if (!money.CheckMoney(oc, Money.Currency.OC, false) || !money.CheckMoney(rm, Money.Currency.RM, false))
        {
            GuestGate.Notice(GuestGate.KindShop, "Your wallet was short, the trade was handed back");
            return false;
        }
        money.RemoveMoney(oc, false);
        money.RemoveMoney(rm, true);
        TakeStock(trade);
        AddToInventory(trade);
        RefreshUi(trade);
        CoopLog.Log.Info("coop: bought " + trade.ItemId + " for " + oc + " OC and " + rm + " RM");
        GuestProfile.AfterTransfer();
        return true;
    }

    private static bool ApplySell(Trade trade, int oc, int rm)
    {
        Money money = Money.instance;
        if (money == null)
        {
            CoopLog.Once("ShopGuest.ApplySell", "the sold item was not paid for: no Money instance");
            GuestGate.Notice(GuestGate.KindShop, "Your wallet is not ready, the trade was handed back");
            return false;
        }
        if (!RemoveFromInventory(trade))
        {
            GuestGate.Notice(GuestGate.KindShop, "The sold item could not be taken from your inventory, the trade was handed back");
            RefreshUi(trade);
            return false;
        }
        trade.Shop.AddItem(trade.Furniture, MetaOf(trade), null, trade.Amount);
        money.AddMoney(oc, false);
        money.AddMoney(rm, true);
        GuestProfile.AfterTransfer();
        RefreshUi(trade);
        CoopLog.Log.Info("coop: sold " + trade.ItemId + " for " + oc + " OC and " + rm + " RM");
        return true;
    }

    private static void SendDone(Trade trade)
    {
        try
        {
            if (!Session.IsInLobby || Session.IsHost || Session.HostId == 0) return;
            Session.Send(Session.HostId, ShopWire.Channel, ShopWire.Done(trade.ReqId, trade.Transfer.JoinEpoch), true);
            CoopLog.Log.Info("coop: trade " + trade.ReqId + " was applied on the guest");
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopGuestTrade.SendDone", e);
        }
    }

    private static void SendNack(Trade trade)
    {
        SendNack(trade.ReqId, trade.Transfer.JoinEpoch, trade.Key);
    }

    private static void SendNack(int reqId, int epoch, string key)
    {
        try
        {
            if (!Session.IsInLobby || Session.IsHost || Session.HostId == 0) return;
            Session.Send(Session.HostId, ShopWire.Channel, ShopWire.Nack(reqId, epoch, key ?? "-"), true);
            CoopLog.Log.Info("coop: trade " + reqId + " was handed back to the host");
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopGuest.SendNack", e);
        }
    }

    private static BuildingSystem.FurnitureInfo.Meta MetaOf(Trade trade)
    {
        return trade.Meta != null ? trade.Meta : new BuildingSystem.FurnitureInfo.Meta();
    }

    private static void TakeStock(Trade trade)
    {
        try
        {
            List<BuildingSystem.FurnitureInfo> items = trade.Shop.GetAvailableItems();
            for (int i = 0; items != null && i < items.Count; i++)
            {
                if (items[i] != trade.Info) continue;
                trade.Shop.RemoveItem(items[i], trade.Amount, false);
                return;
            }
            CoopLog.Once("ShopGuest.TakeStock", "the bought item was already gone from my copy of the shop");
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopGuest.TakeStock", e);
        }
    }

    private static void DropLocalStock(Trade trade)
    {
        try
        {
            List<BuildingSystem.FurnitureInfo> items = trade.Shop.GetAvailableItems();
            for (int i = items.Count - 1; i >= 0; i--)
            {
                BuildingSystem.FurnitureInfo info = items[i];
                if (info == null || info.furniture == null) continue;
                if (ShopWire.ItemKey(info.furniture) != trade.ItemId) continue;
                trade.Shop.RemoveItem(info, 1, true);
            }
            RefreshUi(trade);
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopGuest.DropLocalStock", e);
        }
    }

    private static void AddToInventory(Trade trade)
    {
        try
        {
            BuildingSystem system = BuildingSystem.instance;
            if (system == null)
            {
                CoopLog.Once("ShopGuest.AddToInventory", "the bought item was not added: no BuildingSystem");
                return;
            }
            GameObject saved;
            system.AddFurniture(trade.Furniture, null, out saved, trade.Amount, MetaOf(trade));
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopGuest.AddToInventory", e);
        }
    }

    private static bool RemoveFromInventory(Trade trade)
    {
        try
        {
            BuildingSystem system = BuildingSystem.instance;
            if (system == null) return false;
            if (Holds(system, trade.Info))
            {
                system.RemoveFurniture(trade.Info, trade.Amount, false);
                return true;
            }
            List<BuildingSystem.FurnitureInfo> list = system.availableFurnitures;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                BuildingSystem.FurnitureInfo info = list[i];
                if (info == null || info.furniture == null || info.gameObject != null) continue;
                if (ShopWire.ItemKey(info.furniture) != trade.ItemId) continue;
                system.RemoveFurniture(info, trade.Amount, false);
                return true;
            }
            CoopLog.Once("ShopGuest.RemoveFromInventory", "the sold item was not found in my furniture list");
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopGuest.RemoveFromInventory", e);
            return false;
        }
    }

    private static bool Holds(BuildingSystem system, BuildingSystem.FurnitureInfo info)
    {
        List<BuildingSystem.FurnitureInfo> list = system.availableFurnitures;
        for (int i = 0; list != null && i < list.Count; i++)
        {
            if (list[i] == info) return true;
        }
        return false;
    }

    private static void RefreshUi(Trade trade)
    {
        try
        {
            ShopBaseUI ui = trade.Ui;
            if (ui == null) return;
            ui.UpdateUI(-1);
            ui.UpdateMoney();
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopGuest.RefreshUi", e);
        }
    }

    private static void Remember(int reqId)
    {
        settled.Add(reqId);
        while (settled.Count > MaxSettled) settled.RemoveAt(0);
    }
}
