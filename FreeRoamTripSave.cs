using System;
using UnityEngine;

internal static partial class FreeRoam
{
    private const float SaveDeferLimit = 20f;
    private const float SaveDeferStep = 1f;
    private static ulong deferGuest;
    private static float deferAt;
    private static int deferTries;

    private static void TripSaveReset()
    {
        deferGuest = 0UL;
        deferAt = 0f;
        deferTries = 0;
    }

    private static bool TripSaveWait(Trip t)
    {
        float now = Time.unscaledTime;
        string reason = WorldShip.PendingSaveReason();
        if (reason == null)
        {
            deferGuest = 0UL;
            return false;
        }
        if (deferGuest != t.Guest)
        {
            deferGuest = t.Guest;
            deferAt = now;
            deferTries = 1;
            CoopLog.Log.Info("coop: ticket save deferred (" + reason + "), retry 1");
            return true;
        }
        if (now - deferAt >= SaveDeferLimit)
        {
            deferGuest = 0UL;
            trips.Remove(t);
            Session.Send(t.Guest, Channel, "GOERR|" + Cut(reason), true);
            CoopLog.Log.Info("coop: guest " + t.Guest + " travel to " + t.Scene + " refused after " + deferTries + " save retries: " + reason);
            return true;
        }
        if (now - deferAt < deferTries * SaveDeferStep) return true;
        deferTries++;
        if (deferTries % 5 == 0) CoopLog.Log.Info("coop: ticket save deferred (" + reason + "), retry " + deferTries);
        return true;
    }
}
