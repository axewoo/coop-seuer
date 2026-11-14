using System;
using System.Collections.Generic;
using ObenseuerCore;
using OS.Items;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static partial class ItemGuest
{
    private const float HelloInterval = 5f;
    private const float RequestTimeout = 10f;
    private const float OrphanLife = 120f;
    private const int MaxAttempts = 3;
    private const int MaxSettled = 64;
    private const float MatchRadius = 1f;
    private const float DropForward = 0.5f;
    private static readonly Dictionary<string, GameObject> items = new Dictionary<string, GameObject>();
    private static readonly HashSet<GameObject> mapped = new HashSet<GameObject>();
    private static readonly List<GameObject> spawned = new List<GameObject>();
    private static readonly List<int> doneReqs = new List<int>();
    private static readonly Dictionary<int, Orphan> orphans = new Dictionary<int, Orphan>();
    private static readonly List<int> stale = new List<int>();
    internal static int Mirrored { get { return items.Count; } }
    private static string snapScene;
    private static int snapHandle;
    private static bool wasShared;
    private static float nextHello;
    private static int lastReqId = SeedReqId();
    private static Pending pending;
    private static bool applying;

    private enum Kind
    {
        Pick,
        Drop
    }

    private sealed class Pending
    {
        public int ReqId;
        public Kind Kind;
        public string Id;
        public string Text;
        public ItemData Data;
        public int ItemId;
        public int Count;
        public bool Local;
        public bool DropAll;
        public int Attempts;
        public float SentAt;
    }

    private sealed class Orphan
    {
        public int ItemId;
        public int Count;
        public ItemStack Stack;
        public float Expires;
    }

    public static bool Shared
    {
        get
        {
            if (!Session.IsInLobby || !GuestSession.InHostWorld || FreeRoam.Authority == 0UL || FreeRoam.IAmAuthority) return false;
            string area = GameRefs.AreaId;
            return area != null && Session.AreaOf(FreeRoam.Authority) == area;
        }
    }

    public static bool Active
    {
        get
        {
            string area = GameRefs.AreaId;
            return Shared && snapScene != null && snapScene == area;
        }
    }

    public static bool Applying
    {
        get { return applying; }
    }

    public static void Init() { }

    public static void Tick()
    {
        try
        {
            bool shared = Shared;
            if (wasShared && !shared) Reset();
            wasShared = shared;
            float now = Time.unscaledTime;
            PruneOrphans(now);
            SendHello(now);
            if (pending != null && now - pending.SentAt > RequestTimeout) Retry(now);
            RemainderTick();
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemGuest.Tick", e);
        }
    }

    private static void Retry(float now)
    {
        Pending lost = pending;
        if (lost.Attempts < MaxAttempts && Active && Session.Send(FreeRoam.Authority, ItemWire.Channel, lost.Text, true))
        {
            lost.Attempts++;
            lost.SentAt = now;
            CoopLog.Log.Debug("item.retry", "no answer to request " + lost.ReqId + ", attempt " + lost.Attempts);
            return;
        }
        pending = null;
        CoopLog.Log.Debug("item.timeout", "the host did not answer request " + lost.ReqId + "; nothing changed locally");
        Notify("The host did not answer, nothing changed.");
    }

    public static void OnSceneLoaded()
    {
        if (snapScene != null && snapScene == GameRefs.AreaId && snapHandle == SceneManager.GetActiveScene().handle) return;
        Reset();
    }

    public static void Reset()
    {
        ForgetAll();
        ManifestGuestReset();
        KeepRecordsForSession();
        pending = null;
        snapScene = null;
        nextHello = 0f;
        applying = false;
    }

    public static void Handle(ulong from, ItemMsg m)
    {
        try
        {
            if (from != FreeRoam.Authority || FreeRoam.IAmAuthority || !Session.IsInLobby) return;
            switch (m.Kind)
            {
                case "SNAP":
                    OnSnap(m);
                    break;
                case "SNAPI":
                case "SPAWN":
                    OnSpawn(m);
                    break;
                case "SNAPEND":
                    OnSnapEnd(m);
                    break;
                case "GONE":
                    OnGone(m);
                    break;
                case "IGIVE":
                    OnGive(m);
                    break;
                case "IMONEY":
                    OnMoney(m);
                    break;
                case "ACK":
                    OnAck(m);
                    break;
                case "IM":
                    OnManifest(m); break;
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemGuest.Handle", e);
        }
    }

    public static bool ItemInteractPrefix(CollectibleItem piece)
    {
        bool shared = Shared;
        try
        {
            if (GuestGate.Block(GuestGate.KindItem)) return false;
            if (!shared) return true;
            return PickupPrefix(piece);
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemGuest.ItemInteract", e);
            return !shared;
        }
    }

    public static bool InvokeActionsPrefix()
    {
        bool shared = Shared;
        try
        {
            if (GuestGate.Block(GuestGate.KindItem)) return false;
            if (!shared) return true;
            Notify("Pick it up first to use it in co-op");
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemGuest.InvokeActions", e);
            return !shared;
        }
    }

    public static bool PickupPrefix(Collectible item)
    {
        bool shared = Shared;
        try
        {
            if (GuestGate.Block(GuestGate.KindItem)) return false;
            if (!shared) return true;
            if (!Active || pending != null)
            {
                Refused("pickup", item, !Active ? "the host item list for this scene has not arrived yet" : "another item request is still waiting for the host");
                return false;
            }
            CollectibleItem piece = item as CollectibleItem;
            if (piece != null && !piece.InventoryIsNotFull())
            {
                RefuseFull(item);
                return false;
            }
            string id = IdOf(item);
            if (id == null)
            {
                if (LocalDrops.TakeMatch(item)) return true;
                if (LocalLoot(item))
                {
                    ItemDiag.Info("guest.item.local." + item.name, "item: local spawner pickup " + item.name);
                    return true;
                }
                Refused("pickup", item, "the item has no synced id and is not one of my unshared drops");
                return false;
            }
            int reqId = ++lastReqId;
            string text = ItemWire.ReqPick(reqId, GameRefs.AreaId, id);
            if (CoopDebug.On("item")) CoopDebug.Event("item", "reqpick", "asked the host to pick id " + id + " req " + reqId + " object " + item.name + " scene " + GameRefs.AreaId);
            if (!Session.Send(FreeRoam.Authority, ItemWire.Channel, text, true))
            {
                Refused("pickup", item, "the request could not be sent to the host");
                return false;
            }
            pending = new Pending { ReqId = reqId, Kind = Kind.Pick, Id = id, Text = text, Attempts = 1, SentAt = Time.unscaledTime };
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemGuest.PickupPrefix", e);
            return !shared;
        }
    }

    public static bool DropPrefix(ItemData data, int amount, bool dropAll)
    {
        bool shared = Shared;
        try
        {
            if (applying) return true;
            if (GuestGate.Block(GuestGate.KindItem)) return false;
            if (!shared) return LocalDrops.Remember(data);
            if (data == null || data.stack == null || data.stack.itemReference == null || data.stack.itemReference.Item == null) return true;
            if (data.stack.itemId < 2) return true;
            if (!Active || pending != null)
            {
                Refused("drop", null, !Active ? "the host item list for this scene has not arrived yet, the item stays in your inventory" : "another item request is still waiting for the host, the item stays in your inventory");
                return false;
            }
            if (ItemMeta.LosesData(data.meta))
            {
                Notify(ItemWire.NotYet + ": the contents of this item do not travel yet, it stays in your inventory");
                return false;
            }
            string meta;
            string refused;
            if (!ItemMeta.TryEncode(data.meta, out meta, out refused))
            {
                Notify(ItemWire.NotYet + ": " + refused);
                return false;
            }
            int count = dropAll ? data.amount : amount;
            if (count < 1) return true;
            int reqId = ++lastReqId;
            string text = ItemWire.ReqDrop(reqId, GameRefs.AreaId, data.stack.itemId, count, DropPosition(), meta);
            if (CoopDebug.On("item")) CoopDebug.Event("item", "reqdrop", "asked the host to drop item " + data.stack.itemId + " x" + count + " req " + reqId + " scene " + GameRefs.AreaId);
            if (!Session.Send(FreeRoam.Authority, ItemWire.Channel, text, true))
            {
                Refused("drop", null, "the request could not be sent to the host, the item stays in your inventory");
                return false;
            }
            orphans[reqId] = new Orphan { ItemId = data.stack.itemId, Count = count, Stack = data.stack, Expires = Time.unscaledTime + OrphanLife };
            pending = new Pending { ReqId = reqId, Kind = Kind.Drop, Data = data, ItemId = data.stack.itemId, Count = count, Local = true, DropAll = dropAll, Text = text, Attempts = 1, SentAt = Time.unscaledTime };
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemGuest.DropPrefix", e);
            return !shared;
        }
    }

    public static bool RefuseOther(Collectible item)
    {
        if (item is CollectibleItem || item is CollectibleCoin) return true;
        if (GuestGate.Block(GuestGate.KindItem)) return false;
        if (!Shared) return true;
        Notify(ItemWire.NotYet);
        return false;
    }

    private static void Refused(string kind, Collectible item, string reason)
    {
        string text = "coop: item " + kind + (item != null ? " of " + item.name + Where(item) : "") + " refused: " + reason;
        ItemDiag.Info("guest.item." + kind + "." + ItemDiag.Key(reason), text);
        Warn();
    }

    public static void Warn()
    {
        try
        {
            if (UseText.instance != null) UseText.instance.WarningUseEffect();
            else if (BuildingSystemMenu.instance != null) BuildingSystemMenu.instance.WarningTextAnimation();
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemGuest.Warn", e);
        }
    }

    public static void Notify(string reason)
    {
        if (string.IsNullOrEmpty(reason)) reason = "refused";
        ItemDiag.Info("guest.notice." + ItemDiag.Key(reason), "item refused: " + reason);
        Warn();
        try
        {
            if (Notifications.instance != null) Notifications.instance.CreateNotification("Co-op", reason);
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemGuest.Notify", e);
        }
    }

    private static void SendHello(float now)
    {
        string area = GameRefs.AreaId;
        if (snapScene != null || area == null || now < nextHello || !Shared) return;
        nextHello = now + HelloInterval;
        Session.Send(FreeRoam.Authority, ItemWire.Channel, ItemWire.Hello(area), true);
    }

    private static void ApplyDrop(Pending done)
    {
        if (!done.Local) return;
        bool alive = DropAlive(done);
        bool moved = false;
        if (alive)
        {
            applying = true;
            try
            {
                done.Data.DropItem(done.Count, done.DropAll && done.Data.amount == done.Count);
                moved = true;
            }
            catch (Exception e)
            {
                CoopLog.Once("ItemGuest.ApplyDrop", e);
            }
            finally
            {
                applying = false;
            }
        }
        if (!moved && (!alive || DropAlive(done))) RemoveById(done.ItemId, done.Count, done.Data == null ? null : done.Data.stack);
        GuestProfile.AfterTransfer();
    }

    private static bool DropAlive(Pending done)
    {
        try
        {
            ItemData data = done.Data;
            if (data == null || data.stack == null || data.stack.itemReference == null || data.stack.itemReference.Item == null) return false;
            return data.stack.itemId == done.ItemId && data.amount >= done.Count;
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemGuest.DropAlive", e);
            return false;
        }
    }

    private static Orphan OrphanOf(int reqId)
    {
        Orphan orphan;
        if (!orphans.TryGetValue(reqId, out orphan)) return null;
        orphans.Remove(reqId);
        return orphan;
    }

    private static void PruneOrphans(float now)
    {
        if (orphans.Count == 0) return;
        stale.Clear();
        foreach (KeyValuePair<int, Orphan> pair in orphans)
        {
            if (pair.Value.Expires <= now) stale.Add(pair.Key);
        }
        for (int i = 0; i < stale.Count; i++) orphans.Remove(stale[i]);
    }

    private static object[] Decode(string text)
    {
        object[] meta;
        string refused;
        if (ItemMeta.TryDecode(text, out meta, out refused)) return meta == null ? new object[0] : meta;
        CoopLog.Once("ItemGuest.meta", "a synced item arrived with state this build cannot apply: " + refused);
        return null;
    }

    private static CollectibleItem Match(int itemId, int amount, Vector3 position)
    {
        CollectibleItem best = null;
        float bestDistance = MatchRadius;
        string area = GameRefs.AreaId;
        foreach (Collectible item in Collectible.allCollectibles)
        {
            if (item == null || item.gameObject == null || !item.gameObject.activeInHierarchy) continue;
            if (item.gameObject.scene.name != area) continue;
            CollectibleItem piece = item as CollectibleItem;
            if (piece == null || piece.Item == null || piece.Item.ID != itemId || piece.amount != amount) continue;
            float distance = Vector3.Distance(item.transform.position, position);
            if (distance > bestDistance) continue;
            if (Claimed(item.gameObject)) continue;
            best = piece;
            bestDistance = distance;
        }
        return best;
    }

    private static bool Claimed(GameObject go)
    {
        return go != null && mapped.Contains(go);
    }

    private static void Map(string id, GameObject go)
    {
        items[id] = go;
        if (go != null) mapped.Add(go);
    }

    private static string IdOf(Collectible item)
    {
        string strong = ItemWire.StrongId(item);
        if (strong != null) return strong;
        GameObject go = item.gameObject;
        foreach (KeyValuePair<string, GameObject> pair in items)
        {
            if (pair.Value == go) return pair.Key;
        }
        return null;
    }

    private static Collectible Resolve(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        GameObject go;
        if (items.TryGetValue(id, out go) && go != null) return go.GetComponent<Collectible>();
        GameObject found = WorldItems.Find(id);
        return found == null ? null : found.GetComponent<Collectible>();
    }

    private static void RemoveLocal(string id)
    {
        Collectible item = Resolve(id);
        if (item == null) return;
        items.Remove(id);
        spawned.Remove(item.gameObject);
        mapped.Remove(item.gameObject);
        Remove(item.gameObject);
    }

    private static void Forget()
    {
        ForgetAll();
    }

    private static void Remove(GameObject go)
    {
        if (go == null) return;
        Collectible item = go.GetComponent<Collectible>();
        try
        {
            if (item != null) item.Destroy();
            else UnityEngine.Object.Destroy(go);
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemGuest.Remove", e);
        }
    }

    private static Vector3 DropPosition()
    {
        Transform body = GameRefs.LocalPlayer;
        if (body == null) return Vector3.zero;
        return body.position + body.forward * DropForward;
    }

    private static bool InScene(string scene)
    {
        return scene != null && scene == GameRefs.AreaId;
    }
}
