using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;

internal static class SaunaGuest
{
    private sealed class Pending
    {
        public int ReqId;
        public string Text;
        public string Guid;
        public string Op;
        public int[] Ids;
        public int[] Counts;
        public float SentAt;
        public int Attempts;
    }

    private const float AnswerTimeout = 10f;
    private const int MaxSends = 3;
    private const int MaxOrphans = 4;
    private const int FuelCost = 1;
    private const int FirewoodId = 10530;
    private const int WoodLogId = 13450;
    private static readonly List<int> logIds = new List<int>();
    private static readonly List<string> logTitles = new List<string>();
    private static readonly List<Pending> orphans = new List<Pending>();
    private static bool logsScanned;
    private static Pending pending;
    private static float noticeAt;
    private static string noticeText = "";

    public static void Init()
    {
    }

    public static void Reset()
    {
        pending = null;
        orphans.Clear();
    }

    public static void OnSceneChanged()
    {
        pending = null;
        orphans.Clear();
    }

    public static bool Waiting
    {
        get { return pending != null; }
    }

    public static bool Request(Sauna sauna, string op)
    {
        if (!GuestSession.InHostWorld) return true;
        try
        {
            if (sauna == null || string.IsNullOrEmpty(sauna.GUID))
            {
                Notice("This sauna is not shared in co-op.");
                return false;
            }
            if (pending != null)
            {
                Notice("The sauna is still waiting for the host.");
                return false;
            }
            string scene = GameRefs.AreaId;
            if (scene == null) return false;
            int[] ids = new int[0];
            int[] counts = new int[0];
            int logs = 0;
            if (op == SaunaWire.OpOn)
            {
                ScanLogs();
                logs = HeldLogs(out ids, out counts);
                if (logs < FuelCost && logTitles.Count > 0)
                {
                    Notice("You need " + FuelCost + " firewood in your own inventory to light the sauna.");
                    return false;
                }
            }
            int reqId = Shared.NextReqId();
            string text = SaunaWire.Req(reqId, scene, sauna.GUID, op, logs);
            if (!Session.Send(Session.HostId, SaunaWire.Channel, text, true))
            {
                Notice("The host could not be reached.");
                return false;
            }
            Pending ask = new Pending();
            ask.ReqId = reqId;
            ask.Text = text;
            ask.Guid = sauna.GUID;
            ask.Op = op;
            ask.Ids = ids;
            ask.Counts = counts;
            ask.SentAt = Time.unscaledTime;
            ask.Attempts = 1;
            pending = ask;
            Notice(op == SaunaWire.OpOn ? "Asking the host to light the sauna." : "Asking the host to turn the sauna off.");
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("SaunaGuest.Request", e);
            return false;
        }
    }

    public static bool RequestWater(Sauna sauna)
    {
        if (!GuestSession.InHostWorld) return true;
        try
        {
            if (sauna == null || string.IsNullOrEmpty(sauna.GUID))
            {
                Notice("This sauna is not shared in co-op.");
                return false;
            }
            if (pending != null)
            {
                Notice("The sauna is still waiting for the host.");
                return false;
            }
            string scene = GameRefs.AreaId;
            if (scene == null) return false;
            int reqId = Shared.NextReqId();
            string text = SaunaWire.Water(reqId, scene, sauna.GUID);
            if (!Session.Send(Session.HostId, SaunaWire.Channel, text, true))
            {
                Notice("The host could not be reached.");
                return false;
            }
            Pending ask = new Pending();
            ask.ReqId = reqId;
            ask.Text = text;
            ask.Guid = sauna.GUID;
            ask.Op = "water";
            ask.Ids = new int[0];
            ask.Counts = new int[0];
            ask.SentAt = Time.unscaledTime;
            ask.Attempts = 1;
            pending = ask;
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("SaunaGuest.RequestWater", e);
            return false;
        }
    }

    public static void Handle(ulong from, SaunaMsg m)
    {
        try
        {
            if (from != Session.HostId || !GuestSession.InHostWorld) return;
            if (m.Kind == SaunaWire.LightAck) OnAck(m);
            else if (m.Kind == SaunaWire.WaterAck) OnAck(m);
            else if (m.Kind == SaunaWire.Lit) OnLit(m);
            else if (m.Kind == SaunaWire.State) OnState(m);
        }
        catch (Exception e)
        {
            CoopLog.Once("SaunaGuest.Handle", e);
        }
    }

    public static void Tick()
    {
        try
        {
            if (pending == null) return;
            if (Time.unscaledTime - pending.SentAt < AnswerTimeout) return;
            if (pending.Attempts >= MaxSends)
            {
                Orphan(pending);
                pending = null;
                Notice("The host did not answer in time, a late answer is still applied.");
                return;
            }
            pending.Attempts++;
            pending.SentAt = Time.unscaledTime;
            Session.Send(Session.HostId, SaunaWire.Channel, pending.Text, true);
        }
        catch (Exception e)
        {
            CoopLog.Once("SaunaGuest.Tick", e);
        }
    }

