using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static partial class CraftHost
{
    private const float ResendEvery = 20f;
    private const float PresenceEvery = 1f;
    private const int ResendShare = 2;
    private static readonly HashSet<ulong> present = new HashSet<ulong>();
    private static readonly HashSet<ulong> previous = new HashSet<ulong>();
    private static readonly List<ulong> arrived = new List<ulong>();
    private static readonly HashSet<string> sentKeys = new HashSet<string>();
    private static readonly List<CraftJob> pending = new List<CraftJob>();
    private static readonly Dictionary<string, CraftJob> waiting = new Dictionary<string, CraftJob>();
    private static string resendScene;
    private static ulong resendAuthority;
    private static float nextResend;
    private static float nextPresence;

    public static void ResendTick()
    {
        try
        {
            if (!Session.IsInLobby) return;
            ulong owner = FreeRoam.Authority;
            if (owner != resendAuthority)
            {
                resendAuthority = owner;
                pending.Clear();
                waiting.Clear();
                nextResend = 0f;
            }
            if (!FreeRoam.IAmAuthority) return;
            string scene = GameRefs.AreaId;
            if (scene == null) return;
            float now = Time.unscaledTime;
            if (!string.Equals(resendScene, scene, StringComparison.Ordinal))
            {
                resendScene = scene;
                present.Clear();
                arrived.Clear();
                pending.Clear();
                waiting.Clear();
                nextResend = 0f;
                nextPresence = 0f;
            }
            Drain();
            if (now >= nextPresence)
            {
                nextPresence = now + PresenceEvery;
                Arrivals(scene);
            }
            bool full = now >= nextResend;
            if (!full && arrived.Count == 0) return;
            if (full) nextResend = now + ResendEvery;
            List<ulong> peers = full ? Session.PeersInArea(scene) : new List<ulong>(arrived);
            arrived.Clear();
            if (peers.Count == 0) return;
            Resend(peers);
        }
        catch (Exception e)
        {
            CoopLog.Once("CraftHost.ResendTick", e);
        }
    }

    private static void Arrivals(string scene)
    {
        arrived.Clear();
        previous.Clear();
        foreach (ulong peer in present) previous.Add(peer);
        present.Clear();
        foreach (PresenceRecord record in Session.Remotes())
        {
            if (record.Area != scene || !Session.Peers.Contains(record.Peer)) continue;
            present.Add(record.Peer);
            if (!previous.Contains(record.Peer)) arrived.Add(record.Peer);
        }
    }

    private static void Resend(List<ulong> peers)
    {
        sentKeys.Clear();
        int stations = 0;
        CraftingBase[] all = UnityEngine.Object.FindObjectsByType<CraftingBase>(FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            CraftingBase craft = all[i];
            if (craft == null || craft.gameObject.scene.handle != SceneManager.GetActiveScene().handle) continue;
            Storage input = craft.InputOutputStorage;
            if (input == null) continue;
            string key = ContainerWire.KeyOf(input);
            if (key == null) continue;
            ulong holder;
            if (ContainerHost.HeldBy(key, out holder)) continue;
            if (sentKeys.Add(key))
            {
                Enqueue(input, key, craft.CraftingIProgress(), peers);
                stations++;
            }
            Storage output = craft.OutputStorage;
            if (output == null || output == input) continue;
            string outKey = ContainerWire.KeyOf(output);
            if (outKey == null || ContainerHost.HeldBy(outKey, out holder) || !sentKeys.Add(outKey)) continue;
            Enqueue(output, outKey, craft.CraftingIProgress(), peers);
        }
        if (CoopDebug.On("inter")) CoopDebug.Event("inter", "craft-resend", stations + " stations to " + peers.Count + " peers");
    }

    private static void Enqueue(Storage storage, string key, bool progress, List<ulong> peers)
    {
        CraftJob job;
        if (waiting.TryGetValue(key, out job))
        {
            job.Storage = storage;
            job.Progress = progress;
            for (int i = 0; i < peers.Count; i++)
            {
                if (!job.Peers.Contains(peers[i])) job.Peers.Add(peers[i]);
            }
            return;
        }
        job = new CraftJob();
        job.Storage = storage;
        job.Key = key;
        job.Progress = progress;
        job.Peers = new List<ulong>(peers);
        waiting[key] = job;
        pending.Add(job);
    }

    private static void Drain()
    {
        if (pending.Count == 0 || BlobBudget.Blocked) return;
        int share = ResendShare;
        List<ulong> live = Session.Peers;
        while (share > 0 && pending.Count > 0)
        {
            CraftJob job = pending[0];
            if (job.Storage == null || job.Index >= job.Peers.Count)
            {
                Drop(job);
                continue;
            }
            ulong holder;
            if (ContainerHost.HeldBy(job.Key, out holder))
            {
                Drop(job);
                continue;
            }
            if (!BlobBudget.Take()) return;
            if (job.Data == null)
            {
                string json = ContainerWire.Capture(job.Storage, job.Key);
                if (json == null)
                {
                    CoopLog.Once("CraftHost.Push.capture", "coop: the state of the station storage " + job.Key + " could not be read");
                    Drop(job);
                    continue;
                }
                job.Data = ContainerWire.Bytes(json);
                job.Text = CraftWire.Station(resendScene, job.Key, job.Progress);
            }
            ulong peer = job.Peers[job.Index];
            if (!live.Contains(peer))
            {
                job.Peers.RemoveAt(job.Index);
                continue;
            }
            if (NetBlob.Send(peer, CraftWire.ModId, CraftWire.BlobNameOf(job.Key), job.Data) < 0)
            {
                if (++job.Refusals >= BlobBudget.MaxRefusals)
                {
                    CoopLog.Once("CraftHost.Push.blob", "coop: the state of station storage " + job.Key + " could not be sent to " + peer);
                    Drop(job);
                    continue;
                }
                BlobBudget.Block();
                return;
            }
            job.Refusals = 0;
            Session.Send(peer, CraftWire.Channel, job.Text, true);
            job.Index++;
            share--;
        }
    }

    private static void Drop(CraftJob job)
    {
        pending.RemoveAt(0);
        waiting.Remove(job.Key);
    }

    private sealed class CraftJob
    {
        public Storage Storage;
        public string Key;
        public bool Progress;
        public List<ulong> Peers;
        public int Index;
        public int Refusals;
        public byte[] Data;
        public string Text;
    }
}

internal static class BlobBudget
{
    private const int PerFrame = 4;
    private const float BlockSeconds = 0.5f;
    internal const int MaxRefusals = 6;
    private static int frame = -1;
    private static int used;
    private static float blockedUntil;

    internal static bool Blocked
    {
        get { return Time.unscaledTime < blockedUntil; }
    }

    internal static void Block()
    {
        blockedUntil = Time.unscaledTime + BlockSeconds;
    }

    internal static bool Take()
    {
        if (Time.frameCount != frame)
        {
            frame = Time.frameCount;
            used = 0;
        }
        if (used >= PerFrame) return false;
        used++;
        return true;
    }
}
