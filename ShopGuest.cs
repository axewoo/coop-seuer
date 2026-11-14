using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;

internal static class ShopGuest
{
    private const float RequestTimeout = 5f;
    private const int MaxAttempts = 2;
    private const float TouchInterval = 30f;
    private static int lastReqId;
    private static Pending pending;
    private static string openKey;
    private static BaseShop openShop;
    private static float nextTouch;
    private static bool wasShared;

    private sealed class Pending
    {
        public int ReqId;
        public string Key;
        public BaseShop Shop;
        public string Text;
        public int Attempts;
        public float SentAt;
    }

    public static bool Shared
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
    }

    internal static int NextReqId()
    {
        return ++lastReqId;
    }

    public static void Reset()
    {
        ShopGuestTrade.Reset();
        pending = null;
        if (openKey != null) Abandon(openKey);
        openKey = null;
        openShop = null;
        nextTouch = 0f;
        ShopStock.Forget();
        GuestGate.ForgetGrants();
    }

    public static void OnSceneChanged()
    {
        Reset();
    }

    public static void Tick()
    {
        try
        {
            bool shared = Shared;
            if (wasShared && !shared) Reset();
            wasShared = shared;
            float now = Time.unscaledTime;
            if (pending != null && now - pending.SentAt > RequestTimeout) Retry(now);
            ShopGuestTrade.Tick(now);
            Keep(now);
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopGuest.Tick", e);
        }
    }

    public static void Handle(ulong from, ShopMsg m)
    {
        try
        {
            if (from != Session.HostId || Session.IsHost || !Session.IsInLobby) return;
            if (m.Kind == "SHOPOK") OnOk(m);
            else if (m.Kind == "DENY") OnDeny(m);
            else if (m.Kind == "SHOPSTATE") OnState(m);
            else if (m.Kind == "ACK") ShopGuestTrade.OnAck(m);
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopGuest.Handle", e);
        }
    }

    public static bool RequestOpen(BaseShop shop, string key)
    {
        try
        {
            if (!Shared)
            {
                GuestGate.Notice(GuestGate.KindShop, "Shops are not shared here");
                return false;
            }
            if (pending != null)
            {
                GuestGate.Notice(GuestGate.KindShop, "The host has not answered yet");
                return false;
            }
            int reqId = NextReqId();
            string text = ShopWire.ReqOpen(reqId, GameRefs.AreaId, key);
            if (!Session.Send(Session.HostId, ShopWire.Channel, text, true))
            {
                GuestGate.Notice(GuestGate.KindShop, "The host could not be reached, the shop did not open");
                return false;
            }
            Pending ask = new Pending();
            ask.ReqId = reqId;
            ask.Key = key;
            ask.Shop = shop;
            ask.Text = text;
            ask.Attempts = 1;
            ask.SentAt = Time.unscaledTime;
            pending = ask;
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopGuest.RequestOpen", e);
            return false;
        }
    }

    public static bool RequestTrade(ShopBaseUI ui, BaseShop shop, string key, bool buying)
    {
        return ShopGuestTrade.Request(ui, shop, key, buying);
    }

    public static void OnOpened(BaseShop shop)
    {
        try
        {
            if (shop == null) return;
            string key = ShopWire.KeyOf(shop);
            if (key == null || !GuestGate.Granted(key)) return;
            openKey = key;
            openShop = shop;
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopGuest.OnOpened", e);
        }
    }

    public static void OnClosed(BaseShop shop)
    {
        try
        {
            string key = openKey;
            if (key == null)
            {
                if (shop == null) return;
                key = ShopWire.KeyOf(shop);
                if (key == null) return;
            }
            else if (shop != null && ShopWire.KeyOf(shop) != key)
            {
                return;
            }
            openKey = null;
            openShop = null;
            Abandon(key);
            CoopLog.Log.Info("coop: shop " + key + " closed on the guest");
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopGuest.OnClosed", e);
        }
    }

    private static void Retry(float now)
    {
        Pending lost = pending;
        if (lost.Attempts < MaxAttempts && Shared && Session.Send(Session.HostId, ShopWire.Channel, lost.Text, true))
        {
            lost.Attempts++;
            lost.SentAt = now;
            CoopLog.Log.Debug("shop.retry", "no answer to shop request " + lost.ReqId + ", attempt " + lost.Attempts);
            return;
        }
        pending = null;
        Abandon(lost.Key);
        GuestGate.Notice(GuestGate.KindShop, "The host did not answer, the shop did not open");
    }

    private static void Keep(float now)
    {
        if (openKey == null || now < nextTouch) return;
        ShopController controller = ShopController.instance;
        if (controller == null || !controller.menuIsOpen)
        {
            OnClosed(openShop);
            return;
        }
        nextTouch = now + TouchInterval;
        GuestGate.Grant(openKey);
        if (Shared) Session.Send(Session.HostId, ShopWire.Channel, ShopWire.Touch(GameRefs.AreaId, openKey), true);
    }

    private static void Abandon(string key)
    {
        try
        {
            if (key == null) return;
            if (Session.IsInLobby && !Session.IsHost && Session.HostId != 0)
            {
                string scene = GameRefs.AreaId;
                Session.Send(Session.HostId, ShopWire.Channel, ShopWire.Close(string.IsNullOrEmpty(scene) ? "-" : scene, key), true);
            }
            GuestGate.Forget(key);
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopGuest.Abandon", e);
        }
    }

    private static void OnOk(ShopMsg m)
    {
        Pending done = pending != null && pending.ReqId == m.ReqId ? pending : null;
        if (done == null)
        {
            CoopLog.Log.Debug("shop.ok", "grant " + m.ReqId + " has no request");
            Abandon(m.ShopId);
            return;
        }
        pending = null;
        if (m.ShopId != done.Key)
        {
            CoopLog.Once("ShopGuest.ok", "the host granted a different shop");
            Abandon(done.Key);
            Abandon(m.ShopId);
            return;
        }
        BaseShop shop = done.Shop;
        if (shop == null)
        {
            CoopLog.Once("ShopGuest.ok", "the shop of the granted open is gone");
            Abandon(done.Key);
            return;
        }
        ShopController controller = ShopController.instance;
        if (controller == null)
        {
            CoopLog.Once("ShopGuest.ok", "no shop controller, the granted open is dropped");
            Abandon(done.Key);
            return;
        }
        ApplyTill(shop, m.Oc, m.Rm);
        ShopStock.TryApply(shop, m.Stock);
        GuestGate.Grant(done.Key);
        openKey = done.Key;
        openShop = shop;
        nextTouch = Time.unscaledTime + TouchInterval;
        controller.OpenMenu(shop);
    }

    private static void OnDeny(ShopMsg m)
    {
        if (pending == null || pending.ReqId != m.ReqId) return;
        pending = null;
        GuestGate.Notice(GuestGate.KindShop, m.Reason);
    }

    private static void OnState(ShopMsg m)
    {
        if (m.Scene != GameRefs.AreaId) return;
        BaseShop shop = Resolve(m.ShopId);
        if (shop == null) return;
        ApplyTill(shop, m.Oc, m.Rm);
        ShopStock.TryApply(shop, m.Stock);
        Refresh();
    }

    private static void ApplyTill(BaseShop shop, int oc, int rm)
    {
        try
        {
            shop.currentOcMoney = oc;
            shop.currentRmMoney = rm;
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopGuest.ApplyTill", e);
        }
    }

    private static void Refresh()
    {
        try
        {
            ShopController controller = ShopController.instance;
            if (controller == null || !controller.menuIsOpen) return;
            controller.UpdateCurrentUI();
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopGuest.Refresh", e);
        }
    }

    private static BaseShop Resolve(string shopId)
    {
        string guid = ShopWire.GuidOf(shopId);
        if (guid == null) return null;
        BaseShop shop = SaveController.GetObjectByGUID(guid) as BaseShop;
        if (shop != null) return shop;
        return SaveController.GetObjectByGUID(guid, typeof(BaseShop)) as BaseShop;
    }
}
