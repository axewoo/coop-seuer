using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ObenseuerCore;
using UnityEngine;

internal static partial class ItemHost
{
    private static bool Admit(ulong from, int reqId)
    {
        if (!Session.Peers.Contains(from)) return false;
        Requester r = RequesterOf(from);
        if (r.LastReq == reqId)
        {
            CoopLog.Log.Debug("item.retry", "repeating the answer to request " + reqId);
            if (r.LastReply != null) Enqueue(from, r.LastReply);
            if (r.LastGone != null) Enqueue(from, r.LastGone);
            return false;
        }
        float now = Time.unscaledTime;
        if (now - r.WindowStart >= RateWindow)
        {
            r.WindowStart = now;
            r.Count = 0;
        }
        if (r.Count >= MaxRequests)
        {
            ItemDiag.Info("host.refuse.too many requests", "coop: item request " + reqId + " from " + from + " refused: too many requests");
            Enqueue(from, ItemWire.Ack(reqId, false, "too many requests"));
            return false;
        }
        r.Count++;
        return true;
    }

    private static void Settle(ulong peer, int reqId, string reply, string gone)
    {
        Requester r = RequesterOf(peer);
        r.LastReq = reqId;
        r.LastReply = reply;
        r.LastGone = gone;
    }

    private static Requester RequesterOf(ulong peer)
    {
        Requester r;
        if (!requesters.TryGetValue(peer, out r))
        {
            r = new Requester();
            requesters[peer] = r;
        }
        return r;
    }

    private static void Broadcast(string scene, string text)
    {
        foreach (ulong peer in Session.PeersInArea(scene))
        {
            Enqueue(peer, text);
        }
    }

    private static int Outbox(ulong peer)
    {
        Queue<string> queue;
        return outbox.TryGetValue(peer, out queue) ? queue.Count : 0;
    }

    private static void Enqueue(ulong peer, string text)
    {
        Enqueue(peer, text, false);
    }

    private static void Enqueue(ulong peer, string text, bool force)
    {
        Queue<string> queue;
        if (!outbox.TryGetValue(peer, out queue))
        {
            queue = new Queue<string>();
            outbox[peer] = queue;
        }
        if (!force && queue.Count >= MaxOutbox)
        {
            CoopLog.Once("ItemHost.outbox", "a guest's item outbox is full, newer packets are dropped");
            return;
        }
        queue.Enqueue(text);
    }

    private static void Drain()
    {
        if (outbox.Count == 0) return;
        foreach (ulong peer in new List<ulong>(outbox.Keys))
        {
            Queue<string> queue = outbox[peer];
            for (int i = 0; i < PerFrame && queue.Count > 0; i++)
            {
                if (!Session.Send(peer, ItemWire.Channel, queue.Peek(), true)) break;
                queue.Dequeue();
            }
            if (queue.Count == 0) outbox.Remove(peer);
        }
    }

    private static void Prune()
    {
        PruneGrants(Time.unscaledTime);
        dead.Clear();
        foreach (GameObject go in ids.Keys)
        {
            if (go == null) dead.Add(go);
        }
        foreach (GameObject go in dead)
        {
            string id = ids[go];
            ids.Remove(go);
            objects.Remove(id);
        }
        gone.Clear();
        List<ulong> peers = Session.Peers;
        foreach (ulong peer in outbox.Keys)
        {
            if (!peers.Contains(peer)) gone.Add(peer);
        }
        foreach (ulong peer in requesters.Keys)
        {
            if (!peers.Contains(peer)) gone.Add(peer);
        }
        foreach (ulong peer in gone)
        {
            Departed(peer);
        }
    }
}
