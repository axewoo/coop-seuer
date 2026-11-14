using System;
using System.Collections.Generic;
using PixelCrushers.DialogueSystem;
using UnityEngine;

internal static class TalkHost
{
    private const float IdleSeconds = 600f;
    private const int MaxWritesPerSecond = 128;
    private const int MaxWritesPerLock = 5000;
    private const int MaxNewVarsPerLock = 200;
    private const int MaxLocks = 64;
    private const int MaxShared = 2048;
    private const int MaxOutbox = 2048;
    private const int MaxSendPerTick = 64;
    private static readonly Dictionary<string, Held> locks = new Dictionary<string, Held>();
    private static readonly Dictionary<string, string> shared = new Dictionary<string, string>();
    private static readonly List<Queued> outbox = new List<Queued>();
    private static int applying;
    private static readonly List<string> stale = new List<string>();
    private static ulong rateHolder;
    private static float rateAt;
    private static int rateCount;

    private sealed class Held
    {
        public string Npc;
        public ulong Holder;
        public bool HostOwned;
        public float At;
        public int Writes;
        public int NewVars;
    }

    private sealed class Queued
    {
        public ulong Peer;
        public string Name;
        public string Payload;
    }

    private static bool Active
    {
        get { return Session.IsInLobby && Session.IsHost; }
    }

    public static void Init()
    {
    }

    public static void Reset()
    {
        locks.Clear();
        shared.Clear();
        outbox.Clear();
    }

