using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;

internal static class BuildHost
{
    private const int MaxOutbox = 2000;
    private const int PerFrame = 4;
    private const int MaxRequests = 4;
    private const float RateWindow = 1f;
    private const float PruneInterval = 1f;
    private const float SumInterval = 10f;
    private const float HelloQuiet = 2f;
    private const float NoticeQuiet = 2f;
    private const string ModPrefix = "obx:";
    private static readonly Dictionary<GameObject, string> ids = new Dictionary<GameObject, string>();
    private static readonly Dictionary<string, FurnitureItem> items = new Dictionary<string, FurnitureItem>();
    private static readonly Dictionary<ulong, Queue<string>> outbox = new Dictionary<ulong, Queue<string>>();
    private static readonly Dictionary<ulong, Requester> requesters = new Dictionary<ulong, Requester>();
    private static readonly List<GameObject> dead = new List<GameObject>();
    private static readonly List<ulong> gone = new List<ulong>();
    private static readonly List<string> list = new List<string>();
    private static bool subscribed;
    private static int counter;
    private static float nextPrune;
    private static float nextSum;
    private static float noticeAt;
    private static string noticeText = "";
    private static string mappedScene;

    private sealed class Requester
    {
        public int LastReq;
        public float WindowStart;
        public int Count;
        public float HelloAt;
    }

    private static bool Active
    {
        get { return FreeRoam.IAmAuthority; }
    }

    public static void Init()
    {
        if (subscribed) return;
        subscribed = true;
        Building.FurniturePlaced += OnPlaced;
        Building.FurnitureRemoved += OnRemoved;
    }

    public static void Handle(ulong from, BuildMsg m)
    {
        try
        {
            if (!Active) return;
            if (m.Kind == "HELLO") Hello(from, m.Scene);
            else if (m.Kind == "REQP") Place(from, m);
            else if (m.Kind == "REQT") Take(from, m);
            else if (m.Kind == "REQM") Move(from, m);
        }
        catch (Exception e)
        {
            CoopLog.Once("BuildHost.Handle", e);
        }
    }

    public static void Tick()
    {
        try
        {
            string scene = HostScene();
            if (scene != mappedScene) StartScene(scene);
            float now = Time.unscaledTime;
            if (now >= nextPrune)
            {
                nextPrune = now + PruneInterval;
                Prune();
            }
            Drain();
            BuildRecon.Tick();
            SendSum(now, scene);
        }
        catch (Exception e)
        {
            CoopLog.Once("BuildHost.Tick", e);
        }
    }

    public static void Reset()
    {
        ids.Clear();
        items.Clear();
        outbox.Clear();
        requesters.Clear();
        BuildRecon.Reset();
        counter = 0;
        mappedScene = null;
    }

    private static string HostScene()
    {
        return Active ? GameRefs.AreaId : null;
    }

    private static void StartScene(string scene)
    {
        mappedScene = scene;
        ids.Clear();
        items.Clear();
        CoopLog.Log.Info("build ids start for scene " + (scene == null ? "none" : scene));
    }

    private static void Hello(ulong from, string scene)
    {
        string host = HostScene();
        if (host == null || scene != host || !Session.Peers.Contains(from)) return;
        Requester requester = RequesterOf(from);
        float now = Time.unscaledTime;
        if (now - requester.HelloAt < HelloQuiet) return;
        requester.HelloAt = now;
        foreach (FurnitureManagerInfo manager in Building.Managers(host))
        {
            List<FurnitureItem> pieces = Pieces(manager.Guid);
            Enqueue(from, BuildWire.Snap(host, manager.Guid, pieces.Count));
            foreach (FurnitureItem item in pieces)
            {
                Enqueue(from, BuildWire.SnapItem(PieceOf(host, manager.Guid, item)));
            }
            Enqueue(from, BuildWire.SnapEnd(host, manager.Guid));
        }
        BuildRecon.OnHello(from);
    }

    private static void Place(ulong from, BuildMsg m)
    {
        if (!Admit(from, m.ReqId)) return;
        string reason = Refusal(from, GameRefs.AreaId, m.Piece.Scene, m.Piece.Manager);
        if (reason == null && IsMod(m.Piece.FurnitureId)) reason = "furniture from another mod is not synced";
        if (reason != null)
        {
            if (CoopDebug.On("build")) CoopDebug.Event("build", "place", "refused the placement from " + from + " req " + m.ReqId + ": " + reason);
            Enqueue(from, BuildWire.Ack(m.ReqId, false, reason));
            return;
        }
        FurnitureItem item = Building.Place(m.Piece.Manager, m.Piece.FurnitureId, m.Piece.Position, m.Piece.Rotation, m.Piece.ChildRotation, m.Piece.Skin, m.Piece.Alt, true);
        if (item == null)
        {
            Enqueue(from, BuildWire.Ack(m.ReqId, false, "the framework refused the placement"));
            return;
        }
        Enqueue(from, BuildWire.Ack(m.ReqId, true, ""));
    }

