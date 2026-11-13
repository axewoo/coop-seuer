using System;
using UnityEngine;

internal static partial class FreeRoam
{
    private const int MaxXfers = 64;
    private const float QuitCollectWait = 8f;

    private sealed class Xfer
    {
        public string Scene;
        public ulong Old;
        public ulong New;
        public int Gen;
        public int OldGen;
        public float At;
    }

    private static readonly System.Collections.Generic.Dictionary<string, Xfer> xfers = new System.Collections.Generic.Dictionary<string, Xfer>();
    private static string xferMineScene;
    private static int xferMineGen;
    private static System.Collections.Generic.HashSet<string> quitWanted;
    private static int quitAsked;
    private static float quitByeAt = -1f;
    private static string quitReason;
    private static bool quitLeave;
    private static bool quitByeSent;

    public static bool LeaveBypass;

    private static ulong DesiredHolder(string scene)
    {
        return string.Equals(scene, GameRefs.AreaId, StringComparison.Ordinal) ? Session.Me : HolderOf(scene);
    }

    private static bool XferOld(string scene, out ulong old)
    {
        Xfer x;
        if (!string.IsNullOrEmpty(scene) && xfers.TryGetValue(scene, out x))
        {
            old = x.Old;
            return true;
        }
        old = 0UL;
        return false;
    }

    private static void StartXfer(string scene, ulong old, ulong next, float now)
    {
        if (xfers.Count > MaxXfers && !xfers.ContainsKey(scene)) xfers.Clear();
        Xfer x = new Xfer();
        x.Scene = scene;
        x.Old = old;
        x.New = next;
        x.OldGen = Gen(scene);
        x.Gen = NextGen(scene);
        x.At = now;
        xfers[scene] = x;
        Session.Send(old, Channel, "HOLDXFER|" + scene + "|" + Wire(next) + "|" + Num(x.Gen), true);
        CoopLog.Log.Info("coop: " + scene + " is handed from guest " + old + " to " + next + ", the old holder is asked first (gen " + Num(x.Gen) + ")");
        if (CoopDebug.On("auth")) CoopDebug.Event("auth", "xfer sent " + scene, "HOLDXFER " + scene + " old " + old + " new " + next + " gen " + Num(x.Gen));
    }

    private static void TickXfers()
    {
        if (xfers.Count == 0) return;
        float now = Time.unscaledTime;
        System.Collections.Generic.List<string> keys = new System.Collections.Generic.List<string>(xfers.Keys);
        for (int i = 0; i < keys.Count; i++)
        {
            Xfer x;
            if (!xfers.TryGetValue(keys[i], out x)) continue;
            if (Stale(x))
            {
                CancelXfer(x, "the decision changed while the handoff was open");
                continue;
            }
            if (!Session.Peers.Contains(x.Old) || !string.Equals(Session.AreaOf(x.Old), x.Scene, StringComparison.Ordinal))
            {
                FinishXfer(x, "the old holder left the scene");
                continue;
            }
            if (now - x.At >= HoldWait) FinishXfer(x, "the old holder did not answer inside " + Num((int)HoldWait) + " s");
        }
    }

    private static bool Stale(Xfer x)
    {
        if (x.New == Session.Me) return !string.Equals(x.Scene, GameRefs.AreaId, StringComparison.Ordinal);
        return !Session.Peers.Contains(x.New) || !string.Equals(Session.AreaOf(x.New), x.Scene, StringComparison.Ordinal);
    }

    private static void CancelXfer(Xfer x, string why)
    {
        xfers.Remove(x.Scene);
        CoopLog.Log.Info("coop: the handoff of " + x.Scene + " to " + x.New + " was dropped (" + why + ")");
        if (CoopDebug.On("auth")) CoopDebug.Event("auth", "xfer drop " + x.Scene, x.Scene + " to " + x.New + " dropped, " + why);
    }

