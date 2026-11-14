using System;
using System.Globalization;
using ObenseuerCore;

internal sealed class ShopMsg
{
    public string Kind;
    public int ReqId;
    public int Epoch;
    public string Scene;
    public string ShopId;
    public string Reason;
    public int Oc;
    public int Rm;
    public string Stock;
    public string ItemId;
    public int Amount;
    public string Meta;
    public bool Ok;
}

internal static class ShopWire
{
    public const string Channel = "shop";
    public const string KeyPrefix = "s-";
    public const int MaxStock = 2048;
    public const int MaxStockItems = 48;
    private const int MaxText = 200;
    private const int MaxReason = 120;
    private const int MaxMeta = 16;
    private const int MaxMetaValue = 64;
    private const int MaxReqId = 999999999;
    private const int MaxEpoch = 1000000000;
    private const int MaxMoney = 100000000;
    private const int MaxAmount = 999;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly char[] Bad = { '|', '\r', '\n' };
    private static readonly char[] BadKey = { '|', ',', ';', '\r', '\n' };

    public static string ReqOpen(int reqId, string scene, string shopId)
    {
        return string.Format(Inv, "SHOPREQ|{0}|{1}|{2}", reqId, scene, shopId);
    }

    public static string Ok(int reqId, string shopId, int oc, int rm, string stock)
    {
        return string.Format(Inv, "SHOPOK|{0}|{1}|{2}|{3}|{4}", reqId, shopId, oc, rm, stock);
    }

    public static string Deny(int reqId, string reason)
    {
        return string.Format(Inv, "DENY|{0}|{1}", reqId, Clean(reason));
    }

    public static string Close(string scene, string shopId)
    {
        return string.Format(Inv, "SHOPCLOSE|{0}|{1}", scene, shopId);
    }

    public static string Touch(string scene, string shopId)
    {
        return string.Format(Inv, "SHOPTOUCH|{0}|{1}", scene, shopId);
    }

    public static string State(string scene, string shopId, int oc, int rm, string stock)
    {
        return string.Format(Inv, "SHOPSTATE|{0}|{1}|{2}|{3}|{4}", scene, shopId, oc, rm, stock);
    }

    public static string ReqBuy(int reqId, int epoch, string shopId, string itemId, int amount)
    {
        return string.Format(Inv, "SHOPBUY|{0}|{1}|{2}|{3}|{4}", reqId, epoch, shopId, itemId, amount);
    }

    public static string ReqSell(int reqId, int epoch, string shopId, string itemId, int amount, int oc, int rm, string meta)
    {
        return string.Format(Inv, "SHOPSELL|{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}", reqId, epoch, shopId, itemId, amount, oc, rm, meta);
    }

    public static string Ack(int reqId, bool ok, int oc, int rm, string reason)
    {
        return string.Format(Inv, "ACK|{0}|{1}|{2}|{3}|{4}", reqId, ok ? "1" : "0", oc, rm, Clean(reason));
    }

    public static string Nack(int reqId, int epoch, string shopId)
    {
        return string.Format(Inv, "SHOPNACK|{0}|{1}|{2}", reqId, epoch, shopId);
    }

    public static string Done(int reqId, int epoch)
    {
        return string.Format(Inv, "SHOPDONE|{0}|{1}", reqId, epoch);
    }

    public static string KeyOf(BaseShop shop)
    {
        if (shop == null) return null;
        string guid = SaveController.GetObjectGUID(shop);
        if (string.IsNullOrEmpty(guid) || guid.Length > MaxText || guid.IndexOfAny(Bad) >= 0) return null;
        return KeyPrefix + guid;
    }

    public static string GuidOf(string shopId)
    {
        if (shopId == null || !shopId.StartsWith(KeyPrefix, StringComparison.Ordinal)) return null;
        string guid = shopId.Substring(KeyPrefix.Length);
        return guid.Length == 0 ? null : guid;
    }

    public static string MetaText(BuildingSystem.FurnitureInfo.Meta meta)
    {
        if (meta == null) return "";
        return string.Format(Inv, "{0},{1}", meta.currentSkin, meta.currentAlternativePrefab);
    }

    public static BuildingSystem.FurnitureInfo.Meta MetaOf(string text)
    {
        BuildingSystem.FurnitureInfo.Meta meta = new BuildingSystem.FurnitureInfo.Meta();
        int skin;
        int alt;
        if (!TryMeta(text, out skin, out alt)) return meta;
        meta.currentSkin = skin;
        meta.currentAlternativePrefab = alt;
        return meta;
    }

    public static bool TryParse(string payload, out ShopMsg msg)
    {
        msg = null;
        try
        {
            if (payload == null) return false;
            string[] f = payload.Split('|');
            ShopMsg m = new ShopMsg();
            m.Kind = f[0];
            if (!Fields(f, m)) return false;
            msg = m;
            return true;
        }
        catch (Exception)
        {
            msg = null;
            return false;
        }
    }

