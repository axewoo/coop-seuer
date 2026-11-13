using System;
using System.Collections.Generic;
using PixelCrushers.DialogueSystem;
using UnityEngine;

internal static class TalkGuest
{
    private const float RetrySeconds = 3f;
    private const float GiveUpSeconds = 10f;
    private const float GrantSeconds = 10f;
    private const int MaxBusy = 64;
    private static readonly HashSet<string> busy = new HashSet<string>();
    private static readonly HashSet<string> refused = new HashSet<string>();
    private static int nextReq = 1;
    private static Pending pending;
    private static string granted = "";
    private static float grantedAt;
    private static float captureUntil;
    private static bool live;
    private static bool arrived;
    private static int applying;

    private sealed class Pending
    {
        public int ReqId;
        public string Npc;
        public InteractableTalk Talk;
        public float At;
        public float NextTry;
    }

    public static void Init()
    {
    }

    public static void Reset()
    {
        if (granted.Length > 0 && Session.IsGuest && Session.IsInLobby && GuestSession.InHostWorld) Send(TalkWire.End(granted));
        pending = null;
        granted = "";
        grantedAt = 0f;
        captureUntil = 0f;
        live = false;
        arrived = false;
        busy.Clear();
    }

    public static void OnSceneChanged()
    {
        Reset();
    }

    public static bool Allows(string npc)
    {
        return npc != null && granted.Length > 0 && granted == npc;
    }

    public static bool Start(InteractableTalk talk)
    {
        if (!GuestSession.InHostWorld) return true;
        if (talk == null) return false;
        if (GuestSession.Following)
        {
            GuestGate.Notice(GuestGate.KindDialogue, "Waiting to follow the host");
            return false;
        }
        string npc = TalkWire.NpcKey(talk);
        if (npc == null) return false;
        if (Allows(npc)) return true;
        if (busy.Contains(npc))
        {
            GuestGate.Notice(GuestGate.KindDialogue, "Someone else is talking to them");
            return false;
        }
        if (pending != null && pending.Npc == npc)
        {
            GuestGate.Notice(GuestGate.KindDialogue, "Asking the host to talk");
            return false;
        }
        Request(talk, npc);
        return false;
    }

    public static void Started(string npc)
    {
        if (!Allows(npc)) return;
        grantedAt = Time.unscaledTime;
        live = true;
    }

    public static void Ended(string npc)
    {
        if (npc == null || granted.Length == 0 || granted != npc) return;
        Send(TalkWire.End(granted));
        CoopLog.Log.Info("coop: talk ended on " + npc + ", the lock is released");
        granted = "";
        grantedAt = 0f;
        captureUntil = 0f;
        live = false;
    }

