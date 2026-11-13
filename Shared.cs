using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

internal static class Shared
{
    private const int MaxReqId = 999999999;
    private const float PruneInterval = 1f;
    private static readonly Dictionary<string, Stored> answers = new Dictionary<string, Stored>();
    private static readonly List<string> gone = new List<string>();
    private static readonly List<string> keys = new List<string>();
    private static int counter;
    private static int applying;
    private static float nextPrune;

    private sealed class Stored
    {
        public int Req;
        public string Kind;
        public string Reply;
        public ulong Peer;
    }

    public static bool Applying
    {
        get { return applying > 0; }
    }

    public static void Init()
    {
    }

    public static void Reset()
    {
        answers.Clear();
        gone.Clear();
        counter = 0;
        applying = 0;
        nextPrune = 0f;
    }

    public static void Tick()
    {
        try
        {
            float now = Time.unscaledTime;
            if (now < nextPrune) return;
            nextPrune = now + PruneInterval;
            gone.Clear();
            foreach (KeyValuePair<string, Stored> pair in answers)
            {
                if (!Session.Peers.Contains(pair.Value.Peer)) gone.Add(pair.Key);
            }
            for (int i = 0; i < gone.Count; i++) answers.Remove(gone[i]);
        }
        catch (Exception e)
        {
            CoopLog.Once("Shared.Tick", e);
        }
    }

    public static int NextReqId()
    {
        counter++;
        if (counter < 1 || counter > MaxReqId) counter = 1;
        return counter;
    }

    private static string StoreKey(ulong peer, string channel)
    {
        return peer.ToString(CultureInfo.InvariantCulture) + "|" + channel;
    }

    public static void Enter()
    {
        applying++;
    }

    public static void Exit()
    {
        if (applying > 0) applying--;
    }

    public static void Apply(Action body)
    {
        Enter();
        try
        {
            body();
        }
        finally
        {
            Exit();
        }
    }

    public static bool Admit(ulong peer, string channel, int reqId, string kind)
    {
        if (!Session.Peers.Contains(peer)) return false;
        Stored stored;
        if (answers.TryGetValue(StoreKey(peer, channel), out stored) && stored.Req == reqId && stored.Kind == kind)
        {
            CoopLog.Log.Debug("shared.retry", "repeating the answer to the " + kind + " request " + reqId);
            if (CoopDebug.On("net")) CoopDebug.Event("net", "admit." + channel, "the " + kind + " request " + reqId + " from " + peer + " on channel " + channel + " was a repeat, the stored answer was sent again");
            if (stored.Reply != null) Session.Send(peer, channel, stored.Reply, true);
            return false;
        }
        return true;
    }

    public static void Answer(ulong peer, string channel, int reqId, string kind, string reply)
    {
        Stored stored = new Stored();
        stored.Req = reqId;
        stored.Kind = kind;
        stored.Reply = reply;
        stored.Peer = peer;
        answers[StoreKey(peer, channel)] = stored;
        Session.Send(peer, channel, reply, true);
    }

    public static void Forget(ulong peer)
    {
        string prefix = peer.ToString(CultureInfo.InvariantCulture) + "|";
        keys.Clear();
        foreach (KeyValuePair<string, Stored> pair in answers)
        {
            if (pair.Key.StartsWith(prefix, StringComparison.Ordinal)) keys.Add(pair.Key);
        }
        for (int i = 0; i < keys.Count; i++) answers.Remove(keys[i]);
    }
}
