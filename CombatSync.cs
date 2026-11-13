using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

internal static class CombatSync
{
    public const string Channel = "hit";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Dictionary<string, MonoBehaviour> receivers = new Dictionary<string, MonoBehaviour>();
    private static readonly Dictionary<ulong, float[]> rates = new Dictionary<ulong, float[]>();
    private static int sceneHandle = -1;
    private static float nextReindex;
    private static int applying;
    public static int Patched;

    public static void Install()
    {
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".hit");
        HarmonyMethod postfix = new HarmonyMethod(typeof(CombatSync), "DamagePostfix");
        foreach (Type type in typeof(DamageReceiver).Assembly.GetTypes())
        {
            if (!typeof(IDamageReceiver).IsAssignableFrom(type) || !typeof(MonoBehaviour).IsAssignableFrom(type) || Excluded(type)) continue;
            MethodInfo m = type.GetMethod("Damage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, new[] { typeof(int), typeof(Vector3), typeof(Vector3), typeof(DamageReceiver.DamageType) }, null);
            if (m == null) continue;
            try
            {
                harmony.Patch(m, postfix: postfix);
                Patched++;
            }
            catch (Exception e)
            {
                CoopLog.Once("CombatSync.Install." + type.Name, e);
            }
        }
        CoopLog.Log.Info("coop: hit sync patched " + Patched + " Damage overrides");
    }

    internal static bool Excluded(Type type)
    {
        string n = type.Name;
        return n.IndexOf("SpawnItem", StringComparison.Ordinal) >= 0 || n.IndexOf("WorkableResource", StringComparison.Ordinal) >= 0;
    }

    public static void DamagePostfix(MonoBehaviour __instance, int damage, Vector3 direction, Vector3 point, DamageReceiver.DamageType type)
    {
        try
        {
            if (applying > 0 || __instance == null || damage <= 0) return;
            string guid = KeyOf(__instance);
            if (string.IsNullOrEmpty(guid)) return;
            string here = GameRefs.AreaId;
            if (!Session.IsInLobby || here == null) return;
            List<ulong> peers = Session.PeersInArea(here);
            if (peers.Count == 0) return;
            string text = here + "|" + guid + "|" + damage.ToString(Inv) + "|" + ((int)type).ToString(Inv) + "|" + V(direction) + "|" + V(point);
            if (text.Length > 300) return;
            for (int i = 0; i < peers.Count; i++) Session.Send(peers[i], Channel, text, true);
        }
        catch (Exception e)
        {
            CoopLog.Once("CombatSync.DamagePostfix", e);
        }
    }

    private static string V(Vector3 v)
    {
        return v.x.ToString("F2", Inv) + "," + v.y.ToString("F2", Inv) + "," + v.z.ToString("F2", Inv);
    }

    public static void Handle(ulong from, string text)
    {
        try
        {
            if (!Session.Peers.Contains(from) || text == null || text.Length > 300 || !Admit(from)) return;
            string[] f = text.Split('|');
            string area = Session.AreaOf(from);
            if (f.Length != 6 || f[0] != GameRefs.AreaId || (!string.IsNullOrEmpty(area) && area != f[0])) return;
            int damage, type;
            Vector3 dir, point;
            if (!int.TryParse(f[2], NumberStyles.Integer, Inv, out damage) || damage <= 0 || damage > 100000) return;
            if (!int.TryParse(f[3], NumberStyles.Integer, Inv, out type) || !Enum.IsDefined(typeof(DamageReceiver.DamageType), type)) return;
            if (!Vec(f[4], out dir) || !Vec(f[5], out point)) return;
            Index(false);
            MonoBehaviour target;
            if (!receivers.TryGetValue(f[1], out target) || target == null)
            {
                Index(true);
                if (!receivers.TryGetValue(f[1], out target) || target == null) return;
            }
            applying++;
            try
            {
                IDamageReceiver hit = Excluded(target.GetType()) ? null : target as IDamageReceiver;
                if (hit != null) hit.Damage(damage, dir, point, (DamageReceiver.DamageType)type);
            }
            finally
            {
                applying--;
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("CombatSync.Handle", e);
        }
    }

    internal static bool Vec(string s, out Vector3 v)
    {
        v = Vector3.zero;
        string[] c = s.Split(',');
        float x, y, z;
        if (c.Length != 3 || !float.TryParse(c[0], NumberStyles.Float, Inv, out x) || !float.TryParse(c[1], NumberStyles.Float, Inv, out y) || !float.TryParse(c[2], NumberStyles.Float, Inv, out z)) return false;
        if (float.IsNaN(x + y + z) || float.IsInfinity(x + y + z) || Mathf.Abs(x) > 100000f || Mathf.Abs(y) > 100000f || Mathf.Abs(z) > 100000f) return false;
        v = new Vector3(x, y, z);
        return true;
    }

    internal static bool Admit(ulong peer)
    {
        float now = Time.unscaledTime;
        float[] r;
        if (!rates.TryGetValue(peer, out r)) rates[peer] = r = new float[2];
        if (now - r[0] >= 1f)
        {
            r[0] = now;
            r[1] = 0f;
        }
        r[1] += 1f;
        return r[1] <= 20f;
    }

    private static void Index(bool unknown)
    {
        int handle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
        if (handle == sceneHandle && (!unknown || Time.unscaledTime < nextReindex)) return;
        sceneHandle = handle;
        nextReindex = Time.unscaledTime + 1f;
        receivers.Clear();
        foreach (MonoBehaviour r in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
        {
            if (!(r is IDamageReceiver)) continue;
            string g = r == null ? null : KeyOf(r);
            if (!string.IsNullOrEmpty(g)) receivers[g] = r;
        }
    }

    internal static string KeyOf(MonoBehaviour r)
    {
        string g = null;
        try { g = SaveController.GetObjectGUID(r); }
        catch (Exception) { }
        if (!string.IsNullOrEmpty(g)) return g;
        Transform t = r.transform;
        string path = "";
        int depth = 0;
        while (t != null && depth < 24)
        {
            int ordinal = 0;
            Transform parent = t.parent;
            if (parent != null)
            {
                for (int i = 0; i < parent.childCount && parent.GetChild(i) != t; i++)
                {
                    if (parent.GetChild(i).name == t.name) ordinal++;
                }
            }
            path = t.name + "#" + ordinal + "/" + path;
            t = parent;
            depth++;
        }
        path = "p:" + r.GetType().Name + ":" + path;
        return path.Length > 180 ? "p:" + Fnv(path).ToString("x16", Inv) : path.Replace("|", "_");
    }

    internal static ulong Fnv(string s)
    {
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < s.Length; i++)
        {
            h ^= s[i];
            h *= 1099511628211UL;
        }
        return h;
    }

    internal static MonoBehaviour Find(string key)
    {
        MonoBehaviour target;
        Index(false);
        if (receivers.TryGetValue(key, out target) && target != null) return target;
        Index(true);
        return receivers.TryGetValue(key, out target) ? target : null;
    }

    public static void Reset()
    {
        sceneHandle = -1;
        receivers.Clear();
    }
}
