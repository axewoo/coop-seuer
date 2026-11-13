using System;
using System.Reflection;
using HarmonyLib;
using ObenseuerCore;
using UnityEngine;

internal static partial class BuildGuest
{
    private static readonly FieldInfo currentRot = AccessTools.Field(typeof(BuildingSystem), "currentrot");
    private static string movingId;
    private static BuildingSystem.FurnitureInfo movingInfo;
    private static bool movingAdded;

    internal static void SetChildRotation(GameObject go, Quaternion rotation)
    {
        if (go == null || go.transform.childCount == 0) return;
        go.transform.GetChild(0).localRotation = rotation;
    }

    public static bool BeginMove(string syncId)
    {
        if (pending != null)
        {
            Warn();
            return true;
        }
        FurnitureItem item;
        if (syncId == null || !items.TryGetValue(syncId, out item) || item == null || item.Object == null || BuildingSystem.instance == null) return false;
        FurniturePlaceable placeable = item.Object.GetComponent<FurniturePlaceable>();
        Furniture furniture = placeable == null ? null : placeable.furniture;
        if (furniture == null || placeable.meta == null || BuildingSystemMenu.instance == null) return false;
        CancelMove();
        movingAdded = false;
        movingInfo = BuildingSystem.FurnitureInfo.FindFurnitureFromList(BuildingSystem.instance.availableFurnitures, furniture, placeable.meta);
        try
        {
            if (movingInfo == null)
            {
                BuildingSystem.instance.AddFurniture(furniture, 1, placeable.meta);
                movingAdded = true;
                movingInfo = BuildingSystem.FurnitureInfo.FindFurnitureFromList(BuildingSystem.instance.availableFurnitures, furniture, placeable.meta);
            }
            if (movingInfo == null || !BuildingSystemMenu.instance.FindFurnitureAndSelectIt(furniture, placeable.meta, null, null))
            {
                CoopLog.Once("BuildGuest.move", "the build menu did not select " + furniture.Id);
                CancelMove();
                return false;
            }
            KeepRotation(item.Object);
            movingId = syncId;
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("BuildGuest.move", e);
            if (movingAdded && movingInfo == null) movingInfo = BuildingSystem.FurnitureInfo.FindFurnitureFromList(BuildingSystem.instance.availableFurnitures, furniture, placeable.meta);
            CancelMove();
            return false;
        }
    }

    public static bool TryCompleteMove(BuildingSystem.FurnitureInfo info, Vector3 position, Quaternion rotation, Quaternion rootRotation)
    {
        if (movingId == null) return false;
        if (info != movingInfo)
        {
            CancelMove();
            return false;
        }
        if (pending != null)
        {
            Warn();
            return true;
        }
        int id = ++lastReqId;
        if (!Session.Send(Session.HostId, BuildWire.Channel, BuildWire.ReqMove(id, GameRefs.AreaId, movingId, position, rotation, rootRotation), true))
        {
            CancelMove();
            Warn();
            return true;
        }
        Pending request = new Pending();
        request.ReqId = id;
        request.Move = true;
        request.Info = movingInfo;
        request.Added = movingAdded;
        request.SentAt = Time.unscaledTime;
        pending = request;
        movingId = null;
        movingInfo = null;
        movingAdded = false;
        return true;
    }

    private static void CheckMove()
    {
        if (movingId == null) return;
        if (BuildingSystemMenu.instance != null && BuildingSystemMenu.instance.menuIsOpen) return;
        CancelMove();
    }

    private static void CancelMove()
    {
        BuildingSystem.FurnitureInfo info = movingInfo;
        bool added = movingAdded;
        movingId = null;
        movingInfo = null;
        movingAdded = false;
        if (!added || info == null || BuildingSystem.instance == null) return;
        BuildingSystem.instance.RemoveFurniture(info, 1, false);
        if (BuildingSystemMenu.instance != null) BuildingSystemMenu.instance.UpdateMenu();
    }

    private static void DropPhantom(Pending request)
    {
        if (request == null || !request.Added || !Available(request.Info)) return;
        BuildingSystem.instance.RemoveFurniture(request.Info, 1, false);
        if (BuildingSystemMenu.instance != null) BuildingSystemMenu.instance.UpdateMenu();
    }

    private static void KeepRotation(GameObject go)
    {
        if (currentRot == null || BuildingSystem.instance == null) return;
        float yaw = go.transform.childCount > 0 ? go.transform.GetChild(0).localEulerAngles.y : go.transform.eulerAngles.y;
        currentRot.SetValue(BuildingSystem.instance, new Vector3(0f, yaw, 0f));
    }
}
