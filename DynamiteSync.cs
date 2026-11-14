using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

internal static class DynamiteSync
{
    public const string Channel = "dyn";
    private static readonly Dictionary<string, InteractableDynamiteCone> cones = new Dictionary<string, InteractableDynamiteCone>();
    private static readonly FieldInfo DynamiteField = AccessTools.Field(typeof(InteractableDynamiteCone), "dynamite");
    private static readonly FieldInfo ParticleField = AccessTools.Field(typeof(InteractableDynamiteCone), "particle");
    private static readonly FieldInfo RelayField = AccessTools.Field(typeof(InteractableDynamiteCone), "onDetonate");
    private static int sceneHandle = -1;
    private static float nextReindex;
    private static int applying;

    public static void Install()
    {
        try
        {
            Harmony harmony = new Harmony(Plugin.PluginGuid + ".dyn");
            harmony.Patch(AccessTools.Method(typeof(InteractableDynamiteCone), "PlaceDynamite"), prefix: new HarmonyMethod(typeof(DynamiteSync), "StatePrefix"), postfix: new HarmonyMethod(typeof(DynamiteSync), "StatePostfix"));
            harmony.Patch(AccessTools.Method(typeof(InteractableDynamiteCone), "RemoveDynamite"), prefix: new HarmonyMethod(typeof(DynamiteSync), "StatePrefix"), postfix: new HarmonyMethod(typeof(DynamiteSync), "StatePostfix"));
            harmony.Patch(AccessTools.Method(typeof(InteractableDynamiteCone), "Detonate"), prefix: new HarmonyMethod(typeof(DynamiteSync), "StatePrefix"), postfix: new HarmonyMethod(typeof(DynamiteSync), "BlastPostfix"));
            CoopLog.Log.Info("coop: dynamite sync hooks installed");
        }
        catch (Exception e)
        {
            CoopLog.Once("DynamiteSync.Install", e);
        }
    }

    public static void StatePrefix(InteractableDynamiteCone __instance, out bool __state)
    {
        __state = __instance != null && __instance.dynamitePlaced;
    }

    public static void StatePostfix(InteractableDynamiteCone __instance, bool __state)
    {
        if (applying > 0 || __instance == null || __instance.dynamitePlaced == __state) return;
        Send(__instance, __instance.dynamitePlaced ? "DY" : "DN");
    }

    public static void BlastPostfix(InteractableDynamiteCone __instance, bool __state)
    {
        if (applying > 0 || __instance == null || !__state || !__instance.detonated) return;
        Send(__instance, "DX");
    }

    private static void Send(InteractableDynamiteCone cone, string what)
    {
        try
        {
            string here = GameRefs.AreaId;
            if (!Session.IsInLobby || here == null) return;
            string key = Key(cone);
            if (key == null) return;
            List<ulong> peers = Session.PeersInArea(here);
            for (int i = 0; i < peers.Count; i++) SendTo(peers[i], here, key, what);
        }
        catch (Exception e)
        {
            CoopLog.Once("DynamiteSync.Send", e);
        }
    }

    private static void SendTo(ulong peer, string scene, string key, string what)
    {
        Session.Send(peer, Channel, scene + "|" + what + "|" + key, true);
    }

    public static void ManifestFor(ulong peer)
    {
        try
        {
            string here = GameRefs.AreaId;
            if (here == null || !Session.IsInLobby) return;
            Find("~");
            foreach (InteractableDynamiteCone cone in cones.Values)
            {
                if (cone == null) continue;
                string key = Key(cone);
                if (key == null) continue;
                SendTo(peer, here, key, cone.detonated ? "DX" : cone.dynamitePlaced ? "DY" : "DN");
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("DynamiteSync.ManifestFor", e);
        }
    }

    public static void Handle(ulong from, string text)
    {
        try
        {
            if (!Session.Peers.Contains(from) || text == null || text.Length > 200 || !CombatSync.Admit(from)) return;
            string[] f = text.Split('|');
            if (f.Length != 3 || f[0] != GameRefs.AreaId) return;
            InteractableDynamiteCone cone = Find(f[2]);
            if (cone == null) return;
            applying++;
            try
            {
                if (f[1] == "DY" || f[1] == "DN") SetPlaced(cone, f[1] == "DY");
                else if (f[1] == "DX") Blast(cone);
            }
            finally
            {
                applying--;
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("DynamiteSync.Handle", e);
        }
    }

    private static void SetPlaced(InteractableDynamiteCone cone, bool placed)
    {
        if (cone.detonated) return;
        cone.dynamitePlaced = placed;
        GameObject stick = DynamiteField == null ? null : DynamiteField.GetValue(cone) as GameObject;
        if (stick != null) stick.SetActive(placed);
    }

    private static void Blast(InteractableDynamiteCone cone)
    {
        if (cone.detonated) return;
        cone.dynamitePlaced = false;
        cone.detonated = true;
        cone.Detonated();
        ParticleSystem particle = ParticleField == null ? null : ParticleField.GetValue(cone) as ParticleSystem;
        if (particle != null) particle.Play();
        Relay relay = RelayField == null ? null : RelayField.GetValue(cone) as Relay;
        if (relay != null) relay.triggerOutputs();
        CoopLog.Log.Info("coop: dynamite blast from another player shown");
    }

    private static string Key(InteractableDynamiteCone cone)
    {
        string k = string.IsNullOrEmpty(cone.GUID) ? CombatSync.KeyOf(cone) : cone.GUID;
        if (string.IsNullOrEmpty(k)) return null;
        return k.Length > 60 ? "h" + CombatSync.Fnv(k).ToString("x16", System.Globalization.CultureInfo.InvariantCulture) : k.Replace("|", "_");
    }

    private static InteractableDynamiteCone Find(string key)
    {
        int handle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
        InteractableDynamiteCone cone;
        if (handle == sceneHandle && cones.TryGetValue(key, out cone) && cone != null) return cone;
        if (handle == sceneHandle && Time.unscaledTime < nextReindex) return null;
        sceneHandle = handle;
        nextReindex = Time.unscaledTime + 1f;
        cones.Clear();
        foreach (InteractableDynamiteCone c in UnityEngine.Object.FindObjectsByType<InteractableDynamiteCone>(FindObjectsSortMode.None))
        {
            string k = c == null ? null : Key(c);
            if (k != null) cones[k] = c;
        }
        return cones.TryGetValue(key, out cone) ? cone : null;
    }
}
