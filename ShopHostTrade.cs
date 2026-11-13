using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;

internal static class ShopHostTrade
{
    private const int MaxRollbacks = 256;
    private const float MaxTrackAge = 300f;
    private const float NackWindow = 45f;
    private const float SweepSeconds = 5f;
    private static readonly TransferLedger ledger = new TransferLedger();
    private static readonly Dictionary<TransferKey, Rollback> rollbacks = new Dictionary<TransferKey, Rollback>();
    private static readonly List<TransferKey> staleKeys = new List<TransferKey>();
    private static float nextSweep;

    private sealed class Rollback
    {
        public TransferKey Key;
        public string ShopId;
        public bool Buying;
        public Furniture Item;
        public BuildingSystem.FurnitureInfo.Meta Meta;
        public string ItemId;
        public int Amount;
        public int Oc;
        public int Rm;
        public float At;
    }

    internal static void Reset()
    {
        ledger.Clear();
        rollbacks.Clear();
        nextSweep = 0f;
    }

    internal static void Tick(float now)
    {
        try
        {
            if (now < nextSweep) return;
            nextSweep = now + SweepSeconds;
            ledger.Now = now;
            if (ledger.Expire(now, MaxTrackAge) == 0) return;
            Expired();
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopHostTrade.Tick", e);
        }
    }

    internal static void Left(ulong peer)
    {
        try
        {
            int dropped = ledger.Forget(peer);
            staleKeys.Clear();
            foreach (KeyValuePair<TransferKey, Rollback> pair in rollbacks)
            {
                if (pair.Key.GuestId == peer) staleKeys.Add(pair.Key);
            }
            for (int i = 0; i < staleKeys.Count; i++) rollbacks.Remove(staleKeys[i]);
            if (dropped > 0) CoopLog.Log.Info("coop: guest " + peer + " left with " + dropped + " shop trades on record, nothing was put back, an undone trade is undone by that guest's nack alone");
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopHostTrade.Left", e);
        }
    }

    internal static void Done(ulong from, ShopMsg m)
    {
        try
        {
            TransferKey key = Key(from, m);
            TransferEntry entry = ledger.Find(key);
            if (entry == null || entry.State != TransferState.Granted)
            {
                CoopLog.Log.Debug("shop.done", "the ack for trade " + TransferLedger.KeyText(key) + " has no granted entry");
                return;
            }
            ledger.Now = Time.unscaledTime;
            rollbacks.Remove(key);
            if (!ledger.Advance(key, TransferState.Applied)) return;
            ledger.Advance(key, TransferState.Closed);
            CoopLog.Log.Debug("shop.done", "trade " + TransferLedger.KeyText(key) + " was applied on the guest and is closed");
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopHostTrade.Done", e);
        }
    }

    internal static void Buy(ulong from, ShopMsg m)
    {
        try
        {
            TransferKey key = Key(from, m);
            TransferEntry entry = Track(key, "buy");
            if (entry == null)
            {
                ShopHost.Send(from, ShopWire.Ack(m.ReqId, false, 0, 0, "Too many trades are open"));
                return;
            }
            if (entry.State != TransferState.Requested)
            {
                Replay(from, entry);
                return;
            }
            string reason;
            BaseShop shop = ShopHost.TradeShop(from, m.ShopId, out reason);
            if (shop == null)
            {
                Refuse(from, key, entry, reason);
                return;
            }
            BuildingSystem.FurnitureInfo info = ShopHost.FindStock(shop, m.ItemId, m.Amount);
            if (info == null)
            {
                Refuse(from, key, entry, "That item is sold out");
                return;
            }
            int oc = shop.GetOcBuyPrice(info) * m.Amount;
            int rm = shop.GetRmBuyPrice(info) * m.Amount;
            if (!ledger.Advance(key, TransferState.Reserved)) return;
            shop.currentOcMoney += oc;
            shop.currentRmMoney += rm;
            shop.RemoveItem(info, m.Amount, false);
            Rollback done = Record(key, m.ShopId, true, info.furniture, MetaOf(info), m.ItemId, m.Amount, oc, rm);
            CoopLog.Log.Info("coop: guest " + from + " bought " + m.Amount + " of " + m.ItemId);
            Granted(entry, done, shop);
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopHostTrade.Buy", e);
        }
    }

