using System;
using System.Collections.Generic;
using UnityEngine;

internal static partial class ItemHost
{
    private const float GrantLife = 900f;
    private const int MaxGrants = 64;
    private static readonly Dictionary<ulong, List<Grant>> grants = new Dictionary<ulong, List<Grant>>();
    private static readonly List<ulong> expiredGrants = new List<ulong>();

    private sealed class Grant
    {
        public int ReqId;
        public string Text;
        public float SentAt;
    }

    private static void RememberGrant(ulong peer, int reqId, string text)
    {
        List<Grant> list = GrantsOf(peer);
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].ReqId == reqId) return;
        }
        Grant grant = new Grant();
        grant.ReqId = reqId;
        grant.Text = text;
        grant.SentAt = Time.unscaledTime;
        list.Add(grant);
        while (list.Count > MaxGrants)
        {
            CoopLog.Once("ItemHost.grants", "a peer owes more grant answers than the ledger keeps, the oldest one is given up; item or money request " + list[0].ReqId);
            list.RemoveAt(0);
        }
        if (CoopDebug.On("item")) CoopDebug.Event("item", "grant", "queued the grant of request " + reqId + " for " + peer);
    }

    private static void AckGrant(ulong peer, int reqId)
    {
        List<Grant> list = FindGrants(peer);
        if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].ReqId != reqId) continue;
            list.RemoveAt(i);
            if (CoopDebug.On("item")) CoopDebug.Event("item", "gack", "the grant of request " + reqId + " was applied by " + peer);
            break;
        }
        if (list.Count == 0) grants.Remove(peer);
    }

    private static bool ResendGrantsOnHello(ulong peer)
    {
        bool streaming = Outbox(peer) > 0;
        List<Grant> list = FindGrants(peer);
        if (list == null) return streaming;
        for (int i = 0; i < list.Count; i++) Enqueue(peer, list[i].Text, true);
        if (CoopDebug.On("item")) CoopDebug.Event("item", "grant", "re-sent " + list.Count + " pending grant(s) to " + peer + " after the handshake");
        return streaming;
    }

    private static bool HasGrant(ulong peer)
    {
        List<Grant> list = FindGrants(peer);
        return list != null && list.Count > 0;
    }

    private static void PruneGrants(float now)
    {
        if (grants.Count == 0) return;
        expiredGrants.Clear();
        foreach (KeyValuePair<ulong, List<Grant>> pair in grants)
        {
            List<Grant> list = pair.Value;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (now - list[i].SentAt < GrantLife) continue;
                CoopLog.Log.Info("coop: the grant of item or money request " + list[i].ReqId + " to " + pair.Key + " was given up after " + (int)GrantLife + " s without an answer");
                if (CoopDebug.On("item")) CoopDebug.Event("item", "grantdrop", "gave up the grant of request " + list[i].ReqId + " to " + pair.Key);
                list.RemoveAt(i);
            }
            if (list.Count == 0) expiredGrants.Add(pair.Key);
        }
        for (int i = 0; i < expiredGrants.Count; i++) grants.Remove(expiredGrants[i]);
    }

    private static readonly List<string> departedKeep = new List<string>();

    private static void Departed(ulong peer)
    {
        if (!HasGrant(peer))
        {
            outbox.Remove(peer);
            requesters.Remove(peer);
            return;
        }
        Queue<string> queue;
        if (!outbox.TryGetValue(peer, out queue)) return;
        departedKeep.Clear();
        foreach (string text in queue)
        {
            if (text.StartsWith("IGIVE|", StringComparison.Ordinal) || text.StartsWith("IMONEY|", StringComparison.Ordinal)) departedKeep.Add(text);
        }
        queue.Clear();
        for (int i = 0; i < departedKeep.Count; i++) queue.Enqueue(departedKeep[i]);
        if (queue.Count == 0) outbox.Remove(peer);
    }

    private static List<Grant> FindGrants(ulong peer)
    {
        List<Grant> list;
        return grants.TryGetValue(peer, out list) ? list : null;
    }

    private static List<Grant> GrantsOf(ulong peer)
    {
        List<Grant> list;
        if (!grants.TryGetValue(peer, out list))
        {
            list = new List<Grant>();
            grants[peer] = list;
        }
        return list;
    }
}
