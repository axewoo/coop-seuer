using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using ObenseuerCore;
using UnityEngine;

internal static partial class LiquidSync
{
    public const string Channel = "liquid";
    private const string BlobHead = "lq";
    private static readonly Dictionary<string, ulong> held = new Dictionary<string, ulong>();
    private static readonly Dictionary<string, float> heldAt = new Dictionary<string, float>();
    private static readonly Dictionary<string, LiquidStorage> tanks = new Dictionary<string, LiquidStorage>();
    private static int sceneHandle = -1;
    private static float nextReindex;
    private static LiquidStorage pending;
    private static string pendingKey;
    private static float pendingAt;
    private static LiquidStorage open;
    private static string openKey;
    private static ulong leaseAuthority;
    private static string leaseScene;
    private static float touchAt;
    private static bool reentry;
    private static LiquidStorage hostOpen;
    private static readonly Dictionary<string, float> unsent = new Dictionary<string, float>();
    private static readonly Dictionary<string, List<string>> groups = new Dictionary<string, List<string>>();
    private static readonly Dictionary<string, string> unapplied = new Dictionary<string, string>();
    private static readonly Dictionary<string, string> baseline = new Dictionary<string, string>();
    private static readonly Dictionary<string, string> discarded = new Dictionary<string, string>();

