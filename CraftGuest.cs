using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

internal static class CraftGuest
{
    private const float RetrySeconds = 3f;
    private const float TimeoutSeconds = 10f;
    private const float AckKeepSeconds = 15f;
    private const float NoticeQuiet = 2f;
    private const int MaxBuffered = 4;
    private const int MaxKnown = 16;
    private static readonly Dictionary<string, string> buffered = new Dictionary<string, string>();
    private static readonly List<string> order = new List<string>();
    private static readonly Dictionary<string, string> known = new Dictionary<string, string>();
    private static FieldInfo selectedField;
    private static int pendingReq;
    private static string pendingKey;
    private static int pendingRecipe;
    private static int pendingSkill = -1;
    private static int pendingSkillType;
    private static float pendingEffect = -1f;
    private static float pendingAt;
    private static float pendingStart;
    private static bool pendingAcked;
    private static bool retried;
    private static bool syncing;
    private static string syncKey;
    private static int syncRecipe;
    private static int syncSkill = -1;
    private static int syncSkillType;
    private static float syncAt;
    private static float noticeAt;
    private static string noticeText = "";

    public static void Init()
    {
        selectedField = AccessTools.Field(typeof(CraftingBase), "selectedRecipe");
    }

    public static void Reset()
    {
        Clear();
    }

    public static void OnSceneChanged()
    {
        Clear();
    }

    public static void OnStationUnavailable(Storage storage)
    {
        try
        {
            if (storage == null) return;
            CraftingBase station = storage.GetComponent<CraftingBase>();
            if (station == null) station = storage.GetComponentInParent<CraftingBase>();
            if (station == null || CraftingBase.currentCraftingBase != station) return;
            station.Exit();
        }
        catch (Exception e)
        {
            CoopLog.Once("CraftGuest.OnStationUnavailable", e);
        }
    }

    public static bool Request(manufacturing station)
    {
        try
        {
            if (!GuestSession.InHostWorld || station == null) return true;
            Recipe recipe = selectedField == null ? null : selectedField.GetValue(station) as Recipe;
            if (recipe == null)
            {
                Notice("Select a recipe first");
                return false;
            }
            if (recipe.ID < 0)
            {
                Notice("This recipe is not shared in co-op");
                return false;
            }
            string key = ContainerWire.KeyOf(station.InputOutputStorage);
            if (key == null)
            {
                Notice("This station is not shared in co-op");
                return false;
            }
            if (ContainerGuest.IsOpen(key))
            {
                if (syncing)
                {
                    Notice("Waiting for the host");
                    return false;
                }
                ContainerGuest.TakeReleased(key);
                syncing = true;
                syncKey = key;
                syncRecipe = recipe.ID;
                syncSkill = SkillOf(recipe);
                syncSkillType = (int)recipe.RelevantSkill;
                syncAt = Time.unscaledTime;
                station.Exit();
                Notice("Sent to the host");
                return false;
            }
            if (ContainerGuest.IsPending(station.InputOutputStorage))
            {
                Notice("Waiting for the host");
                return false;
            }
            if (syncing)
            {
                Notice("Waiting for the host");
                return false;
            }
            if (pendingReq != 0 && !pendingAcked && pendingKey == key && Time.unscaledTime - pendingAt < RetrySeconds) return false;
            Send(key, recipe.ID, SkillOf(recipe), (int)recipe.RelevantSkill);
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("CraftGuest.Request", e);
            return false;
        }
    }

    private static void Send(string key, int recipeId, int skill, int skillType)
    {
        try
        {
            int reqId = Shared.NextReqId();
            float effect = EffectOf(skill, skillType);
            if (!Session.Send(GuestSession.HostId, CraftWire.Channel, CraftWire.ReqCraft(reqId, GameRefs.AreaId, key, recipeId, skill, skillType, effect), true))
            {
                ClearPending();
                Notice("The host could not be reached");
                return;
            }
            pendingReq = reqId;
            pendingKey = key;
            pendingRecipe = recipeId;
            pendingSkill = skill;
            pendingSkillType = skillType;
            pendingEffect = effect;
            pendingAt = Time.unscaledTime;
            pendingStart = pendingAt;
            pendingAcked = false;
            retried = false;
            Notice("Sent to the host");
        }
        catch (Exception e)
        {
            CoopLog.Once("CraftGuest.Send", e);
        }
    }

