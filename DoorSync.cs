using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using ObenseuerCore;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static partial class DoorSync
{
    public const string Channel = "door";
    private const int MaxText = 900;
    private const float SceneGrace = 5f;
    private const int MaxDeferred = 64;
    private static readonly Dictionary<string, Door> doors = new Dictionary<string, Door>();
    private static readonly Dictionary<string, LightTurnOffOn> lights = new Dictionary<string, LightTurnOffOn>();
    private static readonly Dictionary<string, DoorChangelevel> changelevels = new Dictionary<string, DoorChangelevel>();
    private static readonly Dictionary<string, Door> deferred = new Dictionary<string, Door>();
    private static readonly HashSet<string> collided = new HashSet<string>();
    internal static int Indexed { get { return doors.Count; } }
    private static int sceneHandle = -1;
    private static int sendScene = int.MinValue;
    private static float sendSceneAt;
    private static float nextForce;
    private static int applying;
    public static bool Ready;

    public static void Install()
    {
        try
        {
            Harmony harmony = new Harmony(Plugin.PluginGuid + ".door");
            harmony.Patch(AccessTools.Method(typeof(Door), "OnOpen"), postfix: new HarmonyMethod(typeof(DoorSync), "OpenPostfix"));
            harmony.Patch(AccessTools.Method(typeof(Door), "Close"), prefix: new HarmonyMethod(typeof(DoorSync), "ClosePrefix"), postfix: new HarmonyMethod(typeof(DoorSync), "ClosePostfix"));
            harmony.Patch(AccessTools.Method(typeof(Door), "ToggleInterrupt"), prefix: new HarmonyMethod(typeof(DoorSync), "InterruptPrefix"), postfix: new HarmonyMethod(typeof(DoorSync), "InterruptPostfix"));
            harmony.Patch(AccessTools.Method(typeof(Door), "Lock"), postfix: new HarmonyMethod(typeof(DoorSync), "LockPostfix"));
            harmony.Patch(AccessTools.Method(typeof(Door), "Unlock"), postfix: new HarmonyMethod(typeof(DoorSync), "UnlockPostfix"));
            harmony.Patch(AccessTools.Method(typeof(LightTurnOffOn), "turnLightOn"), postfix: new HarmonyMethod(typeof(DoorSync), "LightOnPostfix"));
            harmony.Patch(AccessTools.Method(typeof(LightTurnOffOn), "turnLightOff"), postfix: new HarmonyMethod(typeof(DoorSync), "LightOffPostfix"));
            Ready = true;
            CoopLog.Log.Info("coop: door and light sync hooks installed");
        }
        catch (Exception e)
        {
            CoopLog.Once("DoorSync.Install", e);
        }
    }

    public static void OpenPostfix(Door __instance)
    {
        Send(__instance, "DO|" + (Reversed(__instance) ? "2" : "1"));
    }

    public static void ClosePrefix(Door __instance, out bool __state)
    {
        __state = __instance != null && __instance.DoorIsOpenOrAjar();
    }

    public static void ClosePostfix(Door __instance, bool __state)
    {
        if (__state) Send(__instance, "DO|0");
    }

    internal sealed class InterruptState
    {
        public bool Moving;
        public bool Direction;
        public float Position;
    }

    public static void InterruptPrefix(Door __instance, out InterruptState __state)
    {
        __state = null;
        if (__instance == null) return;
        InterruptState state = new InterruptState();
        state.Moving = __instance.moving;
        state.Direction = DirectionOf(__instance);
        state.Position = PositionOf(__instance);
        __state = state;
    }

    public static void InterruptPostfix(Door __instance, InterruptState __state)
    {
        if (__instance == null || __state == null) return;
        if (!__instance.moving) return;
        bool direction = DirectionOf(__instance);
        bool flipped = !__state.Moving || direction != __state.Direction || PositionOf(__instance) != __state.Position;
        if (!flipped) return;
        if (direction && __instance.Locked && !__instance.doorIsOpen) return;
        Send(__instance, direction ? "DO|" + (Reversed(__instance) ? "2" : "1") : "DO|0");
    }

    private static readonly System.Reflection.FieldInfo DirectionField = AccessTools.Field(typeof(Door), "direction");
    private static readonly System.Reflection.FieldInfo PositionField = AccessTools.Field(typeof(Door), "position");

    private static bool DirectionOf(Door door)
    {
        object v = DirectionField == null ? null : DirectionField.GetValue(door);
        return v is bool && (bool)v;
    }

    private static float PositionOf(Door door)
    {
        object v = PositionField == null ? null : PositionField.GetValue(door);
        return v is float ? (float)v : (door.DoorIsOpenOrAjar() ? 1f : 0f);
    }

    public static void LockPostfix(Door __instance) { Send(__instance, "DL|1"); }
    public static void UnlockPostfix(Door __instance) { Send(__instance, "DL|0"); }
    public static void LightOnPostfix(LightTurnOffOn __instance) { SendLight(__instance, "1"); }
    public static void LightOffPostfix(LightTurnOffOn __instance) { SendLight(__instance, "0"); }

    private static bool Reversed(Door door)
    {
        try
        {
            object v = AccessTools.Field(typeof(Door), "ReversedDir").GetValue(door);
            return v is bool && (bool)v;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool SceneGraceActive()
    {
        int handle = SceneManager.GetActiveScene().handle;
        if (handle != sendScene)
        {
            sendScene = handle;
            sendSceneAt = Time.unscaledTime;
            return true;
        }
        return Time.unscaledTime < sendSceneAt + SceneGrace;
    }

    private static void Send(Door door, string what)
    {
        if (applying > 0 || door == null) return;
        if (!Session.IsInLobby || SaveData.Busy) return;
        string dk;
        try
        {
            dk = Key(door);
        }
        catch (Exception e)
        {
            CoopLog.Once("DoorSync.Send", e);
            return;
        }
        if (string.IsNullOrEmpty(dk)) return;
        if (SceneGraceActive())
        {
            if (deferred.Count < MaxDeferred) deferred[dk] = door;
            return;
        }
        SendNow(dk, what);
    }

    private static void SendNow(string key, string what)
    {
        if (CoopDebug.On("door")) CoopDebug.Event("door", "send." + key, "sent " + what + " for " + key + " in " + GameRefs.AreaId);
        Broadcast(what.Substring(0, 2) + "|" + key + what.Substring(2));
    }

    private static void FlushDeferred()
    {
        if (deferred.Count == 0) return;
        if (!Session.IsInLobby || SaveData.Busy) return;
        if (SceneGraceActive()) return;
        List<string> keys = new List<string>(deferred.Keys);
        for (int i = 0; i < keys.Count; i++)
        {
            Door door;
            if (!deferred.TryGetValue(keys[i], out door)) continue;
            deferred.Remove(keys[i]);
            if (door == null) continue;
            try
            {
                SendNow(keys[i], PositionOf(door) > 0.02f ? (Reversed(door) ? "DO|2" : "DO|1") : "DO|0");
                SendNow(keys[i], door.Locked ? "DL|1" : "DL|0");
            }
            catch (Exception e)
            {
                CoopLog.Once("DoorSync.FlushDeferred", e);
            }
        }
    }

    private static void SendLight(LightTurnOffOn light, string on)
    {
        if (applying > 0 || light == null) return;
        if (!Session.IsInLobby || SaveData.Busy) return;
        string lk;
        try
        {
            lk = Key(light);
        }
        catch (Exception e)
        {
            CoopLog.Once("DoorSync.SendLight", e);
            return;
        }
        if (string.IsNullOrEmpty(lk)) return;
        if (SceneGraceActive()) return;
        if (CoopDebug.On("door")) CoopDebug.Event("door", "send." + lk, "sent LT " + on + " for " + lk + " in " + GameRefs.AreaId);
        Broadcast("LT|" + lk + "|" + on);
    }

    private static void Broadcast(string body)
    {
        try
        {
            string here = GameRefs.AreaId;
            if (!Session.IsInLobby || here == null) return;
            string text = here + "|" + body;
            if (Encoding.UTF8.GetByteCount(text) > MaxText) return;
            ulong authority = FreeRoam.Authority;
            if (authority == 0UL || authority == Session.Me || Session.IsHost)
            {
                List<ulong> peers = Session.PeersInArea(here);
                for (int i = 0; i < peers.Count; i++) Session.Send(peers[i], Channel, text, true);
                return;
            }
            if (Session.Peers.Contains(authority)) Session.Send(authority, Channel, text, true);
        }
        catch (Exception e)
        {
            CoopLog.Once("DoorSync.Broadcast", e);
        }
    }

    private static void Relay(ulong from, string text)
    {
        try
        {
            if (!FreeRoam.IAmAuthority) return;
            List<ulong> peers = Session.PeersInArea(GameRefs.AreaId);
            if (CoopDebug.On("door")) CoopDebug.Event("door", "relay", "relaying " + text + " to " + peers.Count + " peers in " + GameRefs.AreaId);
            for (int i = 0; i < peers.Count; i++)
            {
                if (peers[i] == from) continue;
                Session.Send(peers[i], Channel, text, true);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("DoorSync.Relay", e);
        }
    }

    public static void Handle(ulong from, string text)
    {
        try
        {
            if (!Session.Peers.Contains(from) || text == null || Encoding.UTF8.GetByteCount(text) > MaxText) return;
            string[] f = text.Split('|');
            if (f.Length < 3 || f[0] != GameRefs.AreaId) return;
            string area = Session.AreaOf(from);
            if (!string.IsNullOrEmpty(area) && area != f[0]) return;
            if (from == Session.Me) return;
            ulong authority = FreeRoam.Authority;
            bool batch = f[1] == "DB";
            if (!batch && (authority == 0UL || (from != authority && !FreeRoam.IAmAuthority)))
            {
                if (CoopDebug.On("door")) CoopDebug.Event("door", "stale." + from, "dropped " + f[1] + " from " + from + ", the authority of " + f[0] + " is " + authority);
                return;
            }
            if (batch && from != authority) return;
            if (!batch && !Admit(from)) return;
            if (batch) ApplyBatch(f);
            else if (f.Length == 4) ApplyOne(f);
            else return;
            if (from != Session.Me && !batch) Relay(from, text);
        }
        catch (Exception e)
        {
            CoopLog.Once("DoorSync.Handle", e);
        }
    }

    private static void ApplyOne(string[] f)
    {
        Index(false);
        if (!doors.ContainsKey(f[2]) && !lights.ContainsKey(f[2])) Index(true);
        if (CoopDebug.On("door")) CoopDebug.Event("door", "apply." + f[2], "applied " + f[1] + " " + f[3] + " to " + f[2] + " in " + f[0]);
        applying++;
        try
        {
            if (f[1] == "LT")
            {
                LightTurnOffOn light;
                if (!lights.TryGetValue(f[2], out light) || light == null) return;
                if (f[3] == "1" && !light.on) light.turnLightOn();
                else if (f[3] == "0" && light.on) light.turnLightOff();
                return;
            }
            Door door;
            if (!doors.TryGetValue(f[2], out door) || door == null) return;
            if (f[1] == "DO")
            {
                bool wantOpen = f[3] != "0";
                if (MovingTowards(door, wantOpen)) return;
                float position = PositionOf(door);
                if (!wantOpen && position > 0f) door.Close();
                else if (wantOpen && position < 1f)
                {
                    if (f[3] == "2") door.ForceOpenOpposite();
                    else door.ForceOpen();
                }
            }
            else if (f[1] == "DL")
            {
                if (f[3] == "1" && !door.Locked) door.Lock();
                else if (f[3] == "0" && door.Locked) door.Unlock();
            }
        }
        finally
        {
            applying--;
        }
    }

    private static bool MovingTowards(Door door, bool open)
    {
        return door.moving && DirectionOf(door) == open;
    }

    private static string Key(MonoBehaviour m)
    {
        string k = CombatSync.KeyOf(m);
        if (string.IsNullOrEmpty(k)) return null;
        return k.Length > 60 ? "h" + CombatSync.Fnv(k).ToString("x16", CultureInfo.InvariantCulture) : k.Replace("|", "_");
    }

    private static readonly Dictionary<ulong, float[]> rates = new Dictionary<ulong, float[]>();

    private static bool Admit(ulong peer)
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
        return r[1] <= 30f;
    }

    private static readonly Dictionary<string, int> counts = new Dictionary<string, int>();
    private static readonly List<string> keys = new List<string>();

    private static void Index(bool force)
    {
        int handle = SceneManager.GetActiveScene().handle;
        float now = Time.unscaledTime;
        if (handle == sceneHandle && (!force || now < nextForce)) return;
        if (handle != sceneHandle)
        {
            sentCodes.Clear();
            nextFull = 0f;
        }
        sceneHandle = handle;
        nextForce = now + 1f;
        doors.Clear();
        lights.Clear();
        changelevels.Clear();
        scanned.Clear();
        scannedByKey.Clear();
        counts.Clear();
        keys.Clear();
        foreach (Door d in UnityEngine.Object.FindObjectsByType<Door>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (d == null || d.gameObject.scene.handle != handle || !Syncable(d)) continue;
            string dk = Key(d);
            if (string.IsNullOrEmpty(dk)) continue;
            Count(counts, keys, dk);
            doors[dk] = d;
            Track(dk, d, null, null);
        }
        foreach (LightTurnOffOn l in UnityEngine.Object.FindObjectsByType<LightTurnOffOn>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (l == null || l.gameObject.scene.handle != handle || !Syncable(l)) continue;
            string lk = Key(l);
            if (string.IsNullOrEmpty(lk)) continue;
            Count(counts, keys, lk);
            lights[lk] = l;
            Track(lk, null, l, null);
        }
        foreach (DoorChangelevel c in UnityEngine.Object.FindObjectsByType<DoorChangelevel>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (c == null || c.DontSyncLocks || c.gameObject.scene.handle != handle || !Syncable(c)) continue;
            string ck = Key(c);
            if (string.IsNullOrEmpty(ck)) continue;
            Count(counts, keys, ck);
            changelevels[ck] = c;
            Track(ck, null, null, c);
        }
        Drop(keys, counts);
        PruneSent();
    }

    public static void Reset()
    {
        sceneHandle = -1;
        sendScene = int.MinValue;
        nextForce = 0f;
        doors.Clear();
        lights.Clear();
        changelevels.Clear();
        deferred.Clear();
        ResetScan();
    }

    public static void OnSceneChanged() { Reset(); }
}
