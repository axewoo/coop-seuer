using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

internal static class BuildHooks
{
    private const string NotShared = "That piece is not shared in co-op.";
    private static bool installed;

    public static bool Ready { get; private set; }

    public static bool Install()
    {
        if (installed) return true;
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".build");
        try
        {
            Patch(harmony, "Place", Type.EmptyTypes, "PlacePrefix");
            Patch(harmony, "Take", new[] { typeof(GameObject).MakeByRefType() }, "TakePrefix");
            Patch(harmony, "Move", Type.EmptyTypes, "MovePrefix");
            BuildGate.Install();
            installed = true;
            Ready = true;
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("BuildHooks.Install", e);
            harmony.UnpatchSelf();
            return false;
        }
    }

    public static bool PlacePrefix(BuildingSystem __instance, Vector3 ___currentpos)
    {
        bool replicated = false;
        try
        {
            if (GuestGate.Block(GuestGate.KindBuild)) return false;
            replicated = IsReplicatedArea();
            if (!replicated) return true;
            return GuestPlace(__instance, ___currentpos);
        }
        catch (Exception e)
        {
            CoopLog.Once("BuildHooks.Place", e);
            return !replicated;
        }
    }

    public static bool TakePrefix(out GameObject savedGameObject, ref List<FurniturePlaceable> __result, RaycastHit ___hit)
    {
        savedGameObject = null;
        bool replicated = false;
        try
        {
            if (GuestGate.Block(GuestGate.KindBuild)) return false;
            replicated = IsReplicatedArea();
            if (!replicated) return true;
            FurniturePlaceable placeable = PlaceableOf(___hit);
            string id = placeable == null ? null : BuildGuest.IdOf(placeable.gameObject);
            if (id != null)
            {
                __result = new List<FurniturePlaceable>();
                BuildGuest.RequestTake(id);
                return false;
            }
            if (placeable != null)
            {
                __result = new List<FurniturePlaceable>();
                BuildGuest.Notice(NotShared);
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("BuildHooks.Take", e);
            return !replicated;
        }
    }

    public static bool MovePrefix(RaycastHit ___hit)
    {
        bool replicated = false;
        try
        {
            if (GuestGate.Block(GuestGate.KindBuild)) return false;
            replicated = IsReplicatedArea();
            if (!replicated) return true;
            FurniturePlaceable placeable = PlaceableOf(___hit);
            string id = placeable == null ? null : BuildGuest.IdOf(placeable.gameObject);
            if (id == null)
            {
                if (placeable == null) return true;
                BuildGuest.Notice(NotShared);
                return false;
            }
            if (!BuildGuest.BeginMove(id)) BuildGuest.Notice("That piece cannot be moved here.");
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("BuildHooks.Move", e);
            return !replicated;
        }
    }

    private static void Patch(Harmony harmony, string name, Type[] args, string prefix)
    {
        MethodBase original = AccessTools.Method(typeof(BuildingSystem), name, args);
        if (original == null) throw new MissingMethodException(typeof(BuildingSystem).Name, name);
        harmony.Patch(original, prefix: new HarmonyMethod(typeof(BuildHooks), prefix));
    }

    private static bool IsReplicatedArea()
    {
        FurnitureManager manager = BuildingSystem.activeManager;
        return BuildGuest.Active && manager != null && BuildGuest.IsReplicated(manager.GUID);
    }

    private static FurniturePlaceable PlaceableOf(RaycastHit hit)
    {
        if (hit.collider == null) return null;
        return hit.collider.GetComponentInParent<FurniturePlaceable>();
    }

    private static bool GuestPlace(BuildingSystem system, Vector3 pos)
    {
        if (system.currentPreview == null) return false;
        if (!system.currentPreview.gameObject.activeInHierarchy) return false;
        ObjectPreview preview = system.currentPreview.GetComponentInChildren<ObjectPreview>(true);
        if (preview == null || !preview.isPlaceable) return true;
        BuildingSystem.FurnitureInfo info = system.currentFurniture;
        if (info == null || info.furniture == null)
        {
            BuildGuest.Warn();
            return false;
        }
        if (BuildGuest.TryCompleteMove(info, pos, ChildRotation(system.currentPreview), RootRotation(BuildingSystem.activeManager, system.currentPreview))) return false;
        if (!BuildGuest.Available(info))
        {
            BuildGuest.Warn();
            return false;
        }
        if (BuildGuest.IsModFurniture(info.furniture))
        {
            BuildGuest.Notice("Furniture from another mod is not in co-op yet.");
            return false;
        }
        if (info.gameObject != null || IsItemStack(info))
        {
            BuildGuest.Warn();
            return false;
        }
        BuildGuest.RequestPlace(PieceFor(system, pos, info), info);
        return false;
    }

    private static bool IsItemStack(BuildingSystem.FurnitureInfo info)
    {
        return info.itemStack != null && info.itemStack.itemId != -1 && info.itemStack.itemAmount != 0;
    }

    private static Quaternion ChildRotation(Transform preview)
    {
        if (preview == null || preview.childCount == 0) return Quaternion.identity;
        return Quaternion.Euler(preview.GetChild(0).localEulerAngles);
    }

    private static Quaternion RootRotation(FurnitureManager manager, Transform preview)
    {
        if (manager == null || preview == null) return Quaternion.identity;
        return Quaternion.Inverse(manager.transform.rotation) * Quaternion.Euler(preview.localEulerAngles);
    }

    private static Piece PieceFor(BuildingSystem system, Vector3 pos, BuildingSystem.FurnitureInfo info)
    {
        FurnitureManager manager = BuildingSystem.activeManager;
        Transform preview = system.currentPreview;
        return new Piece
        {
            Scene = GameRefs.AreaId,
            Manager = manager.GUID,
            FurnitureId = info.furniture.Id,
            Position = pos,
            Rotation = RootRotation(manager, preview),
            ChildRotation = ChildRotation(preview),
            Skin = info.meta == null ? 0 : info.meta.currentSkin,
            Alt = info.meta == null ? 0 : info.meta.currentAlternativePrefab
        };
    }
}