    private static void FinishXfer(Xfer x, string why)
    {
        float took = Time.unscaledTime - x.At;
        xfers.Remove(x.Scene);
        RememberXferGen(x.Scene, x.OldGen);
        if (Gen(x.Scene) < x.Gen) holdGen[x.Scene] = x.Gen;
        int gen = Gen(x.Scene);
        told[x.Scene] = x.New;
        System.Collections.Generic.List<ulong> list;
        bool here = arrivals.TryGetValue(x.Scene, out list);
        if (here)
        {
            for (int i = 0; i < list.Count; i++) SendHold(x.Scene, x.New, gen, list[i]);
        }
        if (x.Old != 0UL && x.Old != x.New && Session.Peers.Contains(x.Old) && (!here || !list.Contains(x.Old))) SendHold(x.Scene, x.New, gen, x.Old);
        CoopLog.Log.Info("coop: the handoff of " + x.Scene + " to " + x.New + " is done in " + Shot(took) + " s (" + why + ")");
        if (CoopDebug.On("auth")) CoopDebug.Event("auth", "xfer done " + x.Scene, x.Scene + " holder " + x.New + " gen " + Num(gen) + " after " + Shot(took) + " s, " + why);
    }

    private sealed class XferGen
    {
        public int Gen;
        public float At;
    }

    private static readonly System.Collections.Generic.Dictionary<string, XferGen> xferGen = new System.Collections.Generic.Dictionary<string, XferGen>();

    internal static bool HeldXferGen(string scene, int gen, out float at)
    {
        at = 0f;
        XferGen seen;
        if (string.IsNullOrEmpty(scene) || !xferGen.TryGetValue(scene, out seen) || seen.Gen != gen) return false;
        at = seen.At;
        return true;
    }

    private static void RememberXferGen(string scene, int gen)
    {
        if (string.IsNullOrEmpty(scene) || gen <= 0) return;
        if (xferGen.Count > MaxXfers && !xferGen.ContainsKey(scene)) xferGen.Clear();
        XferGen seen = new XferGen();
        seen.Gen = gen;
        seen.At = Time.unscaledTime;
        xferGen[scene] = seen;
    }