    private static int SkillOf(Recipe recipe)
    {
        if (recipe == null || recipe.RelevantSkill == PlayerStats.PlayerSkillType.None || PlayerStats.instance == null) return -1;
        return PlayerStats.instance.GetSkill(recipe.RelevantSkill);
    }

    private static float EffectOf(int skill, int skillType)
    {
        PlayerStats.PlayerSkillType type = (PlayerStats.PlayerSkillType)skillType;
        if (skill < 0 || type == PlayerStats.PlayerSkillType.None || PlayerStats.instance == null) return -1f;
        return PlayerStats.instance.GetSkillWithEffects(type);
    }

    public static void Handle(ulong from, CraftMsg m)
    {
        try
        {
            if (m == null || Session.IsHost || !GuestSession.InHostWorld || from != GuestSession.HostId) return;
            if (m.Kind == "CRAFTACK") OnAck(m);
            else if (m.Kind == "STATION") OnStation(m);
            else if (m.Kind == "CRAFTSKILL") OnSkill(m);
        }
        catch (Exception e)
        {
            CoopLog.Once("CraftGuest.Handle", e);
        }
    }

    private static void OnSkill(CraftMsg m)
    {
        if (PlayerStats.instance == null || m.Amount <= 0f) return;
        if (!Enum.IsDefined(typeof(PlayerStats.PlayerSkillType), m.SkillType)) return;
        PlayerStats.PlayerSkillType type = (PlayerStats.PlayerSkillType)m.SkillType;
        if (type == PlayerStats.PlayerSkillType.None) return;
        PlayerStats.instance.IncreaseSkill(type, m.Amount);
    }

    public static void State(ulong from, string name, string json)
    {
        try
        {
            if (Session.IsHost || !GuestSession.InHostWorld || from != GuestSession.HostId || json == null) return;
            string key;
            if (known.TryGetValue(name, out key))
            {
                Apply(key, json);
                return;
            }
            if (!buffered.ContainsKey(name))
            {
                if (order.Count >= MaxBuffered)
                {
                    buffered.Remove(order[0]);
                    order.RemoveAt(0);
                }
                order.Add(name);
            }
            buffered[name] = json;
        }
        catch (Exception e)
        {
            CoopLog.Once("CraftGuest.State", e);
        }
    }

    public static void Tick()
    {
        try
        {
            if (syncing)
            {
                SyncTick();
                return;
            }
            if (pendingReq == 0) return;
            float now = Time.unscaledTime;
            if (pendingAcked)
            {
                if (now - pendingAt >= AckKeepSeconds) ClearPending();
                return;
            }
            if (!retried && now - pendingStart >= RetrySeconds)
            {
                retried = true;
                pendingAt = now;
                Session.Send(GuestSession.HostId, CraftWire.Channel, CraftWire.ReqCraft(pendingReq, GameRefs.AreaId, pendingKey, pendingRecipe, pendingSkill, pendingSkillType, pendingEffect), true);
                return;
            }
            if (now - pendingStart < TimeoutSeconds) return;
            ClearPending();
            Notice("The host did not answer");
        }
        catch (Exception e)
        {
            CoopLog.Once("CraftGuest.Tick", e);
        }
    }

