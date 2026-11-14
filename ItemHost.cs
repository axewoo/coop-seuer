using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ObenseuerCore;
using UnityEngine;

internal static partial class ItemHost
{
    private const int MaxOutbox = 2000;
    private const int PerFrame = 4;
    private const int MaxRequests = 4;
    private const float RateWindow = 1f;
    private const float PruneInterval = 1f;
    private const int MaxGrant = 100000;
    private const int GroundMask = 8523777;
    private const float GroundUp = 1f;
    private const float GroundDown = 4f;
    private static readonly Dictionary<GameObject, string> ids = new Dictionary<GameObject, string>();
    private static readonly Dictionary<string, GameObject> objects = new Dictionary<string, GameObject>();
    private static readonly Dictionary<ulong, Queue<string>> outbox = new Dictionary<ulong, Queue<string>>();
    private static readonly Dictionary<ulong, Requester> requesters = new Dictionary<ulong, Requester>();
    private static readonly HashSet<string> taken = new HashSet<string>();
    private static readonly List<GameObject> dead = new List<GameObject>();
    private static readonly List<ulong> gone = new List<ulong>();
    private static readonly List<CollectibleItem> pendingSpawns = new List<CollectibleItem>();
    internal static int Advertised { get { return objects.Count; } }
    private static FieldInfo spawnSettings;
    private static FieldInfo respawnEnabled;
    private static int counter;
    private static int quiet;
    private static string takenScene;
    private static float nextPrune;

    private sealed class Requester
    {
        public int LastReq;
        public string LastReply;
        public string LastGone;
        public float WindowStart;
        public int Count;
    }

    private sealed class Send
    {
        public CollectibleItem Item;
        public string Meta;
    }

    public static bool Active
    {
        get { return FreeRoam.IAmAuthority; }
    }

    public static bool Quiet
    {
        get { return quiet > 0; }
    }

    public static void Init()
    {
    }

    public static void Handle(ulong from, ItemMsg m)
    {
        try
        {
            if (!Active) return;
            if (CoopDebug.On("item")) CoopDebug.Event("item", "req." + m.Kind, "host got " + m.Kind + " from " + from + " req " + m.ReqId + " id " + m.Id + " item " + m.ItemId + " x" + m.Amount + " scene " + m.Scene);
            if (m.Kind == "HELLO") Hello(from, m);
            else if (m.Kind == "REQPICK") Pick(from, m);
            else if (m.Kind == "REQDROP") Drop(from, m);
            else if (m.Kind == "GACK") AckGrant(from, m.ReqId);
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemHost.Handle", e);
        }
    }

    public static void Tick()
    {
        try
        {
            float now = Time.unscaledTime;
            if (now >= nextPrune)
            {
                nextPrune = now + PruneInterval;
                Prune();
            }
            FlushSpawns();
            Drain();
            ManifestTick();
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemHost.Tick", e);
        }
    }

    private static void FlushSpawns()
    {
        if (pendingSpawns.Count == 0) return;
        List<CollectibleItem> batch = new List<CollectibleItem>(pendingSpawns);
        pendingSpawns.Clear();
        string scene = Scene();
        for (int i = 0; i < batch.Count; i++)
        {
            CollectibleItem item = batch[i];
            if (item == null || item.gameObject == null || !item.gameObject.activeInHierarchy) continue;
            if (localOnly.Contains(item.GetInstanceID())) continue;
            if (scene == null || item.gameObject.scene.name != scene) continue;
            if (item.Item == null || item.Item.Item == null || item.amount <= 0) continue;
            bool dropped = playerDrops.Contains(item.GetInstanceID());
            if (dropped ? (item.spawner != null || Respawns(item)) : Kept(item))
            {
                CoopLog.Once("ItemHost.kept", "a spawned item a spawner, a respawn setting or the building system keeps is not broadcast: item " + item.Item.ID);
                continue;
            }
            if (ItemMeta.LosesData(item.meta)) continue;
            string meta;
            string refused;
            if (!ItemMeta.TryEncode(item.meta, out meta, out refused))
            {
                CoopLog.Log.Info("coop: a dropped item is not shown to guests, its meta state is not in co-op yet: " + refused);
                continue;
            }
            string id = IdFor(item);
            if (CoopDebug.On("item")) CoopDebug.Event("item", "spawn", "advertised floor item " + item.Item.ID + " x" + item.amount + " id " + id + " scene " + scene);
            Broadcast(scene, ItemWire.Spawn(scene, id, item.Item.ID, item.amount, item.transform.position, meta));
        }
    }

