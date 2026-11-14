using System;
using System.Collections.Generic;
using ObenseuerCore;
using OS.Items;
using UnityEngine;

internal static partial class ItemGuest
{
    private static string recordsWorld;

    private static void SendGack(int reqId)
    {
        try
        {
            if (reqId < 1 || !Session.IsInLobby || FreeRoam.IAmAuthority) return;
            ulong host = FreeRoam.Authority;
            if (host == 0UL) return;
            Session.Send(host, ItemWire.Channel, ItemWire.Gack(reqId), true);
            if (CoopDebug.On("item")) CoopDebug.Event("item", "gack", "told the host that the grant of request " + reqId + " was applied");
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemGuest.gack", e);
        }
    }

    private static void KeepRecordsForSession()
    {
        if (!Session.IsInLobby)
        {
            ClearRecords(null);
            return;
        }
        string world = GuestSession.WorldId;
        if (string.IsNullOrEmpty(world) || world == recordsWorld) return;
        ClearRecords(world);
    }

    private static int SeedReqId()
    {
        long seconds = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        int seed = (int)(seconds % 100000000L) * 10;
        return seed < 1 ? 1 : seed;
    }

    private static void ClearRecords(string world)
    {
        recordsWorld = world;
        doneReqs.Clear();
        doneGrants.Clear();
        orphans.Clear();
        lastReqId = SeedReqId();
        RemainderReset();
    }

    private const int MaxGrantAnswers = 256;
    private static readonly List<int> doneGrants = new List<int>();

    private static bool GrantAnswered(int reqId)
    {
        return doneGrants.Contains(reqId);
    }

    private static void RememberGrant(int reqId)
    {
        doneGrants.Add(reqId);
        while (doneGrants.Count > MaxGrantAnswers) doneGrants.RemoveAt(0);
    }

    private static void RemoveById(int itemId, int count, ItemStack wanted)
    {
        try
        {
            Inventory inventory = Inventory.instance;
            if (inventory == null || itemId < 2 || count < 1)
            {
                CoopLog.Once("ItemGuest.remove", "a synced drop is still in my inventory because it could not be taken out: item " + itemId + " x" + count);
                return;
            }
            SlotController slot = StackFor(inventory, itemId, count, wanted);
            if (slot == null)
            {
                CoopLog.Log.Info("item.drop: the drop of item " + itemId + " x" + count + " stays in my inventory, no single stack of that item holds the whole count");
                return;
            }
            slot.TakeItem(count);
            CoopLog.Log.Info("item.drop: removed " + count + " of item " + itemId + " from one inventory stack for a drop the host already spawned");
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemGuest.remove", e);
        }
    }

    private static SlotController StackFor(Inventory inventory, int itemId, int count, ItemStack wanted)
    {
        SlotController first = null;
        SlotController[] slots = inventory.AllSlots(false, false);
        for (int i = 0; i < slots.Length; i++)
        {
            SlotController slot = slots[i];
            if (slot == null) continue;
            ItemStack stack = slot.itemStack;
            if (stack == null || stack.itemId != itemId || stack.itemAmount < count) continue;
            if (wanted != null && ReferenceEquals(stack, wanted)) return slot;
            if (first == null) first = slot;
        }
        return first;
    }
}
