using System;
using System.Collections.Generic;
using System.Reflection;
using System.Globalization;
using System.Text;
using HarmonyLib;
using ObenseuerCore;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static class PowerSync
{
    public const string Channel = "power";
    private sealed class Station
    {
        public CraftingBase Craft;
        public string Key;
        public Fuel Suspended;
        public bool Mirrored;
        public int Burn;
        public bool Burning;
        public float ReceivedAt;
    }

    private static readonly Dictionary<string, Station> stations = new Dictionary<string, Station>();
    private static readonly Dictionary<string, byte> sent = new Dictionary<string, byte>();
    private static readonly Dictionary<string, int> sentBurn = new Dictionary<string, int>();
    private static readonly Dictionary<string, byte> received = new Dictionary<string, byte>();
    private static readonly HashSet<string> dirty = new HashSet<string>();
    private static readonly HashSet<ulong> present = new HashSet<ulong>();
    private static readonly HashSet<ulong> previous = new HashSet<ulong>();
    private static readonly List<string> changes = new List<string>();
    private static readonly EventInfo SecondsPassed = typeof(TimeOfDayAzure).GetEvent("SecondsPassed");
    private static readonly MethodInfo FuelTick = AccessTools.Method(typeof(Fuel), "DeltaSeconds");
    private static readonly FieldInfo PanelField = AccessTools.Field(typeof(CraftingBase), "craftingPanelBaseUI");
    private static string scene;
    private static int sceneHandle = -1;
    private static ulong authority;
    private static float nextScan;
    private static float nextIndex;
    private static float nextFull;
    private static bool gridDirty;
    private static int applying;

    public static void Init()
    {
        try
        {
            bool ok = CorePatcher.Postfix(typeof(CraftingBase), "SetPowerSourceStatus", typeof(PowerSync), "StationChanged", Plugin.PluginGuid);
            ok &= CorePatcher.Postfix(typeof(PowerSource), "FuelStateChange", typeof(PowerSync), "FuelChanged", Plugin.PluginGuid);
            ok &= CorePatcher.Postfix(typeof(FurnitureStats), "setPower", typeof(PowerSync), "GridChanged", Plugin.PluginGuid);
            if (ok) CoopLog.Log.Info("coop: live station power hooks installed");
            else CoopLog.Log.Error("coop: some live power hooks were refused, polling remains active");
        }
        catch (Exception e) { CoopLog.Once("PowerSync.Init", e); }
    }

    private static bool CanDirty()
    {
        return applying == 0 && Session.IsInLobby && FreeRoam.IAmAuthority;
    }

    public static void StationChanged(CraftingBase __instance)
    {
        try
        {
            if (!CanDirty() || __instance == null) return;
            string key = Key(__instance);
            if (key != null)
            {
                dirty.Add(key);
                Station entry;
                if (stations.TryGetValue(key, out entry)) entry.Mirrored = false;
            }
        }
        catch (Exception e) { CoopLog.Once("PowerSync.StationChanged", e); }
    }

    public static void FuelChanged(PowerSource __instance)
    {
        try
        {
            if (!CanDirty() || __instance == null || __instance.Parent == null) return;
            CraftingBase craft = __instance.Parent.GetComponent<CraftingBase>();
            if (craft == null) craft = __instance.Parent.GetComponentInParent<CraftingBase>();
            StationChanged(craft);
        }
        catch (Exception e) { CoopLog.Once("PowerSync.FuelChanged", e); }
    }

    public static void GridChanged()
    {
        try { if (CanDirty()) gridDirty = true; }
        catch (Exception e) { CoopLog.Once("PowerSync.GridChanged", e); }
    }

    public static void Reset()
    {
        if (!Session.IsInLobby) ResumeCounters();
        stations.Clear();
        sent.Clear();
        sentBurn.Clear();
        received.Clear();
        dirty.Clear();
        present.Clear();
        previous.Clear();
        changes.Clear();
        scene = null;
        sceneHandle = -1;
        authority = 0;
        nextScan = nextIndex = nextFull = 0f;
        gridDirty = false;
    }

    public static void OnSceneChanged() { Reset(); }
    public static void OnMemberLeft(ulong peer) { present.Remove(peer); nextScan = 0f; }

    public static void Tick()
    {
        try
        {
            if (!Session.IsInLobby || GameRefs.AreaId == null) return;
            float now = Time.unscaledTime;
            int handle = SceneManager.GetActiveScene().handle;
            if (now < nextScan && !gridDirty && dirty.Count == 0 && scene == GameRefs.AreaId && sceneHandle == handle) return;
            if (scene != GameRefs.AreaId || sceneHandle != handle)
            {
                Reset();
                scene = GameRefs.AreaId;
                sceneHandle = handle;
            }
            ulong owner = FreeRoam.AuthorityOf(scene);
            if (authority != owner)
            {
                authority = owner;
                sent.Clear();
                sentBurn.Clear();
                received.Clear();
                if (owner != 0UL && owner != Session.Me)
                {
                    foreach (Station entry in stations.Values) entry.Mirrored = false;
                }
                nextFull = nextScan = 0f;
                dirty.Clear();
                gridDirty = false;
            }
            bool poll = now >= nextScan;
            if (!poll && !gridDirty && dirty.Count == 0) return;
            if (poll) nextScan = now + 1f;
            if (now >= nextIndex) Reindex(now);
            if (!FreeRoam.IAmAuthority)
            {
                Follow();
                return;
            }
            ResumeCounters();
            bool full = now >= nextFull;
            if (poll && Arrivals()) full = true;
            changes.Clear();
            foreach (Station station in stations.Values)
            {
                if (station.Craft == null || station.Craft._Power == null) continue;
                if (!full && !poll && !gridDirty && !dirty.Contains(station.Key)) continue;
                byte code = Code(station.Craft);
                byte old;
                Fuel fuel = station.Craft._Power._Fuel;
                int burn = fuel == null ? 0 : Math.Max(0, fuel.BurnTimeRemaining);
                int oldBurn;
                bool fuelChanged = fuel != null && (!sentBurn.TryGetValue(station.Key, out oldBurn) || Math.Abs((long)oldBurn - burn) >= 5);
                if (full || !sent.TryGetValue(station.Key, out old) || old != code || fuelChanged || dirty.Contains(station.Key))
                {
                    changes.Add(((char)('0' + code)).ToString() + station.Key + "," + burn.ToString(CultureInfo.InvariantCulture));
                    sent[station.Key] = code;
                    sentBurn[station.Key] = burn;
                }
            }
            dirty.Clear();
            gridDirty = false;
            if (full) nextFull = now + 20f;
            SendChanges();
            if (CoopDebug.On("power") && (changes.Count > 0 || full)) CoopDebug.Event("power", "scan", changes.Count + " station changes of " + stations.Count + " indexed stations" + (full ? ", this scan was a full resend" : "") + ", " + Session.Peers.Count + " peers in the lobby");
        }
        catch (Exception e) { CoopLog.Once("PowerSync.Tick", e); }
    }

    private static bool Arrivals()
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

    private static byte Code(CraftingBase craft)
    {
        Fuel fuel = craft._Power._Fuel;
        return (byte)((craft.On ? 1 : 0) | (craft._Power.IsPowered() ? 2 : 0) | (fuel != null && fuel.active ? 4 : 0));
    }

    private static string ContainerKey(CraftingBase craft)
    {
        Storage storage = craft.InputOutputStorage;
        if (storage == null) storage = craft.GetComponent<Storage>();
        return ContainerWire.KeyOf(storage);
    }

    private static string Key(CraftingBase craft)
    {
        string key = ContainerKey(craft);
        if (string.IsNullOrEmpty(key)) key = CombatSync.KeyOf(craft);
        if (string.IsNullOrEmpty(key)) return null;
        if (Encoding.UTF8.GetByteCount(key) > 60) key = "h" + CombatSync.Fnv(key).ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(key));
    }

    private static void Reindex(float now)
    {
        nextIndex = now + 5f;
        Dictionary<string, Station> old = new Dictionary<string, Station>(stations);
        stations.Clear();
        foreach (CraftingBase craft in UnityEngine.Object.FindObjectsByType<CraftingBase>(FindObjectsSortMode.None))
        {
            if (craft == null || craft.gameObject.scene.handle != sceneHandle) continue;
            string key = Key(craft);
            if (key == null) continue;
            Station entry;
            if (!old.TryGetValue(key, out entry) || entry.Craft != craft) entry = new Station();
            entry.Craft = craft;
            entry.Key = key;
            stations[key] = entry;
        }
    }

    private static void SendChanges()
    {
        if (changes.Count == 0) return;
        List<ulong> peers = Session.PeersInArea(scene);
        string header = scene + "|P|";
        StringBuilder packet = new StringBuilder(header);
        int bytes = Encoding.UTF8.GetByteCount(header);
        for (int i = 0; i < changes.Count; i++)
        {
            string entry = changes[i];
            int size = Encoding.UTF8.GetByteCount(entry) + 1;
            if (bytes + size > 880 && packet.Length > header.Length)
            {
                Broadcast(peers, packet.ToString());
                packet.Length = 0;
                packet.Append(header);
                bytes = Encoding.UTF8.GetByteCount(header);
            }
            if (packet.Length > header.Length) packet.Append('|');
            packet.Append(entry);
            bytes += size;
        }
        if (packet.Length > header.Length) Broadcast(peers, packet.ToString());
    }

    private static void Broadcast(List<ulong> peers, string packet)
    {
        for (int i = 0; i < peers.Count; i++) Session.Send(peers[i], Channel, packet, true);
    }

    public static void Handle(ulong from, string text)
    {
        try
        {
            if (!Session.IsInLobby || FreeRoam.IAmAuthority || !Session.Peers.Contains(from) || from != FreeRoam.Authority || text == null || Encoding.UTF8.GetByteCount(text) > 900) return;
            string[] fields = text.Split('|');
            if (fields.Length < 3 || fields[0] != GameRefs.AreaId || fields[1] != "P") return;
            if (scene != fields[0] || sceneHandle != SceneManager.GetActiveScene().handle)
            {
                Reset();
                scene = fields[0];
                sceneHandle = SceneManager.GetActiveScene().handle;
                Reindex(Time.unscaledTime);
            }
            if (authority != from)
            {
                authority = from;
                received.Clear();
                sent.Clear();
            }
            for (int i = 2; i < fields.Length; i++)
            {
                string entry = fields[i];
                if (entry.Length < 4 || entry.Length > 96 || entry[0] < '0' || entry[0] > '7') continue;
                int sep = entry.IndexOf(',');
                int burn;
                if (sep < 2 || !int.TryParse(entry.Substring(sep + 1), NumberStyles.None, CultureInfo.InvariantCulture, out burn) || burn < 0) continue;
                string key = entry.Substring(1, sep - 1);
                Station station;
                if (!stations.TryGetValue(key, out station)) continue;
                byte code = (byte)(entry[0] - '0');
                received[key] = code;
                station.Mirrored = true;
                station.Burn = burn;
                station.Burning = (code & 4) != 0;
                station.ReceivedAt = Time.realtimeSinceStartup;
                Apply(station, code);
            }
        }
        catch (Exception e) { CoopLog.Once("PowerSync.Handle", e); }
    }

    private static void Follow()
    {
        foreach (Station station in stations.Values)
        {
            try
            {
                if (station.Craft == null || station.Craft._Power == null) continue;
                SuspendCounter(station);
                byte code;
                if (!Leased(station) && received.TryGetValue(station.Key, out code)) Apply(station, code);
            }
            catch (Exception e) { CoopLog.Once("PowerSync.Follow", e); }
        }
    }

    private static void SuspendCounter(Station station)
    {
        PowerSource power = station.Craft._Power;
        Fuel fuel = power == null ? null : power._Fuel;
        if (power == null || !power.RequiresFuel || fuel == null) return;
        if (fuel != station.Suspended || fuel.fuelCounterOn)
        {
            if (SecondsPassed != null && FuelTick != null) SecondsPassed.RemoveEventHandler(null, Delegate.CreateDelegate(SecondsPassed.EventHandlerType, fuel, FuelTick));
            fuel.fuelCounterOn = false;
            station.Suspended = fuel;
        }
    }

    private static void ResumeCounters()
    {
        foreach (Station station in stations.Values)
        {
            if (station.Craft == null || station.Craft._Power == null || !station.Craft._Power.RequiresFuel) continue;
            Fuel fuel = station.Craft._Power._Fuel;
            if (fuel == null) continue;
            if (station.Mirrored)
            {
                float elapsed = station.Burning ? Mathf.Max(0f, Time.realtimeSinceStartup - station.ReceivedAt) : 0f;
                fuel.BurnTimeRemaining = Mathf.Max(0, station.Burn - Mathf.FloorToInt(elapsed));
                station.Mirrored = false;
            }
            if (fuel.active && !fuel.fuelCounterOn && SecondsPassed != null && FuelTick != null)
            {
                SecondsPassed.AddEventHandler(null, Delegate.CreateDelegate(SecondsPassed.EventHandlerType, fuel, FuelTick));
                fuel.fuelCounterOn = true;
            }
            station.Suspended = null;
        }
    }

    private static bool Leased(Station station)
    {
        return station.Craft != null && ContainerGuest.IsOpen(ContainerKey(station.Craft));
    }

    private static void Apply(Station station, byte code)
    {
        CraftingBase craft = station.Craft;
        if (FreeRoam.IAmAuthority || craft == null || craft._Power == null || Leased(station)) return;
        bool on = (code & 1) != 0;
        bool powered = (code & 2) != 0;
        PowerSource power = craft._Power;
        applying++;
        try
        {
            if (power.RequiresFuel)
            {
                Fuel fuel = power._Fuel;
                if (fuel == null) return;
                SuspendCounter(station);
                bool active = (code & 4) != 0;
                if (station.Mirrored) fuel.BurnTimeRemaining = station.Burn;
                bool changed = fuel.active != active || craft.On != on;
                craft.On = on;
                fuel.active = active;
                if (changed) power.FuelStateChange(craft.PoweredAndActive);
            }
            else
            {
                if (craft.On != on && !on && !craft.AlwaysOn) craft.Toggle(false);
                if (power.PowerInput != powered) craft.SetPowerSourceStatus(powered);
                if (craft.On != on && !craft.AlwaysOn) craft.Toggle(on);
                craft.On = on;
            }
            if (CraftingBase.currentCraftingBase == craft && PanelField != null)
            {
                CraftingPanelBaseUI panel = PanelField.GetValue(craft) as CraftingPanelBaseUI;
                if (panel != null && panel.manufacturingPower != null) panel.manufacturingPower.SetPowerInputImage(power.Type, power._Fuel, powered || craft.AlwaysOn);
            }
        }
        finally { applying--; }
    }
}