    private static void Take(ulong from, BuildMsg m)
    {
        if (!Admit(from, m.ReqId)) return;
        FurnitureItem item = null;
        string reason = Refusal(from, GameRefs.AreaId, m.Scene, null);
        if (reason == null)
        {
            item = Find(m.SyncId);
            if (item == null) reason = "not found";
            else if (!Building.Remove(item)) reason = "the piece cannot be taken";
        }
        if (reason != null)
        {
            if (CoopDebug.On("build")) CoopDebug.Event("build", "take", "refused the take from " + from + " req " + m.ReqId + ": " + reason);
            Enqueue(from, BuildWire.Ack(m.ReqId, false, reason));
            return;
        }
        Enqueue(from, BuildWire.Give(m.ReqId, item.Id, item.Skin, item.AltPrefab));
        Enqueue(from, BuildWire.Ack(m.ReqId, true, ""));
        if (CoopDebug.On("build")) CoopDebug.Event("build", "take", "gave piece " + item.Id + " to " + from + " req " + m.ReqId);
    }

    private static void Move(ulong from, BuildMsg m)
    {
        if (!Admit(from, m.ReqId)) return;
        FurnitureItem item = null;
        string reason = Refusal(from, GameRefs.AreaId, m.Scene, null);
        if (reason == null)
        {
            item = Find(m.SyncId);
            if (item == null) reason = "not found";
            else if (!Building.Move(item, m.Position, m.RootRotation)) reason = "the piece cannot be moved";
        }
        if (reason != null)
        {
            Enqueue(from, BuildWire.Ack(m.ReqId, false, reason));
            return;
        }
        BuildGuest.SetChildRotation(item.Object, m.Rotation);
        if (CoopDebug.On("build")) CoopDebug.Event("build", "move", "sent MOVE of piece " + m.SyncId + " in " + m.Scene + " for " + from + " req " + m.ReqId);
        Broadcast(m.Scene, BuildWire.Move(m.Scene, m.SyncId, m.Position, m.Rotation, m.RootRotation));
        Enqueue(from, BuildWire.Ack(m.ReqId, true, ""));
    }

    private static bool Admit(ulong from, int reqId)
    {
        if (!Session.Peers.Contains(from)) return false;
        Requester r = RequesterOf(from);
        if (r.LastReq == reqId) return false;
        r.LastReq = reqId;
        float now = Time.unscaledTime;
        if (now - r.WindowStart >= RateWindow)
        {
            r.WindowStart = now;
            r.Count = 0;
        }
        if (r.Count >= MaxRequests)
        {
            Enqueue(from, BuildWire.Ack(reqId, false, "too many requests"));
            return false;
        }
        r.Count++;
        return true;
    }

    private static Requester RequesterOf(ulong peer)
    {
        Requester r;
        if (!requesters.TryGetValue(peer, out r))
        {
            r = new Requester();
            requesters[peer] = r;
        }
        return r;
    }

    private static string Refusal(ulong from, string scene, string pieceScene, string manager)
    {
        if (scene == null) return "the host is not in a scene";
        if (Session.AreaOf(from) != scene) return "not in this scene";
        if (pieceScene != scene) return "wrong scene";
        if (manager == null) return null;
        return ManagerIn(scene, manager) ? null : "unknown area";
    }

    private static bool ManagerIn(string scene, string manager)
    {
        if (string.IsNullOrEmpty(scene)) return false;
        foreach (FurnitureManagerInfo info in Building.Managers(scene))
        {
            if (info.Guid == manager) return true;
        }
        return false;
    }

    private static FurnitureItem Find(string syncId)
    {
        FurnitureItem item;
        if (syncId == null || !items.TryGetValue(syncId, out item) || item == null || item.Object == null) return null;
        return item;
    }

    private static List<FurnitureItem> Pieces(string manager)
    {
        List<FurnitureItem> result = new List<FurnitureItem>();
        foreach (FurnitureItem item in Building.GetPlacedFurniture(manager))
        {
            if (Replicable(item)) result.Add(item);
        }
        return result;
    }

    private static bool Replicable(FurnitureItem item)
    {
        return item != null && !string.IsNullOrEmpty(item.Id) && item.Object != null && !IsMod(item.Id);
    }

    private static bool IsMod(string id)
    {
        return id != null && id.StartsWith(ModPrefix, StringComparison.Ordinal);
    }

    internal static bool IsModId(string id)
    {
        return IsMod(id);
    }

    internal static string KeyFor(FurnitureItem item)
    {
        return IdFor(item);
    }

    internal static bool Shared(FurnitureItem item)
    {
        return Replicable(item);
    }

    private static Piece PieceOf(string scene, string manager, FurnitureItem item)
    {
        Piece p = new Piece();
        p.Scene = scene;
        p.Manager = manager;
        p.SyncId = IdFor(item);
        p.FurnitureId = item.Id;
        p.Position = item.Position;
        p.Rotation = item.Rotation;
        p.ChildRotation = item.ChildRotation;
        p.Skin = item.Skin;
        p.Alt = item.AltPrefab;
        return p;
    }