    public static void Reset()
    {
        ids.Clear();
        objects.Clear();
        outbox.Clear();
        requesters.Clear();
        grants.Clear();
        pendingSpawns.Clear();
        localOnly.Clear();
        localHolds = 0;
        quiet = 0;
        taken.Clear();
        takenScene = null;
        ManifestReset();
    }

    public static string Scene()
    {
        return Active ? GameRefs.AreaId : null;
    }

    public static string IdFor(Collectible item)
    {
        if (item == null) return null;
        GameObject go = item.gameObject;
        string id;
        if (ids.TryGetValue(go, out id)) return id;
        string strong = ItemWire.StrongId(item);
        if (strong != null && !objects.ContainsKey(strong))
        {
            ids[go] = strong;
            objects[strong] = go;
            return strong;
        }
        id = (Session.IsHost ? "i" : "g" + (Session.Me % 100000UL) + "i") + (++counter);
        ids[go] = id;
        objects[id] = go;
        return id;
    }

    public static string KnownId(Collectible item)
    {
        if (item == null) return null;
        string id;
        if (ids.TryGetValue(item.gameObject, out id)) return id;
        string strong = ItemWire.StrongId(item);
        return strong != null && !objects.ContainsKey(strong) ? strong : null;
    }

    public static void OnDestroyed(Collectible item)
    {
        try
        {
            if (item == null) return;
            localOnly.Remove(item.GetInstanceID());
            if (Quiet) return;
            Destroyed(item);
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemHost.OnDestroyed", e);
        }
    }

    public static bool PlayerDropping;
    private static readonly HashSet<int> playerDrops = new HashSet<int>();
    private static int localHolds;
    private static readonly HashSet<int> localOnly = new HashSet<int>();

    public static void ForgetDrops()
    {
        playerDrops.Clear();
        localOnly.Clear();
    }

    public static void HoldLocal()
    {
        if (!PrisonLocal.Active()) return;
        localHolds++;
    }

    public static void ReleaseLocal()
    {
        if (localHolds > 0) localHolds--;
    }

    public static void OnSpawned(CollectibleItem item)
    {
        try
        {
            if (Quiet || item == null) return;
            if (localHolds > 0)
            {
                localOnly.Add(item.GetInstanceID());
                return;
            }
            string scene = Scene();
            if (scene == null || item.gameObject.scene.name != scene) return;
            IdFor(item);
            pendingSpawns.Add(item);
            if (PlayerDropping) playerDrops.Add(item.GetInstanceID());
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemHost.OnSpawned", e);
        }
    }

    private static void Pick(ulong from, ItemMsg m)
    {
        if (!Admit(from, m.ReqId)) return;
        string scene = Scene();
        if (scene == null || m.Scene != scene || Session.AreaOf(from) != scene)
        {
            Refuse(from, m.ReqId, "not in this scene");
            return;
        }
        Collectible item = Resolve(m.Id);
        if (item == null)
        {
            Refuse(from, m.ReqId, "not found");
            return;
        }
        CollectibleItem piece = item as CollectibleItem;
        if (piece != null)
        {
            PickItem(from, m, piece, scene);
            return;
        }
        CollectibleCoin coin = item as CollectibleCoin;
        if (coin != null)
        {
            PickCoin(from, m, coin, scene);
            return;
        }
        Refuse(from, m.ReqId, ItemWire.NotYet);
    }

