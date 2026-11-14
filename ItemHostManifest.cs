using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ObenseuerCore;
using UnityEngine;

internal static partial class ItemHost
{
    private const float ManifestInterval = 20f;
    private const float ManifestGrace = 10f;
    private const int MaxManifestPacket = 850;
    private const int MaxManifestIds = 200;
    private const int MaxManifestIdsTotal = 3900;
    private const float HostSettle = 6f;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly List<string> manifestIds = new List<string>();
    private static readonly List<string> manifestPackets = new List<string>();
    private static readonly HashSet<ulong> present = new HashSet<ulong>();
    private static readonly HashSet<ulong> previous = new HashSet<ulong>();
    private static readonly StringBuilder manifestBody = new StringBuilder(900);
    private static string manifestScene;
    private static bool wasAuthority;
    private static int manifestSeq;
    private static float nextManifest;
    private static float noSendBefore;
    private static bool hostLoadHooked;
    private static float hostLoadSeenAt = -1000f;

    public static void ManifestReset()
    {
        manifestIds.Clear();
        hostLoadSeenAt = -1000f;
        manifestPackets.Clear();
        manifestBody.Length = 0;
        present.Clear();
        previous.Clear();
        manifestScene = null;
        wasAuthority = false;
        manifestSeq = 0;
        nextManifest = 0f;
        noSendBefore = 0f;
    }

    private static void HookHostLoad()
    {
        if (hostLoadHooked) return;
        hostLoadHooked = true;
        SaveController.LoadingDone += NoteHostLoad;
    }

    private static void NoteHostLoad()
    {
        hostLoadSeenAt = Time.unscaledTime;
    }

    public static void ManifestTick()
    {
        try
        {
            if (!Session.IsInLobby) return;
            HookHostLoad();
            bool authority = FreeRoam.IAmAuthority;
            if (wasAuthority != authority)
            {
                wasAuthority = authority;
                manifestScene = null;
                present.Clear();
                previous.Clear();
                manifestSeq = 0;
                nextManifest = 0f;
                noSendBefore = Time.unscaledTime + ManifestGrace;
            }
            if (!authority) return;
            string scene = GameRefs.AreaId;
            if (scene == null) return;
            float now = Time.unscaledTime;
            if (manifestScene != scene)
            {
                manifestScene = scene;
                present.Clear();
                previous.Clear();
                manifestSeq = 0;
                nextManifest = now;
            }
            if (Session.PeersInArea(scene).Count == 0)
            {
                present.Clear();
                previous.Clear();
                return;
            }
            bool arrived = Arrivals(scene);
            if (arrived) nextManifest = now;
            if (now < noSendBefore) return;
            if (SaveController.Loading || now - hostLoadSeenAt < HostSettle) return;
            if (arrived || now >= nextManifest)
            {
                nextManifest = now + ManifestInterval;
                SendManifest(0UL);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemHost.ManifestTick", e);
        }
    }

    private static bool Arrivals(string scene)
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

    private static void SendManifest(ulong only)
    {
        string scene = manifestScene;
        if (scene == null) return;
        List<ulong> peers = only != 0UL ? new List<ulong> { only } : Session.PeersInArea(scene);
        if (peers.Count == 0) return;
        List<Send> weak = WeakInScene(scene);
        manifestIds.Clear();
        for (int i = 0; i < weak.Count; i++)
        {
            if (manifestIds.Count >= MaxManifestIdsTotal)
            {
                CoopLog.Once("ItemHost.manifestids", "a scene holds more floor item ids than one name set may carry, the list is cut at " + MaxManifestIdsTotal);
                break;
            }
            string id = IdFor(weak[i].Item);
            if (!string.IsNullOrEmpty(id)) manifestIds.Add(id);
        }
        manifestSeq++;
        manifestPackets.Clear();
        manifestBody.Length = 0;
        bool any = false;
        int count = 0;
        int bytes = HeadBytes();
        for (int i = 0; i < manifestIds.Count; i++)
        {
            string id = manifestIds[i];
            int size = Encoding.UTF8.GetByteCount(id) + 1;
            if (any && (bytes + size > MaxManifestPacket || count >= MaxManifestIds))
            {
                manifestPackets.Add(manifestBody.ToString());
                manifestBody.Length = 0;
                any = false;
                count = 0;
                bytes = HeadBytes();
            }
            if (any) manifestBody.Append(',');
            manifestBody.Append(id);
            bytes += size;
            any = true;
            count++;
        }
        manifestPackets.Add(manifestBody.ToString());
        for (int i = 0; i < manifestPackets.Count; i++)
        {
            bool last = i == manifestPackets.Count - 1;
            string text = ItemWire.Manifest(scene, manifestSeq, i, last, manifestPackets[i]);
            for (int p = 0; p < peers.Count; p++) Enqueue(peers[p], text, true);
        }
        if (CoopDebug.On("item")) CoopDebug.Event("item", "manifest", manifestIds.Count + " ids in " + manifestPackets.Count + " packets, seq " + manifestSeq + ", " + peers.Count + " peers in " + scene);
    }

    private static int HeadBytes()
    {
        return Encoding.UTF8.GetByteCount("IM|" + manifestScene + "|" + manifestSeq.ToString(Inv) + "|0000|0|");
    }
}
