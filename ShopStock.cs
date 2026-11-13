using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ObenseuerCore;
using UnityEngine;

internal static class ShopStock
{
    public const string NotCarried = "!";
    private const int MaxKey = 200;
    private const int MaxUnresolved = 256;
    private static readonly Dictionary<string, Furniture> resolved = new Dictionary<string, Furniture>();
    private static readonly HashSet<string> unresolved = new HashSet<string>();
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static bool notCarriedLogged;

    public static void Forget()
    {
        resolved.Clear();
        unresolved.Clear();
    }

    public static string Text(BaseShop shop)
    {
        try
        {
            if (shop == null) return NotCarried;
            List<BuildingSystem.FurnitureInfo> items = shop.GetAvailableItems();
            if (items == null) return "";
            StringBuilder text = new StringBuilder();
            int count = 0;
            for (int i = 0; i < items.Count; i++)
            {
                BuildingSystem.FurnitureInfo info = items[i];
                if (info == null || info.furniture == null || info.amount <= 0) continue;
                string key = ShopWire.ItemKey(info.furniture);
                if (key == null) return NotCarried;
                count++;
                if (count > ShopWire.MaxStockItems) return NotCarried;
                if (text.Length > 0) text.Append(';');
                text.Append(key).Append(',').Append(info.amount.ToString(Inv));
                if (text.Length > ShopWire.MaxStock) return NotCarried;
            }
            return text.ToString();
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopStock.Text", e);
            return NotCarried;
        }
    }

    public static bool TryApply(BaseShop shop, string text)
    {
        try
        {
            if (shop == null || text == null) return false;
            if (text == NotCarried)
            {
                if (!notCarriedLogged)
                {
                    notCarriedLogged = true;
                    CoopLog.Log.Info("coop: the host does not carry the stock of a shop, the shop keeps its own list");
                }
                return false;
            }
            Dictionary<string, int> want;
            if (!Parse(text, out want))
            {
                CoopLog.Once("ShopStock.TryApply", "the stock list of the host could not be read, the local stock was left alone");
                return false;
            }
            Trim(shop, want);
            Grow(shop, want);
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopStock.TryApply", e);
            return false;
        }
    }

    public static bool Carried(string itemId, int amount)
    {
        try
        {
            BuildingSystem system = BuildingSystem.instance;
            if (system == null || itemId == null) return false;
            List<BuildingSystem.FurnitureInfo> items = system.availableFurnitures;
            for (int i = 0; items != null && i < items.Count; i++)
            {
                BuildingSystem.FurnitureInfo info = items[i];
                if (info == null || info.furniture == null || info.gameObject != null) continue;
                if (info.amount < amount) continue;
                if (ShopWire.ItemKey(info.furniture) == itemId) return true;
            }
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("ShopStock.Carried", e);
            return false;
        }
    }

    private static bool Parse(string text, out Dictionary<string, int> want)
    {
        want = new Dictionary<string, int>();
        if (text.Length == 0) return true;
        string[] entries = text.Split(';');
        if (entries.Length > ShopWire.MaxStockItems) return false;
        for (int i = 0; i < entries.Length; i++)
        {
            string[] pair = entries[i].Split(',');
            int amount;
            if (pair.Length != 2 || pair[0].Length == 0 || pair[0].Length > MaxKey) return false;
            if (!int.TryParse(pair[1], NumberStyles.Integer, Inv, out amount)) return false;
            if (amount <= 0 || amount > 999) return false;
            int have;
            if (want.TryGetValue(pair[0], out have)) amount += have;
            want[pair[0]] = amount;
        }
        return true;
    }

    private static void Trim(BaseShop shop, Dictionary<string, int> want)
    {
        List<BuildingSystem.FurnitureInfo> items = shop.GetAvailableItems();
        if (items == null) return;
        Dictionary<string, int> left = new Dictionary<string, int>();
        for (int i = items.Count - 1; i >= 0; i--)
        {
            BuildingSystem.FurnitureInfo info = items[i];
            if (info == null || info.furniture == null) continue;
            string key = ShopWire.ItemKey(info.furniture);
            int allowance = 0;
            if (key != null)
            {
                int limit;
                int used;
                if (!want.TryGetValue(key, out limit)) limit = 0;
                left.TryGetValue(key, out used);
                allowance = limit - used;
                if (allowance < 0) allowance = 0;
                left[key] = used + (info.amount <= allowance ? info.amount : allowance);
            }
            if (info.amount <= allowance) continue;
            shop.RemoveItem(info, info.amount - allowance, false);
        }
    }

    private static void Grow(BaseShop shop, Dictionary<string, int> want)
    {
        List<BuildingSystem.FurnitureInfo> items = shop.GetAvailableItems();
        foreach (KeyValuePair<string, int> pair in want)
        {
            int have = 0;
            for (int i = 0; items != null && i < items.Count; i++)
            {
                BuildingSystem.FurnitureInfo info = items[i];
                if (info == null || info.furniture == null) continue;
                if (ShopWire.ItemKey(info.furniture) != pair.Key) continue;
                have += info.amount;
            }
            if (have >= pair.Value) continue;
            Furniture furniture = Find(pair.Key);
            if (furniture == null)
            {
                CoopLog.Once("ShopStock.Grow", "the host stock names a furniture that could not be resolved, it was not added to the local list");
                continue;
            }
            shop.AddItem(furniture, new BuildingSystem.FurnitureInfo.Meta(), null, pair.Value - have);
        }
    }

    public static Furniture Find(string itemId)
    {
        if (itemId == null || unresolved.Contains(itemId)) return null;
        Furniture known;
        if (resolved.TryGetValue(itemId, out known)) return known;
        Furniture found = null;
        if (itemId.IndexOf('/') >= 0) found = SavableScriptableObject.LoadFromPath(itemId) as Furniture;
        if (found == null) found = FindLoaded(itemId);
        if (found != null)
        {
            resolved[itemId] = found;
            return found;
        }
        if (unresolved.Count < MaxUnresolved) unresolved.Add(itemId);
        return null;
    }

    private static Furniture FindLoaded(string id)
    {
        UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(Furniture));
        for (int i = 0; all != null && i < all.Length; i++)
        {
            Furniture piece = all[i] as Furniture;
            if (piece == null) continue;
            if (piece.Id == id || piece.addressableAssetPath == id) return piece;
        }
        return null;
    }
}
