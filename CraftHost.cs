using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ObenseuerCore;
using UnityEngine;

internal static partial class CraftHost
{
    private const string Kind = "craft";
    private const float MaxHold = 3600f;
    private const float StaleGrace = 5f;
    private const float PushQuiet = 0.25f;
    private const float EffectCap = 15f;
    private const float NoticeQuiet = 2f;
    private static readonly Dictionary<string, Held> held = new Dictionary<string, Held>();
    private static readonly List<string> stale = new List<string>();
    private static readonly Dictionary<string, float> pushedAt = new Dictionary<string, float>();
    private static readonly List<string> dirty = new List<string>();
    private static FieldInfo recipesField;
    private static MethodInfo compareMethod;
    private static int hostRun;
    private static int doneCount;
    private static float noticeAt;
    private static string noticeText = "";

    private sealed class Held
    {
        public ulong Peer;
        public float StartedAt;
        public int Skill = -1;
        public int SkillType;
        public float Effect = -1f;
    }

    private static int craftSkill = -1;
    private static int craftSkillType;
    private static ulong craftPeer;
    private static float craftGain;
    private static float craftEffect = -1f;
    private static int craftDepth;

    public static bool InGuestCraft
    {
        get { return craftDepth > 0 && craftSkill >= 0; }
    }

    public static int GuestSkill
    {
        get { return craftSkill; }
    }

    public static int GuestSkillType
    {
        get { return craftSkillType; }
    }

    public static float GuestEffect
    {
        get { return craftEffect; }
    }

    public static bool InHostRun
    {
        get { return hostRun > 0; }
    }

    public static void Init()
    {
        recipesField = AccessTools.Field(typeof(CraftingBase), "AllRecipes");
        compareMethod = AccessTools.Method(typeof(CraftingBase), "CompareRecipeToItems");
    }

    public static void Reset()
    {
        held.Clear();
        stale.Clear();
        pushedAt.Clear();
        dirty.Clear();
        pending.Clear();
        waiting.Clear();
        hostRun = 0;
        doneCount = 0;
        ResetWindow();
    }

    private static void ResetWindow()
    {
        craftDepth = 0;
        craftSkill = -1;
        craftSkillType = 0;
        craftPeer = 0UL;
        craftGain = 0f;
        craftEffect = -1f;
    }

    public static bool BeginGuestCraft(string key)
    {
        if (key == null) return false;
        Held entry;
        if (!held.TryGetValue(key, out entry) || entry.Skill < 0) return false;
        if (craftDepth > 0)
        {
            craftDepth++;
            return true;
        }
        craftDepth = 1;
        craftSkill = entry.Skill;
        craftSkillType = entry.SkillType;
        craftPeer = entry.Peer;
        craftEffect = entry.Effect;
        craftGain = 0f;
        return true;
    }

    public static void NoteGain(float amount)
    {
        craftGain += amount;
    }

    public static void EndGuestCraft()
    {
        if (craftDepth <= 0) return;
        craftDepth--;
        if (craftDepth > 0) return;
        ulong peer = craftPeer;
        int type = craftSkillType;
        float gain = craftGain;
        ResetWindow();
        if (gain <= 0f || peer == 0UL) return;
        try { Session.Send(peer, CraftWire.Channel, CraftWire.SkillGain(type, gain), true); }
        catch (Exception e) { CoopLog.Once("CraftHost.EndGuestCraft", e); }
    }

    public static void OnSceneChanged()
    {
        held.Clear();
        pushedAt.Clear();
        dirty.Clear();
    }

    public static void OnMemberLeft(ulong peer)
    {
        try
        {
            if (craftPeer == peer) ResetWindow();
            if (held.Count == 0) return;
            stale.Clear();
            foreach (KeyValuePair<string, Held> pair in held)
            {
                if (pair.Value.Peer == peer) stale.Add(pair.Key);
            }
            for (int i = 0; i < stale.Count; i++) Finish(stale[i]);
        }
        catch (Exception e)
        {
            CoopLog.Once("CraftHost.OnMemberLeft", e);
        }
    }

    public static void Tick()
    {
        try
        {
            FlushDirty();
            ResendTick();
            if (held.Count == 0) return;
            float now = Time.unscaledTime;
            stale.Clear();
            foreach (KeyValuePair<string, Held> pair in held)
            {
                float age = now - pair.Value.StartedAt;
                if (age >= MaxHold)
                {
                    CoopLog.Log.Error("coop: the station craft on " + pair.Key + " held its lock for " + MaxHold + " s, the lock was dropped");
                    stale.Add(pair.Key);
                    continue;
                }
                if (age < StaleGrace) continue;
                CraftingBase station = StationOf(pair.Key);
                if (station == null || !station.CraftingIProgress()) stale.Add(pair.Key);
            }
            for (int i = 0; i < stale.Count; i++) Finish(stale[i]);
        }
        catch (Exception e)
        {
            CoopLog.Once("CraftHost.Tick", e);
        }
    }

