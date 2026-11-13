using System.Collections.Generic;
using OS.Items;
using UnityEngine;

internal static partial class ItemGuest
{
    private const int MaxManifest = 4000;
    private const float ManifestGrace = 5f;
    private const float ResyncInterval = 30f;
    private const int MaxUnmappable = 512;
    private static readonly HashSet<string> manifest = new HashSet<string>();
    private static readonly List<string> staleIds = new List<string>();
    private static readonly List<string> deadIds = new List<string>();
    private static readonly Dictionary<string, float> mappedAt = new Dictionary<string, float>();
    private static readonly HashSet<string> liveSpawned = new HashSet<string>();
    private static readonly HashSet<string> listedBefore = new HashSet<string>();
    private static readonly HashSet<string> listedNow = new HashSet<string>();
    private static readonly HashSet<string> unmappable = new HashSet<string>();
    private static int manifestSeq = -1;
    private static int manifestIdx;
    private static bool manifestValid;
    private static bool manifestLogged;
    private static string manifestScene;
    private static float nextResync;

    private static void MapAt(string id, GameObject go, bool fromSpawn)
    {
        Map(id, go);
        if (string.IsNullOrEmpty(id)) return;
        ClearUnmappable(id);
        mappedAt[id] = Time.unscaledTime;
        if (fromSpawn) liveSpawned.Add(id);
    }

    private static void ManifestGuestReset()
    {
        manifest.Clear();
        manifestSeq = -1;
        manifestIdx = 0;
        manifestValid = false;
        manifestLogged = false;
        manifestScene = null;
        nextResync = 0f;
        mappedAt.Clear();
        liveSpawned.Clear();
        listedBefore.Clear();
        listedNow.Clear();
        unmappable.Clear();
    }

    private static void NoteUnmappable(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (unmappable.Count > MaxUnmappable) unmappable.Clear();
        unmappable.Add(id);
    }

    private static void ClearUnmappable(string id)
    {
        if (!string.IsNullOrEmpty(id)) unmappable.Remove(id);
    }

    private static void OnManifest(ItemMsg m)
    {
        if (m == null || m.Ids == null) return;
        if (FreeRoam.IAmAuthority || !Active || m.Scene != GameRefs.AreaId) return;
        if (manifestScene != m.Scene)
        {
            string scene = m.Scene;
            ManifestGuestReset();
            manifestScene = scene;
        }
        if (m.Idx == 0)
        {
            manifest.Clear();
            manifestSeq = m.Seq;
            manifestIdx = 0;
            manifestValid = true;
            manifestLogged = false;
        }
        else if (!manifestValid || m.Seq != manifestSeq || m.Idx != manifestIdx)
        {
            InvalidateManifest("a packet of the run was lost, duplicated or out of order");
            return;
        }
        for (int i = 0; i < m.Ids.Count; i++)
        {
            if (manifest.Count >= MaxManifest)
            {
                InvalidateManifest("the run held more ids than one name set may hold");
                return;
            }
            manifest.Add(m.Ids[i]);
        }
        manifestIdx = m.Idx + 1;
        if (!m.Last) return;
        bool valid = manifestValid;
        manifest.Clear();
        manifestSeq = -1;
        manifestIdx = 0;
        manifestValid = false;
        if (!valid) return;
        ApplyManifest();
    }

    private static void InvalidateManifest(string reason)
    {
        manifestValid = false;
        manifest.Clear();
        if (manifestLogged) return;
        manifestLogged = true;
        CoopLog.Log.Info("coop: an item name set was dropped, " + reason);
        CoopDebug.Event("item", "manifestdrop", reason);
    }

    private static void ApplyManifest()
    {
        float now = Time.unscaledTime;
        int removed = 0;
        int requested = 0;
        staleIds.Clear();
        deadIds.Clear();
        listedNow.Clear();
        foreach (string id in manifest) listedNow.Add(id);
        foreach (KeyValuePair<string, GameObject> pair in items)
        {
            if (listedNow.Contains(pair.Key)) continue;
            if (pair.Value == null)
            {
                deadIds.Add(pair.Key);
                continue;
            }
            if (Exempt(pair.Key, pair.Value, now)) continue;
            if (listedBefore.Contains(pair.Key)) continue;
            staleIds.Add(pair.Key);
        }
        for (int i = 0; i < deadIds.Count; i++) items.Remove(deadIds[i]);
        for (int i = 0; i < staleIds.Count; i++)
        {
            string id = staleIds[i];
            CoopDebug.LogV("item", "the name set of the authority no longer holds " + id + ", the mirror was removed");
            RemoveLocal(id);
            if (items.ContainsKey(id)) items.Remove(id);
            removed++;
        }
        foreach (string id in listedNow)
        {
            if (items.ContainsKey(id)) continue;
            if (unmappable.Contains(id)) continue;
            if (!listedBefore.Contains(id)) continue;
            requested++;
        }
        PruneManifest(now);
        SwapListed();
        if (removed > 0 || requested > 0) CoopLog.Log.Info("coop: item manifest removed " + removed + " ghosts, requested " + requested);
        if (CoopDebug.On("item")) CoopDebug.Event("item", "manifestapply", "kept " + items.Count + ", removed " + removed + ", requested " + requested);
        if (requested > 0) Resync(now, requested);
    }

    private static void SwapListed()
    {
        listedBefore.Clear();
        foreach (string id in listedNow) listedBefore.Add(id);
    }

    private static bool Exempt(string id, GameObject go, float now)
    {
        float mappedTime;
        if (mappedAt.TryGetValue(id, out mappedTime) && now - mappedTime < ManifestGrace) return true;
        if (pending != null && pending.Kind == Kind.Drop && liveSpawned.Contains(id)) return true;
        Collectible item = go.GetComponent<Collectible>();
        if (LocalLoot(item)) return true;
        return ItemWire.StrongId(item) != null;
    }

    private static void PruneManifest(float now)
    {
        deadIds.Clear();
        foreach (KeyValuePair<string, float> pair in mappedAt)
        {
            if (!items.ContainsKey(pair.Key) || now - pair.Value >= ManifestGrace) deadIds.Add(pair.Key);
        }
        for (int i = 0; i < deadIds.Count; i++) mappedAt.Remove(deadIds[i]);
        deadIds.Clear();
        foreach (string id in liveSpawned)
        {
            if (!items.ContainsKey(id)) deadIds.Add(id);
        }
        for (int i = 0; i < deadIds.Count; i++) liveSpawned.Remove(deadIds[i]);
        if (unmappable.Count == 0) return;
        deadIds.Clear();
        foreach (string id in unmappable)
        {
            if (!listedNow.Contains(id)) deadIds.Add(id);
        }
        for (int i = 0; i < deadIds.Count; i++) unmappable.Remove(deadIds[i]);
    }

    private static void Resync(float now, int missing)
    {
        if (now < nextResync) return;
        if (!Session.Send(FreeRoam.Authority, ItemWire.Channel, ItemWire.HelloResync(GameRefs.AreaId), true)) return;
        nextResync = now + ResyncInterval;
        CoopLog.Log.Info("coop: " + missing + " floor items of this scene are missing, the authority snapshot was asked for again");
    }
}
