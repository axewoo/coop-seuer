using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

internal static class NpcSync
{
    public const string Channel = "npc";
    private const float SendInterval = 0.2f;
    private const float Stale = 2f;
    private const float Smoothing = 10f;
    private const float SnapDistance = 6f;
    private const int PerPacket = 12;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Dictionary<int, NPCController> npcs = new Dictionary<int, NPCController>();
    private static readonly Dictionary<int, Target> targets = new Dictionary<int, Target>();
    private static readonly Dictionary<int, NPCController> stopped = new Dictionary<int, NPCController>();
    private static readonly Dictionary<int, bool[]> before = new Dictionary<int, bool[]>();
    private static System.Reflection.MethodInfo AnimateMethod;
    private static readonly StringBuilder sb = new StringBuilder();
    private static int sceneHandle = -1;
    private static bool wasAuthority;
    private static float nextSend;
    private static float nextIndex;

    private struct Target
    {
        public Vector3 Position;
        public float Yaw;
        public float At;
    }

    public static void Tick()
    {
        try
        {
            string here = GameRefs.AreaId;
            if (!Session.IsInLobby || here == null)
            {
                Release();
                return;
            }
            Index(false);
            bool authority = FreeRoam.AuthorityOf(here) == Session.Me;
            if (authority && !wasAuthority) Release();
            wasAuthority = authority;
            if (authority) HostTick(here);
            else GuestTick(here);
        }
        catch (Exception e)
        {
            CoopLog.Once("NpcSync.Tick", e);
        }
    }

    private static void HostTick(string here)
    {
        float now = Time.unscaledTime;
        if (now < nextSend) return;
        nextSend = now + SendInterval;
        List<ulong> peers = Session.PeersInArea(here);
        if (peers.Count == 0) return;
        int count = 0;
        sb.Length = 0;
        foreach (KeyValuePair<int, NPCController> pair in npcs)
        {
            NPCController npc = pair.Value;
            if (npc == null || !npc.isActiveAndEnabled) continue;
            Transform t = npc.transform;
            if (count > 0) sb.Append(';');
            sb.Append(pair.Key.ToString(Inv)).Append(',').Append(t.position.x.ToString("F2", Inv)).Append(',').Append(t.position.y.ToString("F2", Inv)).Append(',').Append(t.position.z.ToString("F2", Inv)).Append(',').Append(t.eulerAngles.y.ToString("F0", Inv));
            count++;
            if (count >= PerPacket)
            {
                Flush(peers, here);
                count = 0;
            }
        }
        if (count > 0) Flush(peers, here);
    }

    private static void Flush(List<ulong> peers, string here)
    {
        string text = here + "|" + sb.ToString();
        sb.Length = 0;
        for (int i = 0; i < peers.Count; i++) Session.Send(peers[i], Channel, text, false);
    }

    private static void GuestTick(string here)
    {
        float now = Time.unscaledTime;
        ulong auth = FreeRoam.AuthorityOf(here);
        bool hostHere = auth != 0UL && auth != Session.Me && Session.AreaOf(auth) == here;
        float dt = Time.deltaTime;
        float blend = 1f - Mathf.Exp(-Smoothing * dt);
        bool talking = DialogueController.instance != null && DialogueController.instance.DialogueActive;
        List<int> drop = null;
        foreach (KeyValuePair<int, Target> pair in targets)
        {
            NPCController npc;
            if (!hostHere || now - pair.Value.At > Stale || !npcs.TryGetValue(pair.Key, out npc) || npc == null)
            {
                if (drop == null) drop = new List<int>();
                drop.Add(pair.Key);
                continue;
            }
            if (!stopped.ContainsKey(pair.Key)) Stop(pair.Key, npc);
            if (talking) continue;
            Transform t = npc.transform;
            Vector3 before = t.position;
            if ((t.position - pair.Value.Position).sqrMagnitude > SnapDistance * SnapDistance) t.position = pair.Value.Position;
            else t.position = Vector3.Lerp(t.position, pair.Value.Position, blend);
            t.rotation = Quaternion.Euler(0f, Mathf.LerpAngle(t.eulerAngles.y, pair.Value.Yaw, blend), 0f);
            Animate(npc, dt > 0f ? (t.position - before).magnitude / dt : 0f);
        }
        if (drop == null) return;
        for (int i = 0; i < drop.Count; i++)
        {
            targets.Remove(drop[i]);
            Resume(drop[i]);
        }
    }

