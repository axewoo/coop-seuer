using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;

internal static class ShopHost
{
    private const float IdleSeconds = 600f;
    private const float NoticeQuiet = 2f;
    private const int MaxLocks = 64;
    private static readonly Dictionary<string, Held> locks = new Dictionary<string, Held>();
    private static readonly List<string> stale = new List<string>();
    private static float noticeAt;
    private static string noticeText = "";
    private static bool noGuidLogged;
    private static bool noShopLogged;
    private static bool noItemLogged;
    private const float AwayGrace = 5f;

    private sealed class Held
    {
        public string Key;
        public string Scene;
        public ulong Holder;
        public bool HostOwned;
        public float TouchedAt;
        public float AwaySince;
    }

    private static bool Active
    {
        get { return Session.IsInLobby && Session.IsHost; }
    }

    public static void Init()
    {
    }

    public static void Reset()
    {
        locks.Clear();
        ShopHostTrade.Reset();
        ShopStock.Forget();
    }

    public static void OnSceneChanged()
    {
        try
        {
            ShopStock.Forget();
            if (locks.Count == 0) return;
            stale.Clear();
            foreach (KeyValuePair<string, Held> pair in locks) stale.Add(pair.Key);
            for (int i = 0; i < stale.Count; i++)
            {
                CoopLog.Log.Info("coop: shop " + stale[i] + " released, the scene changed");
                Release(stale[i]);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopHost.OnSceneChanged", e);
        }
    }

    public static void Tick()
    {
        try
        {
            float now = Time.unscaledTime;
            ShopHostTrade.Tick(now);
            if (locks.Count == 0) return;
            stale.Clear();
            foreach (KeyValuePair<string, Held> pair in locks)
            {
                if (pair.Value.HostOwned) continue;
                if (now - pair.Value.TouchedAt >= IdleSeconds)
                {
                    CoopLog.Log.Info("coop: shop " + pair.Key + " released, it was idle");
                    stale.Add(pair.Key);
                    continue;
                }
                string area = Session.AreaOf(pair.Value.Holder);
                if (area == null || area.Length == 0 || area == pair.Value.Scene)
                {
                    pair.Value.AwaySince = 0f;
                    continue;
                }
                if (pair.Value.AwaySince == 0f)
                {
                    pair.Value.AwaySince = now;
                    continue;
                }
                if (now - pair.Value.AwaySince >= AwayGrace)
                {
                    CoopLog.Log.Info("coop: shop " + pair.Key + " released, the holder is in another area");
                    stale.Add(pair.Key);
                }
            }
            for (int i = 0; i < stale.Count; i++) Release(stale[i]);
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopHost.Tick", e);
        }
    }

    public static void Handle(ulong from, ShopMsg m)
    {
        try
        {
            if (!Active || !Session.Peers.Contains(from)) return;
            if (m.Kind == "SHOPREQ") Request(from, m);
            else if (m.Kind == "SHOPCLOSE") Close(from, m);
            else if (m.Kind == "SHOPTOUCH") Touch(from, m);
            else if (m.Kind == "SHOPBUY") ShopHostTrade.Buy(from, m);
            else if (m.Kind == "SHOPSELL") ShopHostTrade.Sell(from, m);
            else if (m.Kind == "SHOPNACK") ShopHostTrade.Nack(from, m);
            else if (m.Kind == "SHOPDONE") ShopHostTrade.Done(from, m);
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopHost.Handle", e);
        }
    }

    public static void OnMemberLeft(ulong peer)
    {
        try
        {
            if (!Active) return;
            stale.Clear();
            foreach (KeyValuePair<string, Held> pair in locks)
            {
                if (pair.Value.Holder == peer) stale.Add(pair.Key);
            }
            for (int i = 0; i < stale.Count; i++)
            {
                CoopLog.Log.Info("coop: shop " + stale[i] + " released, the holder left");
                Release(stale[i]);
            }
            ShopHostTrade.Left(peer);
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopHost.OnMemberLeft", e);
        }
    }

    public static bool GuestHolds(BaseShop shop)
    {
        if (!GuestHoldsQuiet(shop)) return false;
        Notice("A guest is using that shop");
        return true;
    }

    public static bool GuestHoldsQuiet(BaseShop shop)
    {
        if (!Active || shop == null) return false;
        string key = ShopWire.KeyOf(shop);
        if (key == null) return false;
        Held held;
        if (!locks.TryGetValue(key, out held)) return false;
        if (held.HostOwned || held.Holder == 0) return false;
        return true;
    }

    public static void OnOpened(BaseShop shop)
    {
        try
        {
            if (!Active || shop == null || SaveData.Busy) return;
            string key = ShopWire.KeyOf(shop);
            if (key == null)
            {
                if (!noGuidLogged)
                {
                    noGuidLogged = true;
                    CoopLog.Log.Error("coop: a shop has no GUID, so it cannot be shared: " + shop.name);
                }
                return;
            }
            if (locks.ContainsKey(key)) return;
            Held held = new Held();
            held.Key = key;
            held.Scene = GameRefs.AreaId;
            held.HostOwned = true;
            held.TouchedAt = Time.unscaledTime;
            locks[key] = held;
            CoopLog.Log.Info("coop: shop " + key + " opened on the host");
            Publish(shop);
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopHost.OnOpened", e);
        }
    }

    public static void OnClosed(BaseShop shop)
    {
        try
        {
            if (!Active || shop == null) return;
            string key = ShopWire.KeyOf(shop);
            Held held;
            if (key == null || !locks.TryGetValue(key, out held) || !held.HostOwned) return;
            CoopLog.Log.Info("coop: shop " + key + " released, the host closed it");
            Release(key);
            Publish(shop);
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopHost.OnClosed", e);
        }
    }

    public static void Publish(BaseShop shop)
    {
        try
        {
            if (!Active || shop == null) return;
            string key = ShopWire.KeyOf(shop);
            if (key == null) return;
            SendAll(GameRefs.AreaId, ShopWire.State(GameRefs.AreaId, key, shop.currentOcMoney, shop.currentRmMoney, ShopStock.Text(shop)));
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopHost.Publish", e);
        }
    }

    internal static BaseShop TradeShop(ulong from, string shopId, out string reason)
    {
        reason = null;
        Held held;
        if (!locks.TryGetValue(shopId, out held) || held.HostOwned || held.Holder != from)
        {
            reason = "That shop is not open for you";
            return null;
        }
        if (SaveData.Busy)
        {
            reason = "The game is loading or saving";
            return null;
        }
        BaseShop shop = Resolve(shopId);
        if (shop == null) reason = "That shop could not be found";
        return shop;
    }

    internal static Furniture ResolveFurniture(string itemId)
    {
        Furniture found = ShopStock.Find(itemId);
        if (found != null || itemId == null) return found;
        if (!noItemLogged)
        {
            noItemLogged = true;
            CoopLog.Log.Error("coop: the host could not resolve the furniture " + itemId + ", a guest sale is refused");
        }
        return null;
    }

    internal static BuildingSystem.FurnitureInfo FindStock(BaseShop shop, string itemId, int amount)
    {
        List<BuildingSystem.FurnitureInfo> items = shop.GetAvailableItems();
        for (int i = 0; items != null && i < items.Count; i++)
        {
            BuildingSystem.FurnitureInfo info = items[i];
            if (info == null || info.furniture == null) continue;
            if (ShopWire.KeyOfItem(info) != itemId) continue;
            if (info.amount < amount) continue;
            return info;
        }
        return null;
    }

    private static void Request(ulong from, ShopMsg m)
    {
        string scene = GameRefs.AreaId;
        if (scene == null || m.Scene != scene || Session.AreaOf(from) != scene)
        {
            Deny(from, m.ReqId, "That shop is not in this scene");
            return;
        }
        Held held;
        if (locks.TryGetValue(m.ShopId, out held))
        {
            BaseShop again = held.Holder == from && !held.HostOwned ? Resolve(m.ShopId) : null;
            if (again != null)
            {
                held.TouchedAt = Time.unscaledTime;
                Send(from, ShopWire.Ok(m.ReqId, m.ShopId, again.currentOcMoney, again.currentRmMoney, ShopStock.Text(again)));
                return;
            }
            Deny(from, m.ReqId, "Someone else is using that shop");
            return;
        }
        if (locks.Count >= MaxLocks)
        {
            Deny(from, m.ReqId, "Too many shops are open");
            return;
        }
        if (SaveData.Busy)
        {
            Deny(from, m.ReqId, "The game is loading or saving");
            return;
        }
        BaseShop shop = Resolve(m.ShopId);
        if (shop == null)
        {
            Deny(from, m.ReqId, "That shop could not be found");
            return;
        }
        held = new Held();
        held.Key = m.ShopId;
        held.Scene = scene;
        held.Holder = from;
        held.TouchedAt = Time.unscaledTime;
        locks[m.ShopId] = held;
        Send(from, ShopWire.Ok(m.ReqId, m.ShopId, shop.currentOcMoney, shop.currentRmMoney, ShopStock.Text(shop)));
        CoopLog.Log.Info("coop: shop " + m.ShopId + " opened for guest " + from);
        Publish(shop);
    }

    private static void Close(ulong from, ShopMsg m)
    {
        Held held;
        if (!locks.TryGetValue(m.ShopId, out held) || held.Holder != from) return;
        CoopLog.Log.Info("coop: shop " + m.ShopId + " released, the guest closed it");
        Release(m.ShopId);
    }

    private static void Touch(ulong from, ShopMsg m)
    {
        Held held;
        if (!locks.TryGetValue(m.ShopId, out held) || held.Holder != from) return;
        held.TouchedAt = Time.unscaledTime;
    }

    private static void Release(string key)
    {
        locks.Remove(key);
    }

    internal static BaseShop Resolve(string shopId)
    {
        string guid = ShopWire.GuidOf(shopId);
        if (guid == null) return null;
        BaseShop shop = SaveController.GetObjectByGUID(guid) as BaseShop;
        if (shop != null) return shop;
        shop = SaveController.GetObjectByGUID(guid, typeof(BaseShop)) as BaseShop;
        if (shop == null && !noShopLogged)
        {
            noShopLogged = true;
            CoopLog.Log.Error("coop: the host could not find the shop " + guid + ", a guest open is refused");
        }
        return shop;
    }

    private static void Deny(ulong peer, int reqId, string reason)
    {
        Send(peer, ShopWire.Deny(reqId, reason));
    }

    internal static void Send(ulong peer, string text)
    {
        Session.Send(peer, ShopWire.Channel, text, true);
    }

    private static void SendAll(string scene, string text)
    {
        List<ulong> peers = scene == null ? Session.Peers : Session.PeersInArea(scene);
        for (int i = 0; i < peers.Count; i++) Send(peers[i], text);
    }

    private static void Notice(string text)
    {
        CoopLog.Log.Info("coop: " + text);
        if (string.Equals(text, noticeText, StringComparison.Ordinal) && Time.unscaledTime - noticeAt < NoticeQuiet) return;
        noticeText = text;
        noticeAt = Time.unscaledTime;
        if (Notifications.instance != null) Notifications.instance.CreateNotification("Co-op", text);
    }
}
