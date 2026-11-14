using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;

internal static partial class BuildGuest
{
    private const int MaxManifest = 4000;
    private const float ForcePruneInterval = 5f;
    private static readonly Dictionary<string, FurniturePlaceable> guidBound = new Dictionary<string, FurniturePlaceable>();
    private static readonly HashSet<string> manifest = new HashSet<string>();
    private static readonly List<FurniturePlaceable> childList = new List<FurniturePlaceable>();
    private static readonly List<string> staleKeys = new List<string>();
    private static readonly HashSet<string> forceLogged = new HashSet<string>();
    private static int manifestSeq = -1;
    private static int manifestIdx;
    private static bool manifestValid;
    private static bool manifestLogged;
    private static bool manifestSeen;
    private static float nextForcePrune;

    internal static bool GuestHandle(BuildMsg m)
    {
        if (m.Kind == "BM")
        {
            if (ReconLive(m.Scene)) OnManifest(m);
            return true;
        }
        if (m.Kind == "LOCK")
        {
            if (ReconLive(m.Scene)) OnLock(m);
            return true;
        }
        return false;
    }

    internal static bool ReconInHostWorld(string scene)
    {
        return GuestSession.InHostWorld && InScene(scene);
    }

    private static bool ReconLive(string scene)
    {
        if (!ReconInHostWorld(scene)) return false;
        if (FreeRoam.IAmAuthority || FreeRoam.Authority == 0UL) return false;
        string authorityArea = Session.AreaOf(FreeRoam.Authority);
        return authorityArea == null ? Active : authorityArea == scene;
    }

    internal static void GuestReconReset()
    {
        guidBound.Clear();
        manifest.Clear();
        forceLogged.Clear();
        manifestSeq = -1;
        manifestIdx = 0;
        manifestValid = false;
        manifestLogged = false;
        manifestSeen = false;
        nextForcePrune = 0f;
    }

    internal static void GuestReconTick(float now)
    {
        if (now < nextForcePrune) return;
        nextForcePrune = now + ForcePruneInterval;
        if (guidBound.Count == 0) return;
        staleKeys.Clear();
        foreach (KeyValuePair<string, FurniturePlaceable> pair in guidBound)
        {
            if (pair.Value == null || pair.Value.gameObject == null) staleKeys.Add(pair.Key);
        }
        for (int i = 0; i < staleKeys.Count; i++) guidBound.Remove(staleKeys[i]);
    }

    internal static void ForgetGuid(string key)
    {
        if (key != null) guidBound.Remove(key);
    }

    internal static void GuestDel(BuildMsg m)
    {
        if (!ReconInHostWorld(m.Scene)) return;
        FurnitureItem item;
        bool bound = items.TryGetValue(m.SyncId, out item) && item != null && item.Object != null;
        if (bound)
        {
            if (!Building.Remove(item))
            {
                CoopLog.Log.Info("coop: build forced guest removal of " + m.SyncId + ": " + Reason(item.Object));
                ForceRemove(item.Object, m.SyncId);
            }
        }
        else
        {
            GuestDelGuid(m.Scene, m.SyncId);
        }
        Forget(m.SyncId);
    }

    internal static bool GuestMove(BuildMsg m)
    {
        if (!ReconInHostWorld(m.Scene)) return false;
        FurniturePlaceable placeable;
        if (!guidBound.TryGetValue(m.SyncId, out placeable) || placeable == null || placeable.gameObject == null) return false;
        GameObject go = placeable.gameObject;
        go.transform.position = m.Position;
        go.transform.localRotation = m.RootRotation;
        SetChildRotation(go, m.Rotation);
        return true;
    }

