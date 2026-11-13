using System;
using System.Collections.Generic;
using ObenseuerCore;
using OS.Items;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static partial class ItemGuest
{
    private static void OnSnap(ItemMsg m)
    {
        if (!InScene(m.Scene)) return;
        Forget();
    }

    private static void OnSpawn(ItemMsg m)
    {
        if (!InScene(m.Scene)) return;
        SpawnMirror(m);
    }

    private static void OnSnapEnd(ItemMsg m)
    {
        if (!InScene(m.Scene)) return;
        snapScene = m.Scene;
        snapHandle = SceneManager.GetActiveScene().handle;
        if (CoopDebug.On("item")) CoopDebug.Event("item", "snapend", "applied the host snapshot in " + m.Scene + ", mirrored " + items.Count);
    }

    private static void OnGone(ItemMsg m)
    {
        if (!InScene(m.Scene)) return;
        if (CoopDebug.On("item")) CoopDebug.Event("item", "gone", "the host removed mirrored id " + m.Id + " in " + m.Scene);
        DropMirror(m.Id);
    }

    private static void OnGive(ItemMsg m)
    {
        if (GrantAnswered(m.ReqId))
        {
            CoopLog.Log.Debug("item.give", "ignored a repeated answer to request " + m.ReqId);
            SendGack(m.ReqId);
            return;
        }
        Pending done = pending != null && pending.ReqId == m.ReqId && pending.Kind == Kind.Pick ? pending : null;
        if (done != null)
        {
            pending = null;
            RemoveLocal(done.Id);
        }
        int remaining;
        try
        {
            remaining = AddSynced(m.ItemId, m.Amount, m.Meta);
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemGuest.give", e);
            remaining = m.Amount;
        }
        RememberGrant(m.ReqId);
        if (remaining <= 0)
        {
            SendGack(m.ReqId);
            GuestProfile.AfterTransfer();
            CoopLog.Log.Debug("item.give", "picked up item " + m.ItemId + " x" + m.Amount + " from the host");
            if (CoopDebug.On("item")) CoopDebug.Event("item", "give", "got item " + m.ItemId + " x" + m.Amount + " req " + m.ReqId + " from the host");
            return;
        }
        CoopLog.Once("ItemGuest.remainder", "a synced pickup did not fit: item " + m.ItemId + " x" + remaining + " of " + m.Amount);
        if (remaining >= m.Amount) Notify("Inventory full, the part that did not fit goes back to the world.");
        if (!HandRemainder(m.ItemId, remaining, m.Meta, m.ReqId)) return;
        SendGack(m.ReqId);
        GuestProfile.AfterTransfer();
    }

    private static int AddSynced(int itemId, int amount, string text)
    {
        Item item = ItemDatabase.instance.FetchItemByID(itemId);
        if (item == null)
        {
            CoopLog.Once("ItemGuest.give", "the host named an unknown item: " + itemId);
            return amount;
        }
        ItemOperations.ItemOperationSettings settings = new ItemOperations.ItemOperationSettings();
        settings.disableQualityMix = true;
        settings.ignoreBackpack = true;
        object[] meta = Decode(text);
        meta = ItemMeta.Merge(item, 1, meta);
        return ItemOperations.AddItemAndReturnRemaining(item, -1, null, meta, amount, 0, settings);
    }

    private static void OnMoney(ItemMsg m)
    {
        if (GrantAnswered(m.ReqId))
        {
            CoopLog.Log.Debug("item.money", "ignored a repeated answer to request " + m.ReqId);
            SendGack(m.ReqId);
            return;
        }
        Pending done = pending != null && pending.ReqId == m.ReqId ? pending : null;
        if (done != null)
        {
            pending = null;
            RemoveLocal(done.Id);
        }
        RememberGrant(m.ReqId);
        try
        {
            if (Money.instance != null) Money.instance.AddMoney(m.X, m.Real);
            else CoopLog.Once("ItemGuest.money", "the money taken by the host was not added: no Money instance");
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemGuest.money", e);
        }
        SendGack(m.ReqId);
        GuestProfile.AfterTransfer();
    }

    private static bool AlreadyAnswered(int reqId)
    {
        return doneReqs.Contains(reqId);
    }

    private static void Remember(int reqId)
    {
        doneReqs.Add(reqId);
        while (doneReqs.Count > MaxSettled) doneReqs.RemoveAt(0);
    }

    private static void OnAck(ItemMsg m)
    {
        if (AlreadyAnswered(m.ReqId))
        {
            CoopLog.Log.Debug("item.ack", "ignored a repeated answer to request " + m.ReqId);
            return;
        }
        Pending done = pending != null && pending.ReqId == m.ReqId ? pending : null;
        Orphan orphan = OrphanOf(m.ReqId);
        if (done == null && orphan == null)
        {
            CoopLog.Log.Debug("item.ack", "ack " + m.ReqId + " has no request");
            return;
        }
        if (done != null) pending = null;
        Remember(m.ReqId);
        if (!m.Ok)
        {
            if (done != null) Notify(m.Reason);
            return;
        }
        if (done == null)
        {
            if (orphan != null) RemoveById(orphan.ItemId, orphan.Count, orphan.Stack);
            return;
        }
        if (done.Kind != Kind.Drop) return;
        ApplyDrop(done);
    }
}
