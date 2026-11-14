using System;
using ObenseuerCore;
using UnityEngine;

internal static partial class WorldShip
{
    private const float SceneSettle = 6f;
    private static float hostLoadAt = -1000f;
    private static bool deferring;

    public static bool Deferred
    {
        get { return deferring; }
    }

    private static void OnHostLevelLoaded()
    {
        hostLoadAt = Time.unscaledTime;
    }

    public static string PendingSaveReason()
    {
        if (SaveController.Loading) return "the host is still loading a scene";
        if (SaveData.Busy) return "the game is still busy saving or loading";
        if (string.IsNullOrEmpty(GameRefs.AreaId)) return "the host is not in a world yet";
        if (Time.unscaledTime - hostLoadAt < SceneSettle) return "the host changed scene " + (int)(Time.unscaledTime - hostLoadAt) + " s ago";
        if (!SaveSlots.CanSaveNow) return "the game is not ready to save yet, a load, a wait, a death or a dialogue is still running";
        return null;
    }
}
