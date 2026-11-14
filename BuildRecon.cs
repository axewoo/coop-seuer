using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using ObenseuerCore;
using UnityEngine;

internal static class BuildRecon
{
    private const float ScanInterval = 1f;
    private const float IndexInterval = 5f;
    private const float ManifestInterval = 20f;
    private const float LockInterval = 20f;
    private const float TombstoneSeconds = 300f;
    private const int MaxTombstones = 2000;
    private const int MaxChangesPerScan = 64;
    private const int MaxPacket = 850;
    private const string ModPrefix = "obx:";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly FieldInfo ExamineField = AccessTools.Field(typeof(TenementApartmentPrefabs), "currentExamineCounter");
    private static readonly FieldInfo ExamineMaxField = AccessTools.Field(typeof(TenementApartmentPrefabs), "examineCounterMax");

    private sealed class Rec
    {
        public string Key;
        public string Manager;
        public string FurnitureId;
        public GameObject Go;
        public Vector3 Position;
        public Quaternion Rotation;
        public Quaternion ChildRotation;
        public int Skin;
        public int Alt;
        public int Sig;
        public bool Preplaced;
    }

    private sealed class Tomb
    {
        public string Scene;
        public float At;
    }

    private static readonly Dictionary<string, Rec> sent = new Dictionary<string, Rec>();
    private static readonly Dictionary<string, Tomb> tombstones = new Dictionary<string, Tomb>();
    private static readonly HashSet<string> live = new HashSet<string>();
    private static readonly HashSet<string> skipped = new HashSet<string>();
    private static readonly HashSet<ulong> present = new HashSet<ulong>();
    private static readonly HashSet<ulong> previous = new HashSet<ulong>();
    private static readonly List<Rec> indexed = new List<Rec>();
    private static readonly List<Rec> dropped = new List<Rec>();
    private static readonly List<string> manifestKeys = new List<string>();
    private static readonly List<string> manifestPackets = new List<string>();
    private static readonly List<ulong> targets = new List<ulong>();
    private static readonly List<string> stale = new List<string>();
    private static readonly StringBuilder body = new StringBuilder(900);
    private static string scene;
    private static bool wasAuthority;
    private static bool baseline;
    private static float nextScan;
    private static float nextIndex;
    private static float nextManifest;
    private static float nextLock;
    private static int seq;

    public static string GuidKey(GameObject go)
    {
        if (go == null) return null;
        FurniturePlaceable placeable = go.GetComponent<FurniturePlaceable>();
        if (placeable == null || string.IsNullOrEmpty(placeable.GUID)) return null;
        return "g" + placeable.GUID;
    }

    public static void Reset()
    {
        sent.Clear();
        tombstones.Clear();
        live.Clear();
        skipped.Clear();
        present.Clear();
        previous.Clear();
        indexed.Clear();
        manifestKeys.Clear();
        manifestPackets.Clear();
        scene = null;
        wasAuthority = false;
        baseline = false;
        nextScan = nextIndex = nextManifest = nextLock = 0f;
        seq = 0;
    }

