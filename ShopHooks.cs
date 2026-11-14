using System;
using System.Reflection;
using HarmonyLib;
using ObenseuerCore;

internal static class ShopHooks
{
    private static bool installed;
    public static bool Ready { get; private set; }
    private static bool wasOpen;

    public static bool Install()
    {
        if (installed) return true;
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".shop");
        try
        {
            Patch(harmony, typeof(ShopController), "OpenMenu", new[] { typeof(BaseShop) }, "OpenPrefix", null);
            Patch(harmony, typeof(ShopController), "CloseMenu", Type.EmptyTypes, "ClosePrefix", "ClosePostfix");
            Patch(harmony, typeof(ShopBaseUI), "Buy", Type.EmptyTypes, "BuyPrefix", "BuyPostfix");
            Patch(harmony, typeof(ShopBaseUI), "Sell", Type.EmptyTypes, "SellPrefix", "SellPostfix");
            Patch(harmony, typeof(BaseShop), "DeltaSeconds", new[] { typeof(int) }, "RestockPrefix", null);
            installed = true;
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopHooks.Install", e);
            harmony.UnpatchSelf();
            return false;
        }
        Lobby.MemberLeft += ShopHost.OnMemberLeft;
        Ready = true;
        return true;
    }

    public static bool OpenPrefix(BaseShop shop)
    {
        try
        {
            if (shop == null || SaveData.Busy) return true;
            if (!Session.IsHost)
            {
                ShopGuest.OnOpened(shop);
                return true;
            }
            if (ShopHost.GuestHolds(shop)) return false;
            ShopHost.OnOpened(shop);
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopHooks.OpenPrefix", e);
            return true;
        }
    }

    public static void ClosePrefix(ShopController __instance)
    {
        try
        {
            wasOpen = __instance != null && __instance.menuIsOpen;
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopHooks.ClosePrefix", e);
            wasOpen = false;
        }
    }

    public static void ClosePostfix()
    {
        try
        {
            if (!wasOpen) return;
            wasOpen = false;
            if (ShopController.instance == null) return;
            if (Session.IsHost) ShopHost.OnClosed(ShopController.instance.currentShop);
            else ShopGuest.OnClosed(ShopController.instance.currentShop);
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopHooks.ClosePostfix", e);
        }
    }

    public static bool BuyPrefix(ShopBaseUI __instance)
    {
        return TradePrefix(__instance, true);
    }

    public static bool SellPrefix(ShopBaseUI __instance)
    {
        return TradePrefix(__instance, false);
    }

    private static bool TradePrefix(ShopBaseUI ui, bool buying)
    {
        try
        {
            if (!GuestSession.InHostWorld) return true;
            if (!ShopGuest.Shared)
            {
                GuestGate.Notice(GuestGate.KindShop, "Shops are not shared here");
                return false;
            }
            BaseShop shop = ShopController.instance == null ? null : ShopController.instance.currentShop;
            string key = ShopWire.KeyOf(shop);
            if (key == null || !GuestGate.Granted(key))
            {
                GuestGate.Notice(GuestGate.KindShop, "That shop is not open for you");
                return false;
            }
            return ShopGuest.RequestTrade(ui, shop, key, buying);
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopHooks.TradePrefix", e);
            return false;
        }
    }

    public static void BuyPostfix()
    {
        Publish();
    }

    public static void SellPostfix()
    {
        Publish();
    }

    private static void Publish()
    {
        try
        {
            if (!Session.IsHost || ShopController.instance == null) return;
            ShopHost.Publish(ShopController.instance.currentShop);
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopHooks.Publish", e);
        }
    }

    public static bool RestockPrefix(BaseShop __instance)
    {
        try
        {
            if (GuestSession.InHostWorld) return false;
            return !ShopHost.GuestHoldsQuiet(__instance);
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopHooks.RestockPrefix", e);
            return true;
        }
    }

    public static string SelfCheck()
    {
        try
        {
            if (AccessTools.Method(typeof(ShopController), "OpenMenu", new[] { typeof(BaseShop) }) == null) return "ShopController.OpenMenu(BaseShop) was not found";
            if (AccessTools.Method(typeof(ShopController), "CloseMenu", Type.EmptyTypes) == null) return "ShopController.CloseMenu() was not found";
            if (AccessTools.Field(typeof(ShopController), "menuIsOpen") == null) return "ShopController.menuIsOpen was not found";
            if (AccessTools.Field(typeof(ShopController), "currentShop") == null) return "ShopController.currentShop was not found";
            if (AccessTools.Method(typeof(SaveController), "GetObjectGUID", new[] { typeof(UnityEngine.Object) }) == null) return "SaveController.GetObjectGUID was not found";
            if (AccessTools.Method(typeof(BaseShop), "GetAvailableItems", Type.EmptyTypes) == null) return "BaseShop.GetAvailableItems was not found";
            if (AccessTools.Method(typeof(BaseShop), "DeltaSeconds", new[] { typeof(int) }) == null) return "BaseShop.DeltaSeconds was not found";
            if (AccessTools.Method(typeof(ShopBaseUI), "Buy", Type.EmptyTypes) == null) return "ShopBaseUI.Buy was not found";
            if (AccessTools.Method(typeof(ShopBaseUI), "Sell", Type.EmptyTypes) == null) return "ShopBaseUI.Sell was not found";
            if (AccessTools.Field(typeof(ShopBaseUI), "currentSelection") == null) return "ShopBaseUI.currentSelection was not found";
            if (AccessTools.Method(typeof(SavableScriptableObject), "LoadFromPath", new[] { typeof(string) }) == null) return "SavableScriptableObject.LoadFromPath was not found";
        }
        catch (Exception e)
        {
            return "exception: " + e.Message;
        }
        return "shop hooks ok";
    }

    private static void Patch(Harmony harmony, Type type, string name, Type[] args, string prefix, string postfix)
    {
        MethodBase original = AccessTools.Method(type, name, args);
        if (original == null) throw new MissingMethodException(type.Name, name);
        harmony.Patch(original,
            prefix: prefix == null ? null : new HarmonyMethod(typeof(ShopHooks), prefix),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(ShopHooks), postfix));
    }
}
