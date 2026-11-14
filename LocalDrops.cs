using System.Collections.Generic;
using UnityEngine;

internal static class LocalDrops
{
    private const int MaxRecords = 64;
    private const float ArmWindow = 2f;
    private static readonly HashSet<int> mine = new HashSet<int>();
    private static int armedItem;
    private static float armedUntil;

    public static bool Remember(ItemData data)
    {
        try
        {
            if (!Session.IsInLobby) return true;
            if (data == null || data.stack == null || data.stack.itemId < 2) return true;
            armedItem = data.stack.itemId;
            armedUntil = Time.unscaledTime + ArmWindow;
        }
        catch (System.Exception e)
        {
            CoopLog.Once("LocalDrops.Remember", e);
        }
        return true;
    }

    public static void OnSpawned(CollectibleItem piece)
    {
        try
        {
            if (!Session.IsInLobby) return;
            if (armedItem == 0 || Time.unscaledTime > armedUntil || piece == null) return;
            ItemStack stack = piece.GetItemStack();
            if (stack == null || stack.itemId != armedItem) return;
            armedItem = 0;
            if (mine.Count >= MaxRecords) mine.Clear();
            mine.Add(piece.gameObject.GetInstanceID());
        }
        catch (System.Exception e)
        {
            CoopLog.Once("LocalDrops.OnSpawned", e);
        }
    }

    public static bool TakeMatch(Collectible item)
    {
        if (item == null || !mine.Remove(item.gameObject.GetInstanceID())) return false;
        CoopLog.Log.Info("coop: picking up " + item.name + " locally, I dropped it while the scene was not shared");
        return true;
    }

    public static void Reset()
    {
        mine.Clear();
        armedItem = 0;
    }
}
