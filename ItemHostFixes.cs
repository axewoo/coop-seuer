using System.Collections.Generic;
using ObenseuerCore;

internal static partial class ItemHost
{
    private static void Hello(ulong from, ItemMsg m)
    {
        string host = Scene();
        if (host == null || m == null || m.Scene != host || !Session.Peers.Contains(from) || Session.AreaOf(from) != host) return;
        Requester requester = RequesterOf(from);
        if (!m.Resync)
        {
            requester.LastReq = 0;
            requester.LastReply = null;
            requester.LastGone = null;
        }
        if (ResendGrantsOnHello(from)) return;
        List<Send> weak = WeakInScene(host);
        Enqueue(from, ItemWire.Snap(host, weak.Count));
        for (int i = 0; i < weak.Count; i++)
        {
            CollectibleItem piece = weak[i].Item;
            Enqueue(from, ItemWire.SnapItem(host, IdFor(piece), piece.Item.ID, piece.amount, piece.transform.position, weak[i].Meta));
        }
        Enqueue(from, ItemWire.SnapEnd(host), true);
        if (CoopDebug.On("item")) CoopDebug.Event("item", "snap", "sent the snapshot of " + weak.Count + " items to " + from + " in " + host);
    }
}