    public static void Handle(ulong from, CraftMsg m)
    {
        try
        {
            if (m == null || !Session.IsInLobby || !Session.IsHost) return;
            if (m.Kind != "REQCRAFT") return;
            if (!Shared.Admit(from, CraftWire.Channel, m.ReqId, Kind)) return;
            Request(from, m);
        }
        catch (Exception e)
        {
            CoopLog.Once("CraftHost.Handle", e);
        }
    }

    private static void Request(ulong from, CraftMsg m)
    {
        string scene = GameRefs.AreaId;
        if (scene == null || m.Scene != scene || Session.AreaOf(from) != scene)
        {
            Deny(from, m.ReqId, "not in this scene");
            return;
        }
        if (SaveData.Busy)
        {
            Deny(from, m.ReqId, "the game is loading or saving");
            return;
        }
        Storage storage = ContainerHost.ResolveKey(m.Key);
        if (storage == null)
        {
            Deny(from, m.ReqId, "not found");
            return;
        }
        CraftingBase craftBase = storage.GetComponent<CraftingBase>();
        if (craftBase == null) craftBase = storage.GetComponentInParent<CraftingBase>();
        manufacturing station = craftBase as manufacturing;
        if (station == null || station.InputOutputStorage != storage)
        {
            Deny(from, m.ReqId, "not a crafting station");
            return;
        }
        string key = ContainerWire.KeyOf(storage);
        if (key == null || key != m.Key)
        {
            Deny(from, m.ReqId, "not a crafting station");
            return;
        }
        if (held.ContainsKey(key))
        {
            Deny(from, m.ReqId, "busy");
            return;
        }
        if (CraftingBase.currentCraftingBase == station)
        {
            Deny(from, m.ReqId, "busy");
            return;
        }
        if (station.CraftingIProgress())
        {
            Deny(from, m.ReqId, "busy");
            return;
        }
        if (!station.PoweredAndActive)
        {
            Deny(from, m.ReqId, "the station has no power");
            return;
        }
        if (station._Lock != null && station._Lock.Locked)
        {
            Deny(from, m.ReqId, "locked");
            return;
        }
        Recipe recipe = FindRecipe(station, m.RecipeId);
        if (recipe == null)
        {
            Deny(from, m.ReqId, "unknown recipe");
            return;
        }
        if (!CanCraft(station, recipe))
        {
            Deny(from, m.ReqId, "missing ingredients");
            return;
        }
        if (!ContainerHost.HoldCraft(key, scene, from))
        {
            Deny(from, m.ReqId, "in use");
            return;
        }
        Held entry = new Held();
        entry.Peer = from;
        entry.StartedAt = Time.unscaledTime;
        entry.SkillType = (int)recipe.RelevantSkill;
        entry.Skill = recipe.RelevantSkill != PlayerStats.PlayerSkillType.None && m.Skill >= 0 ? Mathf.Clamp(m.Skill, 0, PlayerStats.maxLevel) : -1;
        entry.Effect = entry.Skill >= 0 && m.Effect >= 0f ? Mathf.Min(m.Effect, EffectCap) : entry.Skill;
        held[key] = entry;
        CoopLog.Log.Info("coop: guest " + Session.DisplayName(from) + " asked for recipe " + m.RecipeId + " on " + key);
        int doneBefore = doneCount;
        hostRun++;
        bool guestWindow = BeginGuestCraft(key);
        try
        {
            station.SetSelectedRecipe(recipe);
            station.Craft();
        }
        catch (Exception e)
        {
            CoopLog.Once("CraftHost.Craft", e);
        }
        finally
        {
            if (guestWindow) EndGuestCraft();
            if (hostRun > 0) hostRun--;
        }
        bool finished = doneCount != doneBefore;
        if (held.ContainsKey(key) && !finished) PushAll(station);
        if (!finished && !station.CraftingIProgress())
        {
            Finish(key);
            Deny(from, m.ReqId, "the station did not start");
            return;
        }
        Shared.Answer(from, CraftWire.Channel, m.ReqId, Kind, CraftWire.Ack(m.ReqId, true, ""));
    }

