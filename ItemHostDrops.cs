using System;
using ObenseuerCore;
using UnityEngine;

internal static partial class ItemHost
{
    private static void SpawnDrop(ItemStack stack, Vector3 position)
    {
        RaycastHit hit;
        if (Physics.Raycast(position + Vector3.up * GroundUp, Vector3.down, out hit, GroundDown, GroundMask)) ItemOperations.SpawnItemToPosition(stack, hit.point, hit);
        else ItemOperations.SpawnItemToPosition(stack, position, default(RaycastHit));
    }

    public static string Register(Collectible item)
    {
        try
        {
            if (item == null || item.gameObject == null || !Active) return null;
            if (item.gameObject.scene.name != Scene()) return null;
            return IdFor(item);
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemHost.Register", e);
            return null;
        }
    }

    private static void Destroyed(Collectible item)
    {
        string scene = Scene();
        if (scene == null || item.gameObject.scene.name != scene) return;
        string id = KnownId(item);
        if (id == null)
        {
            ItemDiag.Once("ItemHost.destroyed." + item.GetType().Name, "ItemHost.destroyed unknown item " + item.name);
            return;
        }
        Forget(item.gameObject, id);
        Broadcast(scene, ItemWire.Gone(scene, id));
    }
}