    private static void SyncTick()
    {
        if (ContainerGuest.TakeReleased(syncKey))
        {
            string key = syncKey;
            int recipe = syncRecipe;
            int skill = syncSkill;
            int skillType = syncSkillType;
            syncing = false;
            syncKey = null;
            syncRecipe = 0;
            syncSkill = -1;
            syncSkillType = 0;
            Send(key, recipe, skill, skillType);
            return;
        }
        if (Time.unscaledTime - syncAt < TimeoutSeconds) return;
        syncing = false;
        syncKey = null;
        syncRecipe = 0;
        syncSkill = -1;
        syncSkillType = 0;
        Notice("The host did not answer");
    }

    private static void OnAck(CraftMsg m)
    {
        if (pendingReq == 0 || m.ReqId != pendingReq) return;
        if (m.Ok)
        {
            pendingAcked = true;
            pendingAt = Time.unscaledTime;
            return;
        }
        string reason = m.Reason;
        ClearPending();
        Notice("The host refused: " + reason);
    }

    private static void OnStation(CraftMsg m)
    {
        if (m.Key == null) return;
        string name = CraftWire.BlobNameOf(m.Key);
        Remember(name, m.Key);
        string json;
        if (!buffered.TryGetValue(name, out json)) return;
        buffered.Remove(name);
        order.Remove(name);
        Apply(m.Key, json);
    }

    private static void Remember(string name, string key)
    {
        if (known.ContainsKey(name))
        {
            known[name] = key;
            return;
        }
        if (known.Count >= MaxKnown) known.Clear();
        known[name] = key;
    }

    private static void Apply(string key, string json)
    {
        if (key == null || json == null || ContainerGuest.HeldLocally(key)) return;
        Storage storage = ContainerHost.ResolveKey(key);
        if (storage == null)
        {
            CoopLog.Once("CraftGuest.Apply.missing." + key, "coop: the state of station " + key + " arrived but the storage is gone");
            return;
        }
        string current = null;
        try { current = ContainerWire.Capture(storage, key); }
        catch (Exception e) { CoopLog.Once("CraftGuest.Apply.capture", e); }
        if (current != null && current == json) return;
        bool ok = false;
        Shared.Apply(() => { ok = ContainerWire.Apply(storage, key, json); });
        if (!ok)
        {
            CoopLog.Log.Error("coop: the state of station " + key + " was refused, the guest keeps its own copy");
            return;
        }
        Refresh(storage);
        RefreshList(storage);
    }

    private static void RefreshList(Storage storage)
    {
        CraftingBase station = storage.GetComponent<CraftingBase>();
        if (station == null) station = storage.GetComponentInParent<CraftingBase>();
        if (station == null || CraftingBase.currentCraftingBase != station) return;
        station.CheckItems(false);
        manufacturing machine = station as manufacturing;
        if (machine != null && ManufacturingPanel.instance != null) ManufacturingPanel.instance.SetInventorySlotPanelSize();
    }

    private static void Refresh(Storage storage)
    {
        try
        {
            storage.UpdateSlots();
        }
        catch (Exception e)
        {
            CoopLog.Once("CraftGuest.Refresh", e);
        }
    }

    private static void Clear()
    {
        buffered.Clear();
        order.Clear();
        known.Clear();
        syncing = false;
        syncKey = null;
        syncRecipe = 0;
        syncSkill = -1;
        syncSkillType = 0;
        syncAt = 0f;
        ClearPending();
    }

    private static void ClearPending()
    {
        pendingReq = 0;
        pendingKey = null;
        pendingRecipe = 0;
        pendingSkill = -1;
        pendingSkillType = 0;
        pendingEffect = -1f;
        pendingAt = 0f;
        pendingStart = 0f;
        pendingAcked = false;
        retried = false;
    }

    private static void Notice(string text)
    {
        CoopLog.Log.Info("coop: " + text);
        if (string.Equals(text, noticeText, StringComparison.Ordinal) && Time.unscaledTime - noticeAt < NoticeQuiet) return;
        noticeText = text;
        noticeAt = Time.unscaledTime;
        if (Notifications.instance != null) Notifications.instance.CreateNotification("Co-op", text);
    }
}
