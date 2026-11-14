using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static partial class BuildGuest
{
    private const float HelloInterval = 5f;
    private const float RequestTimeout = 10f;
    private const float PruneInterval = 5f;
    private const float NoticeQuiet = 2f;
    private const float ResyncQuiet = 2.5f;
    private const float PairDistance = 2f;
    private const int MaxLate = 8;
    private const string ModPrefix = "obx:";
    private static readonly Dictionary<string, FurnitureItem> items = new Dictionary<string, FurnitureItem>();
    private static readonly Dictionary<GameObject, string> ids = new Dictionary<GameObject, string>();
    private static readonly Dictionary<string, string> missing = new Dictionary<string, string>();
    private static readonly HashSet<string> blind = new HashSet<string>();
    private static readonly HashSet<string> replicated = new HashSet<string>();
    private static readonly List<Pending> late = new List<Pending>();
    private static readonly List<Piece> snap = new List<Piece>();
    private static readonly List<FurnitureItem> loose = new List<FurnitureItem>();
    private static readonly List<string> drop = new List<string>();
    private static readonly HashSet<string> keep = new HashSet<string>();
    private static readonly List<string> sum = new List<string>();
    private static string snapScene;
    private static int snapHandle;
    private static string snapManager;
    private static int snapCount;
    private static bool snapping;
    private static bool wasActive;
    private static float nextHello;
    private static float nextPrune;
    private static float noticeAt;
    private static string noticeText = "";
    private static int lastReqId;
    private static Pending pending;

    public static bool Active
    {
        get
        {
            return snapScene != null && Session.IsInLobby && !FreeRoam.IAmAuthority && FreeRoam.Authority != 0
                && GuestSession.InHostWorld
                && snapScene == GameRefs.AreaId && Session.AreaOf(FreeRoam.Authority) == snapScene;
        }
    }

    public static bool ReconActive
    {
        get
        {
            if (!Session.IsInLobby || FreeRoam.IAmAuthority || !GuestSession.InHostWorld) return false;
            string area = GameRefs.AreaId;
            if (area == null || FreeRoam.Authority == 0UL) return false;
            return Session.AreaOf(FreeRoam.Authority) == area;
        }
    }

    public static void Init()
    {
    }

    public static bool IsReplicated(string managerGuid)
    {
        return managerGuid != null && replicated.Contains(managerGuid);
    }

    public static string IdOf(GameObject go)
    {
        string id;
        return go != null && ids.TryGetValue(go, out id) ? id : null;
    }

    public static bool Available(BuildingSystem.FurnitureInfo info)
    {
        return info != null && BuildingSystem.instance != null && BuildingSystem.instance.availableFurnitures.Contains(info);
    }

    public static bool IsModFurniture(Furniture furniture)
    {
        return furniture != null && furniture.Id != null && furniture.Id.StartsWith(ModPrefix, StringComparison.Ordinal);
    }

    public static void OnSceneLoaded()
    {
        if (snapScene != null && snapScene == GameRefs.AreaId && snapHandle == SceneManager.GetActiveScene().handle) return;
        Reset();
    }

    public static void Reset()
    {
        ForgetAll();
        replicated.Clear();
        late.Clear();
        snap.Clear();
        snapping = false;
        CancelMove();
        pending = null;
        snapScene = null;
        snapManager = null;
        snapCount = 0;
        nextHello = 0f;
        GuestReconReset();
    }

    public static void Tick()
    {
        try
        {
            bool recon = ReconActive;
            if (wasActive && !recon) Reset();
            wasActive = recon;
            float now = Time.unscaledTime;
            if (recon)
            {
                GuestReconTick(now);
                SendHello(now);
                if (now >= nextPrune)
                {
                    nextPrune = now + PruneInterval;
                    PruneItems();
                }
                CheckMove();
            }
            if (pending != null && now - pending.SentAt > RequestTimeout)
            {
                Pending done = pending;
                pending = null;
                if (done.Move) DropPhantom(done);
                else if (!done.Take) KeepLate(done);
                Warn();
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("BuildGuest.Tick", e);
        }
    }

    public static void Handle(ulong from, BuildMsg m)
    {
        try
        {
            if (from != FreeRoam.Authority || FreeRoam.IAmAuthority || !Session.IsInLobby) return;
            if (GuestHandle(m)) return;
            switch (m.Kind)
            {
                case "SNAP":
                    OnSnap(m);
                    break;
                case "SNAPI":
                    OnSnapPiece(m.Piece);
                    break;
                case "ADD":
                    Attach(m.Piece);
                    break;
                case "SNAPEND":
                    OnSnapEnd(m);
                    break;
                case "DEL":
                    OnDel(m);
                    break;
                case "MOVE":
                    OnMove(m);
                    break;
                case "SUM":
                    OnSum(m);
                    break;
                case "ACK":
                    OnAck(m);
                    break;
                case "GIVE":
                    OnGive(m.ReqId, m.Piece);
                    break;
            }
            if (CoopDebug.On("build")) CoopDebug.Event("build", "apply", "guest applied " + m.Kind + " in " + m.Scene + " from " + from);
        }
        catch (Exception e)
        {
            CoopLog.Once("BuildGuest.Handle", e);
        }
    }

    public static void RequestPlace(Piece piece, BuildingSystem.FurnitureInfo info)
    {
        if (pending != null || piece == null)
        {
            Warn();
            return;
        }
        int id = ++lastReqId;
        if (!Session.Send(FreeRoam.Authority, BuildWire.Channel, BuildWire.ReqPlace(id, piece), true))
        {
            Warn();
            return;
        }
        pending = new Pending();
        pending.ReqId = id;
        pending.Info = info;
        pending.SentAt = Time.unscaledTime;
    }

    public static void RequestTake(string syncId)
    {
        if (pending != null)
        {
            Warn();
            return;
        }
        int id = ++lastReqId;
        if (!Session.Send(FreeRoam.Authority, BuildWire.Channel, BuildWire.ReqTake(id, GameRefs.AreaId, syncId), true))
        {
            Warn();
            return;
        }
        pending = new Pending();
        pending.ReqId = id;
        pending.Take = true;
        pending.SentAt = Time.unscaledTime;
    }

    internal static void Warn()
    {
        if (BuildingSystemMenu.instance != null) BuildingSystemMenu.instance.WarningTextAnimation();
    }

    internal static void Notice(string text)
    {
        CoopLog.Log.Info("coop: " + text);
        if (string.Equals(text, noticeText, StringComparison.Ordinal) && Time.unscaledTime - noticeAt < NoticeQuiet) return;
        noticeText = text;
        noticeAt = Time.unscaledTime;
        if (Notifications.instance != null) Notifications.instance.CreateNotification("Co-op", text);
    }

    private static void SendHello(float now)
    {
        string area = GameRefs.AreaId;
        if (snapScene != null || area == null || now < nextHello || !ReconActive) return;
        nextHello = now + HelloInterval;
        Session.Send(FreeRoam.Authority, BuildWire.Channel, BuildWire.Hello(area), true);
    }

    private static void Resync()
    {
        string area = GameRefs.AreaId;
        if (area == null) return;
        float now = Time.unscaledTime;
        if (now < nextHello) return;
        nextHello = now + ResyncQuiet;
        Session.Send(FreeRoam.Authority, BuildWire.Channel, BuildWire.Hello(area), true);
        CoopLog.Log.Info("build resync asked for " + area);
    }

    private static void OnSnap(BuildMsg m)
    {
        if (!InScene(m.Scene)) return;
        if (FindManager(m.Manager) == null)
        {
            blind.Add(m.Manager);
            return;
        }
        blind.Remove(m.Manager);
        snapping = true;
        snapManager = m.Manager;
        snapCount = m.Count;
        snap.Clear();
    }

    private static void OnSnapPiece(Piece piece)
    {
        if (piece == null || !InScene(piece.Scene)) return;
        if (snapping && piece.Manager == snapManager)
        {
            if (snap.Count < snapCount) snap.Add(piece);
            return;
        }
        Attach(piece);
    }

    private static void OnSnapEnd(BuildMsg m)
    {
        if (!InScene(m.Scene)) return;
        if (FindManager(m.Manager) == null)
        {
            blind.Add(m.Manager);
            return;
        }
        blind.Remove(m.Manager);
        if (snapping && m.Manager == snapManager)
        {
            snapping = false;
            if (snap.Count != snapCount)
            {
                CoopLog.Once("BuildGuest.snap", "a snapshot was cut short");
                Resync();
                return;
            }
            Reconcile(m.Manager, snap);
            snap.Clear();
        }
        replicated.Add(m.Manager);
        snapScene = m.Scene;
        snapHandle = SceneManager.GetActiveScene().handle;
    }

    private static void Reconcile(string manager, List<Piece> snapshot)
    {
        keep.Clear();
        foreach (Piece piece in snapshot) keep.Add(piece.SyncId);
        ForgetManager(manager, keep);
        List<Piece> fresh = new List<Piece>();
        foreach (Piece piece in snapshot)
        {
            FurnitureItem item;
            if (Bound(piece, out item))
            {
                ApplyPose(item, piece);
                continue;
            }
            Unbind(piece.SyncId);
            fresh.Add(piece);
        }
        if (fresh.Count == 0) return;
        Loose(manager);
        int adopted = 0;
        int placed = 0;
        foreach (Piece piece in fresh)
        {
            if (missing.ContainsKey(piece.SyncId)) continue;
            FurnitureItem match = Nearest(piece);
            if (match != null)
            {
                loose.Remove(match);
                ApplyPose(match, piece);
                Bind(piece.SyncId, piece.Manager, match);
                adopted++;
                continue;
            }
            FurnitureItem made = Place(manager, piece);
            if (made == null) continue;
            Bind(piece.SyncId, piece.Manager, made);
            placed++;
        }
        CoopLog.Log.Info("build snapshot " + manager + ": " + adopted + " bound, " + placed + " placed, " + loose.Count + " local left");
    }

    private static void Attach(Piece piece)
    {
        if (piece == null || !InScene(piece.Scene) || FindManager(piece.Manager) == null) return;
        if (missing.ContainsKey(piece.SyncId)) return;
        FurnitureItem item;
        if (Bound(piece, out item))
        {
            ApplyPose(item, piece);
            return;
        }
        Unbind(piece.SyncId);
        Loose(piece.Manager);
        FurnitureItem match = Nearest(piece);
        if (match != null)
        {
            ApplyPose(match, piece);
            Bind(piece.SyncId, piece.Manager, match);
            return;
        }
        FurnitureItem made = Place(piece.Manager, piece);
        if (made != null) Bind(piece.SyncId, piece.Manager, made);
    }

    private static void OnDel(BuildMsg m)
    {
        if (!ReconInHostWorld(m.Scene)) return;
        GuestDel(m);
    }

    private static void OnMove(BuildMsg m)
    {
        if (!ReconInHostWorld(m.Scene)) return;
        if (GuestMove(m)) return;
        FurnitureItem item;
        if (!items.TryGetValue(m.SyncId, out item) || item == null || item.Object == null) return;
        if (!Building.Move(item, m.Position, m.RootRotation))
        {
            CoopLog.Once("BuildGuest.move", "the framework refused a move of " + m.SyncId);
            return;
        }
        SetChildRotation(item.Object, m.Rotation);
    }

    private static void OnSum(BuildMsg m)
    {
        if (!InScene(m.Scene)) return;
        if (blind.Count > 0)
        {
            if (blind.RemoveWhere(BlindFound) > 0) Resync();
            if (blind.Count > 0)
            {
                CoopLog.Once("BuildGuest.sum", "a sum check was skipped, a host manager is not in this scene");
                return;
            }
        }
        int count;
        string hash;
        SumOf(m.Scene, out count, out hash);
        if (count == m.Count && string.Equals(hash, m.Sum, StringComparison.Ordinal)) return;
        CoopLog.Log.Info("build sum mismatch in " + m.Scene + ": host " + m.Count + "/" + m.Sum + ", here " + count + "/" + hash);
        Resync();
    }

    private static bool BlindFound(string guid)
    {
        return FindManager(guid) != null;
    }

    private static void OnAck(BuildMsg m)
    {
        if (pending != null && pending.ReqId == m.ReqId)
        {
            Pending done = pending;
            pending = null;
            Settle(done, m);
            return;
        }
        for (int i = 0; i < late.Count; i++)
        {
            if (late[i].ReqId != m.ReqId) continue;
            Pending old = late[i];
            late.RemoveAt(i);
            Settle(old, m);
            return;
        }
    }

    private static void KeepLate(Pending request)
    {
        late.Add(request);
        while (late.Count > MaxLate) late.RemoveAt(0);
    }

    private static void Settle(Pending request, BuildMsg m)
    {
        if (request.Move)
        {
            if (!m.Ok)
            {
                CoopLog.Log.Debug("build.ack", "move refused: " + m.Reason);
                Notice("The host refused: " + m.Reason);
            }
            if (!m.Ok) Warn();
            DropPhantom(request);
            return;
        }
        if (!m.Ok)
        {
            CoopLog.Log.Debug("build.ack", "request refused: " + m.Reason);
            Notice("The host refused: " + m.Reason);
            Warn();
            return;
        }
        if (!request.Take && Available(request.Info))
        {
            BuildingSystem.instance.RemoveFurniture(request.Info, 1, true);
        }
    }

    private static void OnGive(int reqId, Piece piece)
    {
        if (pending != null && pending.Take && pending.ReqId == reqId) pending = null;
        if (piece == null || BuildingSystem.instance == null) return;
        Furniture furniture = Resolve(piece.FurnitureId);
        if (furniture == null) return;
        BuildingSystem.FurnitureInfo.Meta meta = new BuildingSystem.FurnitureInfo.Meta();
        meta.currentSkin = piece.Skin;
        meta.currentAlternativePrefab = piece.Alt;
        BuildingSystem.instance.AddFurniture(furniture, 1, meta);
        if (BuildingSystemMenu.instance != null) BuildingSystemMenu.instance.UpdateMenu();
    }

    private static Furniture Resolve(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (id.StartsWith(ModPrefix, StringComparison.Ordinal))
        {
            Notice("Furniture from another mod is not in co-op yet.");
            return null;
        }
        Furniture furniture = SavableScriptableObject.LoadFromPath(id) as Furniture;
        if (furniture == null) CoopLog.Once("BuildGuest.furniture", "furniture not found: " + id);
        return furniture;
    }

    private static FurnitureItem Place(string manager, Piece piece)
    {
        if (SaveData.Busy)
        {
            CoopLog.Log.Debug("build.busy", "a piece waited for the save gate");
            Resync();
            return null;
        }
        FurnitureItem made = Building.Place(manager, piece.FurnitureId, piece.Position, piece.Rotation, piece.ChildRotation, piece.Skin, piece.Alt, true);
        if (made != null) return made;
        missing[piece.SyncId] = manager;
        CoopLog.Once("BuildGuest.place", "the framework did not place " + piece.FurnitureId);
        Notice("A piece from the host could not be shown.");
        return null;
    }

    private static void ApplyPose(FurnitureItem item, Piece piece)
    {
        if (item == null || item.Object == null) return;
        if (!Building.Move(item, piece.Position, piece.Rotation))
        {
            CoopLog.Once("BuildGuest.pose", "the framework refused a move of " + piece.FurnitureId);
            return;
        }
        SetChildRotation(item.Object, piece.ChildRotation);
    }

    private static bool Bound(Piece piece, out FurnitureItem item)
    {
        item = null;
        if (piece == null) return false;
        if (!items.TryGetValue(piece.SyncId, out item)) return false;
        if (item == null || item.Object == null || item.Id != piece.FurnitureId)
        {
            item = null;
            return false;
        }
        return true;
    }

    private static void Bind(string syncId, string manager, FurnitureItem item)
    {
        if (item == null || item.Object == null) return;
        ids[item.Object] = syncId;
        items[syncId] = item;
    }

    private static void Forget(string syncId)
    {
        ForgetGuid(syncId);
        missing.Remove(syncId);
        FurnitureItem item;
        if (!items.TryGetValue(syncId, out item)) return;
        items.Remove(syncId);
        if (item != null && item.Object != null) ids.Remove(item.Object);
    }

    private static void Unbind(string syncId)
    {
        FurnitureItem item;
        if (!items.TryGetValue(syncId, out item)) return;
        items.Remove(syncId);
        if (item != null && item.Object != null) ids.Remove(item.Object);
    }

    private static void ForgetAll()
    {
        items.Clear();
        ids.Clear();
        missing.Clear();
        blind.Clear();
    }

    private static void ForgetManager(string manager, HashSet<string> keepIds)
    {
        drop.Clear();
        foreach (KeyValuePair<string, FurnitureItem> pair in items)
        {
            if (pair.Value != null && pair.Value.AreaId == manager && !keepIds.Contains(pair.Key)) drop.Add(pair.Key);
        }
        foreach (string id in drop) Forget(id);
        drop.Clear();
        foreach (KeyValuePair<string, string> pair in missing)
        {
            if (pair.Value == manager && !keepIds.Contains(pair.Key)) drop.Add(pair.Key);
        }
        foreach (string id in drop) missing.Remove(id);
    }

    private static void PruneItems()
    {
        drop.Clear();
        foreach (KeyValuePair<string, FurnitureItem> pair in items)
        {
            if (pair.Value == null || pair.Value.Object == null) drop.Add(pair.Key);
        }
        foreach (string id in drop) Forget(id);
    }

    private static void Loose(string manager)
    {
        loose.Clear();
        foreach (FurnitureItem item in Building.GetPlacedFurniture(manager))
        {
            if (item != null && item.Object != null && !ids.ContainsKey(item.Object)) loose.Add(item);
        }
    }

    private static FurnitureItem Nearest(Piece piece)
    {
        FurnitureItem best = null;
        float bestDistance = 0f;
        float limit = PairDistance * PairDistance;
        foreach (FurnitureItem item in loose)
        {
            if (item.Id != piece.FurnitureId) continue;
            float distance = (item.Position - piece.Position).sqrMagnitude;
            if (distance > limit) continue;
            if (best != null && distance >= bestDistance) continue;
            best = item;
            bestDistance = distance;
        }
        return best;
    }

    private static void SumOf(string scene, out int count, out string hash)
    {
        sum.Clear();
        foreach (KeyValuePair<string, FurnitureItem> pair in items)
        {
            FurnitureItem item = pair.Value;
            if (item == null || item.Object == null) continue;
            if (!ManagerInScene(scene, item.AreaId)) continue;
            sum.Add(pair.Key);
        }
        foreach (KeyValuePair<string, string> pair in missing)
        {
            if (ManagerInScene(scene, pair.Value)) sum.Add(pair.Key);
        }
        count = sum.Count;
        hash = BuildWire.Sum(sum);
    }

    private static FurnitureManager FindManager(string guid)
    {
        string scene = GameRefs.AreaId;
        if (scene == null || BuildingSystem.instance == null) return null;
        foreach (FurnitureManager manager in BuildingSystem.instance.furnitureManagers)
        {
            if (manager != null && manager.GUID == guid && manager.gameObject.scene.name == scene) return manager;
        }
        CoopLog.Once("BuildGuest.manager", "no local manager " + guid + " in " + scene);
        return null;
    }

    private static bool ManagerInScene(string scene, string guid)
    {
        if (guid == null) return false;
        foreach (FurnitureManagerInfo info in Building.Managers(scene))
        {
            if (info.Guid == guid) return true;
        }
        return false;
    }

    private static bool InScene(string scene)
    {
        return scene != null && scene == GameRefs.AreaId;
    }

    private sealed class Pending
    {
        public int ReqId;
        public bool Take;
        public bool Move;
        public bool Added;
        public BuildingSystem.FurnitureInfo Info;
        public float SentAt;
    }
}
