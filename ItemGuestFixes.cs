using ObenseuerCore;
using UnityEngine;

internal static partial class ItemGuest
{
    private static int remainderItemId;
    private static int remainderCount;
    private static string remainderMeta;
    private static int remainderReq;

    private static bool HandRemainder(int itemId, int count, string meta, int grantReq)
    {
        if (SendRemainder(itemId, count, meta)) return true;
        if (remainderCount > 0)
        {
            CoopLog.Log.Info("coop: the part of a synced pickup that did not fit is replaced by a newer one, item " + remainderItemId + " x" + remainderCount + " is given up");
        }
        remainderItemId = itemId;
        remainderCount = count;
        remainderMeta = meta;
        remainderReq = grantReq;
        CoopLog.Log.Info("coop: the part of a synced pickup that did not fit is held until a request may be sent, item " + itemId + " x" + count);
        return false;
    }

    private static bool SendRemainder(int itemId, int count, string meta)
    {
        if (!Active || pending != null || count < 1) return false;
        int reqId = ++lastReqId;
        string text = ItemWire.ReqDrop(reqId, GameRefs.AreaId, itemId, count, DropPosition(), meta);
        if (!Session.Send(FreeRoam.Authority, ItemWire.Channel, text, true)) return false;
        pending = new Pending { ReqId = reqId, Kind = Kind.Drop, ItemId = itemId, Count = count, Local = false, Text = text, Attempts = 1, SentAt = Time.unscaledTime };
        return true;
    }

    private static void RemainderTick()
    {
        if (remainderCount < 1) return;
        if (!SendRemainder(remainderItemId, remainderCount, remainderMeta)) return;
        int grant = remainderReq;
        CoopLog.Log.Info("coop: the part of a synced pickup that did not fit was sent back to the host, item " + remainderItemId + " x" + remainderCount);
        ClearRemainder();
        SendGack(grant);
        GuestProfile.AfterTransfer();
    }

    private static void RemainderReset()
    {
        if (remainderCount < 1) return;
        CoopLog.Log.Info("coop: the part of a synced pickup that did not fit was given up, item " + remainderItemId + " x" + remainderCount);
        ClearRemainder();
    }

    private static void ClearRemainder()
    {
        remainderItemId = 0;
        remainderCount = 0;
        remainderMeta = null;
        remainderReq = 0;
    }
}
