using System;
using UnityEngine;

internal static partial class FreeRoam
{
    private const float TripGrace = 8f;
    private const float TripOuter = 90f;
    private static ulong tripGuest;
    private static string tripScene;
    private static float tripAt;
    private static float tripStopAt;

    private static void TripReset()
    {
        tripGuest = 0UL;
        tripScene = null;
        tripAt = 0f;
        tripStopAt = 0f;
    }

    private static void TripStarted(ulong guest, string scene)
    {
        tripGuest = guest;
        tripScene = scene;
        tripAt = Time.unscaledTime;
        tripStopAt = WorldShip.StoppedAt;
    }

    private static void TripWatch()
    {
        if (tripGuest == 0UL) return;
        ulong guest = tripGuest;
        string scene = tripScene;
        if (string.Equals(Session.AreaOf(guest), scene, StringComparison.Ordinal))
        {
            tripGuest = 0UL;
            return;
        }
        if (WorldShip.StoppedAt > tripStopAt)
        {
            tripGuest = 0UL;
            Session.Send(guest, Channel, "GOERR|" + Cut(WorldShip.StopReason), true);
            CoopLog.Log.Info("coop: guest " + guest + " travel to " + scene + " stopped (" + WorldShip.StopReason + "), the guest is told");
            return;
        }
        if ((!WorldShip.Busy && Time.unscaledTime - tripAt > TripGrace) || Time.unscaledTime - tripAt > TripOuter) tripGuest = 0UL;
    }
}