    private static void HostXferAck(ulong from, string[] f)
    {
        Xfer x;
        if (!xfers.TryGetValue(f[1], out x))
        {
            if (CoopDebug.On("auth")) CoopDebug.Event("auth", "xfer ack late " + f[1], "XFERACK for " + f[1] + " from " + from + " has no open handoff");
            return;
        }
        if (x.Old != from)
        {
            CoopLog.Log.Info("coop: the handoff answer for " + f[1] + " from " + from + " was dropped, guest " + x.Old + " is the old holder");
            return;
        }
        int gen;
        if (!int.TryParse(f[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out gen)) return;
        if (gen != x.Gen)
        {
            CoopLog.Log.Info("coop: the handoff answer for " + f[1] + " named gen " + Num(gen) + " instead of " + Num(x.Gen));
            if (CoopDebug.On("auth")) CoopDebug.Event("auth", "xfer ack gen " + f[1], "XFERACK " + f[1] + " gen " + Num(gen) + " is not " + Num(x.Gen));
            return;
        }
        CoopLog.Log.Info("coop: the old holder " + from + " answered the handoff of " + f[1] + " (gen " + Num(gen) + ")");
        if (CoopDebug.On("auth")) CoopDebug.Event("auth", "xfer ack recv " + f[1], "XFERACK " + f[1] + " from " + from + " gen " + Num(gen));
        FinishXfer(x, "the old holder answered");
    }

    private static void HandleHoldXfer(string[] f)
    {
        ulong next;
        int gen;
        if (!Plain(f[1])) return;
        if (!ulong.TryParse(f[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out next)) return;
        if (!int.TryParse(f[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out gen)) return;
        if (CoopDebug.On("auth")) CoopDebug.Event("auth", "xfer recv " + f[1], "HOLDXFER " + f[1] + " new holder " + next + " gen " + Num(gen));
        if (next == Session.Me) return;
        ulong named;
        bool held = holders.TryGetValue(f[1], out named) && named == Session.Me;
        if (!held || !string.Equals(GameRefs.AreaId, f[1], StringComparison.Ordinal))
        {
            AnswerXfer(f[1], gen);
            return;
        }
        xferMineScene = f[1];
        xferMineGen = gen;
        int asked = FlushFollowers();
        if (asked > 0)
        {
            if (CoopDebug.On("auth")) CoopDebug.Event("auth", "xfer flush " + f[1], "asked " + asked + " follower(s) of " + f[1] + " to close their containers before the handoff");
            shipAt = Time.realtimeSinceStartup + FlushWait;
            return;
        }
        if (CoopDebug.On("auth")) CoopDebug.Event("auth", "xfer flush " + f[1], "no follower is in " + f[1] + ", the copy is handed in at once");
        ShipMine("the planned handoff of " + f[1]);
        AnswerXfer(f[1], gen);
    }

    private static void AnswerXfer(string scene, int gen)
    {
        if (xferMineScene != null && string.Equals(xferMineScene, scene, StringComparison.Ordinal))
        {
            xferMineScene = null;
            xferMineGen = 0;
        }
        if (!Session.IsGuest) return;
        Session.Send(Session.HostId, Channel, "XFERACK|" + scene + "|" + Num(gen), true);
        CoopLog.Log.Info("coop: the handoff of " + scene + " was answered for gen " + Num(gen));
        if (CoopDebug.On("auth")) CoopDebug.Event("auth", "xfer ack sent " + scene, "XFERACK " + scene + " gen " + Num(gen) + " to the host");
    }

    public static void BeginQuitCollect(string reason, float wait)
    {
        try
        {
            if (!Session.IsHost || !Session.IsInLobby)
            {
                Bye(reason);
                return;
            }
            System.Collections.Generic.HashSet<string> asked = AskHolders();
            PinStore();
            if (asked == null || asked.Count == 0)
            {
                CoopLog.Log.Info("coop: no guest holds a scene of its own, the goodbye goes out at once");
                if (CoopDebug.On("auth")) CoopDebug.Event("auth", "quit", "no guest held scene to collect, the goodbye goes out at once");
                Bye(reason);
                return;
            }
            quitWanted = asked;
            quitAsked = asked.Count;
            quitReason = reason;
            quitLeave = false;
            if (string.Equals(reason, "reload", StringComparison.Ordinal))
            {
                quitByeSent = true;
                Bye(reason);
            }
            quitByeAt = Time.realtimeSinceStartup + wait;
            CoopLog.Log.Info("coop: the goodbye waits up to " + wait + " s for " + quitAsked + " guest held scene(s)");
        }
        catch (Exception e)
        {
            CoopLog.Once("FreeRoam.BeginQuitCollect", e);
            Bye(reason);
        }
    }

    public static bool LeaveCollect()
    {
        try
        {
            if (quitByeAt >= 0f)
            {
                if (!quitLeave)
                {
                    quitLeave = true;
                    quitReason = "closed";
                    CoopLog.Log.Info("coop: the leave is added to the goodbye that is already waiting");
                }
                return true;
            }
            if (!Session.IsHost || !Session.IsInLobby) return false;
            System.Collections.Generic.HashSet<string> asked = AskHolders();
            PinStore();
            if (asked == null || asked.Count == 0)
            {
                CoopLog.Log.Info("coop: no guest holds a scene of its own, the leave is not deferred");
                return false;
            }
            quitWanted = asked;
            quitAsked = asked.Count;
            quitReason = "closed";
            quitLeave = true;
            quitByeAt = Time.realtimeSinceStartup + QuitCollectWait;
            CoopLog.Log.Info("coop: the leave waits up to " + QuitCollectWait + " s for " + quitAsked + " guest held scene(s)");
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("FreeRoam.LeaveCollect", e);
            return false;
        }
    }

    public static void QuitNow()
    {
        try
        {
            if (!Session.IsHost || !Session.IsInLobby)
            {
                Bye(null);
                return;
            }
            System.Collections.Generic.HashSet<string> asked = AskHolders();
            int count = asked == null ? 0 : asked.Count;
            CoopLog.Log.Info("coop: the desktop quit asks " + count + " guest held scene(s) and cannot wait for their copies");
            if (CoopDebug.On("auth")) CoopDebug.Event("auth", "quit desktop", "SCENEREQ for " + count + " scene(s), the wait is skipped because the process is ending");
            Bye(null);
        }
        catch (Exception e)
        {
            CoopLog.Once("FreeRoam.QuitNow", e);
            Bye(null);
        }
    }

    private static System.Collections.Generic.HashSet<string> AskHolders()
    {
        System.Collections.Generic.HashSet<string> asked = new System.Collections.Generic.HashSet<string>();
        System.Collections.Generic.List<string> scenes = new System.Collections.Generic.List<string>(arrivals.Keys);
        for (int i = 0; i < scenes.Count; i++)
        {
            ulong holder = HolderOf(scenes[i]);
            if (holder == 0UL || !Session.Peers.Contains(holder)) continue;
            Session.Send(holder, Channel, "SCENEREQ|" + scenes[i], true);
            asked.Add(scenes[i]);
            if (CoopDebug.On("auth")) CoopDebug.Event("auth", "quit req " + scenes[i], "SCENEREQ " + scenes[i] + " asked of guest " + holder + " before the goodbye");
        }
        return asked;
    }

    private static void TickQuit(float now)
    {
        if (quitByeAt < 0f) return;
        if (now < quitByeAt) return;
        SendQuitBye("the wait ended");
    }

    public static void QuitBlobScene(string scene)
    {
        if (quitWanted == null || scene == null || !quitWanted.Remove(scene)) return;
        int got = quitAsked - quitWanted.Count;
        CoopLog.Log.Info("coop: the copy of " + scene + " arrived while the goodbye waited (" + got + " of " + quitAsked + ")");
        if (CoopDebug.On("auth")) CoopDebug.Event("auth", "quit blob " + scene, scene + " arrived, " + got + " of " + quitAsked + " copies are in");
        if (quitWanted.Count == 0) SendQuitBye("every holder answered");
    }

    private static void SendQuitBye(string why)
    {
        quitByeAt = -1f;
        string reason = quitReason;
        bool leave = quitLeave;
        bool sent = quitByeSent;
        int want = quitAsked;
        int got = quitAsked - (quitWanted == null ? 0 : quitWanted.Count);
        quitWanted = null;
        quitAsked = 0;
        quitReason = null;
        quitLeave = false;
        quitByeSent = false;
        CoopLog.Log.Info("coop: the goodbye goes out with " + got + " of " + want + " copies collected (" + why + ")");
        if (CoopDebug.On("auth")) CoopDebug.Event("auth", "quit bye", "BYE after " + got + " of " + want + " copies, " + why);
        if (!sent && !leave) Bye(reason);
        if (!leave) return;
        LeaveBypass = true;
        try { Session.Leave(); }
        finally { LeaveBypass = false; }
    }

    private static void Bye(string reason)
    {
        if (reason == null) Session.SayGoodbye();
        else Session.SayGoodbye(reason);
    }

    private static void HandoffReset()
    {
        xfers.Clear();
        xferGen.Clear();
        xferMineScene = null;
        xferMineGen = 0;
        quitWanted = null;
        quitAsked = 0;
        quitByeAt = -1f;
        quitReason = null;
        quitLeave = false;
        quitByeSent = false;
    }

    private static string Wire(ulong value)
    {
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Shot(float seconds)
    {
        return seconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    }
}