    public static void Tick()
    {
        try
        {
            Arrival();
            float now = Time.unscaledTime;
            if (pending != null)
            {
                if (now - pending.At >= GiveUpSeconds)
                {
                    CoopLog.Log.Info("coop: the host did not answer the talk request for " + pending.Npc);
                    GuestGate.Notice(GuestGate.KindDialogue, "The host did not answer, the talk did not start");
                    pending = null;
                }
                else if (now >= pending.NextTry)
                {
                    pending.NextTry = now + RetrySeconds;
                    Send(TalkWire.Req(pending.ReqId, pending.Npc));
                }
            }
            if (!live && granted.Length > 0 && now - grantedAt >= GrantSeconds)
            {
                CoopLog.Log.Info("coop: the talk grant on " + granted + " was dropped, the talk never started");
                Send(TalkWire.End(granted));
                granted = "";
                grantedAt = 0f;
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("TalkGuest.Tick", e);
        }
    }

    public static void Handle(ulong from, TalkMsg m)
    {
        try
        {
            if (Session.IsHost || from != Session.HostId) return;
            if (m.Kind == "TOK") Granted_(m);
            else if (m.Kind == "TDENY") Denied(m);
            else if (m.Kind == "TLOCK") Locked(m);
            else if (m.Kind == "TVARS") Remote(m);
            else if (m.Kind == "THVAR") HostVar(m);
            else if (m.Kind == "TREV") Revoked(m);
        }
        catch (Exception e)
        {
            CoopLog.Once("TalkGuest.Handle", e);
        }
    }

    public static void NoteVariable(string name, object value)
    {
        if (!Capturing()) return;
        if (Participant(name)) return;
        if (TalkWire.Blocked(name))
        {
            Refused("var " + name, name);
            return;
        }
        Send(TalkWire.Var(granted, name, value));
    }

    public static void NoteField(string table, string element, string field, object value)
    {
        if (!Capturing()) return;
        string name = element + "." + field;
        if (TalkWire.Blocked(name))
        {
            Refused("field " + table + "." + name, name);
            return;
        }
        Send(TalkWire.Field(granted, table, element, field, value));
    }

    private static void Request(InteractableTalk talk, string npc)
    {
        if (nextReq > 999999000) nextReq = 1;
        Pending entry = new Pending();
        entry.ReqId = nextReq++;
        entry.Npc = npc;
        entry.Talk = talk;
        entry.At = Time.unscaledTime;
        entry.NextTry = entry.At + RetrySeconds;
        pending = entry;
        Send(TalkWire.Req(entry.ReqId, npc));
        GuestGate.Notice(GuestGate.KindDialogue, "Asking the host to talk");
        CoopLog.Log.Info("coop: talk requested on " + npc);
    }

    private static void Granted_(TalkMsg m)
    {
        if (pending == null || pending.ReqId != m.ReqId)
        {
            if (pending != null && pending.Npc == m.Npc)
            {
                CoopLog.Log.Info("coop: a late talk grant on " + m.Npc + " was ignored, a newer request owns it");
                return;
            }
            if (m.Npc != null && m.Npc.Length > 0 && m.Npc != granted)
            {
                Send(TalkWire.End(m.Npc));
                CoopLog.Log.Info("coop: a talk grant on " + m.Npc + " arrived with no request, it was released");
            }
            return;
        }
        InteractableTalk talk = pending.Talk;
        if (granted.Length > 0 && granted != m.Npc)
        {
            CoopLog.Log.Info("coop: the talk on " + granted + " was released for the granted talk on " + m.Npc);
            Send(TalkWire.End(granted));
        }
        granted = m.Npc;
        live = false;
        grantedAt = Time.unscaledTime;
        captureUntil = grantedAt + 3f;
        pending = null;
        busy.Remove(m.Npc);
        CoopLog.Log.Info("coop: the host granted the talk on " + m.Npc + ", starting the conversation");
        try
        {
            if (talk == null)
            {
                CoopLog.Log.Info("coop: the granted talk has no NPC object left, the grant is dropped");
                Ended(m.Npc);
                return;
            }
            talk.Interact();
        }
        catch (Exception e)
        {
            CoopLog.Once("TalkGuest.Granted", e);
        }
    }

    private static void Denied(TalkMsg m)
    {
        if (pending != null && pending.ReqId == m.ReqId) pending = null;
        CoopLog.Log.Info("coop: the host refused the talk: " + m.Reason);
        GuestGate.Notice(GuestGate.KindDialogue, m.Reason.Length > 0 ? m.Reason : "Only the host can talk and do quests");
    }

    private static void Locked(TalkMsg m)
    {
        if (m.Holder == 0 || m.Holder == Session.Me)
        {
            busy.Remove(m.Npc);
            return;
        }
        if (busy.Count >= MaxBusy) busy.Clear();
        busy.Add(m.Npc);
    }

    private static void Revoked(TalkMsg m)
    {
        if (m.Npc == null || granted.Length == 0 || granted != m.Npc) return;
        granted = "";
        grantedAt = 0f;
        captureUntil = 0f;
        live = false;
        CoopLog.Log.Info("coop: the host took over the talk on " + m.Npc + ", the guest talk ended");
        GuestGate.Notice(GuestGate.KindDialogue, "The host took over that conversation");
        try
        {
            if (DialogueManager.isConversationActive) DialogueManager.StopConversation();
        }
        catch (Exception e)
        {
            CoopLog.Once("TalkGuest.Revoked", e);
        }
    }

    private static void Remote(TalkMsg m)
    {
        if (!Session.IsGuest || !GuestSession.InHostWorld) return;
        if (!TalkWire.IsWrite(m)) return;
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
            CoopLog.Log.Debug("talk", "applied the host talk " + m.Kind + " " + m.Table);
        }
        catch (Exception e)
        {
            CoopLog.Once("TalkGuest.Remote", e);
        }
    }

    private static void Arrival()
    {
        bool here = Session.IsInLobby && Session.IsGuest && GuestSession.Loaded;
        if (here == arrived) return;
        arrived = here;
        if (!here) return;
        Send(TalkWire.HostVarWant());
        CoopLog.Log.Info("coop: the guest arrived, the host was asked for its dialogue variables");
    }

    private static void HostVar(TalkMsg m)
    {
        if (!Session.IsGuest || !GuestSession.InHostWorld) return;
        if (!TalkWire.HostVarOk(m)) return;
        try
        {
            applying++;
            try
            {
                TalkWire.ApplyHostVar(m);
            }
            finally
            {
                applying--;
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("TalkGuest.HostVar", e);
        }
    }

    private static bool Capturing()
    {
        if (applying > 0) return false;
        if (granted.Length == 0) return false;
        if (!GuestSession.InHostWorld) return false;
        return Active() || Time.unscaledTime <= captureUntil;
    }

    private static bool Active()
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

    private static bool Participant(string name)
    {
        return name == "Actor" || name == "Conversant" || name == "Speaker" || name == "ActorIndex" || name == "ConversantIndex";
    }

    private static void Refused(string what, string name)
    {
        if (!refused.Add(name)) return;
        CoopLog.Log.Info("coop: refused to forward " + what + " for the guest, it touches profile or host owned state");
    }

    private static void Send(string payload)
    {
        Session.Send(Session.HostId, TalkWire.Channel, payload, true);
    }
}