    private static string IdFor(FurnitureItem item)
    {
        string id;
        if (ids.TryGetValue(item.Object, out id)) return id;
        id = Next();
        ids[item.Object] = id;
        items[id] = item;
        return id;
    }

    private static string Next()
    {
        return (Session.IsHost ? "h" : "g" + (Session.Me % 100000UL) + "h") + (++counter);
    }

    private static void OnPlaced(FurnitureItem item)
    {
        try
        {
            string scene = HostScene();
            if (scene == null || item == null || SaveData.Busy) return;
            if (IsMod(item.Id))
            {
                if (Session.PeersInArea(scene).Count > 0) Notice("Mod furniture is not shared in co-op yet");
                return;
            }
            if (!Replicable(item) || !ManagerIn(scene, item.AreaId)) return;
            if (ids.ContainsKey(item.Object)) return;
            if (CoopDebug.On("build")) CoopDebug.Event("build", "add", "sent ADD of furniture " + item.Id + " in " + item.AreaId + " scene " + scene);
            Broadcast(scene, BuildWire.Add(PieceOf(scene, item.AreaId, item)));
        }
        catch (Exception e)
        {
            CoopLog.Once("BuildHost.OnPlaced", e);
        }
    }

    private static void Notice(string text)
    {
        CoopLog.Log.Info("coop: " + text);
        if (string.Equals(text, noticeText, StringComparison.Ordinal) && Time.unscaledTime - noticeAt < NoticeQuiet) return;
        noticeText = text;
        noticeAt = Time.unscaledTime;
        if (Notifications.instance != null) Notifications.instance.CreateNotification("Co-op", text);
    }

    private static void OnRemoved(FurnitureItem item)
    {
        try
        {
            string scene = HostScene();
            if (scene == null || item == null) return;
            string id = null;
            bool known = item.Object != null && ids.TryGetValue(item.Object, out id);
            if (known)
            {
                ids.Remove(item.Object);
                items.Remove(id);
                if (CoopDebug.On("build")) CoopDebug.Event("build", "del", "sent DEL of piece " + id + " in " + scene);
                Broadcast(scene, BuildWire.Del(scene, id));
                return;
            }
            if (!Replicable(item) || !ManagerIn(scene, item.AreaId)) return;
            FurniturePlaceable placeable = item.Object.GetComponent<FurniturePlaceable>();
            if (placeable == null || (!placeable.isPreplacedFurniture && !placeable.preplacedFurnitureSecondary)) return;
            BuildRecon.OnRemoved(scene, item);
        }
        catch (Exception e)
        {
            CoopLog.Once("BuildHost.OnRemoved", e);
        }
    }

    private static void SendSum(float now, string scene)
    {
        if (now < nextSum) return;
        nextSum = now + SumInterval;
        if (scene == null) return;
        List<ulong> peers = Session.PeersInArea(scene);
        if (peers.Count == 0) return;
        list.Clear();
        foreach (FurnitureManagerInfo manager in Building.Managers(scene))
        {
            foreach (FurnitureItem item in Building.GetPlacedFurniture(manager.Guid))
            {
                if (Replicable(item)) list.Add(IdFor(item));
            }
        }
        string text = BuildWire.Sum(scene, list.Count, BuildWire.Sum(list));
        foreach (ulong peer in peers) Enqueue(peer, text);
    }

    private static void Broadcast(string scene, string text)
    {
        foreach (ulong peer in Session.PeersInArea(scene))
        {
            Enqueue(peer, text);
        }
    }

    private static void Enqueue(ulong peer, string text)
    {
        Queue<string> queue;
        if (!outbox.TryGetValue(peer, out queue))
        {
            queue = new Queue<string>();
            outbox[peer] = queue;
        }
        if (queue.Count >= MaxOutbox)
        {
            CoopLog.Once("BuildHost.outbox", "a guest's build outbox is full, newer packets are dropped");
            return;
        }
        queue.Enqueue(text);
    }

    private static void Drain()
    {
        if (outbox.Count == 0) return;
        foreach (ulong peer in new List<ulong>(outbox.Keys))
        {
            Queue<string> queue = outbox[peer];
            for (int i = 0; i < PerFrame && queue.Count > 0; i++)
            {
                if (!Session.Send(peer, BuildWire.Channel, queue.Peek(), true)) break;
                queue.Dequeue();
            }
            if (queue.Count == 0) outbox.Remove(peer);
        }
    }

    private static void Prune()
    {
        dead.Clear();
        foreach (GameObject go in ids.Keys)
        {
            if (go == null) dead.Add(go);
        }
        foreach (GameObject go in dead)
        {
            string id = ids[go];
            ids.Remove(go);
            items.Remove(id);
        }
        gone.Clear();
        List<ulong> peers = Session.Peers;
        foreach (ulong peer in outbox.Keys)
        {
            if (!peers.Contains(peer)) gone.Add(peer);
        }
        foreach (ulong peer in requesters.Keys)
        {
            if (!peers.Contains(peer)) gone.Add(peer);
        }
        foreach (ulong peer in gone)
        {
            outbox.Remove(peer);
            requesters.Remove(peer);
        }
    }
}
