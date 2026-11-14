using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;

internal static partial class PrisonStore
{
    private const int NoCell = -1;
    private const string GuidPrefix = "coop-prison-";
    private const string GuidSuffix = "-storage";
    private const float CaptureSeconds = 10f;
    private const float PollSeconds = 1f;
    private const float KeepSeconds = 1800f;
    private const float SettleSeconds = 2f;
    private const int RestoreAttempts = 3;

    public sealed class Record
    {
        public string steamId;
        public string worldId;
        public long savedAt;
        public int cell;
        public string state;
        public bool released;
    }

    private static bool installed;
    private static bool ready;
    private static Record mine;
    private static bool previous;
    private static bool pending;
    private static Storage pendingStorage;
    private static int pendingCell = NoCell;
    private static float pendingUntil;
    private static float nextPoll;
    private static string diskWorld;
    private static bool refused;
    private static int restoreAttempts;
    private static float releasedAt;
    private static string askedWorld;
    private static bool captureBlocked;
    private static float cellSeenAt;
    private static readonly long SessionStart = DateTime.UtcNow.Ticks;

    public static void Init()
    {
        if (installed) return;
        installed = true;
        ready = Session.Register(PrisonStoreWire.Channel, Handle);
        if (!ready) CoopLog.Log.Error("coop: prison: channel " + PrisonStoreWire.Channel + " is owned by another mod, the durable jailed items store is off");
        NetBlob.Received += OnBlob;
        CoopLog.Log.Info("coop: prison store ready" + (ready ? ", the channel " + PrisonStoreWire.Channel + " is registered" : ", the channel is missing"));
        SelfCheck();
    }

    public static void Tick()
    {
        if (!installed || !ready) return;
        try
        {
            TryCapture();
            float now = Time.unscaledTime;
            if (now < nextPoll) return;
            nextPoll = now + PollSeconds;
            if (!Session.IsInLobby) return;
            AskHost();
            ResolveDisk();
            Poll();
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonStore.Tick", e);
        }
    }

    public static void Reset()
    {
        if (!installed) return;
        try
        {
            pending = false;
            pendingStorage = null;
            pendingCell = NoCell;
            pendingUntil = 0f;
            captureBlocked = false;
            if (mine != null && mine.released)
            {
                TellDeleted(mine.steamId);
                DeleteFiles(mine.steamId);
            }
            mine = null;
            previous = false;
            refused = false;
            restoreAttempts = 0;
            releasedAt = 0f;
            diskWorld = null;
            askedWorld = "";
            nextPoll = 0f;
            cellSeenAt = 0f;
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonStore.Reset", e);
        }
    }

    public static void SelfCheck()
    {
        try
        {
            string error;
            List<string> paths = Paths(Id(), out error);
            if (paths == null)
            {
                CoopLog.Log.Info("coop: prison: the store has no folder yet, " + error);
                return;
            }
            CoopLog.Log.Info("coop: prison: the store resolved its folders, the record is written to " + paths[0]);
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonStore.SelfCheck", e);
        }
    }

    public static void Captured(Storage storage, int cell)
    {
        if (!installed || !ready) return;
        try
        {
            pending = false;
            pendingStorage = null;
            if (storage == null || storage.IsEmpty())
            {
                if (CoopDebug.On("prison")) CoopDebug.Event("prison", "empty", "the arrest confiscated nothing, no record is written");
                return;
            }
            if (mine != null)
            {
                CoopLog.Log.Info("coop: prison: the record of " + mine.steamId + " is dropped, this arrest confiscates into cell " + cell);
                if (CoopDebug.On("prison")) CoopDebug.Event("prison", "replaced", "the record of " + mine.steamId + " was dropped for the new arrest");
            }
            pending = true;
            pendingStorage = storage;
            pendingCell = cell;
            pendingUntil = Time.unscaledTime + CaptureSeconds;
            mine = null;
            previous = false;
            refused = false;
            restoreAttempts = 0;
            releasedAt = 0f;
            if (CoopDebug.On("prison")) CoopDebug.Event("prison", "arrest", "an arrest confiscated into cell " + cell + ", the record is captured when the save gate opens");
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonStore.Captured", e);
        }
    }

