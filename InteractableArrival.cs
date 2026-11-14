using System;
using System.Collections.Generic;
using ObenseuerCore;

internal static partial class InteractableSync
{
    private const int MaxManifest = 200;
    private static readonly HashSet<ulong> manifestPresent = new HashSet<ulong>();
    private static readonly HashSet<ulong> manifestPrevious = new HashSet<ulong>();
    private static string manifestScene;

    public static void ManifestTick()
    {
        try
        {
            string scene = Session.IsInLobby ? GameRefs.AreaId : null;
            if (scene == null || !FreeRoam.IAmAuthority)
            {
                manifestScene = null;
                manifestPresent.Clear();
                manifestPrevious.Clear();
                return;
            }
            if (manifestScene != scene)
            {
                manifestScene = scene;
                manifestPresent.Clear();
                manifestPrevious.Clear();
            }
            manifestPrevious.Clear();
            foreach (ulong peer in manifestPresent) manifestPrevious.Add(peer);
            manifestPresent.Clear();
            foreach (PresenceRecord record in Session.Remotes())
            {
                if (record.Area != scene || !Session.Peers.Contains(record.Peer)) continue;
                manifestPresent.Add(record.Peer);
                if (manifestPrevious.Contains(record.Peer)) continue;
                ManifestFor(record.Peer);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.ManifestTick", e);
        }
    }

    private static void ManifestFor(ulong peer)
    {
        nextIndex = 0f;
        nextMiss = 0f;
        Find("~");
        int sent = 0;
        foreach (KeyValuePair<string, Interactable> pair in cache)
        {
            if (pair.Value == null || !Of(pair.Value, Stateful)) continue;
            if (sent >= MaxManifest)
            {
                CoopLog.Once("InteractableSync.manifestcap", "coop: this scene holds more shared switch state than one arrival replay carries, the rest keeps the state of the shipped scene");
                break;
            }
            sent++;
            Push(pair.Value, pair.Key, peer);
        }
        DynamiteSync.ManifestFor(peer);
    }
}