    internal static void Sell(ulong from, ShopMsg m)
    {
        try
        {
            TransferKey key = Key(from, m);
            TransferEntry entry = Track(key, "sell");
            if (entry == null)
            {
                ShopHost.Send(from, ShopWire.Ack(m.ReqId, false, 0, 0, "Too many trades are open"));
                return;
            }
            if (entry.State != TransferState.Requested)
            {
                Replay(from, entry);
                return;
            }
            string reason;
            BaseShop shop = ShopHost.TradeShop(from, m.ShopId, out reason);
            if (shop == null)
            {
                Refuse(from, key, entry, reason);
                return;
            }
            Furniture furniture = ShopHost.ResolveFurniture(m.ItemId);
            if (furniture == null)
            {
                Refuse(from, key, entry, "That shop does not know that item");
                return;
            }
            if (furniture.IsFree())
            {
                Refuse(from, key, entry, "That shop does not buy that");
                return;
            }
            BuildingSystem.FurnitureInfo.Meta meta = ShopWire.MetaOf(m.Meta);
            BuildingSystem.FurnitureInfo temp = new BuildingSystem.FurnitureInfo(furniture, meta, null, null, m.Amount);
            int oc = shop.GetOcSellPrice(temp) * m.Amount;
            int rm = shop.GetRmSellPrice(temp) * m.Amount;
            if (shop.currentOcMoney < oc || shop.currentRmMoney < rm)
            {
                Refuse(from, key, entry, "That shop cannot afford it");
                return;
            }
            if (!shop.CanSell(temp))
            {
                Refuse(from, key, entry, "That shop does not buy that");
                return;
            }
            if (!ledger.Advance(key, TransferState.Reserved)) return;
            shop.currentOcMoney -= oc;
            shop.currentRmMoney -= rm;
            shop.AddItem(furniture, meta, null, m.Amount);
            string addedKey = ShopWire.ItemKey(furniture);
            Rollback done = Record(key, m.ShopId, false, furniture, meta, addedKey == null ? m.ItemId : addedKey, m.Amount, oc, rm);
            CoopLog.Log.Info("coop: guest " + from + " sold " + m.Amount + " of " + m.ItemId);
            Granted(entry, done, shop);
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopHostTrade.Sell", e);
        }
    }

    internal static void Nack(ulong from, ShopMsg m)
    {
        try
        {
            TransferKey key = Key(from, m);
            TransferEntry entry = ledger.Find(key);
            Rollback done;
            if (entry == null || entry.State != TransferState.Granted || !rollbacks.TryGetValue(key, out done))
            {
                CoopLog.Log.Debug("shop.nack", "nack " + m.ReqId + " has no applied trade");
                return;
            }
            if (Time.unscaledTime - done.At > NackWindow)
            {
                CoopLog.Log.Info("coop: the nack for trade " + m.ReqId + " was refused, it is older than the rollback window");
                return;
            }
            string reason;
            BaseShop shop = ShopHost.TradeShop(from, done.ShopId, out reason);
            if (shop == null)
            {
                CoopLog.Log.Debug("shop.nack", "nack " + m.ReqId + " was dropped: " + reason);
                return;
            }
            if (!CanReverse(done, shop)) return;
            ledger.Now = Time.unscaledTime;
            Undo(shop, done);
            rollbacks.Remove(key);
            entry.Payload = ShopWire.Ack(m.ReqId, false, 0, 0, "The trade was undone by the guest");
            ledger.Advance(key, TransferState.RolledBack);
            CoopLog.Log.Info("coop: guest " + from + " undid the trade " + m.ReqId + ", the shop was put back");
            ShopHost.Publish(shop);
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopHostTrade.Nack", e);
        }
    }

    private static void Expired()
    {
        if (rollbacks.Count == 0) return;
        staleKeys.Clear();
        foreach (KeyValuePair<TransferKey, Rollback> pair in rollbacks) staleKeys.Add(pair.Key);
        for (int i = 0; i < staleKeys.Count; i++)
        {
            TransferKey key = staleKeys[i];
            TransferEntry entry = ledger.Find(key);
            if (entry != null && entry.State != TransferState.Expired) continue;
            rollbacks.Remove(key);
            if (entry != null) entry.Payload = ShopWire.Ack(key.Seq, false, 0, 0, "The trade expired");
            CoopLog.Log.Info("coop: trade " + TransferLedger.KeyText(key) + " expired with no answer from the guest, the shop keeps what the trade left on it and the trade can no longer be undone");
        }
    }

