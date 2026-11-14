using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using ObenseuerCore;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static partial class InteractableSync
{
    private static void Fired(Interactable target, string key)
    {
        string here = GameRefs.AreaId;
        if (here == null) return;
        string text = here + "|IF|" + key + "|" + target.GetType().Name;
        if (text.Length > MaxInline) return;
        List<ulong> peers = Session.PeersInArea(here);
        for (int i = 0; i < peers.Count; i++) Session.Send(peers[i], Channel, text, true);
    }

    private static bool Push(Interactable target, string key, ulong only)
    {
        string json = Capture(target, key);
        if (json == null) return false;
        string here = GameRefs.AreaId;
        if (here == null) return false;
        List<ulong> peers = only != 0UL ? new List<ulong> { only } : Session.PeersInArea(here);
        string inline = here + "|IS|" + key + "|" + target.GetType().Name + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        bool ok = true;
        if (inline.Length <= MaxInline)
        {
            for (int i = 0; i < peers.Count; i++)
            {
                if (!Session.Send(peers[i], Channel, inline, true)) ok = false;
            }
            return ok;
        }
        byte[] data = Encoding.UTF8.GetBytes(json);
        string name = BlobName(key);
        for (int i = 0; i < peers.Count; i++)
        {
            if (NetBlob.Send(peers[i], Plugin.PluginGuid, name, data) < 0) ok = false;
        }
        return ok;
    }

    private static string Decode(string b64)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.decode", e);
            return null;
        }
    }

    private static string BlobName(string key)
    {
        return BlobHead + CombatSync.Fnv(key == null ? "" : key).ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Surrogate(string key)
    {
        return "coop-" + CombatSync.Fnv(key).ToString("x16", System.Globalization.CultureInfo.InvariantCulture) + "-int";
    }

    private static string Capture(Interactable target, string key)
    {
        string real = SaveController.GetObjectGUID(target);
        SaveController.SetObjectGUID(target, Surrogate(key));
        try { return SaveData.Capture(target); }
        finally { SaveController.SetObjectGUID(target, real); }
    }

    private static bool Apply(Interactable target, string key, string json)
    {
        string real = SaveController.GetObjectGUID(target);
        SaveController.SetObjectGUID(target, Surrogate(key));
        try
        {
            BreakableLock breakable = target as BreakableLock;
            TimeOfDayAzure.Timer before = breakable != null && breakable._Lock != null ? breakable._Lock.timer : null;
            try { return SaveData.Apply(target, json); }
            finally { DropRelock(target, before); }
        }
        finally { SaveController.SetObjectGUID(target, real); }
    }

    private static string Key(Interactable target)
    {
        try
        {
            if (target == null) return null;
            string k = null;
            GameObject piece = ContainerWire.PieceOf(target);
            string id = piece == null ? null : ContainerWire.IdOf(piece);
            if (id != null) k = "p" + id;
            if (string.IsNullOrEmpty(k)) k = CombatSync.KeyOf(target);
            if (string.IsNullOrEmpty(k)) return null;
            return k.Length > 60 ? "h" + CombatSync.Fnv(k).ToString("x16", System.Globalization.CultureInfo.InvariantCulture) : k.Replace("|", "_");
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.Key", e);
            return null;
        }
    }

    private static bool Of(Interactable target, string[] names)
    {
        Type type = target == null ? null : target.GetType();
        int depth = 0;
        while (type != null && type != typeof(Interactable) && depth < 16)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (type.Name == names[i]) return true;
            }
            type = type.BaseType;
            depth++;
        }
        return false;
    }

    private static bool TypeMatches(Interactable target, string type)
    {
        if (target == null) return false;
        if (string.IsNullOrEmpty(type)) return true;
        return target.GetType().Name == type || Of(target, new string[] { type });
    }

    private static Interactable Find(string key)
    {
        int handle = SceneManager.GetActiveScene().handle;
        Interactable target;
        if (handle == sceneHandle)
        {
            if (cache.TryGetValue(key, out target) && target != null) return target;
            if (Time.unscaledTime < nextMiss && Time.unscaledTime < nextIndex) return null;
        }
        sceneHandle = handle;
        nextIndex = Time.unscaledTime + IndexEvery;
        nextMiss = Time.unscaledTime + MissEvery;
        cache.Clear();
        Interactable[] all = UnityEngine.Object.FindObjectsByType<Interactable>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            Interactable item = all[i];
            if (item == null || !Of(item, Allow)) continue;
            string k = Key(item);
            if (k == null) continue;
            if (cache.ContainsKey(k))
            {
                CoopLog.Once("InteractableSync.clash." + k, "coop: two interactables of this scene share the key " + k + ", both are left out");
                cache[k] = null;
            }
            else cache[k] = item;
        }
        return cache.TryGetValue(key, out target) ? target : null;
    }

    public static void OnSceneChanged()
    {
        int handle = SceneManager.GetActiveScene().handle;
        if (handle == sceneHandle) return;
        sceneHandle = handle;
        cache.Clear();
        toPush.Clear();
        pushAt.Clear();
        pushAgain.Clear();
        toStop.Clear();
        stopAt.Clear();
        unapplied.Clear();
        unappliedUntil.Clear();
        askedAt.Clear();
        lockSeen.Clear();
        ResumeLocks();
        nextLockFind = 0f;
        serving.Clear();
        pendingKey = null;
        nextIndex = 0f;
        nextMiss = 0f;
        unknownSince = 0f;
    }

    public static void Reset()
    {
        cache.Clear();
        toPush.Clear();
        pushAt.Clear();
        pushAgain.Clear();
        toStop.Clear();
        stopAt.Clear();
        unapplied.Clear();
        unappliedUntil.Clear();
        askedAt.Clear();
        lockSeen.Clear();
        ResumeLocks();
        nextLockFind = 0f;
        rates.Clear();
        serving.Clear();
        sceneHandle = -1;
        nextIndex = 0f;
        nextMiss = 0f;
        reentry = 0;
        pendingKey = null;
        pendingAt = 0f;
        unknownSince = 0f;
        nextPrune = 0f;
    }
}
