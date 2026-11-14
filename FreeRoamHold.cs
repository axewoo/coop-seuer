using System;
using UnityEngine;

internal static partial class FreeRoam
{
    private const float TellRepeat = 5f;
    private const float HolderLapse = 20f;
    private const float NoticeTrust = 12f;
    private const int MaxHoldScenes = 64;

    private static readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<ulong>> arrivals = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<ulong>>();

    private static ulong HolderOf(string scene)
    {
        System.Collections.Generic.List<ulong> list;
        if (string.Equals(scene, GameRefs.AreaId, StringComparison.Ordinal) || !arrivals.TryGetValue(scene, out list) || list.Count == 0) return 0UL;
        return list[0];
    }

    private static readonly System.Collections.Generic.Dictionary<string, ulong> told = new System.Collections.Generic.Dictionary<string, ulong>();
    private static readonly System.Collections.Generic.Dictionary<string, ulong> holders = new System.Collections.Generic.Dictionary<string, ulong>();
    private static readonly System.Collections.Generic.Dictionary<string, float> heardAt = new System.Collections.Generic.Dictionary<string, float>();
    private static readonly System.Collections.Generic.Dictionary<string, int> holdGen = new System.Collections.Generic.Dictionary<string, int>();
    private static readonly System.Collections.Generic.HashSet<string> staleHoldLogged = new System.Collections.Generic.HashSet<string>();
    private static readonly System.Collections.Generic.HashSet<string> lapseLogged = new System.Collections.Generic.HashSet<string>();
    private static float nextTell;
    private static string hostHere;
    private static string hostLeft;

    public static ulong Authority
    {
        get { return AuthorityOf(GameRefs.AreaId); }
    }

    public static bool IAmAuthority
    {
        get { return Session.IsInLobby && GameRefs.AreaId != null && Authority == Session.Me; }
    }

    public static ulong AuthorityOf(string scene)
    {
        if (string.IsNullOrEmpty(scene)) return 0UL;
        if (Session.IsHost)
        {
            ulong held;
            if (XferOld(scene, out held)) return held;
            return string.Equals(scene, GameRefs.AreaId, StringComparison.Ordinal) ? Session.Me : HolderOf(scene);
        }
        if (Session.AreaOf(Session.HostId) == scene) return Session.HostId;
        ulong id;
        float at;
        if (!holders.TryGetValue(scene, out id) || !heardAt.TryGetValue(scene, out at)) return 0UL;
        if (id == Session.Me && Time.unscaledTime - at > HolderLapse && HostReachable())
        {
            Lapsed(scene);
            return 0UL;
        }
        if (Time.unscaledTime - at > NoticeTrust && !(id == Session.Me ? GameRefs.AreaId == scene : Session.AreaOf(id) == scene)) return 0UL;
        return id;
    }

    private static bool HostReachable()
    {
        return Session.IsInLobby && Session.Peers.Contains(Session.HostId);
    }

    private static void Lapsed(string scene)
    {
        if (lapseLogged.Count > MaxHoldScenes) lapseLogged.Clear();
        if (!lapseLogged.Add(scene)) return;
        CoopLog.Log.Info("coop: authority lapsed: no HOLD for 20 s in " + scene);
        if (CoopDebug.On("auth")) CoopDebug.Event("auth", "lapse " + scene, scene + " has no HOLD for 20 s, gen " + Num(Gen(scene)) + ", this machine steps down");
    }

    private static void TellHolders()
    {
        float now = Time.unscaledTime;
        bool periodic = now >= nextTell;
        if (periodic) nextTell = now + TellRepeat;
        string here = GameRefs.AreaId;
        hostLeft = null;
        if (!string.Equals(here, hostHere, StringComparison.Ordinal))
        {
            hostLeft = hostHere;
            hostHere = here;
            Bump(hostLeft);
            Bump(here);
        }
        foreach (System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.List<ulong>> pair in arrivals)
        {
            ulong holder = DesiredHolder(pair.Key);
            ulong last;
            told.TryGetValue(pair.Key, out last);
            bool moved = hostLeft != null && (string.Equals(pair.Key, here, StringComparison.Ordinal) || string.Equals(pair.Key, hostLeft, StringComparison.Ordinal));
            if (holder == last && !periodic && !moved) continue;
            if (xfers.ContainsKey(pair.Key)) continue;
            if (holder != last && last != 0UL && Session.Peers.Contains(last) && string.Equals(Session.AreaOf(last), pair.Key, StringComparison.Ordinal))
            {
                StartXfer(pair.Key, last, holder, now);
                continue;
            }
            if (holder != last) Bump(pair.Key);
            int gen = Gen(pair.Key);
            told[pair.Key] = holder;
            for (int i = 0; i < pair.Value.Count; i++) SendHold(pair.Key, holder, gen, pair.Value[i]);
            if (holder != last && last != 0UL && Session.Peers.Contains(last) && !pair.Value.Contains(last)) SendHold(pair.Key, holder, gen, last);
        }
    }