    private static bool CanReverse(Rollback done, BaseShop shop)
    {
        if (done.Buying && (shop.currentOcMoney < done.Oc || shop.currentRmMoney < done.Rm))
        {
            CoopLog.Log.Info("coop: the rollback of trade " + TransferLedger.KeyText(done.Key) + " was refused, the rollback would drive the till below zero");
            return false;
        }
        if (!done.Buying && StockCount(shop, done.ItemId) < done.Amount)
        {
            CoopLog.Once("ShopHostTrade.CanReverse", "the rollback of sale " + TransferLedger.KeyText(done.Key) + " was refused, the shop no longer holds the whole " + done.Amount + " of " + done.ItemId);
            return false;
        }
        return true;
    }

    private static int StockCount(BaseShop shop, string itemId)
    {
        List<BuildingSystem.FurnitureInfo> items = shop.GetAvailableItems();
        int count = 0;
        for (int i = 0; items != null && i < items.Count; i++)
        {
            BuildingSystem.FurnitureInfo info = items[i];
            if (info == null || info.furniture == null) continue;
            if (ShopWire.KeyOfItem(info) != itemId) continue;
            count += info.amount;
        }
        return count;
    }

    private static void Undo(BaseShop shop, Rollback done)
    {
        if (done.Buying)
        {
            shop.currentOcMoney -= done.Oc;
            shop.currentRmMoney -= done.Rm;
            if (done.Item != null) shop.AddItem(done.Item, done.Meta, null, done.Amount);
            return;
        }
        shop.currentOcMoney += done.Oc;
        shop.currentRmMoney += done.Rm;
        if (!UndoSell(shop, done)) CoopLog.Log.Error("coop: the rollback of the sale " + done.Key.Seq + " did not take the whole amount back");
    }

    private static bool UndoSell(BaseShop shop, Rollback done)
    {
        List<BuildingSystem.FurnitureInfo> items = shop.GetAvailableItems();
        if (items == null) return false;
        int left = done.Amount;
        for (int i = items.Count - 1; i >= 0 && left > 0; i--)
        {
            BuildingSystem.FurnitureInfo info = items[i];
            if (info == null || info.furniture == null) continue;
            if (ShopWire.KeyOfItem(info) != done.ItemId) continue;
            int take = info.amount < left ? info.amount : left;
            shop.RemoveItem(info, take, false);
            left -= take;
        }
        return left == 0;
    }

    private static TransferKey Key(ulong from, ShopMsg m)
    {
        return new TransferKey(from, m.Epoch, m.ReqId);
    }

    private static TransferEntry Track(TransferKey key, string kind)
    {
        ledger.Now = Time.unscaledTime;
        TransferEntry entry = ledger.Find(key);
        if (entry != null) return entry;
        if (rollbacks.Count >= MaxRollbacks)
        {
            CoopLog.Once("ShopHostTrade.Track", "too many guest trades are open, a trade was refused");
            return null;
        }
        return ledger.Open(key, kind, "");
    }

    private static void Replay(ulong peer, TransferEntry entry)
    {
        CoopLog.Log.Debug("shop.repeat", "trade " + TransferLedger.KeyText(entry.Key) + " was answered from the ledger, state " + entry.State);
        if (entry.Payload.Length > 0) ShopHost.Send(peer, entry.Payload);
    }

    private static void Refuse(ulong peer, TransferKey key, TransferEntry entry, string reason)
    {
        entry.Payload = ShopWire.Ack(key.Seq, false, 0, 0, reason);
        ledger.Advance(key, TransferState.Refused);
        ShopHost.Send(peer, entry.Payload);
    }

    private static void Granted(TransferEntry entry, Rollback done, BaseShop shop)
    {
        rollbacks[entry.Key] = done;
        entry.Payload = ShopWire.Ack(entry.Key.Seq, true, done.Oc, done.Rm, "");
        ledger.Advance(entry.Key, TransferState.Granted);
        ShopHost.Send(entry.Key.GuestId, entry.Payload);
        ShopHost.Publish(shop);
    }

    private static Rollback Record(TransferKey key, string shopId, bool buying, Furniture item, BuildingSystem.FurnitureInfo.Meta meta, string itemId, int amount, int oc, int rm)
    {
        Rollback done = new Rollback();
        done.Key = key;
        done.ShopId = shopId;
        done.Buying = buying;
        done.Item = item;
        done.Meta = meta;
        done.ItemId = itemId;
        done.Amount = amount;
        done.Oc = oc;
        done.Rm = rm;
        done.At = Time.unscaledTime;
        return done;
    }

    private static BuildingSystem.FurnitureInfo.Meta MetaOf(BuildingSystem.FurnitureInfo info)
    {
        return info != null && info.meta != null ? info.meta : new BuildingSystem.FurnitureInfo.Meta();
    }
}