    private static void PickItem(ulong from, ItemMsg m, CollectibleItem piece, string scene)
    {
        if (!piece.enabled)
        {
            Refuse(from, m.ReqId, "already taken");
            return;
        }
        if (piece.spawner != null || Respawns(piece))
        {
            Refuse(from, m.ReqId, ItemWire.NotYet);
            return;
        }
        if (ItemMeta.LosesData(piece.meta))
        {
            Refuse(from, m.ReqId, ItemWire.NotYet + ": the contents of this item do not travel yet, it stays on the floor");
            return;
        }
        string meta;
        string refused;
        if (!ItemMeta.TryEncode(piece.meta, out meta, out refused))
        {
            Refuse(from, m.ReqId, ItemWire.NotYet + ": " + refused);
            return;
        }
        ItemStack stack;
        string id = IdFor(piece);
        try
        {
            stack = piece.GetItemStack();
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemHost.GetItemStack", e);
            Refuse(from, m.ReqId, "cannot take");
            return;
        }
        if (stack == null || stack.itemId < 2 || stack.itemAmount <= 0)
        {
            Refuse(from, m.ReqId, "cannot take");
            return;
        }
        int itemId = stack.itemId;
        int amount = stack.itemAmount;
        if (amount > ItemWire.MaxAmount)
        {
            Refuse(from, m.ReqId, "too many items");
            return;
        }
        if (!Destroy(piece))
        {
            Refuse(from, m.ReqId, "cannot take");
            return;
        }
        string give = ItemWire.Give(m.ReqId, itemId, amount, meta);
        if (CoopDebug.On("item")) CoopDebug.Event("item", "give", "gave item " + itemId + " x" + amount + " to " + from + " req " + m.ReqId + " id " + id);
        string gone = ItemWire.Gone(scene, id);
        Forget(piece.gameObject, id);
        Mark(id);
        Enqueue(from, give, true);
        RememberGrant(from, m.ReqId, give);
        Broadcast(scene, gone);
        Settle(from, m.ReqId, give, gone);
    }

    private static void PickCoin(ulong from, ItemMsg m, CollectibleCoin coin, string scene)
    {
        if (!coin.enabled || Respawns(coin) || Spawned(coin))
        {
            Refuse(from, m.ReqId, "already taken");
            return;
        }
        float amount = coin.Amount;
        bool real = coin.isRealMoney;
        if (amount < 0.01f)
        {
            Refuse(from, m.ReqId, "cannot take");
            return;
        }
        if (amount > ItemWire.MaxMoney)
        {
            Refuse(from, m.ReqId, "too much money");
            return;
        }
        string id = IdFor(coin);
        if (!Destroy(coin))
        {
            Refuse(from, m.ReqId, "cannot take");
            return;
        }
        string give = ItemWire.Money(m.ReqId, real, amount);
        string gone = ItemWire.Gone(scene, id);
        Forget(coin.gameObject, id);
        Mark(id);
        Enqueue(from, give, true);
        RememberGrant(from, m.ReqId, give);
        Broadcast(scene, gone);
        Settle(from, m.ReqId, give, gone);
    }

    private static void Drop(ulong from, ItemMsg m)
    {
        if (!Admit(from, m.ReqId)) return;
        string scene = Scene();
        string area = Session.AreaOf(from);
        if (scene == null || m.Scene != scene || (!string.IsNullOrEmpty(area) && area != scene))
        {
            Refuse(from, m.ReqId, "not in this scene (host " + (scene ?? "none") + ", request " + (m.Scene ?? "none") + ", presence " + (area ?? "unknown") + ")");
            return;
        }
        if (m.Amount < 1 || m.Amount > MaxGrant)
        {
            Refuse(from, m.ReqId, "bad amount");
            return;
        }
        ItemStack stack = new ItemStack();
        stack.itemId = m.ItemId;
        stack.itemAmount = m.Amount;
        stack.SetItemReference();
        if (stack.itemReference == null || stack.itemReference.Item == null)
        {
            Refuse(from, m.ReqId, "unknown item");
            return;
        }
        object[] meta;
        string refused;
        if (!ItemMeta.TryDecode(m.Meta, out meta, out refused))
        {
            Refuse(from, m.ReqId, ItemWire.NotYet + ": " + refused);
            return;
        }
        stack.Meta = ItemMeta.Merge(stack.itemReference.Item, m.Amount, meta);
        Vector3 position = new Vector3(m.X, m.Y, m.Z);
        bool wasDropping = PlayerDropping;
        PlayerDropping = true;
        try
        {
            SpawnDrop(stack, position);
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemHost.Drop", e);
            Refuse(from, m.ReqId, "spawn failed");
            return;
        }
        finally
        {
            PlayerDropping = wasDropping;
        }
        if (CoopDebug.On("item")) CoopDebug.Event("item", "drop", "spawned a drop of item " + m.ItemId + " x" + m.Amount + " for " + from + " req " + m.ReqId + " in " + scene);
        string ack = ItemWire.Ack(m.ReqId, true, "");
        Enqueue(from, ack);
        Settle(from, m.ReqId, ack, null);
    }