    public static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(LiquidStorage), "ExitExternally"), postfix: new HarmonyMethod(typeof(LiquidSync), "ExitPostfix"));
        harmony.Patch(AccessTools.Method(typeof(LiquidStorage), "EnterExternally", new[] { typeof(bool) }), postfix: new HarmonyMethod(typeof(LiquidSync), "EnterPostfix"));
        NetBlob.Received += OnBlob;
    }

    private static bool Follower
    {
        get
        {
            if (!Session.IsInLobby || !GuestSession.InHostWorld || FreeRoam.IAmAuthority) return false;
            ulong auth = FreeRoam.Authority;
            return auth != 0UL && Session.AreaOf(auth) == GameRefs.AreaId;
        }
    }

    public static bool OpenPrefix(LiquidStorage tank)
    {
        try
        {
            if (tank == null || reentry || !Session.IsInLobby) return true;
            if (!CheckAuthority() || discarded.Count != 0) return false;
            string key = Key(tank);
            if (FreeRoam.IAmAuthority)
            {
                if (key == null) return true;
                List<string> group;
                string why = Group(tank, key, out group);
                if (why == null) why = Busy(group, false);
                if (why != null)
                {
                    CoopNotice.Show("liquid|busy", why, 3f);
                    return false;
                }
                return true;
            }
            if (key == null)
            {
                CoopNotice.Show("liquid|nokey", "This tank cannot be shared yet", 3f);
                return false;
            }
            if (GuestSession.InHostWorld && FreeRoam.Authority == 0UL)
            {
                CoopNotice.Show("liquid|owner", "Waiting to learn who runs this scene, try again in a moment", 3f);
                return false;
            }
            if (!Follower) return true;
            if (open == tank) return true;
            if (pending != null && Time.unscaledTime - pendingAt < 10f) return false;
            if (open != null) CloseRunning();
            if (unsent.Count != 0) return false;
            leaseAuthority = FreeRoam.Authority;
            leaseScene = GameRefs.AreaId;
            pending = tank;
            pendingKey = key;
            pendingAt = Time.unscaledTime;
            Session.Send(FreeRoam.Authority, Channel, "LQREQ|" + GameRefs.AreaId + "|" + key, true);
            CoopNotice.Show("liquid|wait", "Waiting for the host...", 2f);
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("LiquidSync.OpenPrefix", e);
            return false;
        }
    }

    public static void EnterPostfix(LiquidStorage __instance)
    {
        if (__instance != null && Session.IsInLobby && FreeRoam.IAmAuthority) hostOpen = __instance;
    }

    private static bool Holds(string key, out ulong holder)
    {
        return held.TryGetValue(key, out holder);
    }

    public static void ExitPostfix(LiquidStorage __instance)
    {
        try
        {
            if (__instance == null || !Session.IsInLobby) return;
            string key = Key(__instance);
            if (key == null) return;
            if (open == __instance)
            {
                open = null;
                openKey = null;
                unsent[key] = Time.unscaledTime + 30f;
                SendBack();
                return;
            }
            if (FreeRoam.IAmAuthority && hostOpen == __instance)
            {
                hostOpen = null;
                Push(__instance, key, 0UL);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("LiquidSync.ExitPostfix", e);
        }
    }

    private static void SendBack()
    {
        if (!CheckAuthority() || unsent.Count == 0) return;
        foreach (KeyValuePair<string, float> pair in new Dictionary<string, float>(unsent))
        {
            string key = pair.Key;
            LiquidStorage tank = Find(key);
            string json = tank == null ? null : Capture(tank, key);
            string packed = json == null ? null : Pack(tank, json);
            if (packed != null && NetBlob.Send(leaseAuthority, Plugin.PluginGuid, HeldName(key), Encoding.UTF8.GetBytes(packed)) >= 0)
            {
                Session.Send(leaseAuthority, Channel, "LQCLOSE|" + GameRefs.AreaId + "|" + key + "|1", true);
                unsent.Remove(key);
                baseline.Remove(key);
                leaseNames.Remove(key);
                continue;
            }
            if (Time.unscaledTime < pair.Value) continue;
            Session.Send(leaseAuthority, Channel, "LQCLOSE|" + GameRefs.AreaId + "|" + key + "|0", true);
            CoopLog.Log.Error("coop: the tank state of " + key + " could not be sent, the host keeps its own");
            string original;
            if (baseline.TryGetValue(key, out original)) discarded[key] = original;
            baseline.Remove(key);
            unsent.Remove(key);
            leaseNames.Remove(key);
        }
    }

    public static void Tick()
    {
        try
        {
            if (!Session.IsInLobby) return;
            RestoreDiscarded();
            if (!CheckAuthority() || discarded.Count != 0) return;
            SendBack();
            ResendTick();
            float now = Time.realtimeSinceStartup;
            if (open != null && (LiquidStorage.currentStorage != open || GameUIController.instance == null || !GameUIController.instance.IsMenuVisible)) ExitPostfix(open);
            if (open != null && openKey != null && now >= touchAt)
            {
                touchAt = now + 30f;
                Session.Send(leaseAuthority, Channel, "LQTOUCH|" + leaseScene + "|" + openKey, true);
            }
            if (pending != null && Time.unscaledTime - pendingAt >= 10f)
            {
                Session.Send(leaseAuthority, Channel, "LQCLOSE|" + leaseScene + "|" + pendingKey + "|0", true);
                pending = null;
                pendingKey = null;
                CoopNotice.Show("liquid|no", "The tank did not open, try again", 3f);
            }
            if (FreeRoam.IAmAuthority)
            {
                foreach (string key in new List<string>(groups.Keys))
                {
                    ulong holder;
                    float at;
                    if (unapplied.ContainsKey(key)) continue;
                    bool alive = Holds(key, out holder) && Session.Peers.Contains(holder) && Session.AreaOf(holder) == GameRefs.AreaId;
                    if (alive && (!heldAt.TryGetValue(key, out at) || now - at < 600f)) continue;
                    CoopLog.Log.Info("coop: liquid lease " + key + " released, " + (alive ? "idle for ten minutes" : "the holder left"));
                    Release(key);
                }
            }
            if (unapplied.Count == 0 || SaveData.Busy) return;
            foreach (KeyValuePair<string, string> pair in new Dictionary<string, string>(unapplied))
            {
                LiquidStorage tank = Find(pair.Key);
                if (tank == null || ApplyAll(tank, pair.Key, pair.Value))
                {
                    unapplied.Remove(pair.Key);
                    Release(pair.Key);
                }
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("LiquidSync.Tick", e);
        }
    }

    public static void Handle(ulong from, string text)
    {
        try
        {
            if (!Session.Peers.Contains(from) || text == null || text.Length > 300) return;
            string[] f = text.Split('|');
            if (f.Length < 3 || f[1] != GameRefs.AreaId) return;
            if (f[0] == "LQREQ" && FreeRoam.IAmAuthority && Session.AreaOf(from) == f[1]) Grant(from, f[2]);
            else if (f[0] == "LQCLOSE" && FreeRoam.IAmAuthority && f.Length == 4) Close(from, f[2], f[3] == "1");
            else if (f[0] == "LQTOUCH" && FreeRoam.IAmAuthority) Touch(from, f[2]);
            else if (f[0] == "LQNO" && from == FreeRoam.Authority && f[2] == pendingKey)
            {
                pending = null;
                pendingKey = null;
                CoopNotice.Show("liquid|no", f.Length > 3 ? f[3] : "Not available", 3f);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("LiquidSync.Handle", e);
        }
    }

    private static void Grant(ulong from, string key)
    {
        FreeRoam.WatchAuthority();
        LiquidStorage tank = Find(key);
        List<string> group;
        string why = discarded.Count != 0 ? "The tank state is settling, try again" : tank == null ? "Not found" : null;
        group = new List<string>();
        if (why == null) why = Group(tank, key, out group);
        if (why == null && Time.unscaledTime - FreeRoam.BecameAuthorityAt < 10f) why = "The scene is changing hands, try again in a moment";
        if (why == null) why = Busy(group, true);
        if (why != null)
        {
            Session.Send(from, Channel, "LQNO|" + GameRefs.AreaId + "|" + key + "|" + why, true);
            return;
        }
        string previous = LeaseId(key);
        string lockId = ContainerWire.NewLockId();
        leaseIds[key] = lockId;
        if (!Push(tank, key, from))
        {
            if (previous == null) leaseIds.Remove(key);
            else leaseIds[key] = previous;
            CoopLog.Log.Error("coop: the tank state of " + key + " could not be sent to " + from + ", the lease was not granted");
            Session.Send(from, Channel, "LQNO|" + GameRefs.AreaId + "|" + key + "|The tank state could not be sent, try again", true);
            return;
        }
        for (int i = 0; i < group.Count; i++)
        {
            held[group[i]] = from;
            heldAt[group[i]] = Time.realtimeSinceStartup;
        }
        groups[key] = group;
        if (CoopDebug.On("cont")) CoopDebug.Event("cont", key, "liquid lease granted to " + from + " under lock " + lockId);
    }

    private static void Close(ulong from, string key, bool hasState)
    {
        ulong holder;
        if (!held.TryGetValue(key, out holder) || holder != from) return;
        Touch(from, key);
        if (!hasState) Release(key);
    }

    private static bool HostUses(LiquidStorage t)
    {
        LiquidStorage mine = hostOpen;
        if (mine == null) return false;
        List<LiquidStorage> linked = Linked(mine);
        return linked.Contains(t);
    }

    private static void Release(string key)
    {
        List<string> group;
        if (!groups.TryGetValue(key, out group)) group = new List<string> { key };
        groups.Remove(key);
        for (int i = 0; i < group.Count; i++)
        {
            held.Remove(group[i]);
            heldAt.Remove(group[i]);
        }
        leaseIds.Remove(key);
        LiquidStorage tank = Find(key);
        if (tank != null) Push(tank, key, 0UL);
    }

    private static bool Push(LiquidStorage tank, string key, ulong only)
    {
        List<ulong> peers = only != 0UL ? new List<ulong> { only } : Session.PeersInArea(GameRefs.AreaId);
        bool refused;
        return PushState(tank, key, peers, only != 0UL, out refused);
    }

    private static bool PushState(LiquidStorage tank, string key, List<ulong> peers, bool lease, out bool refused)
    {
        refused = false;
        string json = Capture(tank, key);
        if (json == null) return false;
        string packed = Pack(tank, json);
        if (packed == null) return false;
        byte[] data = Encoding.UTF8.GetBytes(packed);
        bool ok = true;
        string name = lease ? LeaseName(key) : BlobName(key);
        for (int i = 0; i < peers.Count; i++)
        {
            ulong holder;
            if (!lease && Holds(key, out holder) && holder == peers[i]) continue;
            if (NetBlob.Send(peers[i], Plugin.PluginGuid, name, data) < 0)
            {
                ok = false;
                refused = true;
            }
        }
        return ok;
    }

    private static void OnBlob(ulong peer, string modId, string name, byte[] data)
    {
        try
        {
            if (modId != Plugin.PluginGuid || name == null || !name.StartsWith(BlobHead, StringComparison.Ordinal) || data == null || data.Length > 1048576) return;
            RestoreDiscarded();
            if (!CheckAuthority() || discarded.Count != 0) return;
            string json = Encoding.UTF8.GetString(data);
            if (FreeRoam.IAmAuthority)
            {
                foreach (KeyValuePair<string, ulong> pair in new Dictionary<string, ulong>(held))
                {
                    if (pair.Value != peer || LeaseName(pair.Key) != name) continue;
                    Touch(peer, pair.Key);
                    LiquidStorage tank = Find(pair.Key);
                    if (tank != null && !ApplyAll(tank, pair.Key, json))
                    {
                        unapplied[pair.Key] = json;
                        return;
                    }
                    Release(pair.Key);
                    return;
                }
                StaleLease(peer, name);
                return;
            }
            if (peer != FreeRoam.Authority) return;
            if (pending == null && openKey != null && HeldName(openKey) == name) return;
            if (pending != null && IsLeaseNameOf(name, pendingKey))
            {
                LiquidStorage tank = pending;
                string key = pendingKey;
                pending = null;
                pendingKey = null;
                leaseNames[key] = name;
                if (!ApplyAll(tank, key, json))
                {
                    Session.Send(leaseAuthority, Channel, "LQCLOSE|" + GameRefs.AreaId + "|" + key + "|0", true);
                    return;
                }
                baseline[key] = json;
                open = tank;
                openKey = key;
                touchAt = 0f;
                reentry = true;
                try { tank.UseStorage(); }
                finally { reentry = false; }
                return;
            }
            Find("~");
            foreach (KeyValuePair<string, LiquidStorage> pair in tanks)
            {
                if (BlobName(pair.Key) != name || pair.Value == null || pair.Value == open) continue;
                if (pair.Key == pendingKey || pair.Key == openKey || unsent.ContainsKey(pair.Key)) continue;
                string current = Pack(pair.Value, pair.Key);
                if (current == null || current != json) ApplyAll(pair.Value, pair.Key, json);
                return;
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("LiquidSync.OnBlob", e);
        }
    }

    private static List<LiquidStorage> Linked(LiquidStorage tank)
    {
        List<LiquidStorage> list = new List<LiquidStorage>();
        LiquidStorage input = tank.ExternalInputStorage;
        LiquidStorage output = tank.ExternalOutputStorage;
        if (input != null && input != tank) list.Add(input);
        if (output != null && output != tank && !list.Contains(output)) list.Add(output);
        return list;
    }

    private static string Pack(LiquidStorage tank, string json)
    {
        StringBuilder sb = new StringBuilder(json);
        if (tank == null) return json;
        List<LiquidStorage> linked = Linked(tank);
        for (int i = 0; i < linked.Count; i++)
        {
            string k = Key(linked[i]);
            string part = k == null ? null : Capture(linked[i], k);
            if (part == null) return null;
            sb.Append('\u0002').Append(k).Append('\u0003').Append(part);
        }
        return sb.ToString();
    }

    private static bool ApplyAll(LiquidStorage tank, string key, string packed)
    {
        string[] parts = packed.Split('\u0002');
        if (!Apply(tank, key, parts[0])) return false;
        for (int i = 1; i < parts.Length; i++)
        {
            int sep = parts[i].IndexOf('\u0003');
            if (sep <= 0) return false;
            string k = parts[i].Substring(0, sep);
            LiquidStorage other = Find(k);
            if (other == null || !Apply(other, k, parts[i].Substring(sep + 1)))
            {
                CoopLog.Log.Error("coop: the linked tank " + k + " was refused, retrying");
                return false;
            }
        }
        return true;
    }

    private static string BlobName(string key)
    {
        return BlobHead + CombatSync.Fnv(key ?? "").ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Surrogate(string key)
    {
        return "coop-" + CombatSync.Fnv(key).ToString("x16", System.Globalization.CultureInfo.InvariantCulture) + "-liquid";
    }

    private static string Capture(LiquidStorage tank, string key)
    {
        string real = SaveController.GetObjectGUID(tank);
        SaveController.SetObjectGUID(tank, Surrogate(key));
        try { return SaveData.Capture(tank); }
        finally { SaveController.SetObjectGUID(tank, real); }
    }

    private static bool Apply(LiquidStorage tank, string key, string json)
    {
        string real = SaveController.GetObjectGUID(tank);
        SaveController.SetObjectGUID(tank, Surrogate(key));
        try { return SaveData.Apply(tank, json); }
        finally { SaveController.SetObjectGUID(tank, real); }
    }

    private static string Key(LiquidStorage tank)
    {
        try
        {
            if (tank == null) return null;
            string k = null;
            GameObject piece = ContainerWire.PieceOf(tank);
            string id = piece == null ? null : ContainerWire.IdOf(piece);
            if (id != null) k = "p" + id;
            if (string.IsNullOrEmpty(k)) k = CombatSync.KeyOf(tank);
            if (string.IsNullOrEmpty(k)) return null;
            return k.Length > 60 ? "h" + CombatSync.Fnv(k).ToString("x16", System.Globalization.CultureInfo.InvariantCulture) : k.Replace("|", "_");
        }
        catch (Exception e)
        {
            CoopLog.Once("LiquidSync.Key", e);
            return null;
        }
    }

    private static LiquidStorage Find(string key)
    {
        int handle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
        LiquidStorage tank;
        if (handle == sceneHandle && tanks.TryGetValue(key, out tank) && tank != null) return tank;
        if (handle == sceneHandle && Time.unscaledTime < nextReindex) return null;
        Reindex();
        return tanks.TryGetValue(key, out tank) ? tank : null;
    }

    private static void Reindex()
    {
        sceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
        nextReindex = Time.unscaledTime + 1f;
        tanks.Clear();
        foreach (LiquidStorage t in UnityEngine.Object.FindObjectsByType<LiquidStorage>(FindObjectsSortMode.None))
        {
            string k = t == null ? null : Key(t);
            if (k != null) tanks[k] = t;
        }
    }

    private static string Group(LiquidStorage tank, string key, out List<string> group)
    {
        group = new List<string> { key };
        List<LiquidStorage> linked = Linked(tank);
        for (int i = 0; i < linked.Count; i++)
        {
            string k = Key(linked[i]);
            if (k == null) return "This tank cannot be shared";
            if (!group.Contains(k)) group.Add(k);
        }
        return null;
    }

    private static string Busy(List<string> group, bool guest)
    {
        for (int i = 0; i < group.Count; i++)
        {
            ulong holder;
            if (Holds(group[i], out holder)) return Session.DisplayName(holder) + " is using this";
            LiquidStorage t = Find(group[i]);
            if (unapplied.ContainsKey(group[i]) || (guest && t != null && (hostOpen == t || HostUses(t)))) return "The host is using this";
        }
        return null;
    }

    private static void Touch(ulong from, string key)
    {
        ulong holder;
        List<string> group;
        if (!held.TryGetValue(key, out holder) || holder != from || !groups.TryGetValue(key, out group)) return;
        for (int i = 0; i < group.Count; i++) heldAt[group[i]] = Time.realtimeSinceStartup;
    }

    public static void CloseRunning()
    {
        if (!CheckAuthority()) return;
        if (pendingKey != null) Session.Send(leaseAuthority, Channel, "LQCLOSE|" + leaseScene + "|" + pendingKey + "|0", true);
        pending = null;
        pendingKey = null;
        LiquidStorage running = open;
        if (running != null)
        {
            ClosePanel(running);
            CoopNotice.Show("liquid|handover", "The tank was closed for the scene handover", 4f);
        }
        SendBack();
    }

    private static void ClosePanel(LiquidStorage running)
    {
        bool visible = LiquidStorage.currentStorage == running && GameUIController.instance != null && GameUIController.instance.IsMenuVisible;
        running.ExitExternally();
        if (visible) GameUIController.instance.HideGameMenu();
    }

    private static bool CheckAuthority()
    {
        if (leaseAuthority == 0UL || (leaseAuthority == FreeRoam.Authority && leaseScene == GameRefs.AreaId)) return true;
        AuthorityChanged();
        return false;
    }

    public static void AuthorityChanged()
    {
        bool lost = open != null || pending != null || unsent.Count != 0;
        LiquidStorage running = open != null ? open : hostOpen;
        open = null;
        openKey = null;
        pending = null;
        pendingKey = null;
        unsent.Clear();
        resendPending.Clear();
        resendWaiting.Clear();
        if (leaseScene == GameRefs.AreaId)
        {
            foreach (KeyValuePair<string, string> pair in baseline) discarded[pair.Key] = pair.Value;
        }
        baseline.Clear();
        leaseAuthority = 0UL;
        leaseScene = null;
        hostOpen = null;
        if (running != null) ClosePanel(running);
        RestoreDiscarded();
        held.Clear();
        heldAt.Clear();
        groups.Clear();
        unapplied.Clear();
        leaseIds.Clear();
        leaseNames.Clear();
        if (lost) CoopNotice.Show("liquid|handover", "The scene authority changed, unsent tank changes were discarded", 5f);
    }

    private static void RestoreDiscarded()
    {
        if (SaveData.Busy || discarded.Count == 0) return;
        foreach (KeyValuePair<string, string> pair in new Dictionary<string, string>(discarded))
        {
            LiquidStorage tank = Find(pair.Key);
            if (tank == null || ApplyAll(tank, pair.Key, pair.Value)) discarded.Remove(pair.Key);
        }
    }

    public static void Reset()
    {
        held.Clear();
        heldAt.Clear();
        tanks.Clear();
        baseline.Clear();
        discarded.Clear();
        sceneHandle = -1;
        leaseAuthority = 0UL;
        leaseScene = null;
        pending = null;
        pendingKey = null;
        open = null;
        openKey = null;
        hostOpen = null;
        unsent.Clear();
        groups.Clear();
        unapplied.Clear();
        leaseIds.Clear();
        leaseNames.Clear();
        resendPending.Clear();
        resendWaiting.Clear();
    }
}