    public static void OnSceneChanged()
    {
        try
        {
            List<string> keys = new List<string>(locks.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                Held held;
                if (!locks.TryGetValue(keys[i], out held)) continue;
                if (!held.HostOwned) Session.Send(held.Holder, TalkWire.Channel, TalkWire.Revoke(keys[i]), true);
                Release(keys[i]);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("TalkHost.OnSceneChanged", e);
        }
        locks.Clear();
    }

    public static void Tick()
    {
        try
        {
            Flush();
            if (locks.Count == 0) return;
            float now = Time.unscaledTime;
            bool hostTalking = Talking();
            stale.Clear();
            foreach (KeyValuePair<string, Held> pair in locks)
            {
                Held held = pair.Value;
                if (held.HostOwned)
                {
                    if (hostTalking) held.At = now;
                    else if (now - held.At >= IdleSeconds) stale.Add(held.Npc);
                }
                else if (!Session.Peers.Contains(held.Holder) || now - held.At >= IdleSeconds)
                {
                    stale.Add(held.Npc);
                }
            }
            for (int i = 0; i < stale.Count; i++)
            {
                Held done;
                if (!locks.TryGetValue(stale[i], out done)) continue;
                string why = done.HostOwned || Session.Peers.Contains(done.Holder) ? " after " + (int)IdleSeconds + "s idle" : ", the holder left";
                CoopLog.Log.Info("coop: talk lock on " + stale[i] + " released" + why);
                Release(stale[i]);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("TalkHost.Tick", e);
        }
    }

    public static void Handle(ulong from, TalkMsg m)
    {
        try
        {
            if (!Active || !Session.Peers.Contains(from)) return;
            if (m.Kind == "TREQ") Request(from, m);
            else if (m.Kind == "TEND") End(from, m);
            else if (m.Kind == "TVARS") Write(from, m);
            else if (m.Kind == "THWANT") Want(from);
        }
        catch (Exception e)
        {
            CoopLog.Once("TalkHost.Handle", e);
        }
    }

    public static void HostStarted(string npc)
    {
        try
        {
            if (!Active || npc == null) return;
            Held held;
            if (locks.TryGetValue(npc, out held) && !held.HostOwned)
            {
                CoopLog.Log.Info("coop: the host took the talk lock on " + npc + " from " + Session.DisplayName(held.Holder));
                Session.Send(held.Holder, TalkWire.Channel, TalkWire.Revoke(npc), true);
                SendAll(TalkWire.Lock(npc, 0));
                locks.Remove(npc);
            }
            if (!locks.ContainsKey(npc) && locks.Count >= MaxLocks) return;
            Held entry = new Held();
            entry.Npc = npc;
            entry.Holder = Session.Me;
            entry.HostOwned = true;
            entry.At = Time.unscaledTime;
            locks[npc] = entry;
            SendAll(TalkWire.Lock(npc, Session.Me));
            CoopLog.Log.Info("coop: talk lock taken by the host on " + npc);
        }
        catch (Exception e)
        {
            CoopLog.Once("TalkHost.HostStarted", e);
        }
    }

    public static void HostEnded(string npc)
    {
        try
        {
            if (!Active || npc == null) return;
            Held held;
            if (!locks.TryGetValue(npc, out held) || !held.HostOwned) return;
            CoopLog.Log.Info("coop: talk lock on " + npc + " released by the host");
            Release(npc);
        }
        catch (Exception e)
        {
            CoopLog.Once("TalkHost.HostEnded", e);
        }
    }

    private static bool Talking()
    {
        try
        {
            DialogueController controller = DialogueController.instance;
            if (controller == null) return false;
            return controller.DialogueActive;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Request(ulong from, TalkMsg m)
    {
        Held held;
        if (locks.TryGetValue(m.Npc, out held) && held.Holder != from)
        {
            string who = held.HostOwned ? "the host" : Session.DisplayName(held.Holder);
            Session.Send(from, TalkWire.Channel, TalkWire.Deny(m.ReqId, "someone else is using that"), true);
            CoopLog.Log.Info("coop: talk refused to " + Session.DisplayName(from) + " on " + m.Npc + ", " + who + " holds it");
            return;
        }
        List<string> other = null;
        foreach (KeyValuePair<string, Held> pair in locks)
        {
            if (!pair.Value.HostOwned && pair.Value.Holder == from && pair.Key != m.Npc)
            {
                if (other == null) other = new List<string>();
                other.Add(pair.Key);
            }
        }
        if (other != null)
        {
            for (int i = 0; i < other.Count; i++)
            {
                CoopLog.Log.Info("coop: the talk lock on " + other[i] + " was released for the talk on " + m.Npc);
                Release(other[i]);
            }
        }
        if (!locks.ContainsKey(m.Npc) && locks.Count >= MaxLocks)
        {
            Session.Send(from, TalkWire.Channel, TalkWire.Deny(m.ReqId, "too many talks are locked"), true);
            CoopLog.Log.Info("coop: talk refused to " + Session.DisplayName(from) + ", the host holds " + locks.Count + " locks");
            return;
        }
        Held entry = new Held();
        entry.Npc = m.Npc;
        entry.Holder = from;
        entry.HostOwned = false;
        entry.At = Time.unscaledTime;
        locks[m.Npc] = entry;
        Session.Send(from, TalkWire.Channel, TalkWire.Ok(m.ReqId, m.Npc), true);
        SendAll(TalkWire.Lock(m.Npc, from));
        CoopLog.Log.Info("coop: talk granted to " + Session.DisplayName(from) + " on " + m.Npc);
    }

    private static void End(ulong from, TalkMsg m)
    {
        Held held;
        if (!locks.TryGetValue(m.Npc, out held) || held.Holder != from || held.HostOwned) return;
        CoopLog.Log.Info("coop: talk lock on " + m.Npc + " released by " + Session.DisplayName(from));
        Release(m.Npc);
    }

    private static void Write(ulong from, TalkMsg m)
    {
        if (!TalkWire.IsWrite(m)) return;
        Held held;
        if (!locks.TryGetValue(m.Npc, out held) || held.HostOwned || held.Holder != from)
        {
            CoopLog.Once("TalkHost.Write." + m.Npc, "coop: a Lua write on " + m.Npc + " was dropped, " + Session.DisplayName(from) + " does not hold the talk lock");
            return;
        }
        string reason;
        if (!Rate(from)) return;
        if (!TalkWire.Valid(m, out reason))
        {
            CoopLog.Once("TalkHost.Valid." + m.Npc, "coop: a talk write on " + m.Npc + " from " + Session.DisplayName(from) + " was refused: " + reason);
            return;
        }
        if (TalkWire.NewVariable(m))
        {
            if (held.NewVars >= MaxNewVarsPerLock)
            {
                CoopLog.Once("TalkHost.NewVar." + m.Npc, "coop: the talk lock on " + m.Npc + " reached " + MaxNewVarsPerLock + " new variables, the rest of that talk is dropped");
                return;
            }
            held.NewVars++;
        }
        if (held.Writes >= MaxWritesPerLock)
        {
            CoopLog.Once("TalkHost.Cap." + m.Npc, "coop: the talk lock on " + m.Npc + " reached " + MaxWritesPerLock + " writes, the rest of that talk is dropped");
            return;
        }
        held.Writes++;
        held.At = Time.unscaledTime;
        Apply(m, true);
        foreach (ulong peer in Session.Peers)
        {
            if (peer == from) continue;
            Session.Send(peer, TalkWire.Channel, m.Raw, true);
        }
    }

    private static bool Rate(ulong from)
    {
        float now = Time.unscaledTime;
        if (from != rateHolder || now - rateAt >= 1f)
        {
            rateHolder = from;
            rateAt = now;
            rateCount = 0;
        }
        rateCount++;
        if (rateCount > MaxWritesPerSecond)
        {
            CoopLog.Once("TalkHost.Rate", "coop: more than " + MaxWritesPerSecond + " talk writes arrived in one second, the rest of that burst is dropped");
            return false;
        }
        return true;
    }
    internal static void Apply(TalkMsg m, bool log)
    {
        try
        {
            applying++;
            try
            {
                TalkWire.Apply(m);
            }
            finally
            {
                applying--;
            }
            if (log) CoopLog.Log.Info("coop: applied a talk " + (m.Kind == "V" ? "variable " + m.Table : "field " + m.Table + "." + m.Element + "." + m.Field) + " from a guest");
        }
        catch (Exception e)
        {
            CoopLog.Once("TalkHost.Apply", e);
        }
    }

    public static void NoteVariable(string name, object value)
    {
        try
        {
            if (!Active || name == null || name.Length == 0) return;
            if (!TalkWire.Shared(name)) return;
            string payload = TalkWire.HostVar(name, value);
            string seen;
            if (shared.TryGetValue(name, out seen) && seen == payload) return;
            if (!shared.ContainsKey(name) && shared.Count >= MaxShared)
            {
                CoopLog.Once("TalkHost.Shared", "coop: the host holds " + MaxShared + " shared dialogue variables, the rest of this session is not broadcast");
                return;
            }
            shared[name] = payload;
            if (applying > 0) return;
            foreach (ulong peer in Session.Peers)
            {
                Queue(peer, name, payload);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("TalkHost.NoteVariable", e);
        }
    }

    private static void Want(ulong from)
    {
        int count = 0;
        foreach (KeyValuePair<string, string> pair in shared)
        {
            Queue(from, pair.Key, pair.Value);
            count++;
        }
        CoopLog.Log.Info("coop: " + count + " host dialogue variables were sent to " + Session.DisplayName(from));
    }

    private static void Queue(ulong peer, string name, string payload)
    {
        for (int i = 0; i < outbox.Count; i++)
        {
            if (outbox[i].Peer != peer || outbox[i].Name != name) continue;
            outbox[i].Payload = payload;
            return;
        }
        if (outbox.Count >= MaxOutbox)
        {
            CoopLog.Once("TalkHost.Outbox", "coop: " + MaxOutbox + " talk messages are waiting, the rest is dropped");
            return;
        }
        Queued item = new Queued();
        item.Peer = peer;
        item.Name = name;
        item.Payload = payload;
        outbox.Add(item);
    }

    private static void Flush()
    {
        if (outbox.Count == 0) return;
        int count = outbox.Count < MaxSendPerTick ? outbox.Count : MaxSendPerTick;
        for (int i = 0; i < count; i++)
        {
            Queued item = outbox[i];
            if (Session.Peers.Contains(item.Peer)) Session.Send(item.Peer, TalkWire.Channel, item.Payload, true);
        }
        outbox.RemoveRange(0, count);
    }

    private static void Release(string npc)
    {
        locks.Remove(npc);
        SendAll(TalkWire.Lock(npc, 0));
    }

    private static void SendAll(string payload)
    {
        foreach (ulong peer in Session.Peers)
        {
            Session.Send(peer, TalkWire.Channel, payload, true);
        }
    }
}