    private static void OnAck(SaunaMsg m)
    {
        Pending done;
        if (pending != null && pending.ReqId == m.ReqId)
        {
            done = pending;
            pending = null;
        }
        else
        {
            done = TakeOrphan(m.ReqId);
            if (done == null) return;
            CoopLog.Log.Info("sauna.guest: a late answer to the sauna request " + m.ReqId + " arrived and is still applied");
        }
        if (!m.Ok)
        {
            Notice("The host refused the sauna: " + m.Reason);
            return;
        }
        if (done.Op == SaunaWire.OpOn && done.Ids.Length > 0) RemoveLogs(done);
    }

    private static void Orphan(Pending ask)
    {
        orphans.Add(ask);
        while (orphans.Count > MaxOrphans) orphans.RemoveAt(0);
    }

    private static Pending TakeOrphan(int reqId)
    {
        for (int i = 0; i < orphans.Count; i++)
        {
            if (orphans[i].ReqId != reqId) continue;
            Pending found = orphans[i];
            orphans.RemoveAt(i);
            return found;
        }
        return null;
    }

    private static void OnLit(SaunaMsg m)
    {
        Sauna sauna = SaunaWire.Find(m.Scene, m.Guid);
        if (sauna == null)
        {
            CoopLog.Once("SaunaGuest.lit", "a sauna light for " + m.Guid + " arrived with no sauna in this scene");
            return;
        }
        if (sauna.On == m.On) return;
        Shared.Apply(delegate { if (m.On) sauna.TurnOn(); else sauna.TurnOff(); });
    }

    private static void OnState(SaunaMsg m)
    {
        Sauna sauna = SaunaWire.Find(m.Scene, m.Guid);
        if (sauna == null)
        {
            CoopLog.Once("SaunaGuest.state", "a sauna state for " + m.Guid + " arrived with no sauna in this scene");
            return;
        }
        Shared.Apply(delegate
        {
            if (sauna.On != m.On)
            {
                if (m.On) sauna.TurnOn();
                else sauna.TurnOff();
            }
            sauna.temperature = m.Temp;
            SaunaWire.SetSteam(sauna, m.Steam);
        });
    }

    private static void RemoveLogs(Pending done)
    {
        Inventory inventory = Inventory.instance;
        if (inventory == null)
        {
            CoopLog.Once("SaunaGuest.RemoveLogs", "the fuel stays in my inventory, there is no Inventory instance");
            return;
        }
        int expected = 0;
        for (int i = 0; i < done.Counts.Length; i++) expected += done.Counts[i];
        int removed = 0;
        for (int i = 0; i < done.Ids.Length; i++)
        {
            int have = inventory.GetItemAmountInInventory(done.Ids[i]);
            int take = have < done.Counts[i] ? have : done.Counts[i];
            if (take <= 0) continue;
            inventory.RemoveItems(done.Ids[i], take, true, -1, true);
            removed += take;
        }
        if (removed > 0) GuestProfile.AfterTransfer();
        if (removed < expected)
        {
            CoopLog.Log.Info("sauna.guest: only " + removed + " of the " + expected + " firewood item(s) were left in my inventory when the host answered, the rest was already gone");
            return;
        }
        CoopLog.Log.Info("sauna.guest: removed " + removed + " log(s) of my own after the host lit the sauna");
    }

    private static void ScanLogs()
    {
        if (logsScanned) return;
        logsScanned = true;
        try
        {
            ItemDatabase database = ItemDatabase.instance;
            if (database == null) return;
            AddFuel(database.FetchItemByID(FirewoodId));
            AddFuel(database.FetchItemByID(WoodLogId));
            string names = logTitles.Count == 0 ? "not in the item database, so lighting costs nothing" : string.Join(", ", logTitles.ToArray());
            CoopLog.Log.Info("coop: the sauna firewood items are " + names + " and the light costs " + FuelCost);
        }
        catch (Exception e)
        {
            CoopLog.Once("SaunaGuest.ScanLogs", e);
        }
    }

    private static void AddFuel(Item item)
    {
        if (item == null || logIds.Contains(item.ID)) return;
        logIds.Add(item.ID);
        logTitles.Add(item.Title + " (" + item.ID + ")");
    }

    private static int HeldLogs(out int[] ids, out int[] counts)
    {
        List<int> idList = new List<int>();
        List<int> countList = new List<int>();
        int left = FuelCost;
        Inventory inventory = Inventory.instance;
        if (inventory != null)
        {
            for (int i = 0; i < logIds.Count && left > 0; i++)
            {
                int have = inventory.GetItemAmountInInventory(logIds[i]);
                if (have <= 0) continue;
                int take = have < left ? have : left;
                idList.Add(logIds[i]);
                countList.Add(take);
                left -= take;
            }
        }
        ids = idList.ToArray();
        counts = countList.ToArray();
        return FuelCost - left;
    }

    private static void Notice(string text)
    {
        if (string.Equals(text, noticeText, StringComparison.Ordinal) && Time.unscaledTime - noticeAt < 2f) return;
        noticeText = text;
        noticeAt = Time.unscaledTime;
        CoopNotice.Show("sauna", text, 3f);
    }
}
