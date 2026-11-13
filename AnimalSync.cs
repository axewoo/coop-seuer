using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using UnityEngine;

internal static class AnimalSync
{
    public const string Channel = "animal";
    private const float SendInterval = 0.2f;
    private const float Stale = 2f;
    private const float Smoothing = 10f;
    private const int PerPacket = 10;
    private static readonly Dictionary<string, DamageType> types = new Dictionary<string, DamageType>();
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Dictionary<string, Animal> animals = new Dictionary<string, Animal>();
    private static readonly Dictionary<string, Vector4> targets = new Dictionary<string, Vector4>();
    private static readonly Dictionary<string, float> seen = new Dictionary<string, float>();
    private static readonly Dictionary<string, bool[]> stopped = new Dictionary<string, bool[]>();
    private static readonly StringBuilder sb = new StringBuilder();
    private static int sceneHandle = -1;
    private static bool wasAuthority;
    private static float nextSend;
    private static float lastPacket;
    private static float nextScan;
    private static readonly Dictionary<string, Animal> hidden = new Dictionary<string, Animal>();

    public static void Install()
    {
        try
        {
            Harmony harmony = new Harmony(Plugin.PluginGuid + ".animal");
            harmony.Patch(AccessTools.Method(typeof(Animal), "OnHuntTargetDetected"), prefix: new HarmonyMethod(typeof(AnimalSync), "DetectPrefix"));
            harmony.Patch(AccessTools.Method(typeof(AnimalStats), "Damage"), prefix: new HarmonyMethod(typeof(AnimalSync), "HitPrefix"));
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("AnimalAttackTrigger"), "OnTriggerStay"), prefix: new HarmonyMethod(typeof(AnimalSync), "BitePrefix"));
            CoopLog.Log.Info("coop: animal sync hooks installed");
        }
        catch (Exception e)
        {
            CoopLog.Once("AnimalSync.Install", e);
        }
    }

    private static bool HostDriven(Animal a)
    {
        ulong auth = FreeRoam.AuthorityOf(GameRefs.AreaId);
        if (a == null || auth == 0UL || auth == Session.Me || Session.AreaOf(auth) != GameRefs.AreaId) return false;
        float at;
        return seen.TryGetValue(KeyOf(a), out at) && Time.unscaledTime - at < Stale;
    }

    public static bool DetectPrefix(Animal __instance, Transform target)
    {
        try
        {
            if (!HostDriven(__instance)) return true;
            Transform me = GameRefs.LocalPlayer;
            if (me != null && target != null && (target == me || target.IsChildOf(me))) Session.Send(FreeRoam.AuthorityOf(GameRefs.AreaId), Channel, "AHUNT|" + GameRefs.AreaId + "|" + KeyOf(__instance), true);
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("AnimalSync.DetectPrefix", e);
            return true;
        }
    }

    public static bool HitPrefix(AnimalStats __instance, DamageType damageType, float effectfactor)
    {
        try
        {
            if (__instance == null || damageType == null) return true;
            Animal a = __instance.GetComponent<Animal>();
            if (!HostDriven(a)) return true;
            Session.Send(FreeRoam.AuthorityOf(GameRefs.AreaId), Channel, "AHIT|" + GameRefs.AreaId + "|" + KeyOf(a) + "|" + Clean(damageType.name) + "|" + effectfactor.ToString("F3", Inv), true);
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("AnimalSync.HitPrefix", e);
            return true;
        }
    }

    public static bool BitePrefix(MonoBehaviour __instance)
    {
        try
        {
            return __instance == null || !HostDriven(__instance.GetComponentInParent<Animal>());
        }
        catch (Exception e)
        {
            CoopLog.Once("AnimalSync.BitePrefix", e);
            return true;
        }
    }

    public static void Bite(ulong peer, DamageType type, float factor)
    {
        if (type == null || !Session.Peers.Contains(peer)) return;
        Session.Send(peer, Channel, "ABITE|" + Clean(type.name) + "|" + factor.ToString("F3", Inv) + "|x", true);
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
            CoopLog.Once("AnimalSync.Tick", e);
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
        foreach (KeyValuePair<string, Animal> pair in animals)
        {
            Animal a = pair.Value;
            if (a == null || !a.isActiveAndEnabled) continue;
            Transform t = a.transform;
            if (count > 0) sb.Append(';');
            sb.Append(pair.Key).Append(',').Append(t.position.x.ToString("F2", Inv)).Append(',').Append(t.position.y.ToString("F2", Inv)).Append(',').Append(t.position.z.ToString("F2", Inv)).Append(',').Append(t.eulerAngles.y.ToString("F0", Inv));
            if (++count >= PerPacket)
            {
                Flush(peers, here);
                count = 0;
            }
        }
        if (count > 0) Flush(peers, here);
    }

    private static void Flush(List<ulong> peers, string here)
    {
        string text = "APOS|" + here + "|" + sb.ToString();
        sb.Length = 0;
        for (int i = 0; i < peers.Count; i++) Session.Send(peers[i], Channel, text, false);
    }

    private static void GuestTick(string here)
    {
        float now = Time.unscaledTime;
        ulong auth = FreeRoam.AuthorityOf(here);
        bool hostHere = auth != 0UL && auth != Session.Me && Session.AreaOf(auth) == here && now - lastPacket < Stale;
        float blend = 1f - Mathf.Exp(-Smoothing * Time.deltaTime);
        List<string> drop = null;
        foreach (KeyValuePair<string, Vector4> pair in targets)
        {
            Animal a;
            float at;
            seen.TryGetValue(pair.Key, out at);
            if (!animals.TryGetValue(pair.Key, out a) || a == null || !hostHere || now - at > Stale)
            {
                if (drop == null) drop = new List<string>();
                drop.Add(pair.Key);
                if (hostHere && a != null && now - at > Stale)
                {
                    hidden[pair.Key] = a;
                    a.gameObject.SetActive(false);
                }
                continue;
            }
            if (!stopped.ContainsKey(pair.Key)) Stop(pair.Key, a);
            Transform t = a.transform;
            Vector3 goal = new Vector3(pair.Value.x, pair.Value.y, pair.Value.z);
            t.position = (t.position - goal).sqrMagnitude > 36f ? goal : Vector3.Lerp(t.position, goal, blend);
            t.rotation = Quaternion.Euler(0f, Mathf.LerpAngle(t.eulerAngles.y, pair.Value.w, blend), 0f);
        }
        if (drop == null) return;
        for (int i = 0; i < drop.Count; i++)
        {
            targets.Remove(drop[i]);
            Resume(drop[i]);
        }
    }

    private static void Stop(string key, Animal a)
    {
        Pathfinding.IAstarAI ai = a.GetComponent<Pathfinding.IAstarAI>();
        stopped[key] = new[] { a.pathingDisabled, ai != null && ai.canMove };
        a.pathingDisabled = true;
        if (ai != null) ai.canMove = false;
    }

    private static void Resume(string key)
    {
        bool[] was;
        if (!stopped.TryGetValue(key, out was)) return;
        stopped.Remove(key);
        Animal a;
        if (!animals.TryGetValue(key, out a) || a == null) return;
        a.pathingDisabled = was[0];
        Pathfinding.IAstarAI ai = a.GetComponent<Pathfinding.IAstarAI>();
        if (ai != null) ai.canMove = was[1];
    }

    public static void Handle(ulong from, string text)
    {
        try
        {
            if (text == null || text.Length > 1000 || !Session.Peers.Contains(from)) return;
            string[] f = text.Split('|');
            if (f.Length < 3) return;
            ulong auth = FreeRoam.AuthorityOf(GameRefs.AreaId);
            bool mine = auth == Session.Me;
            if (f[0] == "APOS" && !mine && from == auth && f[1] == GameRefs.AreaId) Positions(f[2]);
            else if (f[0] == "ABITE" && !mine && from == auth && f.Length == 4) BiteMe(f[1], f[2]);
            else if (f[0] == "AHUNT" && mine && f[1] == GameRefs.AreaId && Session.AreaOf(from) == f[1] && f.Length == 3) Hunt(from, f[2]);
            else if (f[0] == "AHIT" && mine && f[1] == GameRefs.AreaId && Session.AreaOf(from) == f[1] && f.Length == 5 && CombatSync.Admit(from)) HitOnHost(f[2], f[3], f[4]);
        }
        catch (Exception e)
        {
            CoopLog.Once("AnimalSync.Handle", e);
        }
    }

    private static void Positions(string body)
    {
        float now = Time.unscaledTime;
        lastPacket = now;
        string[] items = body.Split(';');
        for (int i = 0; i < items.Length && i < PerPacket; i++)
        {
            string[] f = items[i].Split(',');
            float x, y, z, yaw;
            if (f.Length != 5 || !F(f[1], out x) || !F(f[2], out y) || !F(f[3], out z) || !F(f[4], out yaw)) continue;
            Animal back;
            if (hidden.TryGetValue(f[0], out back))
            {
                hidden.Remove(f[0]);
                if (back != null)
                {
                    back.gameObject.SetActive(true);
                    animals[f[0]] = back;
                }
            }
            if (!animals.ContainsKey(f[0])) Index(true);
            if (!animals.ContainsKey(f[0])) continue;
            targets[f[0]] = new Vector4(x, y, z, yaw);
            seen[f[0]] = now;
        }
    }

    private static void Hunt(ulong guest, string key)
    {
        Animal a;
        Transform shell = Puppet.ShellOf(guest);
        if (!animals.TryGetValue(key, out a) || a == null || shell == null) return;
        PuppetStats stats = shell.GetComponent<PuppetStats>();
        if (stats == null)
        {
            stats = shell.gameObject.AddComponent<PuppetStats>();
            stats.type = Stats.Type.Player;
            CapsuleCollider capsule = shell.gameObject.AddComponent<CapsuleCollider>();
            capsule.isTrigger = true;
            capsule.radius = 0.35f;
            capsule.height = 1.8f;
            capsule.center = new Vector3(0f, 0.9f, 0f);
            Rigidbody body = shell.gameObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
        }
        stats.Peer = guest;
        a.OnHuntTargetDetected(shell);
    }

    private static void HitOnHost(string key, string typeName, string factorText)
    {
        Animal a;
        float factor;
        DamageType type = TypeOf(typeName);
        if (!animals.TryGetValue(key, out a) || a == null || type == null || !float.TryParse(factorText, NumberStyles.Float, Inv, out factor) || factor <= 0f || factor > 100f) return;
        AnimalStats stats = a.GetComponent<AnimalStats>();
        if (stats != null) stats.Damage(type, factor);
    }

    private static void BiteMe(string typeName, string factorText)
    {
        float factor;
        DamageType type = TypeOf(typeName);
        Transform me = GameRefs.LocalPlayer;
        if (type == null || me == null || !float.TryParse(factorText, NumberStyles.Float, Inv, out factor) || factor <= 0f || factor > 100f) return;
        Stats mine = ((object)PlayerStats.instance) as Stats ?? me.GetComponentInChildren<Stats>();
        if (mine != null) mine.Damage(type, factor);
    }

    private static string KeyOf(Animal a)
    {
        string g = null;
        AnimalStats s = a == null ? null : a.GetComponent<AnimalStats>();
        try { if (s != null) g = SaveController.GetObjectGUID(s); }
        catch (Exception) { }
        if (string.IsNullOrEmpty(g)) g = a == null ? null : CombatSync.KeyOf(a);
        if (string.IsNullOrEmpty(g)) return null;
        return g.Length > 40 ? "h" + CombatSync.Fnv(g).ToString("x16", Inv) : g.Replace(",", "_").Replace(";", "_");
    }

    private static DamageType TypeOf(string name)
    {
        DamageType cached;
        if (types.TryGetValue(name, out cached) && cached != null) return cached;
        foreach (DamageType t in Resources.FindObjectsOfTypeAll<DamageType>())
        {
            if (t != null && t.name == name)
            {
                types[name] = t;
                return t;
            }
        }
        return null;
    }

    private static bool F(string s, out float v)
    {
        return float.TryParse(s, NumberStyles.Float, Inv, out v) && !float.IsNaN(v) && !float.IsInfinity(v) && Mathf.Abs(v) < 100000f;
    }

    private static string Clean(string s)
    {
        return string.IsNullOrEmpty(s) ? "" : s.Replace('|', '_').Replace(';', '_');
    }

    private static void Index(bool unknown)
    {
        int handle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
        bool changed = handle != sceneHandle;
        if (!changed && !unknown && Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 3f;
        if (changed)
        {
            Release();
            sceneHandle = handle;
        }
        animals.Clear();
        foreach (Animal a in UnityEngine.Object.FindObjectsByType<Animal>(FindObjectsSortMode.None))
        {
            string key = a == null ? null : KeyOf(a);
            if (!string.IsNullOrEmpty(key) && !animals.ContainsKey(key)) animals[key] = a;
        }
    }

    private static void Release()
    {
        foreach (string key in new List<string>(stopped.Keys)) Resume(key);
        targets.Clear();
        seen.Clear();
    }

    public static void Reset()
    {
        Release();
        animals.Clear();
        hidden.Clear();
        sceneHandle = -1;
    }
}

internal class PuppetStats : Stats
{
    public ulong Peer;

    public override void Damage(DamageType damageType, float effectfactor = 1f)
    {
        AnimalSync.Bite(Peer, damageType, effectfactor);
    }
}