    public static void Tick()
    {
        try
        {
            if (!Session.IsInLobby) return;
            bool authority = FreeRoam.IAmAuthority;
            if (wasAuthority != authority)
            {
                wasAuthority = authority;
                sent.Clear();
                indexed.Clear();
                present.Clear();
                previous.Clear();
                baseline = true;
                nextScan = nextIndex = nextManifest = nextLock = 0f;
            }
            if (!authority) return;
            string area = GameRefs.AreaId;
            if (area == null) return;
            if (scene != area) StartScene(area);
            float now = Time.unscaledTime;
            if (Session.PeersInArea(area).Count == 0)
            {
                present.Clear();
                return;
            }
            if (now >= nextIndex)
            {
                nextIndex = now + IndexInterval;
                Reindex();
            }
            if (Arrivals())
            {
                nextManifest = 0f;
                nextLock = now + LockInterval;
                SendTombstones(0UL);
                SendLocks(0UL);
            }
            if (now >= nextScan)
            {
                nextScan = now + ScanInterval;
                Scan();
                PruneTombstones(now);
            }
            if (now >= nextManifest)
            {
                nextManifest = now + ManifestInterval;
                SendManifest(0UL);
            }
            if (now >= nextLock)
            {
                nextLock = now + LockInterval;
                SendLocks(0UL);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("BuildRecon.Tick", e);
        }
    }

    public static void OnHello(ulong peer)
    {
        try
        {
            if (peer == 0UL || GameRefs.AreaId == null || !FreeRoam.IAmAuthority) return;
            if (scene != GameRefs.AreaId) StartScene(GameRefs.AreaId);
            Reindex();
            SendManifest(peer);
            SendTombstones(peer);
            SendLocks(peer);
        }
        catch (Exception e)
        {
            CoopLog.Once("BuildRecon.OnHello", e);
        }
    }

    public static void OnRemoved(string area, FurnitureItem item)
    {
        try
        {
            if (item == null || item.Object == null) return;
            FurniturePlaceable placeable = item.Object.GetComponent<FurniturePlaceable>();
            if (placeable == null || (!placeable.isPreplacedFurniture && !placeable.preplacedFurnitureSecondary)) return;
            string key = GuidKey(item.Object);
            if (key == null) return;
            if (scene != area) StartScene(area);
            Tombstone(key, area, Time.unscaledTime);
            SendDel(area, key, 0UL);
        }
        catch (Exception e)
        {
            CoopLog.Once("BuildRecon.OnRemoved", e);
        }
    }

    private static void StartScene(string area)
    {
        scene = area;
        sent.Clear();
        indexed.Clear();
        manifestKeys.Clear();
        present.Clear();
        previous.Clear();
        seq = 0;
        baseline = true;
        nextScan = nextIndex = nextManifest = nextLock = 0f;
    }

    private static bool Arrivals()
    {
        previous.Clear();
        foreach (ulong peer in present) previous.Add(peer);
        present.Clear();
        bool arrived = false;
        foreach (PresenceRecord record in Session.Remotes())
        {
            if (record.Area != scene || !Session.Peers.Contains(record.Peer)) continue;
            present.Add(record.Peer);
            if (!previous.Contains(record.Peer)) arrived = true;
        }
        return arrived;
    }

    private static void Reindex()
    {
        indexed.Clear();
        if (BuildingSystem.instance == null) return;
        foreach (FurnitureManager manager in BuildingSystem.instance.furnitureManagers)
        {
            if (manager == null || manager.gameObject == null || manager.gameObject.scene.name != scene) continue;
            if (string.IsNullOrEmpty(manager.GUID)) continue;
            foreach (FurnitureItem item in Building.GetPlacedFurniture(manager.GUID))
            {
                if (!BuildHost.Shared(item) || item.Object == null) continue;
                Rec rec = new Rec();
                rec.Key = BuildHost.KeyFor(item);
                rec.Manager = manager.GUID;
                rec.Go = item.Object;
                indexed.Add(rec);
            }
            Preplaced(manager);
        }
    }

    private static void Preplaced(FurnitureManager manager)
    {
        foreach (BuildingSystem.FurnitureInfo info in manager.AllFurnitures)
        {
            if (info == null || info.gameObject == null) continue;
            FurniturePlaceable placeable = info.gameObject.GetComponent<FurniturePlaceable>();
            if (placeable == null || placeable.furniture == null) continue;
            if (!placeable.isPreplacedFurniture && !placeable.preplacedFurnitureSecondary) continue;
            if (BuildHost.IsModId(placeable.furniture.Id)) continue;
            string key = GuidKey(placeable.gameObject);
            if (key == null)
            {
                if (skipped.Add(manager.GUID + placeable.gameObject.name)) CoopLog.Log.Info("coop: a preplaced piece has no GUID and is not shared: " + placeable.gameObject.name);
                continue;
            }
            Rec rec = new Rec();
            rec.Key = key;
            rec.Manager = manager.GUID;
            rec.Go = placeable.gameObject;
            rec.Preplaced = true;
            indexed.Add(rec);
        }
    }

    private static void Scan()
    {
        live.Clear();
        int changes = 0;
        for (int i = 0; i < indexed.Count; i++)
        {
            Rec rec = indexed[i];
            if (rec.Go == null) continue;
            FurniturePlaceable placeable = rec.Go.GetComponent<FurniturePlaceable>();
            if (placeable == null) continue;
            live.Add(rec.Key);
            if (placeable.furniture == null || placeable.meta == null) continue;
            string id = placeable.furniture.Id;
            if (string.IsNullOrEmpty(id) || BuildHost.IsModId(id)) continue;
            rec.FurnitureId = id;
            rec.Position = rec.Go.transform.position;
            rec.Rotation = rec.Go.transform.localRotation;
            rec.ChildRotation = rec.Go.transform.childCount > 0 ? rec.Go.transform.GetChild(0).localRotation : Quaternion.identity;
            rec.Skin = placeable.meta.currentSkin;
            rec.Alt = placeable.meta.currentAlternativePrefab;
            rec.Sig = Sig(rec);
            Rec old;
            if (sent.TryGetValue(rec.Key, out old) && old.Sig == rec.Sig) continue;
            if (old == null)
            {
                if (baseline)
                {
                    sent[rec.Key] = Copy(rec);
                    continue;
                }
                if (changes >= MaxChangesPerScan) continue;
                sent[rec.Key] = Copy(rec);
                if (!rec.Preplaced)
                {
                    SendAdd(rec);
                    changes++;
                }
                continue;
            }
            if (changes >= MaxChangesPerScan) continue;
            bool sameLook = old.FurnitureId == rec.FurnitureId && old.Skin == rec.Skin && old.Alt == rec.Alt;
            if (sameLook) SendMove(rec);
            else SendAdd(rec);
            sent[rec.Key] = Copy(rec);
            changes++;
        }
        baseline = false;
        if (tombstones.Count > 0)
        {
            foreach (string key in live) tombstones.Remove(key);
        }
        dropped.Clear();
        foreach (KeyValuePair<string, Rec> pair in sent)
        {
            if (!live.Contains(pair.Key)) dropped.Add(pair.Value);
        }
        for (int i = 0; i < dropped.Count; i++)
        {
            Rec rec = dropped[i];
            sent.Remove(rec.Key);
            if (!CanReportGone(rec)) continue;
            Tombstone(rec.Key, scene, Time.unscaledTime);
            SendDel(scene, rec.Key, 0UL);
            CoopLog.Log.Info("coop: build piece left the authority " + rec.Key + " in " + scene);
        }
    }

    private static bool CanReportGone(Rec rec)
    {
        if (SaveData.Busy || SaveController.Loading) return false;
        if (scene == null || BuildingSystem.instance == null) return false;
        foreach (FurnitureManager manager in BuildingSystem.instance.furnitureManagers)
        {
            if (manager == null || manager.gameObject == null) continue;
            if (manager.GUID == rec.Manager && manager.gameObject.scene.name == scene) return true;
        }
        return false;
    }

    private static Rec Copy(Rec rec)
    {
        Rec copy = new Rec();
        copy.Key = rec.Key;
        copy.Manager = rec.Manager;
        copy.FurnitureId = rec.FurnitureId;
        copy.Go = rec.Go;
        copy.Position = rec.Position;
        copy.Rotation = rec.Rotation;
        copy.ChildRotation = rec.ChildRotation;
        copy.Skin = rec.Skin;
        copy.Alt = rec.Alt;
        copy.Sig = rec.Sig;
        copy.Preplaced = rec.Preplaced;
        return copy;
    }

    private static int Sig(Rec rec)
    {
        int h = unchecked((int)2166136261);
        h = Mix(h, rec.FurnitureId == null ? 0 : rec.FurnitureId.GetHashCode());
        h = Mix(h, rec.Skin);
        h = Mix(h, rec.Alt);
        h = Mix(h, Mathf.RoundToInt(rec.Position.x * 100f));
        h = Mix(h, Mathf.RoundToInt(rec.Position.y * 100f));
        h = Mix(h, Mathf.RoundToInt(rec.Position.z * 100f));
        h = Mix(h, Mathf.RoundToInt(rec.Rotation.x * 1000f));
        h = Mix(h, Mathf.RoundToInt(rec.Rotation.y * 1000f));
        h = Mix(h, Mathf.RoundToInt(rec.Rotation.z * 1000f));
        h = Mix(h, Mathf.RoundToInt(rec.Rotation.w * 1000f));
        h = Mix(h, Mathf.RoundToInt(rec.ChildRotation.x * 1000f));
        h = Mix(h, Mathf.RoundToInt(rec.ChildRotation.y * 1000f));
        h = Mix(h, Mathf.RoundToInt(rec.ChildRotation.z * 1000f));
        h = Mix(h, Mathf.RoundToInt(rec.ChildRotation.w * 1000f));
        return h;
    }

    private static int Mix(int h, int value)
    {
        return unchecked((h ^ value) * 16777619);
    }

    private static void SendAdd(Rec rec)
    {
        Piece piece = new Piece();
        piece.Scene = scene;
        piece.Manager = rec.Manager;
        piece.SyncId = rec.Key;
        piece.FurnitureId = rec.FurnitureId;
        piece.Position = rec.Position;
        piece.Rotation = rec.Rotation;
        piece.ChildRotation = rec.ChildRotation;
        piece.Skin = rec.Skin;
        piece.Alt = rec.Alt;
        Send(scene, BuildWire.Add(piece), 0UL);
    }

    private static void SendMove(Rec rec)
    {
        Send(scene, BuildWire.Move(scene, rec.Key, rec.Position, rec.ChildRotation, rec.Rotation), 0UL);
    }

    private static void SendDel(string area, string key, ulong only)
    {
        if (area == null || key == null) return;
        Send(area, BuildWire.Del(area, key), only);
    }

    private static void Tombstone(string key, string area, float now)
    {
        if (key == null) return;
        Tomb tomb;
        if (!tombstones.TryGetValue(key, out tomb))
        {
            tomb = new Tomb();
            tombstones[key] = tomb;
        }
        tomb.Scene = area;
        tomb.At = now;
    }

    private static void PruneTombstones(float now)
    {
        if (tombstones.Count == 0) return;
        stale.Clear();
        foreach (KeyValuePair<string, Tomb> pair in tombstones)
        {
            if (now - pair.Value.At > TombstoneSeconds) stale.Add(pair.Key);
        }
        for (int i = 0; i < stale.Count; i++) tombstones.Remove(stale[i]);
        while (tombstones.Count > MaxTombstones)
        {
            string oldest = null;
            float at = float.MaxValue;
            foreach (KeyValuePair<string, Tomb> pair in tombstones)
            {
                if (pair.Value.At < at)
                {
                    at = pair.Value.At;
                    oldest = pair.Key;
                }
            }
            if (oldest == null) break;
            tombstones.Remove(oldest);
        }
    }

    private static void SendTombstones(ulong only)
    {
        if (tombstones.Count == 0 || scene == null) return;
        float now = Time.unscaledTime;
        foreach (KeyValuePair<string, Tomb> pair in tombstones)
        {
            if (pair.Value.Scene != scene || now - pair.Value.At > TombstoneSeconds) continue;
            SendDel(scene, pair.Key, only);
        }
    }

    private static void SendManifest(ulong only)
    {
        if (scene == null) return;
        Reindex();
        if (!Targets(only)) return;
        manifestKeys.Clear();
        for (int i = 0; i < indexed.Count; i++)
        {
            if (indexed[i].Go != null) manifestKeys.Add(indexed[i].Key);
        }
        seq++;
        manifestPackets.Clear();
        body.Length = 0;
        bool any = false;
        int bytes = HeadBytes();
        for (int i = 0; i < manifestKeys.Count; i++)
        {
            string key = manifestKeys[i];
            int size = Encoding.UTF8.GetByteCount(key) + 1;
            if (any && bytes + size > MaxPacket)
            {
                manifestPackets.Add(body.ToString());
                body.Length = 0;
                any = false;
                bytes = HeadBytes();
            }
            if (any) body.Append('|');
            body.Append(key);
            bytes += size;
            any = true;
        }
        manifestPackets.Add(body.ToString());
        for (int i = 0; i < manifestPackets.Count; i++)
        {
            bool last = i == manifestPackets.Count - 1;
            string head = "BM|" + scene + "|" + seq.ToString(Inv) + "|" + i.ToString(Inv) + "|" + (last ? "1" : "0");
            string text = manifestPackets[i].Length > 0 ? head + "|" + manifestPackets[i] : head;
            for (int t = 0; t < targets.Count; t++) Session.Send(targets[t], BuildWire.Channel, text, true);
        }
    }

    private static int HeadBytes()
    {
        return Encoding.UTF8.GetByteCount("BM|" + scene + "|" + seq.ToString(Inv) + "|0000|0|");
    }

    private static void SendLocks(ulong only)
    {
        if (scene == null || BuildingSystem.instance == null) return;
        if (!Targets(only)) return;
        foreach (FurnitureManager manager in BuildingSystem.instance.furnitureManagers)
        {
            if (manager == null || manager.gameObject == null || manager.gameObject.scene.name != scene || string.IsNullOrEmpty(manager.GUID)) continue;
            bool taskOk = manager.requiredTaskItem == null
                || (TaskItemsManager.instance != null && TaskItemsManager.instance.CheckIfPlayerHasTaskItem(manager.requiredTaskItem));
            int examine = -1;
            int examineMax = -1;
            if (manager.tenementApartmentPrefabs != null)
            {
                if (ExamineField != null) examine = (int)ExamineField.GetValue(manager.tenementApartmentPrefabs);
                if (ExamineMaxField != null) examineMax = (int)ExamineMaxField.GetValue(manager.tenementApartmentPrefabs);
            }
            string text = BuildWire.Lock(scene, manager.GUID, manager.BuildMenuDisabled, manager.managerDisabled, manager.notFullyExamined, taskOk, examine, examineMax);
            for (int i = 0; i < targets.Count; i++) Session.Send(targets[i], BuildWire.Channel, text, true);
            CoopLog.Log.Info("coop: build gate sent for " + manager.GUID + " disabled=" + manager.BuildMenuDisabled + " managerDisabled=" + manager.managerDisabled + " taskOk=" + taskOk);
        }
    }

    private static bool Targets(ulong only)
    {
        targets.Clear();
        if (only != 0UL)
        {
            targets.Add(only);
            return true;
        }
        List<ulong> peers = Session.PeersInArea(scene);
        for (int i = 0; i < peers.Count; i++) targets.Add(peers[i]);
        return targets.Count > 0;
    }

    private static void Send(string area, string text, ulong only)
    {
        if (text == null || area == null) return;
        if (only != 0UL)
        {
            Session.Send(only, BuildWire.Channel, text, true);
            return;
        }
        List<ulong> peers = Session.PeersInArea(area);
        for (int i = 0; i < peers.Count; i++) Session.Send(peers[i], BuildWire.Channel, text, true);
    }
}
