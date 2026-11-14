using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using ObenseuerCore;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static partial class InteractableSync
{
    public const string Channel = "interact";
    private const string BlobHead = "in";
    private const int MaxInline = 350;
    private const int MaxIncoming = 400;
    private const int MaxBlob = 262144;
    private const float PushAfter = 1f;
    private const float PendingTimeout = 10f;
    private const float RetryWindow = 10f;
    private const float IndexEvery = 5f;
    private const float MissEvery = 1f;
    private const float RequestGap = 0.5f;
    private const float HandoverGrace = 10f;
    private const int RatePerSecond = 10;
    private const float StopAfter = 0.15f;
    private const float UnknownGrace = 12f;
    private const float PruneEvery = 1f;

    private static readonly string[] Allow = { "Button", "SelectionButton", "Keypad", "Lantern", "EditableSign", "InteractableRelay", "BreakableLock" };
    private static readonly string[] Panel = { "SelectionButton", "Keypad", "EditableSign" };
    private static readonly string[] Stateful = { "Button", "SelectionButton", "Keypad", "Lantern", "EditableSign", "BreakableLock" };

    private static readonly Dictionary<string, Interactable> cache = new Dictionary<string, Interactable>();
    private static readonly Dictionary<string, Interactable> toPush = new Dictionary<string, Interactable>();
    private static readonly Dictionary<string, float> pushAt = new Dictionary<string, float>();
    private static readonly Dictionary<string, float> pushAgain = new Dictionary<string, float>();
    private static readonly Dictionary<string, Interactable> toStop = new Dictionary<string, Interactable>();
    private static readonly Dictionary<string, float> stopAt = new Dictionary<string, float>();
    private static readonly Dictionary<string, Type> resolved = new Dictionary<string, Type>();
    private static readonly Dictionary<string, string> unapplied = new Dictionary<string, string>();
    private static readonly Dictionary<string, float> unappliedUntil = new Dictionary<string, float>();
    private static readonly Dictionary<string, float> askedAt = new Dictionary<string, float>();
    private static readonly Dictionary<ulong, float[]> rates = new Dictionary<ulong, float[]>();
    private static readonly HashSet<string> serving = new HashSet<string>();
    private static int sceneHandle = -1;
    private static float nextIndex;
    private static float nextMiss;
    private static int reentry;
    private static float unknownSince;
    private static float nextPrune;
    private static string pendingKey;
    private static float pendingAt;
    private static bool installed;

    public static bool Install()
    {
        if (installed) return true;
        try
        {
            Type[] types = AssemblyTypes();
            if (types == null)
            {
                CoopLog.Once("InteractableSync.types", "coop: the game assembly types could not be read, interactable sync is off");
                return false;
            }
            Harmony harmony = new Harmony(Plugin.PluginGuid + ".interact");
            List<string> patched = new List<string>();
            for (int i = 0; i < Allow.Length; i++)
            {
                Type type = Resolve(types, Allow[i]);
                if (type != null) resolved[Allow[i]] = type;
                MethodBase original = type == null ? null : AccessTools.Method(type, "Interact", Type.EmptyTypes);
                if (type == null || original == null || original.DeclaringType != type)
                {
                    CoopLog.Once("InteractableSync." + Allow[i], "coop: " + Allow[i] + " declares no Interact of its own, its sync is off");
                    continue;
                }
                try
                {
                    harmony.Patch(original, prefix: new HarmonyMethod(typeof(InteractableSync), "InteractPrefix"), postfix: new HarmonyMethod(typeof(InteractableSync), "InteractPostfix"));
                    patched.Add(Allow[i]);
                }
                catch (Exception e)
                {
                    CoopLog.Once("InteractableSync.patch." + Allow[i], e);
                }
            }
            Guard();
            NetBlob.Received += OnBlob;
            installed = patched.Count != 0;
            CoopLog.Log.Info("coop: interactable hooks installed on " + patched.Count + " of " + Allow.Length + " types: " + string.Join(", ", patched.ToArray()));
            return installed;
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.Install", e);
            return false;
        }
    }

    private static Type[] AssemblyTypes()
    {
        try
        {
            return typeof(Interactable).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            CoopLog.Once("InteractableSync.assembly", e);
            return e.Types;
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.assembly", e);
            return null;
        }
    }

    private static Type Resolve(Type[] types, string name)
    {
        for (int i = 0; i < types.Length; i++)
        {
            Type type = types[i];
            if (type == null || type.Name != name) continue;
            if (typeof(Interactable).IsAssignableFrom(type)) return type;
        }
        return null;
    }

    private static bool Follower
    {
        get
        {
            if (!Session.IsInLobby || FreeRoam.IAmAuthority) return false;
            ulong auth = FreeRoam.Authority;
            return auth != 0UL && Session.AreaOf(auth) == GameRefs.AreaId;
        }
    }

    public static bool InteractPrefix(object __instance, out bool __state)
    {
        __state = false;
        try
        {
            Interactable target = __instance as Interactable;
            if (target == null || reentry > 0 || !Session.IsInLobby) return true;
            if (FreeRoam.IAmAuthority) return true;
            ulong authority = FreeRoam.Authority;
            if (authority == 0UL)
            {
                if (unknownSince == 0f) unknownSince = Time.unscaledTime;
                if (Time.unscaledTime - unknownSince < UnknownGrace)
                {
                    __state = true;
                    CoopNotice.Show("interact|owner", "Waiting for the scene host, try again in a moment", 3f);
                    return false;
                }
                return true;
            }
            unknownSince = 0f;
            if (target.IsInteractDisabled()) return true;
            if (!Follower) return true;
            __state = true;
            string key = Key(target);
            if (key == null)
            {
                CoopNotice.Show("interact|nokey", "This cannot be shared yet", 3f);
                return false;
            }
            if (Of(target, Panel))
            {
                CoopNotice.Show("interact|panel", "That cannot be used by a guest yet", 3f);
                return false;
            }
            string unsafeWhy = GuestReason(target);
            if (unsafeWhy != null)
            {
                CoopNotice.Show("interact|guest", unsafeWhy, 3f);
                return false;
            }
            float was;
            if (key == pendingKey && Time.unscaledTime - pendingAt < PendingTimeout) return false;
            if (askedAt.TryGetValue(key, out was) && Time.unscaledTime - was < RequestGap) return false;
            pendingKey = key;
            pendingAt = Time.unscaledTime;
            askedAt[key] = Time.unscaledTime;
            Session.Send(authority, Channel, GameRefs.AreaId + "|IREQ|" + key + "|" + target.GetType().Name, true);
            CoopNotice.Show("interact|wait", "Waiting for the host...", 2f);
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.InteractPrefix", e);
            return true;
        }
    }

    public static void InteractPostfix(object __instance, bool __state)
    {
        try
        {
            if (__state || !Session.IsInLobby || !FreeRoam.IAmAuthority) return;
            Interactable target = __instance as Interactable;
            if (target == null) return;
            string key = Key(target);
            if (key == null) return;
            if (!Of(target, Stateful))
            {
                Fired(target, key);
                return;
            }
            toPush[key] = target;
            pushAt[key] = Time.unscaledTime + PushAfter;
            float unpress = AutoUnpressOf(target);
            if (unpress > 0f) pushAgain[key] = Time.unscaledTime + PushAfter + unpress + 1f;
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.InteractPostfix", e);
        }
    }

    public static void Handle(ulong from, string text)
    {
        try
        {
            if (!Session.Peers.Contains(from) || text == null || text.Length > MaxIncoming) return;
            string[] f = text.Split('|');
            if (f.Length < 3) return;
            if (f[0] == "IREJ")
            {
                if (from != FreeRoam.Authority || f[1] != pendingKey) return;
                pendingKey = null;
                CoopNotice.Show("interact|no", f[2], 3f);
                return;
            }
            string here = GameRefs.AreaId;
            if (here == null || f[0] != here || f.Length < 4) return;
            if (f[1] == "IREQ")
            {
                if (!FreeRoam.IAmAuthority) return;
                string area = Session.AreaOf(from);
                if (area != null && area != here) return;
                Grant(from, f[2], f[3]);
                return;
            }
            if (f[1] == "IF")
            {
                if (from != FreeRoam.Authority) return;
                if (f[2] == pendingKey) pendingKey = null;
                if (FreeRoam.IAmAuthority) CoopLog.Log.Info("coop: the scene host fired the interactable relay " + f[2] + " for this machine");
                else CoopLog.Log.Debug("interact.relay", "the scene host fired the interactable relay " + f[2]);
                return;
            }
            if (f[1] == "IS" && f.Length == 5)
            {
                if (from != FreeRoam.Authority) return;
                if (f[2] == pendingKey) pendingKey = null;
                Receive(f[2], f[4]);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.Handle", e);
        }
    }

    private static void Grant(ulong from, string key, string type)
    {
        FreeRoam.WatchAuthority();
        if (!Admit(from))
        {
            Reject(from, key, "slow down");
            return;
        }
        string why = null;
        if (serving.Contains(key)) why = "Someone is using this";
        else if (SaveData.Busy || unapplied.Count != 0) why = "The scene is busy, try again";
        else if (Time.unscaledTime - FreeRoam.BecameAuthorityAt < HandoverGrace) why = "The scene is changing hands, try again in a moment";
        Interactable target = why == null ? Find(key) : null;
        if (why == null && target == null) why = "Not found";
        if (why == null && !TypeMatches(target, type)) why = "Not found";
        if (why == null && !Shareable(target)) why = "That cannot be used by a guest yet";
        if (why == null && Of(target, Panel)) why = "That cannot be used by a guest yet";
        if (why == null) why = GuestReason(target);
        if (why != null)
        {
            Reject(from, key, why);
            return;
        }
        serving.Add(key);
        reentry++;
        try
        {
            target.Interact();
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.Grant", e);
        }
        finally
        {
            reentry--;
            serving.Remove(key);
        }
        toStop[key] = target;
        stopAt[key] = Time.unscaledTime + StopAfter;
    }

    private static void ReleaseStops(float now)
    {
        foreach (KeyValuePair<string, float> pair in new Dictionary<string, float>(stopAt))
        {
            if (now < pair.Value) continue;
            Interactable target;
            if (toStop.TryGetValue(pair.Key, out target) && target != null)
            {
                try { target.StopInteract(false); }
                catch (Exception e) { CoopLog.Once("InteractableSync.StopInteract", e); }
            }
            toStop.Remove(pair.Key);
            stopAt.Remove(pair.Key);
        }
    }

    private static void Reject(ulong from, string key, string why)
    {
        Session.Send(from, Channel, "IREJ|" + key + "|" + why, true);
    }

    private static void Receive(string key, string b64)
    {
        try
        {
            if (FreeRoam.IAmAuthority) return;
            Interactable target = Find(key);
            if (target == null)
            {
                CoopLog.Once("InteractableSync.receive", "coop: an interactable state for " + key + " arrived but no object in the scene matched it");
                return;
            }
            if (!Of(target, Stateful)) return;
            string json = Decode(b64);
            if (json == null) return;
            if (SaveData.Busy || !Apply(target, key, json))
            {
                unapplied[key] = json;
                unappliedUntil[key] = Time.unscaledTime + RetryWindow;
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.Receive", e);
        }
    }

    private static void OnBlob(ulong peer, string modId, string name, byte[] data)
    {
        try
        {
            if (modId != Plugin.PluginGuid || name == null || !name.StartsWith(BlobHead, StringComparison.Ordinal) || data == null || data.Length == 0 || data.Length > MaxBlob) return;
            if (!Session.Peers.Contains(peer)) return;
            if (FreeRoam.IAmAuthority || peer != FreeRoam.Authority) return;
            Find("~");
            foreach (KeyValuePair<string, Interactable> pair in new Dictionary<string, Interactable>(cache))
            {
                if (BlobName(pair.Key) == name)
                {
                    Receive(pair.Key, Convert.ToBase64String(data));
                    return;
                }
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.OnBlob", e);
        }
    }

    public static void Tick()
    {
        try
        {
            OnSceneChanged();
            if (!Session.IsInLobby) return;
            float now = Time.unscaledTime;
            if (now >= nextLockScan) ScanLocks(now);
            if (toStop.Count != 0) ReleaseStops(now);
            if (pendingKey != null && now - pendingAt >= PendingTimeout)
            {
                pendingKey = null;
                CoopNotice.Show("interact|no", "The host did not answer, try again", 3f);
            }
            if (!FreeRoam.IAmAuthority)
            {
                if (toPush.Count != 0) ClearPush();
            }
            else if (toPush.Count != 0)
            {
                foreach (KeyValuePair<string, Interactable> pair in new Dictionary<string, Interactable>(toPush))
                {
                    Interactable target = pair.Value;
                    float at;
                    if (!pushAt.TryGetValue(pair.Key, out at)) at = 0f;
                    if (target == null)
                    {
                        Forget(pair.Key);
                        continue;
                    }
                    if (now < at) continue;
                    if (!Push(target, pair.Key, 0UL))
                    {
                        if (now >= at + RetryWindow)
                        {
                            Forget(pair.Key);
                            CoopLog.Log.Error("coop: the state of the interactable " + pair.Key + " could not be sent, the scene host keeps its own");
                        }
                        continue;
                    }
                    float again;
                    if (pushAgain.TryGetValue(pair.Key, out again) && now < again)
                    {
                        pushAt[pair.Key] = again;
                        pushAgain.Remove(pair.Key);
                        continue;
                    }
                    Forget(pair.Key);
                }
            }
            if (unapplied.Count != 0 && !SaveData.Busy && !FreeRoam.IAmAuthority)
            {
                foreach (KeyValuePair<string, string> pair in new Dictionary<string, string>(unapplied))
                {
                    Interactable target = Find(pair.Key);
                    float until;
                    if (!unappliedUntil.TryGetValue(pair.Key, out until)) until = 0f;
                    if (target != null && Apply(target, pair.Key, pair.Value))
                    {
                        unapplied.Remove(pair.Key);
                        unappliedUntil.Remove(pair.Key);
                        continue;
                    }
                    if (now < until) continue;
                    unapplied.Remove(pair.Key);
                    unappliedUntil.Remove(pair.Key);
                    CoopLog.Log.Error("coop: the interactable state of " + pair.Key + " could not be applied, the scene host keeps its own");
                }
            }
            if (rates.Count != 0 && now >= nextPrune)
            {
                nextPrune = now + PruneEvery;
                PruneRates();
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableSync.Tick", e);
        }
    }

    private static void Forget(string key)
    {
        toPush.Remove(key);
        pushAt.Remove(key);
        pushAgain.Remove(key);
    }

    private static void ClearPush()
    {
        toPush.Clear();
        pushAt.Clear();
        pushAgain.Clear();
    }

    private static void PruneRates()
    {
        if (rates.Count == 0) return;
        List<ulong> gone = null;
        foreach (KeyValuePair<ulong, float[]> pair in rates)
        {
            if (Session.Peers.Contains(pair.Key)) continue;
            if (gone == null) gone = new List<ulong>();
            gone.Add(pair.Key);
        }
        if (gone == null) return;
        for (int i = 0; i < gone.Count; i++) rates.Remove(gone[i]);
    }

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
        return r[1] <= RatePerSecond;
    }
}
