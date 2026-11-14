using System;
using System.Collections.Generic;
using ObenseuerCore;
using OS.Items;
using UnityEngine;

internal static partial class ContainerGuest
{
    private const float LiveSeconds = 0.5f;
    private const int MaxAddTries = 3;
    private const int MaxRemainders = 8;
    private static Dictionary<int, int> openSnap;
    private static Dictionary<int, int> liveSnap;
    private static HashSet<int> invSnap;
    private static float liveAt;
    private static readonly List<Remainder> remainders = new List<Remainder>();

    private sealed class Remainder
    {
        public int ItemId;
        public int Count;
        public int Attempts;
        public string Key;
    }

    private sealed class Delta
    {
        public readonly List<int> Ids = new List<int>();
        public readonly List<int> Counts = new List<int>();
        public int Put;
        public int Taken;
        public bool Station;

        public bool Empty
        {
            get { return Ids.Count == 0; }
        }
    }

    private static Dictionary<int, int> Snapshot(Storage storage)
    {
        try
        {
            SlotController[] slots = storage == null ? null : storage.Slots;
            if (slots == null) return null;
            Dictionary<int, int> map = new Dictionary<int, int>();
            for (int i = 0; i < slots.Length; i++)
            {
                SlotController slot = slots[i];
                ItemStack stack = slot == null ? null : slot.itemStack;
                if (stack == null || stack.itemId < 0 || stack.itemAmount <= 0) continue;
                int have;
                map.TryGetValue(stack.itemId, out have);
                map[stack.itemId] = have + stack.itemAmount;
            }
            return map;
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerGuest.Snapshot", e);
            return null;
        }
    }

    private static void OpenSnapshot(Storage storage)
    {
        openSnap = Snapshot(storage);
        liveSnap = openSnap;
        invSnap = InventoryIds();
        liveAt = Time.unscaledTime;
    }