    private static void SendHold(string scene, ulong holder, int gen, ulong peer)
    {
        Session.Send(peer, Channel, "HOLD|" + scene + "|" + holder.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + Num(gen), true);
        if (CoopDebug.On("auth")) CoopDebug.Event("auth", "hold sent " + scene, "HOLD " + scene + " holder " + holder + " gen " + Num(gen) + " to " + peer);
    }

    private static void TrackArrivals()
    {
        foreach (System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.List<ulong>> pair in arrivals)
        {
            pair.Value.RemoveAll(p => !Session.Peers.Contains(p) || Session.AreaOf(p) != pair.Key);
        }
        foreach (ulong peer in Session.Peers)
        {
            string area = Session.AreaOf(peer);
            if (string.IsNullOrEmpty(area)) continue;
            System.Collections.Generic.List<ulong> list;
            if (!arrivals.TryGetValue(area, out list))
            {
                if (arrivals.Count > 128) arrivals.Clear();
                arrivals[area] = list = new System.Collections.Generic.List<ulong>();
            }
            if (!list.Contains(peer)) list.Add(peer);
        }
    }

    private static void HandleHold(string[] f)
    {
        ulong id;
        int gen = 0;
        if (!Plain(f[1]) || !ulong.TryParse(f[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id)) return;
        if (f.Length == 4 && !int.TryParse(f[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out gen)) return;
        if (holders.Count > MaxHoldScenes)
        {
            holders.Clear();
            heardAt.Clear();
            holdGen.Clear();
        }
        int seen = Gen(f[1]);
        if (gen < seen)
        {
            StaleHold(f[1], gen, seen);
            return;
        }
        holders[f[1]] = id;
        heardAt[f[1]] = Time.unscaledTime;
        holdGen[f[1]] = gen;
        if (id == Session.Me) lapseLogged.Remove(f[1]);
        if (CoopDebug.On("auth")) CoopDebug.Event("auth", "hold recv " + f[1], "HOLD " + f[1] + " holder " + id + " gen " + Num(gen));
    }

    private static void StaleHold(string scene, int gen, int seen)
    {
        if (staleHoldLogged.Count > MaxHoldScenes) staleHoldLogged.Clear();
        if (!staleHoldLogged.Add(scene)) return;
        CoopLog.Log.Info("coop: stale HOLD ignored for " + scene + " (gen " + Num(gen) + ", seen " + Num(seen) + ")");
        if (CoopDebug.On("auth")) CoopDebug.Event("auth", "hold stale " + scene, scene + " gen " + Num(gen) + " is behind " + Num(seen));
    }

    private static int Gen(string scene)
    {
        int gen;
        return scene != null && holdGen.TryGetValue(scene, out gen) ? gen : 0;
    }

    private static int epochGen;
    private static readonly System.Collections.Generic.List<string> holdOrder = new System.Collections.Generic.List<string>();

    private static void Bump(string scene)
    {
        NextGen(scene);
    }

    private static int NextGen(string scene)
    {
        if (string.IsNullOrEmpty(scene)) return 0;
        int seed = HoldSeed(scene);
        if (epochGen < seed) epochGen = seed;
        epochGen++;
        EvictHold(scene);
        holdGen[scene] = epochGen;
        return epochGen;
    }

    private static void EvictHold(string scene)
    {
        if (holdGen.ContainsKey(scene))
        {
            if (!holdOrder.Contains(scene)) holdOrder.Add(scene);
            return;
        }
        while (holdGen.Count >= MaxHoldScenes && holdOrder.Count > 0)
        {
            string oldest = holdOrder[0];
            holdOrder.RemoveAt(0);
            holdGen.Remove(oldest);
        }
        if (!holdOrder.Contains(scene)) holdOrder.Add(scene);
    }

    private static string Num(int value)
    {
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