    private static bool Fields(string[] f, ShopMsg m)
    {
        switch (m.Kind)
        {
            case "SHOPREQ":
                return f.Length == 4 && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[2], out m.Scene) && Text(f[3], out m.ShopId);
            case "SHOPOK":
                return f.Length == 6 && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[2], out m.ShopId) && Money(f[3], out m.Oc) && Money(f[4], out m.Rm) && Stock(f[5], out m.Stock);
            case "DENY":
                return f.Length == 3 && Int(f[1], 1, MaxReqId, out m.ReqId) && Note(f[2], out m.Reason);
            case "SHOPCLOSE":
            case "SHOPTOUCH":
                return f.Length == 3 && Text(f[1], out m.Scene) && Text(f[2], out m.ShopId);
            case "SHOPSTATE":
                return f.Length == 6 && Text(f[1], out m.Scene) && Text(f[2], out m.ShopId) && Money(f[3], out m.Oc) && Money(f[4], out m.Rm) && Stock(f[5], out m.Stock);
            case "SHOPBUY":
            {
                int at;
                return Shift(f, 5, 6, m, out at) && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[at], out m.ShopId) && Text(f[at + 1], out m.ItemId) && Int(f[at + 2], 1, MaxAmount, out m.Amount);
            }
            case "SHOPSELL":
            {
                int at;
                return Shift(f, 8, 9, m, out at) && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[at], out m.ShopId) && Text(f[at + 1], out m.ItemId) && Int(f[at + 2], 1, MaxAmount, out m.Amount) && Money(f[at + 3], out m.Oc) && Money(f[at + 4], out m.Rm) && MetaField(f[at + 5], out m.Meta);
            }
            case "SHOPNACK":
            {
                int at;
                return Shift(f, 3, 4, m, out at) && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[at], out m.ShopId);
            }
            case "SHOPDONE":
                return f.Length == 3 && Int(f[1], 1, MaxReqId, out m.ReqId) && Epoch(f[2], out m.Epoch);
            case "ACK":
                return f.Length == 6 && Int(f[1], 1, MaxReqId, out m.ReqId) && Flag(f[2], out m.Ok) && Money(f[3], out m.Oc) && Money(f[4], out m.Rm) && Note(f[5], out m.Reason);
            default:
                return false;
        }
    }

    public static string ItemKey(Furniture furniture)
    {
        if (furniture == null) return null;
        string path = furniture.addressableAssetPath;
        string key = string.IsNullOrEmpty(path) ? furniture.Id : path;
        if (string.IsNullOrEmpty(key) || key.Length > MaxText || key.IndexOfAny(BadKey) >= 0) return null;
        return key;
    }

    public static string KeyOfItem(BuildingSystem.FurnitureInfo info)
    {
        return info == null ? null : ItemKey(info.furniture);
    }

    private static string Clean(string text)
    {
        string plain = (text ?? "").Replace("|", "").Replace("\r", "").Replace("\n", "");
        return plain.Length > MaxReason ? plain.Substring(0, MaxReason) : plain;
    }

    private static bool Flag(string text, out bool value)
    {
        value = text == "1";
        return text == "0" || text == "1";
    }

    private static bool Int(string text, int min, int max, out int value)
    {
        return int.TryParse(text, NumberStyles.Integer, Inv, out value) && value >= min && value <= max;
    }

    private static bool Epoch(string text, out int value)
    {
        return Int(text, 0, MaxEpoch, out value);
    }

    private static bool Shift(string[] f, int plain, int keyed, ShopMsg m, out int at)
    {
        m.Epoch = 0;
        if (f.Length == plain)
        {
            at = 2;
            return true;
        }
        at = 3;
        return f.Length == keyed && Epoch(f[2], out m.Epoch);
    }

    private static bool Money(string text, out int value)
    {
        return Int(text, 0, MaxMoney, out value);
    }

    private static bool Stock(string text, out string value)
    {
        value = text;
        return text != null && text.Length <= MaxStock && text.IndexOfAny(Bad) < 0;
    }

    private static bool MetaField(string text, out string value)
    {
        value = text;
        int skin;
        int alt;
        return text != null && text.Length <= MaxMeta && TryMeta(text, out skin, out alt);
    }

    private static bool TryMeta(string text, out int skin, out int alt)
    {
        skin = 0;
        alt = 0;
        if (string.IsNullOrEmpty(text)) return true;
        string[] parts = text.Split(',');
        return parts.Length == 2 && Int(parts[0], 0, MaxMetaValue, out skin) && Int(parts[1], 0, MaxMetaValue, out alt);
    }

    private static bool Text(string text, out string value)
    {
        value = text;
        return text != null && text.Length >= 1 && text.Length <= MaxText && text.IndexOfAny(Bad) < 0;
    }

    private static bool Note(string text, out string value)
    {
        value = text;
        return text != null && text.Length <= MaxReason;
    }
}
