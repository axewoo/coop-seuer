using System;
using System.Collections.Generic;
using System.Reflection;
using ObenseuerCore;
using UnityEngine;

internal static partial class ContainerHost
{
    private const float IdleSeconds = 600f;
    private const float CloseGrace = 60f;
    private const float BusyGrace = 120f;
    private const float NoticeQuiet = 2f;
    private const int MaxLocks = 64;
    private static readonly Dictionary<string, Held> locks = new Dictionary<string, Held>();
    private static readonly List<string> stale = new List<string>();
    private static FieldInfo idsField;
    private static bool idsTried;
    private static float noticeAt;
    private static string noticeText = "";
    private static string hostOpenKey;

    private sealed class Held
    {
        public string Key;
        public string Scene;
        public ulong Holder;
        public bool HostOwned;
        public bool Craft;
        public bool Closed;
        public bool Final;
        public string Json;
        public string LockId;
        public string OpenHash;
        public float TouchedAt;
        public float ClosedAt;
        public float BusyAt;
    }

    private static bool Active
    {
        get { return FreeRoam.IAmAuthority; }
    }

    public static void Init()
    {
    }

    public static void Reset()
    {
        locks.Clear();
        hostOpenKey = null;
    }

    public static void OnSceneChanged()
    {
        try
        {
            hostOpenKey = null;
            if (locks.Count == 0) return;
            stale.Clear();
            foreach (KeyValuePair<string, Held> pair in locks) stale.Add(pair.Key);
            for (int i = 0; i < stale.Count; i++)
            {
                CoopLog.Log.Info("coop: container " + stale[i] + " released, the scene changed");
                Release(stale[i], false);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerHost.OnSceneChanged", e);
        }
    }

    public static void Tick()
    {
        try
        {
            ScanTick();
            if (!Active)
            {
                if (locks.Count > 0 || hostOpenKey != null)
                {
                    CoopLog.Log.Info("coop: " + locks.Count + " container lock(s) were dropped, this machine no longer runs the scene");
                    locks.Clear();
                    hostOpenKey = null;
                }
                return;
            }
            if (locks.Count == 0) return;
            float now = Time.unscaledTime;
            stale.Clear();
            foreach (KeyValuePair<string, Held> pair in locks)
            {
                Held held = pair.Value;
                if (held.Craft) continue;
                if (held.Closed)
                {
                    if (!held.Final)
                    {
                        if (now >= held.ClosedAt + CloseGrace) stale.Add(held.Key);
                    }
                    else if (!SaveData.Busy) stale.Add(held.Key);
                    else if (held.BusyAt != 0f && now - held.BusyAt >= BusyGrace) stale.Add(held.Key);
                }
                else if (!held.HostOwned && now - held.TouchedAt >= IdleSeconds) stale.Add(held.Key);
            }
            for (int i = 0; i < stale.Count; i++)
            {
                Held done;
                bool apply = locks.TryGetValue(stale[i], out done) && done.Closed && done.Final;
                Release(stale[i], apply);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerHost.Tick", e);
        }
    }

    public static string SyncIdOf(GameObject go)
    {
        Dictionary<GameObject, string> ids = Ids();
        string id;
        return go != null && ids != null && ids.TryGetValue(go, out id) ? id : null;
    }

    public static void Handle(ulong from, ContainerMsg m)
    {
        try
        {
            if (!Active || !Session.Peers.Contains(from)) return;
            if (m.Kind == "REQOPEN") Request(from, m);
            else if (m.Kind == "CLOSE") Close(from, m);
            else if (m.Kind == "TOUCH") Touch(from, m);
            else if (m.Kind == "REQSYNC") SyncRequest(from, m);
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerHost.Handle", e);
        }
    }

    public static void OnState(ulong from, string name, string json)
    {
        try
        {
            if (!Active || json == null) return;
            Held held = ForState(from, name);
            if (held == null)
            {
                Stale(from, name);
                return;
            }
            if (held.Craft)
            {
                CoopLog.Once("ContainerHost.OnState.craft", "coop: a container state arrived for a station craft lock");
                return;
            }
            held.Final = true;
            held.Json = json;
            held.TouchedAt = Time.unscaledTime;
            if (CoopDebug.On("cont")) CoopDebug.Event("cont", held.Key, "a final state arrived from " + from + " under lock " + held.LockId);
            if (held.Closed) Release(held.Key, true);
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerHost.OnState", e);
        }
    }

    public static bool GuestHolds(Storage storage)
    {
        if (!Active || storage == null) return false;
        string key = ContainerWire.KeyOf(storage);
        if (key == null) return false;
        Held held;
        if (!locks.TryGetValue(key, out held)) return false;
        if (held.HostOwned || held.Holder == 0) return false;
        Notice("A guest is using that");
        return true;
    }

    public static bool GuestHoldsAny()
    {
        if (!Active) return false;
        foreach (KeyValuePair<string, Held> pair in locks)
        {
            if (!pair.Value.HostOwned) return true;
        }
        return false;
    }

    public static bool HoldCraft(string key, string scene, ulong holder)
    {
        if (key == null || scene == null) return false;
        if (locks.ContainsKey(key) || key == hostOpenKey) return false;
        if (locks.Count >= MaxLocks) return false;
        Held taken = new Held();
        taken.Key = key;
        taken.Scene = scene;
        taken.Holder = holder;
        taken.HostOwned = false;
        taken.Craft = true;
        taken.TouchedAt = Time.unscaledTime;
        locks[key] = taken;
        SendAll(scene, ContainerWire.Lock(scene, key, holder));
        return true;
    }

    public static bool HeldBy(string key, out ulong holder)
    {
        holder = 0;
        if (key == null) return false;
        Held taken;
        if (!locks.TryGetValue(key, out taken)) return false;
        holder = taken.Holder;
        return true;
    }

    public static void ReleaseKey(string key)
    {
        if (key == null) return;
        Held taken;
        if (!locks.TryGetValue(key, out taken)) return;
        locks.Remove(key);
        SendAll(taken.Scene, ContainerWire.Unlock(taken.Scene, key));
    }

    public static Storage ResolveKey(string key)
    {
        return Resolve(key);
    }

    public static void OnMemberLeft(ulong peer)
    {
        try
        {
            if (!Active) return;
            stale.Clear();
            foreach (KeyValuePair<string, Held> pair in locks)
            {
                if (pair.Value.Holder == peer) stale.Add(pair.Key);
            }
            for (int i = 0; i < stale.Count; i++)
            {
                CoopLog.Log.Info("coop: container " + stale[i] + " released, the holder left");
                Release(stale[i], false);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerHost.OnMemberLeft", e);
        }
    }

    public static void OnOpened(ContainerInfo info)
    {
        try
        {
            if (!Active) return;
            Storage storage = Find(info);
            string key = ContainerWire.KeyOf(storage);
            if (key == null) return;
            hostOpenKey = key;
            if (SaveData.Busy) return;
            Held held;
            if (locks.TryGetValue(key, out held))
            {
                if (held.Holder != 0) Notice(Session.DisplayName(held.Holder) + " has that container open");
                return;
            }
            held = new Held();
            held.Key = key;
            held.Scene = GameRefs.AreaId;
            held.HostOwned = true;
            held.TouchedAt = Time.unscaledTime;
            locks[key] = held;
            SendAll(held.Scene, ContainerWire.Lock(held.Scene, key, Session.Me));
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerHost.OnOpened", e);
        }
    }

    public static void OnClosed(ContainerInfo info)
    {
        try
        {
            if (!Active) return;
            Storage storage = Find(info);
            string key = ContainerWire.KeyOf(storage);
            if (key != null && key == hostOpenKey) hostOpenKey = null;
            Held held;
            if (key == null || !locks.TryGetValue(key, out held) || !held.HostOwned) return;
            locks.Remove(key);
            SendAll(held.Scene, ContainerWire.Unlock(held.Scene, key));
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerHost.OnClosed", e);
        }
    }

    private static void Request(ulong from, ContainerMsg m)
    {
        string scene = GameRefs.AreaId;
        if (scene == null || m.Scene != scene || Session.AreaOf(from) != scene)
        {
            Deny(from, m.ReqId, "not in this scene");
            return;
        }
        if (locks.ContainsKey(m.Key) || m.Key == hostOpenKey)
        {
            Deny(from, m.ReqId, "in use");
            return;
        }
        if (locks.Count >= MaxLocks)
        {
            Deny(from, m.ReqId, "too many open containers");
            return;
        }
        if (SaveData.Busy)
        {
            Deny(from, m.ReqId, "the game is loading or saving");
            return;
        }
        Storage storage = Resolve(m.Key);
        if (storage == null)
        {
            Deny(from, m.ReqId, "not found");
            return;
        }
        if (PrisonLocal.IsCellStorage(storage))
        {
            Deny(from, m.ReqId, "that locker belongs to one player");
            return;
        }
        if (storage._Lock != null && storage._Lock.Locked)
        {
            Deny(from, m.ReqId, "locked");
            return;
        }
        CraftingBase hostPanel = storage.GetComponent<CraftingBase>();
        if (hostPanel == null) hostPanel = storage.GetComponentInParent<CraftingBase>();
        if (hostPanel != null && CraftingBase.currentCraftingBase == hostPanel)
        {
            Deny(from, m.ReqId, "in use");
            return;
        }
        if (storage.StorageOwner != null && storage.StorageOwner.ID > 1 && !storage.PermissionToTake)
        {
            Deny(from, m.ReqId, "the owner does not allow it");
            return;
        }
        string json = ContainerWire.Capture(storage, m.Key);
        if (json == null)
        {
            Deny(from, m.ReqId, "the container state could not be read");
            return;
        }
        Held held = new Held();
        held.Key = m.Key;
        held.Scene = scene;
        held.Holder = from;
        held.LockId = ContainerWire.NewLockId();
        Refresh(storage);
        held.OpenHash = HostHash(storage, m.Key);
        held.TouchedAt = Time.unscaledTime;
        locks[m.Key] = held;
        if (NetBlob.Send(from, ContainerWire.ModId, ContainerWire.BlobNameOf(m.Key, held.LockId), ContainerWire.Bytes(json)) < 0)
        {
            locks.Remove(m.Key);
            Deny(from, m.ReqId, "the container state could not be sent");
            return;
        }
        Send(from, ContainerWire.Open(m.ReqId, m.Key, held.LockId));
        SendAll(scene, ContainerWire.Lock(scene, m.Key, from));
        if (CoopDebug.On("cont")) CoopDebug.Event("cont", m.Key, "open granted to " + from + " under lock " + held.LockId);
    }

    private static void Close(ulong from, ContainerMsg m)
    {
        Held held;
        if (!locks.ContainsKey(m.Key) && m.HasState && m.Key != hostOpenKey && locks.Count < MaxLocks && Session.AreaOf(from) == GameRefs.AreaId && Session.Peers.Contains(from) && Time.unscaledTime - FreeRoam.BecameAuthorityAt < 10f && LateOk(m.Key))
        {
            Held late = new Held();
            late.Key = m.Key;
            late.Scene = GameRefs.AreaId;
            late.Holder = from;
            late.LockId = m.LockId;
            late.TouchedAt = Time.unscaledTime;
            locks[m.Key] = late;
            CoopLog.Log.Info("coop: a late container close for " + m.Key + " from " + from + " is accepted after a scene handover");
        }
        if (!locks.TryGetValue(m.Key, out held))
        {
            RefuseClose(from, m.LockId);
            return;
        }
        if (held.Holder != from || held.Craft) return;
        if (m.LockId != null && held.LockId != null && m.LockId != held.LockId)
        {
            CoopLog.Log.Info("coop: a close for " + m.Key + " named lock " + m.LockId + " which is not the live lock " + held.LockId);
            RefuseClose(from, m.LockId);
            return;
        }
        held.Closed = true;
        held.ClosedAt = Time.unscaledTime;
        held.TouchedAt = Time.unscaledTime;
        if (CoopDebug.On("cont")) CoopDebug.Event("cont", m.Key, "close received from " + from + " with state " + m.HasState + " under lock " + held.LockId);
        if (!m.HasState)
        {
            Release(m.Key, false);
            return;
        }
        if (held.Final) Release(m.Key, true);
    }

    private static bool LateOk(string key)
    {
        Storage storage = Resolve(key);
        if (storage == null || (storage._Lock != null && storage._Lock.Locked)) return false;
        if (storage.StorageOwner != null && storage.StorageOwner.ID > 1 && !storage.PermissionToTake) return false;
        CraftingBase panel = storage.GetComponent<CraftingBase>() ?? storage.GetComponentInParent<CraftingBase>();
        return panel == null || CraftingBase.currentCraftingBase != panel;
    }

    private static void Touch(ulong from, ContainerMsg m)
    {
        Held held;
        if (!locks.TryGetValue(m.Key, out held) || held.Holder != from || held.Craft) return;
        if (held.LockId != null && m.LockId != held.LockId) return;
        held.TouchedAt = Time.unscaledTime;
    }

    private static void Release(string key, bool apply)
    {
        Held held;
        if (!locks.TryGetValue(key, out held)) return;
        string reason = null;
        if (apply && held.Final && held.Json != null && SaveData.Busy)
        {
            if (held.BusyAt == 0f) held.BusyAt = Time.unscaledTime;
            if (Time.unscaledTime - held.BusyAt < BusyGrace)
            {
                held.Closed = true;
                return;
            }
            CoopLog.Log.Error("coop: the game stayed busy for " + BusyGrace + " s, the state of container " + key + " was dropped");
            apply = false;
            reason = "the game stayed busy for too long";
        }
        if (apply && held.Final && held.Json != null && ContainerWire.Picking(Resolve(key)))
        {
            if (held.BusyAt == 0f) held.BusyAt = Time.unscaledTime;
            if (Time.unscaledTime - held.BusyAt < BusyGrace)
            {
                held.Closed = true;
                return;
            }
            CoopLog.Log.Error("coop: the lock of container " + key + " stayed picked or busy for " + BusyGrace + " s, the state was dropped");
            apply = false;
            reason = "the lock was being picked";
        }
        locks.Remove(key);
        CoopLog.Log.Info("coop: container " + key + " released");
        bool applied = false;
        if (apply && held.Final && held.Json != null)
        {
            Storage storage = Resolve(key);
            if (storage == null)
            {
                CoopLog.Log.Error("coop: the state of container " + key + " arrived but the container is gone");
                reason = "the container is gone";
            }
            else if (held.OpenHash != null && StationState.StationOf(storage) == null && HostHash(storage, key) != held.OpenHash)
            {
                CoopLog.Log.Info("coop: the contents of container " + key + " changed while the guest held it, the state was refused");
                reason = "The container changed while you had it open";
            }
            else if (!ContainerWire.Apply(storage, key, held.Json))
            {
                CoopLog.Log.Error("coop: the state of container " + key + " was refused, the host keeps its own contents");
                reason = "the host refused the state";
            }
            else
            {
                applied = true;
                Refresh(storage);
            }
        }
        else if (reason == null)
        {
            reason = held.Closed ? "the host dropped the state" : "the lock was released";
        }
        Answer(held, applied, reason);
        SendAll(held.Scene, ContainerWire.Unlock(held.Scene, key));
    }

    private static void Refresh(Storage storage)
    {
        try
        {
            storage.UpdateSlots();
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerHost.Refresh", e);
        }
    }

    private static Storage Resolve(string key)
    {
        if (key == null) return null;
        string syncId = ContainerWire.SyncIdOf(key);
        if (syncId != null)
        {
            GameObject go = ObjectOf(syncId);
            if (go == null) return null;
            int index = ContainerWire.PieceIndexOf(key);
            Storage[] all = ContainerWire.PieceStorages(go);
            return index >= 0 && index < all.Length && ContainerWire.PieceOf(all[index]) == go ? all[index] : null;
        }
        string guid = ContainerWire.GuidOf(key);
        if (guid == null) return null;
        Storage storage = SaveController.GetObjectByGUID(guid) as Storage;
        return storage != null ? storage : SaveController.GetObjectByGUID(guid, typeof(Storage)) as Storage;
    }

    private static GameObject ObjectOf(string syncId)
    {
        Dictionary<GameObject, string> ids = Ids();
        if (ids == null) return null;
        foreach (KeyValuePair<GameObject, string> pair in ids)
        {
            if (pair.Value == syncId) return pair.Key;
        }
        return null;
    }

    private static Dictionary<GameObject, string> Ids()
    {
        if (!idsTried)
        {
            idsTried = true;
            idsField = typeof(BuildHost).GetField("ids", BindingFlags.NonPublic | BindingFlags.Static);
            if (idsField == null) CoopLog.Log.Error("coop: the build id table was not found, furniture containers cannot be shared");
        }
        return idsField == null ? null : idsField.GetValue(null) as Dictionary<GameObject, string>;
    }

    private static Storage Find(ContainerInfo info)
    {
        if (info == null) return null;
        if (!string.IsNullOrEmpty(info.Guid))
        {
            Storage byGuid = SaveController.GetObjectByGUID(info.Guid) as Storage;
            if (byGuid != null) return byGuid;
        }
        UnityEngine.Object[] found = UnityEngine.Object.FindObjectsByType(typeof(Storage), FindObjectsSortMode.None);
        for (int i = 0; i < found.Length; i++)
        {
            if (found[i] != null && found[i].GetInstanceID() == info.InstanceId) return found[i] as Storage;
        }
        return null;
    }

    private static Held ForState(ulong peer, string name)
    {
        Held found = null;
        foreach (KeyValuePair<string, Held> pair in locks)
        {
            Held held = pair.Value;
            if (held.Craft || held.Holder != peer) continue;
            string mine = held.LockId == null ? null : ContainerWire.BlobNameOf(held.Key, held.LockId);
            bool match = mine == null ? ContainerWire.IsBlobForKey(name, held.Key) : name == mine;
            if (!match) continue;
            if (held.Closed && !held.Final) return held;
            if (found == null) found = held;
        }
        return found;
    }

    private static void Deny(ulong peer, int reqId, string reason)
    {
        Send(peer, ContainerWire.Deny(reqId, reason));
    }

    private static void Send(ulong peer, string text)
    {
        Session.Send(peer, ContainerWire.Channel, text, true);
    }

    private static void SendAll(string scene, string text)
    {
        List<ulong> peers = scene == null ? Session.Peers : Session.PeersInArea(scene);
        for (int i = 0; i < peers.Count; i++) Send(peers[i], text);
    }

    private static void Notice(string text)
    {
        CoopLog.Log.Info("coop: " + text);
        if (string.Equals(text, noticeText, StringComparison.Ordinal) && Time.unscaledTime - noticeAt < NoticeQuiet) return;
        noticeText = text;
        noticeAt = Time.unscaledTime;
        if (Notifications.instance != null) Notifications.instance.CreateNotification("Co-op", text);
    }
}