    public static void Handle(ulong from, string text)
    {
        try
        {
            if (from == Session.Me || from != FreeRoam.AuthorityOf(GameRefs.AreaId) || text == null || text.Length > 2000) return;
            int bar = text.IndexOf('|');
            if (bar <= 0 || text.Substring(0, bar) != GameRefs.AreaId) return;
            float now = Time.unscaledTime;
            string[] items = text.Substring(bar + 1).Split(';');
            for (int i = 0; i < items.Length && i < PerPacket; i++)
            {
                string[] f = items[i].Split(',');
                int id;
                float x, y, z, yaw;
                if (f.Length != 5 || !int.TryParse(f[0], NumberStyles.Integer, Inv, out id) || !F(f[1], out x) || !F(f[2], out y) || !F(f[3], out z) || !F(f[4], out yaw)) continue;
                if (!npcs.ContainsKey(id)) Index(true);
                if (!npcs.ContainsKey(id)) continue;
                targets[id] = new Target { Position = new Vector3(x, y, z), Yaw = yaw, At = now };
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("NpcSync.Handle", e);
        }
    }

    private static bool F(string s, out float v)
    {
        return float.TryParse(s, NumberStyles.Float, Inv, out v) && !float.IsNaN(v) && !float.IsInfinity(v) && Mathf.Abs(v) < 100000f;
    }

    private static void Index(bool unknown)
    {
        int handle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
        float now = Time.unscaledTime;
        bool sceneChanged = handle != sceneHandle;
        if (!sceneChanged && now < nextIndex && !unknown) return;
        if (!sceneChanged && unknown && now < nextIndex - 4f) return;
        if (sceneChanged)
        {
            Release();
            sceneHandle = handle;
        }
        nextIndex = now + 5f;
        npcs.Clear();
        foreach (NPCController npc in UnityEngine.Object.FindObjectsByType<NPCController>(FindObjectsSortMode.None))
        {
            if (npc == null) continue;
            int id = npc.ID;
            if (id != 0 && !npcs.ContainsKey(id)) npcs[id] = npc;
        }
    }

    private static void Animate(NPCController npc, float speed)
    {
        if (npc.movementAnimation == null) return;
        if (AnimateMethod == null) AnimateMethod = HarmonyLib.AccessTools.Method(npc.movementAnimation.GetType(), "Animate", new[] { typeof(float) });
        if (AnimateMethod == null) return;
        try
        {
            AnimateMethod.Invoke(npc.movementAnimation, new object[] { speed });
        }
        catch (Exception e)
        {
            CoopLog.Once("NpcSync.Animate", e);
        }
    }

    private static void Stop(int id, NPCController npc)
    {
        stopped[id] = npc;
        Pathfinding.IAstarAI saved = npc.GetComponent<Pathfinding.IAstarAI>();
        before[id] = new[] { npc.activityDisabled, saved != null && saved.canMove, saved != null && saved.isStopped };
        npc.activityDisabled = true;
        Pathfinding.IAstarAI ai = npc.GetComponent<Pathfinding.IAstarAI>();
        if (ai != null)
        {
            ai.canMove = false;
            ai.isStopped = true;
        }
    }

    private static void Resume(int id)
    {
        NPCController npc;
        if (!stopped.TryGetValue(id, out npc)) return;
        stopped.Remove(id);
        bool[] was;
        if (!before.TryGetValue(id, out was)) was = new[] { false, true, false };
        before.Remove(id);
        if (npc == null) return;
        try
        {
            Pathfinding.IAstarAI ai = npc.GetComponent<Pathfinding.IAstarAI>();
            if (ai != null)
            {
                ai.canMove = was[1];
                ai.isStopped = was[2];
            }
            npc.activityDisabled = was[0];
        }
        catch (Exception e)
        {
            CoopLog.Once("NpcSync.Resume", e);
        }
    }

    private static void Release()
    {
        foreach (int id in new List<int>(stopped.Keys)) Resume(id);
        stopped.Clear();
        targets.Clear();
    }

    public static void Reset()
    {
        Release();
        npcs.Clear();
        sceneHandle = -1;
    }
}
