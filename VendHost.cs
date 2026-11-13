using System;
using System.Collections.Generic;
using UnityEngine;

internal static class VendHost
{
    private sealed class Answer
    {
        public int ReqId;
        public string Text;
        public bool Ok;
        public string Guid;
        public int Index = -1;
        public float AnsweredAt;
    }

    private static readonly Dictionary<ulong, List<Answer>> answers = new Dictionary<ulong, List<Answer>>();
    private static readonly Dictionary<ulong, List<int>> restored = new Dictionary<ulong, List<int>>();
    private static readonly List<ulong> stale = new List<ulong>();
    private const int MaxAnswers = 32;
    private const float NackWindow = 60f;

    public static void Init()
    {
        answers.Clear();
        restored.Clear();
    }

    public static void Reset()
    {
        answers.Clear();
        restored.Clear();
        stale.Clear();
    }

    public static void Tick()
    {
        if (!Session.IsInLobby && (answers.Count > 0 || restored.Count > 0)) Reset();
    }

    public static void Handle(ulong from, VendMsg m)
    {
        try
        {
            if (!Session.IsHost || !Session.Peers.Contains(from)) return;
            if (m.Kind == "VNACK")
            {
                Restore(from, m);
                return;
            }
            if (m.Kind != "VENDREQ") return;
            Answer known = Stored(from, m.ReqId);
            if (known == null)
            {
                string text;
                try
                {
                    text = Apply(from, m);
                }
                catch (Exception e)
                {
                    CoopLog.Once("VendHost.Apply", e);
                    text = VendWire.Ack(m.ReqId, false, "the host could not apply that purchase", -1);
                }
                if (string.IsNullOrEmpty(text)) text = VendWire.Ack(m.ReqId, false, "the host could not apply that purchase", -1);
                VendMsg ack;
                bool ok = VendWire.TryParse(text, out ack) && ack.Ok;
                known = Remember(from, m.ReqId, text, ok, m.Guid, m.Index);
            }
            Session.Send(from, VendWire.Channel, known.Text, true);
        }
        catch (Exception e)
        {
            CoopLog.Once("VendHost.Handle", e);
        }
    }

    public static void OnMemberLeft(ulong peer)
    {
        try
        {
            if (!Session.IsInLobby || !Session.IsHost) return;
            answers.Remove(peer);
            restored.Remove(peer);
        }
        catch (Exception e)
        {
            CoopLog.Once("VendHost.OnMemberLeft", e);
        }
    }

    private static string Apply(ulong from, VendMsg m)
    {
        VendingMachine machine = SaveController.GetObjectByGUID(m.Guid, typeof(VendingMachine)) as VendingMachine;
        if (machine == null) return VendWire.Ack(m.ReqId, false, "that machine is not here", -1);
        VendingMachine.VendingMachineItem item = VendWire.Item(machine, m.Index);
        if (item == null) return VendWire.Ack(m.ReqId, false, "that slot is empty", -1);
        int stock = item.currentStock;
        if (item.stolen && machine.Trader != null && machine.Trader.ID > 0) return VendWire.Ack(m.ReqId, false, "stolen items are not shared yet", stock);
        if (stock == 0) return VendWire.Ack(m.ReqId, false, "that item is sold out", 0);
        if (stock > 0) item.currentStock = stock - 1;
        try
        {
            machine.UpdateMoney();
        }
        catch (Exception e)
        {
            CoopLog.Once("VendHost.UpdateMoney", e);
        }
        CoopLog.Log.Info("coop: vending slot " + m.Index + " of " + m.Guid + " bought by " + from + ", stock " + item.currentStock);
        return VendWire.Ack(m.ReqId, true, "", item.currentStock);
    }

    private static void Restore(ulong from, VendMsg m)
    {
        Answer known = Stored(from, m.ReqId);
        if (known == null || !known.Ok)
        {
            CoopLog.Log.Debug("vend.nack", "a stock restore for request " + m.ReqId + " arrived for a purchase this host did not apply");
            return;
        }
        if (known.Guid != m.Guid || known.Index != m.Index)
        {
            CoopLog.Log.Debug("vend.nack", "a stock restore for request " + m.ReqId + " named a slot the stored answer did not");
            return;
        }
        if (Time.unscaledTime - known.AnsweredAt > NackWindow)
        {
            CoopLog.Log.Debug("vend.nack", "a stock restore for request " + m.ReqId + " arrived more than 60 s after the ok answer");
            return;
        }
        if (WasRestored(from, m.ReqId))
        {
            CoopLog.Log.Debug("vend.nack", "a stock restore for request " + m.ReqId + " was already done");
            return;
        }
        MarkRestored(from, m.ReqId);
        VendingMachine machine = SaveController.GetObjectByGUID(known.Guid, typeof(VendingMachine)) as VendingMachine;
        if (machine == null)
        {
            CoopLog.Once("VendHost.nackMachine", "a stock restore names a machine this host does not have");
            return;
        }
        VendingMachine.VendingMachineItem item = VendWire.Item(machine, known.Index);
        if (item == null)
        {
            CoopLog.Once("VendHost.nackSlot", "a stock restore names a slot this host does not have");
            return;
        }
        if (item.currentStock >= 0) item.currentStock = item.currentStock + 1;
        try
        {
            machine.UpdateMoney();
        }
        catch (Exception e)
        {
            CoopLog.Once("VendHost.UpdateMoney", e);
        }
        CoopLog.Log.Info("coop: vending slot " + known.Index + " of " + known.Guid + " was given back by " + from + ", stock " + item.currentStock);
    }

    private static Answer Stored(ulong peer, int reqId)
    {
        List<Answer> list;
        if (!answers.TryGetValue(peer, out list)) return null;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].ReqId == reqId) return list[i];
        }
        return null;
    }

    private static Answer Remember(ulong peer, int reqId, string text, bool ok, string guid, int index)
    {
        List<Answer> list;
        if (!answers.TryGetValue(peer, out list))
        {
            list = new List<Answer>();
            answers[peer] = list;
        }
        Answer answer = new Answer();
        answer.ReqId = reqId;
        answer.Text = text;
        answer.Ok = ok;
        answer.Guid = guid;
        answer.Index = index;
        answer.AnsweredAt = Time.unscaledTime;
        list.Add(answer);
        while (list.Count > MaxAnswers) list.RemoveAt(0);
        Prune();
        return answer;
    }

    private static bool WasRestored(ulong peer, int reqId)
    {
        List<int> list;
        if (!restored.TryGetValue(peer, out list)) return false;
        return list.Contains(reqId);
    }

    private static void MarkRestored(ulong peer, int reqId)
    {
        List<int> list;
        if (!restored.TryGetValue(peer, out list))
        {
            list = new List<int>();
            restored[peer] = list;
        }
        list.Add(reqId);
        while (list.Count > MaxAnswers) list.RemoveAt(0);
        Prune();
    }

    private static void Prune()
    {
        stale.Clear();
        foreach (KeyValuePair<ulong, List<Answer>> pair in answers)
        {
            if (!Session.Peers.Contains(pair.Key)) stale.Add(pair.Key);
        }
        for (int i = 0; i < stale.Count; i++) answers.Remove(stale[i]);
        stale.Clear();
        foreach (KeyValuePair<ulong, List<int>> pair in restored)
        {
            if (!Session.Peers.Contains(pair.Key)) stale.Add(pair.Key);
        }
        for (int i = 0; i < stale.Count; i++) restored.Remove(stale[i]);
    }
}