    private static void TryCapture()
    {
        if (!pending) return;
        Storage storage = pendingStorage;
        if (storage == null)
        {
            pending = false;
            pendingStorage = null;
            pendingCell = NoCell;
            return;
        }
        if (SaveData.Busy && Time.unscaledTime <= pendingUntil) return;
        string id = Id();
        string state = null;
        if (!SaveData.Busy)
        {
            captureBlocked = false;
            state = CaptureState(storage, id);
        }
        if (state == null)
        {
            if (!captureBlocked && Time.unscaledTime <= pendingUntil) return;
            pending = false;
            pendingStorage = null;
            pendingCell = NoCell;
            CoopLog.Log.Error("coop: prison: the confiscated items could not be captured within " + CaptureSeconds + " s, no record was written for " + id);
            return;
        }
        int cell = pendingCell;
        int stacks = Stacks(storage);
        pending = false;
        pendingStorage = null;
        pendingCell = NoCell;
        Record made = new Record();
        made.steamId = id;
        made.worldId = World();
        made.savedAt = DateTime.UtcNow.Ticks;
        made.cell = cell;
        made.state = state;
        made.released = false;
        mine = made;
        previous = false;
        refused = false;
        restoreAttempts = 0;
        releasedAt = 0f;
        byte[] data = Bytes(made);
        bool written = Store(made, data);
        Ship(made, data);
        CoopLog.Log.Info("coop: prison: the record of " + id + " was saved (" + stacks + " stacks, " + Bytes(data) + " bytes, " + Role() + (written ? "" : ", the file was not written") + ")");
        if (CoopDebug.On("prison")) CoopDebug.Event("prison", "saved", stacks + " stacks and " + Bytes(data) + " bytes were stored for " + id + " on the " + Role());
    }

    public static void Released()
    {
        if (!installed || !ready) return;
        try
        {
            pending = false;
            pendingStorage = null;
            pendingCell = NoCell;
            if (mine == null) LoadDisk(false);
            if (mine == null) return;
            if (mine.released) return;
            mine.released = true;
            releasedAt = Time.unscaledTime;
            Store(mine, Bytes(mine));
            TellReleased(mine.steamId);
            CoopLog.Log.Info("coop: prison: the sentence of " + mine.steamId + " ended, the record is released and is dropped once the cell locker is empty");
            if (CoopDebug.On("prison")) CoopDebug.Event("prison", "released", "the record of " + mine.steamId + " was marked released and the host was told");
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonStore.Released", e);
        }
    }

    private static void Poll()
    {
        Record rec = mine;
        if (rec == null)
        {
            cellSeenAt = 0f;
            return;
        }
        Storage own = PrisonLocal.OwnCellStorage();
        bool empty = own != null && own.IsEmpty();
        if (rec.released)
        {
            if (own == null) cellSeenAt = 0f;
            if (empty) Delete("the sentence ended and the cell locker was handed back empty");
            else if (releasedAt > 0f && Time.unscaledTime - releasedAt >= KeepSeconds) Delete("the sentence ended and the record waited its full delete window");
            return;
        }
        if (own == null)
        {
            cellSeenAt = 0f;
            return;
        }
        if (cellSeenAt <= 0f)
        {
            cellSeenAt = Time.unscaledTime;
            return;
        }
        if (Time.unscaledTime - cellSeenAt < SettleSeconds) return;
        if (!SameWorld(rec.worldId)) return;
        if (!empty)
        {
            if (previous)
            {
                previous = false;
                CoopLog.Log.Info("coop: prison: the cell locker of " + rec.steamId + " held its items when it settled, the record is a record of this session from here");
                if (CoopDebug.On("prison")) CoopDebug.Event("prison", "settled", "the own cell locker held its items when it settled, the record of " + rec.steamId + " stops being restorable");
            }
            return;
        }
        if (!previous)
        {
            TellReleased(rec.steamId);
            Delete("the player took the items out of its own cell in this session");
            return;
        }
        Restore(own);
    }

    private static void Restore(Storage own)
    {
        Record rec = mine;
        if (rec == null || own == null || refused || rec.state == null) return;
        if (SaveData.Busy) return;
        bool ok = ApplyState(own, rec.steamId, rec.state);
        if (!ok || own.IsEmpty())
        {
            restoreAttempts++;
            if (restoreAttempts < RestoreAttempts)
            {
                CoopLog.Log.Info("coop: prison: the restore of " + rec.steamId + " did not take, it is retried (" + restoreAttempts + " of " + RestoreAttempts + ")");
                return;
            }
            refused = true;
            CoopLog.Log.Error("coop: prison: the durable record of " + rec.steamId + " was refused by the save framework " + RestoreAttempts + " times, the items stay lost");
            if (CoopDebug.On("prison")) CoopDebug.Event("prison", "refused", "the record of " + rec.steamId + " was refused by the save framework");
            return;
        }
        previous = false;
        refused = false;
        restoreAttempts = 0;
        int stacks = Stacks(own);
        CoopLog.Log.Info("coop: prison: restored " + stacks + " stacks from the durable store");
        if (CoopDebug.On("prison")) CoopDebug.Event("prison", "restored", stacks + " stacks of " + rec.steamId + " were put back into its own cell");
    }