    private static void OnManifest(BuildMsg m)
    {
        if (m == null || m.Keys == null) return;
        if (m.Idx == 0)
        {
            manifest.Clear();
            manifestSeq = m.Seq;
            manifestIdx = 0;
            manifestValid = true;
        }
        else if (!manifestValid || m.Seq != manifestSeq || m.Idx != manifestIdx)
        {
            InvalidateManifest();
            return;
        }
        for (int i = 0; i < m.Keys.Count; i++)
        {
            if (manifest.Count >= MaxManifest)
            {
                InvalidateManifest();
                return;
            }
            manifest.Add(m.Keys[i]);
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

    private static void InvalidateManifest()
    {
        manifestValid = false;
        manifest.Clear();
        if (manifestLogged) return;
        manifestLogged = true;
        CoopLog.Log.Info("coop: a manifest run was cut short by a lost, duplicated or out-of-order packet, the whole key set was dropped");
    }

    private static void ApplyManifest()
    {
        manifestSeen = manifestSeen || manifest.Count > 0;
        if (!manifestSeen) return;
        bool want = false;
        foreach (string key in manifest)
        {
            if (items.ContainsKey(key) || missing.ContainsKey(key) || guidBound.ContainsKey(key)) continue;
            if (!BindGuid(key)) want = true;
        }
        staleKeys.Clear();
        foreach (KeyValuePair<string, FurnitureItem> pair in items)
        {
            if (pair.Value == null || pair.Value.Object == null) continue;
            if (!replicated.Contains(pair.Value.AreaId) || manifest.Contains(pair.Key)) continue;
            staleKeys.Add(pair.Key);
        }
        for (int i = 0; i < staleKeys.Count; i++)
        {
            string key = staleKeys[i];
            FurnitureItem item;
            if (items.TryGetValue(key, out item) && item != null && item.Object != null)
            {
                CoopLog.Log.Info("coop: build forced guest removal of " + key + ": the host no longer has it");
                ForceRemove(item.Object, key);
            }
            Forget(key);
        }
        staleKeys.Clear();
        foreach (KeyValuePair<string, FurniturePlaceable> pair in guidBound)
        {
            if (manifest.Contains(pair.Key)) continue;
            CoopLog.Log.Info("coop: build forced guest removal of " + pair.Key + ": the host no longer has it");
            ForceRemovePlaceable(pair.Value, pair.Key);
            staleKeys.Add(pair.Key);
        }
        for (int i = 0; i < staleKeys.Count; i++) guidBound.Remove(staleKeys[i]);
        staleKeys.Clear();
        foreach (KeyValuePair<string, string> pair in missing)
        {
            if (!manifest.Contains(pair.Key)) staleKeys.Add(pair.Key);
        }
        for (int i = 0; i < staleKeys.Count; i++) missing.Remove(staleKeys[i]);
        if (want) Resync();
    }

    private static bool BindGuid(string key)
    {
        string guid = GuidOf(key);
        if (guid == null) return false;
        FurniturePlaceable placeable = FindPlaceable(GameRefs.AreaId, guid);
        if (placeable == null) return false;
        guidBound[key] = placeable;
        return true;
    }

    private static void GuestDelGuid(string area, string key)
    {
        string guid = GuidOf(key);
        if (guid == null) return;
        FurniturePlaceable placeable = FindPlaceable(area, guid);
        if (placeable == null) return;
        CoopLog.Log.Info("coop: build forced guest removal of " + key + ": the host no longer has it");
        ForceRemovePlaceable(placeable, key);
    }

    private static string GuidOf(string key)
    {
        if (key == null || key.Length < 16 || key[0] != 'g') return null;
        string rest = key.Substring(1);
        for (int i = 0; i < rest.Length; i++)
        {
            char c = rest[i];
            if ((c < '0' || c > '9') && c != 'h') return rest;
        }
        return null;
    }

    private static FurniturePlaceable FindPlaceable(string area, string guid)
    {
        if (area == null || guid == null || BuildingSystem.instance == null) return null;
        foreach (FurnitureManager manager in BuildingSystem.instance.furnitureManagers)
        {
            if (manager == null || manager.gameObject == null || manager.gameObject.scene.name != area) continue;
            foreach (BuildingSystem.FurnitureInfo info in manager.AllFurnitures)
            {
                if (info == null || info.gameObject == null) continue;
                FurniturePlaceable placeable = info.gameObject.GetComponent<FurniturePlaceable>();
                if (placeable != null && placeable.GUID == guid && (placeable.isPreplacedFurniture || placeable.preplacedFurnitureSecondary)) return placeable;
            }
        }
        return null;
    }

    private static void ForceRemove(GameObject go, string key)
    {
        if (go == null) return;
        FurniturePlaceable placeable = go.GetComponent<FurniturePlaceable>();
        if (placeable == null) return;
        ForceRemovePlaceable(placeable, key);
    }

    private static void ForceRemovePlaceable(FurniturePlaceable placeable, string key)
    {
        if (placeable == null || placeable.gameObject == null) return;
        if (forceLogged.Add(key))
        {
            CoopLog.Log.Info("coop: build forced removal of " + key + " (" + Reason(placeable.gameObject) + ", cantTake=" + placeable.cantTake + ")");
        }
        DestroyPiece(placeable, 0);
    }

    private static void DestroyPiece(FurniturePlaceable placeable, int depth)
    {
        if (placeable == null || placeable.gameObject == null) return;
        if (depth < 3) TakeChildren(placeable, depth);
        FurnitureManager manager = placeable.manager;
        GameObject go = placeable.gameObject;
        if (go == null) return;
        if (placeable.parent != null) placeable.parent.RemoveChildFurniture(placeable);
        if (placeable.attachedto != null) placeable.attachedto.RemoveAttachedFurniture(placeable);
        if (manager != null) manager.TookFurniture(go);
        placeable.Destroyed();
        UnityEngine.Object.Destroy(go);
    }

    private static void TakeChildren(FurniturePlaceable placeable, int depth)
    {
        FurnitureManager manager = placeable.manager;
        if (manager == null) return;
        childList.Clear();
        foreach (BuildingSystem.FurnitureInfo info in manager.AllFurnitures)
        {
            if (info == null || info.gameObject == null) continue;
            FurniturePlaceable other = info.gameObject.GetComponent<FurniturePlaceable>();
            if (other == null || other == placeable) continue;
            if (other.parent == placeable || other.attachedto == placeable) childList.Add(other);
        }
        for (int i = 0; i < childList.Count; i++) DestroyPiece(childList[i], depth + 1);
    }

    private static string Reason(GameObject go)
    {
        FurniturePlaceable placeable = go == null ? null : go.GetComponent<FurniturePlaceable>();
        if (placeable == null) return "the piece is gone";
        if (placeable.cantTake) return "the piece cannot be taken";
        if (placeable.item != null) return "the piece holds an item";
        if (placeable.itemRequired != null && placeable.itemRequired.ID > 0) return "the piece needs an item";
        if (HasChildren(placeable)) return "the piece has children";
        return "the framework refused the removal";
    }

    private static bool HasChildren(FurniturePlaceable placeable)
    {
        FurnitureManager manager = placeable.manager;
        if (manager == null) return false;
        foreach (BuildingSystem.FurnitureInfo info in manager.AllFurnitures)
        {
            if (info == null || info.gameObject == null) continue;
            FurniturePlaceable other = info.gameObject.GetComponent<FurniturePlaceable>();
            if (other != null && other != placeable && (other.parent == placeable || other.attachedto == placeable)) return true;
        }
        return false;
    }

    private static void OnLock(BuildMsg m)
    {
        if (m == null || !ReconInHostWorld(m.Scene)) return;
        FurnitureManager manager = FindManager(m.Manager);
        if (manager == null) return;
        if (!m.Disabled)
        {
            if (manager.notFullyExamined)
            {
                manager.ChangeManagerNotFullyExaminedStatus(false);
                CoopLog.Log.Info("coop: build menu examined status cleared for " + manager.GUID);
            }
            if (manager.managerDisabled)
            {
                manager.ChangeManagerDisabledStatus(false);
                CoopLog.Log.Info("coop: build menu manager status cleared for " + manager.GUID);
            }
        }
        if (!manager.notFullyExamined && m.NotFullyExamined)
        {
            manager.ChangeManagerNotFullyExaminedStatus(true);
            CoopLog.Log.Info("coop: build menu examined status set again for " + manager.GUID);
        }
        if (!manager.managerDisabled && m.ManagerDisabled)
        {
            manager.ChangeManagerDisabledStatus(true);
            CoopLog.Log.Info("coop: build menu manager status set again for " + manager.GUID);
        }
        bool has = manager.requiredTaskItem == null
            || (TaskItemsManager.instance != null && TaskItemsManager.instance.CheckIfPlayerHasTaskItem(manager.requiredTaskItem));
        if (m.TaskOk && !has && manager.requiredTaskItem != null && TaskItemsManager.instance != null)
        {
            try
            {
                if (TaskItemsManager.instance.AddTaskItem(manager.requiredTaskItem, 0, false, null, false, "New item", false))
                {
                    CoopLog.Log.Info("coop: build task item mirrored for " + manager.GUID + ": " + manager.requiredTaskItem.name);
                }
                else
                {
                    CoopLog.Once("BuildGuest.lock." + manager.GUID, "the build task item could not be mirrored for " + manager.GUID);
                }
            }
            catch (Exception e)
            {
                CoopLog.Once("BuildGuest.lock", e);
            }
        }
    }
}