    private static HashSet<int> InventoryIds()
    {
        HashSet<int> ids = new HashSet<int>();
        try
        {
            Inventory inventory = Inventory.instance;
            SlotController[] slots = inventory == null ? null : inventory.AllSlots(false, false);
            if (slots == null) return ids;
            for (int i = 0; i < slots.Length; i++)
            {
                ItemStack stack = slots[i] == null ? null : slots[i].itemStack;
                if (stack != null && stack.itemId >= 0 && stack.itemAmount > 0) ids.Add(stack.itemId);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerGuest.InventoryIds", e);
        }
        return ids;
    }

    private static bool SeenBefore(int itemId)
    {
        return invSnap != null && invSnap.Contains(itemId);
    }

    private static bool IsStation(Storage storage)
    {
        if (storage == null) return false;
        if (StationState.StationOf(storage) != null) return true;
        return storage.GetComponent<CraftingBase>() != null || storage.GetComponentInParent<CraftingBase>() != null;
    }

    private static void Live()
    {
        if (open == null || openSnap == null) return;
        if (Time.unscaledTime - liveAt < LiveSeconds) return;
        liveAt = Time.unscaledTime;
        Dictionary<int, int> snap = Snapshot(open);
        if (snap != null) liveSnap = snap;
    }

    private static Delta FinishOpen(string key, Storage storage)
    {
        Dictionary<int, int> open = openSnap;
        Dictionary<int, int> closed = Snapshot(storage);
        Dictionary<int, int> live = liveSnap;
        openSnap = null;
        liveSnap = null;
        if (open == null)
        {
            invSnap = null;
            CoopLog.Log.Info("coop: no open snapshot exists for container " + key + ", nothing can be undone for it");
            return null;
        }
        if ((closed == null || closed.Count == 0) && live != null) closed = live;
        if (closed == null)
        {
            invSnap = null;
            CoopLog.Log.Error("coop: the contents of container " + key + " could not be read on the way out (" + Size(open) + " items were known), no item delta is undone for it");
            return null;
        }
        Delta delta = DeltaOf(open, closed);
        invSnap = null;
        delta.Station = IsStation(storage);
        if (CoopDebug.On("cont")) CoopDebug.Event("cont", key, "delta on close: " + delta.Taken + " taken, " + delta.Put + " put");
        return delta;
    }

    private static void RevertOpen()
    {
        Dictionary<int, int> open = openSnap;
        Dictionary<int, int> closed = liveSnap;
        string key = openKey != null ? openKey : exitingKey;
        openSnap = null;
        liveSnap = null;
        if (open == null)
        {
            invSnap = null;
            return;
        }
        if (closed == null)
        {
            invSnap = null;
            CoopLog.Log.Error("coop: the contents of container " + key + " were never seen while it was open (" + Size(open) + " items were known), no item delta is undone for it");
            return;
        }
        Delta delta = DeltaOf(open, closed);
        invSnap = null;
        Revert(key, delta);
    }

    private static Delta DeltaOf(Dictionary<int, int> open, Dictionary<int, int> closed)
    {
        Delta delta = new Delta();
        if (open == null || closed == null) return delta;
        foreach (KeyValuePair<int, int> pair in closed)
        {
            int was;
            open.TryGetValue(pair.Key, out was);
            if (pair.Value == was) continue;
            if (pair.Value > was && !open.ContainsKey(pair.Key) && !SeenBefore(pair.Key)) continue;
            Add(delta, pair.Key, pair.Value - was);
        }
        foreach (KeyValuePair<int, int> pair in open)
        {
            if (closed.ContainsKey(pair.Key)) continue;
            Add(delta, pair.Key, -pair.Value);
        }
        return delta;
    }

    private static void Add(Delta delta, int id, int diff)
    {
        delta.Ids.Add(id);
        delta.Counts.Add(diff);
        if (diff > 0) delta.Put += diff;
        else delta.Taken -= diff;
    }

    private static void Revert(string key, Delta delta)
    {
        try
        {
            if (delta == null || delta.Empty) return;
            if (delta.Station)
            {
                CoopLog.Log.Info("coop: the item delta of container " + key + " was not undone, revert skipped for station storage");
                return;
            }
            if (CoopDebug.On("cont")) CoopDebug.Event("cont", key, "revert: " + delta.Put + " item(s) put into the container are taken back into the inventory, " + delta.Taken + " item(s) taken out of it are removed from the inventory again");
            for (int i = 0; i < delta.Ids.Count; i++)
            {
                if (delta.Counts[i] > 0) AddBack(delta.Ids[i], delta.Counts[i], key);
            }
            for (int i = 0; i < delta.Ids.Count; i++)
            {
                if (delta.Counts[i] < 0) RemoveBack(delta.Ids[i], -delta.Counts[i], key);
            }
            CoopLog.Log.Info("coop: the container state for " + key + " was not applied, " + delta.Put + " item(s) put into the container were taken back into the inventory and " + delta.Taken + " item(s) taken out of it were removed from the inventory again");
            GuestProfile.AfterTransfer();
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerGuest.Revert", e);
        }
    }

    private static void AddBack(int itemId, int count, string key)
    {
        Item item = ItemDatabase.instance == null ? null : ItemDatabase.instance.FetchItemByID(itemId);
        if (item == null)
        {
            CoopLog.Log.Error("coop: the item " + itemId + " x" + count + " of the reverted container " + key + " is unknown and was not added back");
            return;
        }
        ItemOperations.ItemOperationSettings settings = new ItemOperations.ItemOperationSettings();
        settings.disableQualityMix = true;
        settings.ignoreBackpack = true;
        int left = TryAdd(item, count, settings);
        if (left > 0) Remember(itemId, left, key);
    }

    private static int TryAdd(Item item, int count, ItemOperations.ItemOperationSettings settings)
    {
        object[] meta = ItemMeta.Merge(item, 1, null);
        return ItemOperations.AddItemAndReturnRemaining(item, -1, null, meta, count, 0, settings);
    }

    private static void Remember(int itemId, int left, string key)
    {
        if (remainders.Count >= MaxRemainders)
        {
            Remainder oldest = remainders[0];
            remainders.RemoveAt(0);
            CoopLog.Log.Error("coop: " + oldest.Count + " of item " + oldest.ItemId + " put into container " + oldest.Key + " could not be given back to the inventory");
        }
        Remainder entry = new Remainder();
        entry.ItemId = itemId;
        entry.Count = left;
        entry.Attempts = 1;
        entry.Key = key;
        remainders.Add(entry);
    }

    private static void RetryRemainders()
    {
        if (remainders.Count == 0) return;
        for (int i = remainders.Count - 1; i >= 0; i--)
        {
            Remainder entry = remainders[i];
            Item item = ItemDatabase.instance == null ? null : ItemDatabase.instance.FetchItemByID(entry.ItemId);
            if (item == null)
            {
                remainders.RemoveAt(i);
                CoopLog.Log.Error("coop: the item " + entry.ItemId + " x" + entry.Count + " of the reverted container " + entry.Key + " is unknown and was not added back");
                continue;
            }
            ItemOperations.ItemOperationSettings settings = new ItemOperations.ItemOperationSettings();
            settings.disableQualityMix = true;
            settings.ignoreBackpack = true;
            int left = TryAdd(item, entry.Count, settings);
            if (left <= 0)
            {
                remainders.RemoveAt(i);
                continue;
            }
            entry.Count = left;
            entry.Attempts++;
            if (entry.Attempts <= MaxAddTries) continue;
            remainders.RemoveAt(i);
            CoopLog.Log.Error("coop: " + left + " of item " + entry.ItemId + " put into container " + entry.Key + " could not be given back to the inventory after " + MaxAddTries + " tries");
        }
    }

    private static void RemoveBack(int itemId, int count, string key)
    {
        Inventory inventory = Inventory.instance;
        if (inventory == null)
        {
            CoopLog.Log.Error("coop: " + count + " of item " + itemId + " taken out of container " + key + " could not be given back, there is no inventory");
            return;
        }
        SlotController[] slots = inventory.AllSlots(false, false);
        if (slots == null)
        {
            CoopLog.Log.Error("coop: " + count + " of item " + itemId + " taken out of container " + key + " could not be given back, the inventory has no slots");
            return;
        }
        int left = count;
        for (int i = 0; i < slots.Length; i++)
        {
            ItemStack stack = slots[i] == null ? null : slots[i].itemStack;
            if (stack == null || stack.itemId != itemId || stack.itemAmount < left) continue;
            slots[i].TakeItem(left);
            left = 0;
            break;
        }
        for (int i = 0; i < slots.Length && left > 0; i++)
        {
            ItemStack stack = slots[i] == null ? null : slots[i].itemStack;
            if (stack == null || stack.itemId != itemId || stack.itemAmount <= 0) continue;
            int take = stack.itemAmount < left ? stack.itemAmount : left;
            slots[i].TakeItem(take);
            left -= take;
        }
        if (left > 0) CoopLog.Log.Error("coop: " + left + " of item " + itemId + " taken out of container " + key + " is no longer in the inventory and stayed in the profile");
    }

    private static int Size(Dictionary<int, int> map)
    {
        if (map == null) return 0;
        int total = 0;
        foreach (KeyValuePair<int, int> pair in map) total += pair.Value;
        return total;
    }
}
