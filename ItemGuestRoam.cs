using System;
using System.Collections.Generic;
using OS.Items;
using UnityEngine;

internal static partial class ItemGuest
{
    private const int MaxRawId = 120;
    private const string SnapItemKind = "SNAPI";

    private static readonly HashSet<GameObject> kept = new HashSet<GameObject>();
    private static bool capturing;
    private static CollectibleItem captured;
    private static int adoptedMatches;
    private static int discardedMatches;

    public static void Adopt()
    {
        List<GameObject> mirrors = new List<GameObject>();
        foreach (GameObject go in spawned)
        {
            if (go != null && !mirrors.Contains(go)) mirrors.Add(go);
        }
        foreach (GameObject go in kept)
        {
            if (go != null && !mirrors.Contains(go)) mirrors.Add(go);
        }
        items.Clear();
        mapped.Clear();
        spawned.Clear();
        kept.Clear();
        pending = null;
        snapScene = null;
        nextHello = 0f;
        applying = false;
        capturing = false;
        captured = null;
        int keptCount = 0;
        int lostCount = 0;
        for (int i = 0; i < mirrors.Count; i++)
        {
            Collectible item = mirrors[i].GetComponent<Collectible>();
            string id = item == null ? null : ItemHost.Register(item);
            if (id == null)
            {
                Remove(mirrors[i]);
                lostCount++;
                continue;
            }
            kept.Add(mirrors[i]);
            keptCount++;
        }
        CoopLog.Log.Info("coop: this machine now runs the floor items of its scene, " + keptCount + " mirrored items are kept and registered, " + lostCount + " could not be identified and were removed");
    }

    public static void OnItemSpawned(CollectibleItem piece)
    {
        if (!capturing || piece == null) return;
        captured = piece;
    }

    private static void SpawnMirror(ItemMsg m)
    {
        GameObject known;
        if (items.TryGetValue(m.Id, out known) && known != null) return;
        Vector3 position = new Vector3(m.X, m.Y, m.Z);
        CollectibleItem match = Match(m.ItemId, m.Amount, position);
        if (match != null && Owned(match))
        {
            MapAt(m.Id, match.gameObject, m.Kind == "SPAWN");
            if (!spawned.Contains(match.gameObject) && !kept.Contains(match.gameObject)) kept.Add(match.gameObject);
            adoptedMatches++;
            CoopLog.Log.Info("coop: synced item " + m.Id + " (item " + m.ItemId + " x" + m.Amount + ") was mapped to the floor object " + match.name + " this machine already had; adopted " + adoptedMatches + ", duplicates removed " + discardedMatches);
            return;
        }
        if (match != null && m.Kind == SnapItemKind && ItemWire.StrongId(match) == null)
        {
            discardedMatches++;
            CoopLog.Log.Info("coop: synced item " + m.Id + " (item " + m.ItemId + " x" + m.Amount + ") found an unclaimed duplicate " + match.name + " in the same place, it was removed before the host copy was spawned; adopted " + adoptedMatches + ", duplicates removed " + discardedMatches);
            Remove(match.gameObject);
        }
        CollectibleItem created = SpawnCapture(m.ItemId, m.Amount, position, m.Meta);
        if (created == null)
        {
            NoteUnmappable(m.Id);
            return;
        }
        spawned.Add(created.gameObject);
        MapAt(m.Id, created.gameObject, m.Kind == "SPAWN");
    }

    private static bool Owned(CollectibleItem piece)
    {
        GameObject go = piece.gameObject;
        if (spawned.Contains(go)) return true;
        if (kept.Contains(go)) return true;
        return LocalDrops.TakeMatch(piece);
    }

    private static bool LocalLoot(Collectible item)
    {
        CollectibleItem piece = item as CollectibleItem;
        return piece != null && (piece.spawner != null || ItemHost.Spawned(piece) || ItemHost.Respawns(piece));
    }

    private static bool IsRawId(string id)
    {
        if (string.IsNullOrEmpty(id)) return true;
        if (id.Length > MaxRawId) return true;
        if (id.IndexOf(ItemWire.PathMarker, StringComparison.Ordinal) >= 0) return true;
        return id.IndexOf("/DestructibleList", StringComparison.Ordinal) >= 0;
    }

    private static void DropMirror(string id)
    {
        GameObject tracked;
        if (items.TryGetValue(id, out tracked))
        {
            items.Remove(id);
            if (tracked == null) return;
            spawned.Remove(tracked);
            mapped.Remove(tracked);
            Remove(tracked);
            return;
        }
        if (IsRawId(id))
        {
            ItemDiag.Info("guest.gone.raw", "coop: a GONE for a floor item this machine never mapped was ignored: " + id);
            return;
        }
        Collectible item = Resolve(id);
        if (item == null) return;
        items.Remove(id);
        spawned.Remove(item.gameObject);
        mapped.Remove(item.gameObject);
        Remove(item.gameObject);
    }

    private static CollectibleItem SpawnCapture(int itemId, int amount, Vector3 position, string meta)
    {
        ItemStack stack = new ItemStack();
        stack.itemId = itemId;
        stack.itemAmount = amount;
        stack.SetItemReference();
        if (stack.itemReference == null || stack.itemReference.Item == null)
        {
            CoopLog.Once("ItemGuest.item", "the host named an unknown item: " + itemId);
            return null;
        }
        object[] decoded = Decode(meta);
        if (decoded == null) return null;
        stack.Meta = ItemMeta.Merge(stack.itemReference.Item, amount, decoded);
        CollectibleItem created = null;
        captured = null;
        capturing = true;
        try
        {
            ItemOperations.SpawnItemToPosition(stack, position, default(RaycastHit));
            created = captured;
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemGuest.spawn", e);
            if (captured != null) Remove(captured.gameObject);
            return null;
        }
        finally
        {
            capturing = false;
            captured = null;
        }
        if (created != null) return created;
        CoopLog.Once("ItemGuest.capture", "a synced item was spawned but the object it created was not seen, its id stays unmapped: item " + itemId + " x" + amount);
        return null;
    }

    private static void ForgetAll()
    {
        foreach (GameObject go in new List<GameObject>(spawned)) Remove(go);
        spawned.Clear();
        items.Clear();
        mapped.Clear();
        capturing = false;
        captured = null;
        PruneKept();
    }

    private static void PruneKept()
    {
        if (kept.Count == 0) return;
        List<GameObject> dead = new List<GameObject>();
        foreach (GameObject go in kept)
        {
            if (go == null) dead.Add(go);
        }
        for (int i = 0; i < dead.Count; i++) kept.Remove(dead[i]);
    }

    private static void RefuseFull(Collectible item)
    {
        Refused("pickup", item, "Your inventory is full");
        try
        {
            if (Notifications.instance != null) Notifications.instance.CreateNotification("Co-op", "Your inventory is full");
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemGuest.RefuseFull", e);
        }
    }

    private static string Where(Collectible item)
    {
        if (item == null) return "";
        Vector3 p = item.transform.position;
        return " at (" + p.x.ToString("F2") + ", " + p.y.ToString("F2") + ", " + p.z.ToString("F2") + ")";
    }
}