    private static void Refuse(ulong peer, int reqId, string reason)
    {
        ItemDiag.Info("host.refuse." + ItemDiag.Key(reason), "coop: item request " + reqId + " from " + peer + " refused: " + reason);
        string ack = ItemWire.Ack(reqId, false, reason);
        Enqueue(peer, ack);
        Settle(peer, reqId, ack, null);
    }

    private static bool Destroy(Collectible item)
    {
        quiet++;
        try
        {
            return item.Destroy();
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemHost.Destroy", e);
            return false;
        }
        finally
        {
            quiet--;
        }
    }

    private static Collectible Resolve(string id)
    {
        if (string.IsNullOrEmpty(id) || id.IndexOf(ItemWire.PathMarker, StringComparison.Ordinal) >= 0 || Taken(id)) return null;
        GameObject go;
        if (objects.TryGetValue(id, out go) && go != null) return go.GetComponent<Collectible>();
        GameObject found = WorldItems.Find(id);
        return found == null ? null : found.GetComponent<Collectible>();
    }

    private static bool Taken(string id)
    {
        SyncTaken();
        return taken.Contains(id);
    }

    private static void Mark(string id)
    {
        SyncTaken();
        taken.Add(id);
    }

    private static void SyncTaken()
    {
        string scene = Scene();
        if (takenScene == scene) return;
        taken.Clear();
        takenScene = scene;
    }

    private static List<Send> WeakInScene(string scene)
    {
        List<Send> result = new List<Send>();
        foreach (Collectible item in Collectible.allCollectibles)
        {
            if (!Live(item, scene) || ItemWire.StrongId(item) != null) continue;
            CollectibleItem piece = item as CollectibleItem;
            if (piece == null || piece.Item == null || piece.Item.Item == null || piece.amount <= 0) continue;
            if (localOnly.Contains(piece.GetInstanceID())) continue;
            if (playerDrops.Contains(piece.GetInstanceID()) ? (piece.spawner != null || Respawns(piece)) : Kept(piece)) continue;
            if (ItemMeta.LosesData(piece.meta)) continue;
            string meta;
            string refused;
            if (!ItemMeta.TryEncode(piece.meta, out meta, out refused))
            {
                CoopLog.Log.Info("coop: a floor item is left out of the snapshot, its meta state is not in co-op yet: " + refused);
                continue;
            }
            Send send = new Send();
            send.Item = piece;
            send.Meta = meta;
            result.Add(send);
        }
        return result;
    }

    private static bool Kept(CollectibleItem piece)
    {
        return piece.spawner != null || Respawns(piece) || Placeable(piece);
    }

    private static bool Placeable(Collectible piece)
    {
        try
        {
            if (piece == null) return true;
            if (piece.DisableSpawnItem) return true;
            return piece.GetComponentInParent<FurniturePlaceable>() != null;
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemHost.placeable", e);
            return true;
        }
    }

    internal static bool Respawns(Collectible item)
    {
        try
        {
            if (item == null) return false;
            if (spawnSettings == null) spawnSettings = AccessTools.Field(typeof(Collectible), "spawnSettings");
            object settings = spawnSettings == null ? null : spawnSettings.GetValue(item);
            if (settings == null) return false;
            if (respawnEnabled == null) respawnEnabled = AccessTools.Field(settings.GetType(), "respawnEnabled");
            return respawnEnabled != null && (bool)respawnEnabled.GetValue(settings);
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemHost.respawn", e);
            return true;
        }
    }

    internal static bool Spawned(Collectible item)
    {
        try
        {
            return item != null && item.GetComponentInParent<CollectibleItemSpawner>() != null;
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemHost.spawner", e);
            return true;
        }
    }

    private static bool Live(Collectible item, string scene)
    {
        return item != null && item.gameObject != null && item.gameObject.activeInHierarchy && item.gameObject.scene.name == scene;
    }

    private static void Forget(GameObject go, string id)
    {
        if (go != null) ids.Remove(go);
        objects.Remove(id);
    }
}