    private static void Delete(string reason)
    {
        Record gone = mine;
        if (gone == null) return;
        mine = null;
        previous = false;
        refused = false;
        restoreAttempts = 0;
        releasedAt = 0f;
        cellSeenAt = 0f;
        TellDeleted(gone.steamId);
        DeleteFiles(gone.steamId);
        CoopLog.Log.Info("coop: prison: the record of " + gone.steamId + " was deleted, " + reason);
        if (CoopDebug.On("prison")) CoopDebug.Event("prison", "deleted", "the record of " + gone.steamId + " was deleted, " + reason);
    }

    private static void AskHost()
    {
        if (!Session.IsGuest || !GuestSession.Loaded)
        {
            askedWorld = "";
            return;
        }
        string world = GuestSession.WorldId;
        if (askedWorld == world) return;
        askedWorld = world;
        if (mine == null) LoadDisk(true);
        ulong host = Session.HostId;
        if (host == 0UL) return;
        Session.Send(host, PrisonStoreWire.Channel, PrisonStoreWire.Notice(PrisonStoreWire.Request, Id()), true);
        if (CoopDebug.On("prison")) CoopDebug.Event("prison", "PRQ", "the record of " + Id() + " was requested from the host");
    }

    private static void ResolveDisk()
    {
        if (mine != null) return;
        if (PrisonLocal.OwnCellStorage() == null) return;
        string world = World();
        if (string.IsNullOrEmpty(world)) return;
        if (diskWorld == world) return;
        diskWorld = world;
        LoadDisk(true);
    }

    private static void LoadDisk(bool announce)
    {
        if (mine != null) return;
        string from;
        Record found = Read(Id(), out from);
        if (found == null) return;
        previous = !SessionRecord(found);
        refused = false;
        restoreAttempts = 0;
        cellSeenAt = 0f;
        releasedAt = found.released ? Time.unscaledTime : 0f;
        mine = found;
        if (!announce) return;
        CoopLog.Log.Info("coop: prison: the record of " + found.steamId + " was read from " + from);
        if (CoopDebug.On("prison")) CoopDebug.Event("prison", "loaded", "the record of " + found.steamId + " was read from " + from);
    }

    private static string CaptureState(Storage storage, string id)
    {
        try
        {
            string real = SaveController.GetObjectGUID(storage);
            if (string.IsNullOrEmpty(real))
            {
                captureBlocked = true;
                return null;
            }
            SaveController.SetObjectGUID(storage, Surrogate(id));
            try
            {
                return SaveData.Capture(storage);
            }
            finally
            {
                SaveController.SetObjectGUID(storage, real);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonStore.CaptureState", e);
            return null;
        }
    }

    private static bool ApplyState(Storage storage, string id, string json)
    {
        string real = SaveController.GetObjectGUID(storage);
        if (string.IsNullOrEmpty(real)) return false;
        SaveController.SetObjectGUID(storage, Surrogate(id));
        try
        {
            return SaveData.Apply(storage, json);
        }
        finally
        {
            SaveController.SetObjectGUID(storage, real);
        }
    }

    private static string Surrogate(string id)
    {
        return GuidPrefix + id + GuidSuffix;
    }

    private static int Stacks(Storage storage)
    {
        StorageSlot[] slots = storage == null ? null : storage.storageSlots;
        if (slots == null) return 0;
        int count = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            StorageSlot slot = slots[i];
            if (slot == null || slot.itemId < 0 || slot.itemAmount <= 0) continue;
            count++;
        }
        return count;
    }

    private static string Role()
    {
        return Session.IsGuest ? "guest" : "host";
    }

    private static string Id()
    {
        ulong me = Session.Me;
        if (me == 0UL) me = Steam.LocalId;
        return PrisonStoreWire.Id(me);
    }

    private static string World()
    {
        if (Session.IsGuest) return GuestSession.WorldId;
        return Session.WorldIdFor(Session.WorldSlot);
    }

    private static bool SameWorld(string id)
    {
        string world = World();
        if (string.IsNullOrEmpty(world) || string.IsNullOrEmpty(id)) return false;
        return string.Equals(world, id, StringComparison.Ordinal);
    }

    private static bool KnownOtherWorld(string id)
    {
        string world = World();
        if (string.IsNullOrEmpty(world) || string.IsNullOrEmpty(id)) return false;
        return !string.Equals(world, id, StringComparison.Ordinal);
    }

    private static bool SessionRecord(Record rec)
    {
        return rec.savedAt >= SessionStart;
    }
}