    public static void OnCraftDone(manufacturing station)
    {
        try
        {
            if (station == null || !Session.IsInLobby || !Session.IsHost) return;
            string key = ContainerWire.KeyOf(station.InputOutputStorage);
            Held entry = null;
            if (key != null) held.TryGetValue(key, out entry);
            doneCount++;
            PushAll(station);
            if (entry != null && !station.CraftingIProgress()) Finish(key);
        }
        catch (Exception e)
        {
            CoopLog.Once("CraftHost.OnCraftDone", e);
        }
    }

    public static void PushAll(CraftingBase station)
    {
        try
        {
            string scene = GameRefs.AreaId;
            if (scene == null || station == null) return;
            bool progress = station.CraftingIProgress();
            Storage input = station.InputOutputStorage;
            Storage output = station.OutputStorage;
            Push(scene, input, progress);
            if (output != null && output != input) Push(scene, output, progress);
        }
        catch (Exception e)
        {
            CoopLog.Once("CraftHost.PushAll", e);
        }
    }

    private static void Push(string scene, Storage storage, bool progress)
    {
        if (storage == null) return;
        string key = ContainerWire.KeyOf(storage);
        if (key == null) return;
        float now = Time.unscaledTime;
        float at;
        if (pushedAt.TryGetValue(key, out at) && now - at < PushQuiet)
        {
            if (!dirty.Contains(key)) dirty.Add(key);
            return;
        }
        pushedAt[key] = now;
        dirty.Remove(key);
        SendState(scene, storage, key, progress, Session.PeersInArea(scene));
    }

    private static void SendState(string scene, Storage storage, string key, bool progress, List<ulong> peers)
    {
        string json = ContainerWire.Capture(storage, key);
        if (json == null)
        {
            CoopLog.Once("CraftHost.Push.capture", "coop: the state of the station storage " + key + " could not be read");
            return;
        }
        byte[] data = ContainerWire.Bytes(json);
        string text = CraftWire.Station(scene, key, progress);
        string name = CraftWire.BlobNameOf(key);
        for (int i = 0; i < peers.Count; i++)
        {
            Session.Send(peers[i], CraftWire.Channel, text, true);
            if (NetBlob.Send(peers[i], CraftWire.ModId, name, data) < 0) CoopLog.Once("CraftHost.Push.blob", "coop: the state of station storage " + key + " could not be sent to " + peers[i]);
        }
    }

    private static void FlushDirty()
    {
        if (dirty.Count == 0) return;
        float now = Time.unscaledTime;
        for (int i = dirty.Count - 1; i >= 0; i--)
        {
            string key = dirty[i];
            float at;
            if (pushedAt.TryGetValue(key, out at) && now - at < PushQuiet) continue;
            dirty.RemoveAt(i);
            CraftingBase station = StationOf(key);
            if (station != null) PushAll(station);
        }
    }

    private static Recipe FindRecipe(CraftingBase station, int recipeId)
    {
        List<Recipe> all = recipesField == null ? null : recipesField.GetValue(station) as List<Recipe>;
        if (all == null) return null;
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i] != null && all[i].ID == recipeId) return all[i];
        }
        return null;
    }

    private static bool CanCraft(CraftingBase station, Recipe recipe)
    {
        if (compareMethod == null || recipe == null) return false;
        try
        {
            object result = compareMethod.Invoke(station, new object[] { recipe, null, false, true });
            return result is bool && (bool)result;
        }
        catch (Exception e)
        {
            CoopLog.Once("CraftHost.CanCraft", e);
            return false;
        }
    }

    private static CraftingBase StationOf(string key)
    {
        Storage storage = ContainerHost.ResolveKey(key);
        if (storage == null) return null;
        CraftingBase craftBase = storage.GetComponent<CraftingBase>();
        if (craftBase == null) craftBase = storage.GetComponentInParent<CraftingBase>();
        return craftBase;
    }

    private static void Finish(string key)
    {
        if (key == null) return;
        Held entry;
        if (!held.TryGetValue(key, out entry)) return;
        held.Remove(key);
        ContainerHost.ReleaseKey(key);
        CoopLog.Log.Info("coop: the craft lock on " + key + " for " + Session.DisplayName(entry.Peer) + " was released");
    }

    private static void Deny(ulong peer, int reqId, string reason)
    {
        Shared.Answer(peer, CraftWire.Channel, reqId, Kind, CraftWire.Ack(reqId, false, reason));
    }

    public static void Notice(string text)
    {
        CoopLog.Log.Info("coop: " + text);
        if (string.Equals(text, noticeText, StringComparison.Ordinal) && Time.unscaledTime - noticeAt < NoticeQuiet) return;
        noticeText = text;
        noticeAt = Time.unscaledTime;
        if (Notifications.instance != null) Notifications.instance.CreateNotification("Co-op", text);
    }
}
