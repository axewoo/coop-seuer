using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ObenseuerCore;
using Outputs;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static partial class PrisonLocal
{
    private const float PendingSeconds = 120f;
    private const int DeferFrames = 2;
    private const int NoCell = -1;
    private const string Ok = "prison hooks ok";

    public static bool Ready { get; private set; }
    private static bool installed;
    private static bool pending;
    private static float pendingAt;
    private static bool arrested;
    private static float arrestAt;
    private static bool held;
    private static int heldCell = NoCell;
    private static bool foreignState;
    private static Prison cached;
    private static bool searched;
    private static int activeHandle;
    private static Prison swappedPrison;
    private static Prison.Cell[] swappedCells;
    private static int swapCell = NoCell;
    private static Storage swapStorage;
    private static bool sharedLogged;
    private static bool sharedCaptureLogged;

    private sealed class Defer
    {
        public Prison Target;
        public int Keep;
        public int Frames;
    }

    private static readonly List<Defer> deferred = new List<Defer>();

    private static readonly FieldInfo InPrisonField = AccessTools.Field(typeof(Prison), "inPrison");
    private static readonly FieldInfo ActiveIndexField = AccessTools.Field(typeof(Prison), "activeCellIndex");
    private static readonly FieldInfo ActiveCellField = AccessTools.Field(typeof(Prison), "activeCell");
    private static readonly FieldInfo ClearStorageField = AccessTools.Field(typeof(Prison), "clearStorage");
    private static readonly MethodInfo DeltaMethod = AccessTools.Method(typeof(Prison), "DeltaSeconds", new[] { typeof(int) });
    private static readonly EventInfo SecondsPassedEvent = typeof(TimeOfDayAzure).GetEvent("SecondsPassed");

    public static bool Install()
    {
        if (installed) return Ready;
        PrisonStore.Init();
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".prison");
        try
        {
            Patch(harmony, typeof(Crime), "TeleportToPrison", Type.EmptyTypes, "TeleportPrefix", null, null);
            Patch(harmony, typeof(Crime), "ConfiscateItems", new[] { typeof(Storage), typeof(bool), typeof(Transform) }, "ConfiscatePrefix", null, "ConfiscateFinalizer");
            Patch(harmony, typeof(Prison), "Awake", Type.EmptyTypes, null, "AwakePostfix", null);
            Patch(harmony, typeof(Prison), "ImprisonPlayer", Type.EmptyTypes, "ImprisonPrefix", "ImprisonPostfix", "ImprisonFinalizer");
            Patch(harmony, typeof(Prison), "OnLoadingGame", Type.EmptyTypes, null, "LoadingPostfix", null);
            Patch(harmony, typeof(Prison), "Release", Type.EmptyTypes, "ReleasePrefix", "ReleasePostfix", null);
            Patch(harmony, typeof(Prison), "Escape", Type.EmptyTypes, "EscapePrefix", "EscapePostfix", null);
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonLocal.Install", e);
            harmony.UnpatchSelf();
            return false;
        }
        installed = true;
        string check = SelfCheck();
        if (check != Ok)
        {
            CoopLog.Log.Error("coop: " + check);
            harmony.UnpatchSelf();
            installed = false;
            Ready = false;
            return false;
        }
        Ready = true;
        CoopLog.Log.Info("coop: per player prison cells installed, " + check);
        return true;
    }

    public static bool Active()
    {
        if (!Session.IsInLobby) return false;
        List<ulong> members = Lobby.Members;
        if (members != null && members.Count > 1) return true;
        return held || PendingNow();
    }

    public static bool TeleportPrefix()
    {
        if (!Active()) return true;
        try
        {
            pending = true;
            pendingAt = Time.realtimeSinceStartup;
            arrested = true;
            arrestAt = pendingAt;
            activeHandle = SceneManager.GetActiveScene().handle;
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonLocal.TeleportPrefix", e);
        }
        return true;
    }

    public static void ReleasePrefix(Prison __instance, out bool __state)
    {
        __state = Cell(__instance) != null;
    }

    public static void EscapePrefix(Prison __instance, out bool __state)
    {
        __state = Cell(__instance) != null;
    }

    public static void ReleasePostfix(bool __state)
    {
        if (!__state) return;
        if (Active()) Clear();
        PrisonStore.Released();
    }

    public static void EscapePostfix(bool __state)
    {
        if (!__state) return;
        if (Active()) Clear();
        PrisonStore.Released();
    }

    public static void Reset()
    {
        Clear();
        deferred.Clear();
        searched = false;
        cached = null;
        sharedLogged = false;
        sharedCaptureLogged = false;
        PrisonStore.Reset();
    }

    private static void Clear()
    {
        ClearMarks();
        arrested = false;
    }

    private static void ClearMarks()
    {
        pending = false;
        held = false;
        heldCell = NoCell;
        foreignState = false;
    }

    public static void ConfiscatePrefix()
    {
        if (!Active()) return;
        ItemHost.HoldLocal();
    }

    public static void ConfiscateFinalizer(Exception __exception)
    {
        ItemHost.ReleaseLocal();
    }

    public static void AwakePostfix(Prison __instance)
    {
        if (!Active()) return;
        cached = __instance;
        searched = true;
    }

    public static void OnSceneChanged()
    {
        try
        {
            searched = false;
            int handle = SceneManager.GetActiveScene().handle;
            if (handle == activeHandle) return;
            activeHandle = handle;
            if (!Session.IsInLobby) return;
            if (CarriesPrison()) return;
            ClearMarks();
            deferred.Clear();
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonLocal.OnSceneChanged", e);
        }
    }

    private static bool CarriesPrison()
    {
        Prison prison = cached;
        if (prison == null) return false;
        return CellCount(prison) > 0;
    }

    public static void Tick()
    {
        try
        {
            PrisonStore.Tick();
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonLocal.Tick.store", e);
        }
        try
        {
            if (!Active())
            {
                if (deferred.Count > 0) deferred.Clear();
                return;
            }
            if (deferred.Count == 0) return;
            for (int i = deferred.Count - 1; i >= 0; i--)
            {
                Defer entry = deferred[i];
                entry.Frames--;
                if (entry.Frames > 0) continue;
                deferred.RemoveAt(i);
                int keep = entry.Keep;
                if (held && heldCell > NoCell && entry.Target != null && CellAt(entry.Target, heldCell) != null) keep = heldCell;
                EmptyCells(entry.Target, keep);
            }
        }
        catch (Exception e)
        {
            deferred.Clear();
            CoopLog.Once("PrisonLocal.Tick", e);
        }
    }

    private static void DeferEmpty(Prison prison, int keep)
    {
        if (prison == null) return;
        for (int i = 0; i < deferred.Count; i++)
        {
            if (deferred[i].Target == prison)
            {
                deferred[i].Keep = keep;
                deferred[i].Frames = DeferFrames;
                return;
            }
        }
        Defer entry = new Defer();
        entry.Target = prison;
        entry.Keep = keep;
        entry.Frames = DeferFrames;
        deferred.Add(entry);
    }

    public static bool IsCellStorage(Storage storage)
    {
        try
        {
            if (!Ready || !Active() || storage == null) return false;
            return CellIndex(PrisonOf(storage), storage) >= 0;
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonLocal.IsCellStorage", e);
            return false;
        }
    }

    public static bool OwnsCell(Storage storage)
    {
        try
        {
            if (!Ready || !Active() || storage == null) return false;
            Prison prison = PrisonOf(storage);
            int index = CellIndex(prison, storage);
            return index >= 0 && index == OwnCell(prison);
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonLocal.OwnsCell", e);
            return false;
        }
    }

    private static int Slot(int cells)
    {
        if (cells < 1 || !Active()) return NoCell;
        ulong owner = Lobby.Owner;
        if (owner == 0UL || Session.Me == owner) return 0;
        return (1 + GuestRank(Session.Me, owner)) % cells;
    }

    private static int GuestRank(ulong id, ulong owner)
    {
        List<ulong> members = Lobby.Members;
        if (members == null) return 0;
        int rank = 0;
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i] != owner && members[i] != id && members[i] < id) rank++;
        }
        return rank;
    }

    private static bool SlotMine(Prison prison, int slot)
    {
        if (!Active() || slot < 0) return false;
        int cells = RealCells(prison);
        if (cells < 1) return false;
        List<ulong> members = Lobby.Members;
        if (members == null) return true;
        ulong owner = Lobby.Owner;
        for (int i = 0; i < members.Count; i++)
        {
            ulong id = members[i];
            if (id == Session.Me) continue;
            int other = owner == 0UL || id == owner ? 0 : (1 + GuestRank(id, owner)) % cells;
            if (other == slot) return false;
        }
        return true;
    }

    private static int SlotCell(Prison prison)
    {
        int slot = Slot(CellCount(prison));
        return slot >= 0 && SlotMine(prison, slot) ? slot : NoCell;
    }

    private static int OwnCell(Prison prison)
    {
        if (!Active()) return NoCell;
        if (held) return heldCell;
        if (!PendingNow() && LoadedOurs(prison)) return Index(prison);
        return SlotCell(prison);
    }

    public static bool ImprisonPrefix(Prison __instance)
    {
        if (!Active()) return true;
        try
        {
            NoteCrowding(__instance);
            int own = OwnCell(__instance);
            EmptyCells(__instance, own);
            Set(ClearStorageField, __instance, false);
            if (!Ours(__instance) && foreignState)
            {
                CoopLog.Log.Info("coop: a prison spawn of a player that was not arrested here is left alone");
                return false;
            }
            int cells = CellCount(__instance);
            Prison.Cell target = own > NoCell && own < cells ? CellAt(__instance, own) : null;
            if (target != null)
            {
                swappedPrison = __instance;
                swappedCells = __instance.prisonCells;
                swapCell = own;
                swapStorage = target.confiscatedStorage;
                __instance.prisonCells = new Prison.Cell[] { target };
            }
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonLocal.ImprisonPrefix", e);
            return true;
        }
    }

    public static void ImprisonPostfix(Prison __instance)
    {
        if (!Active()) return;
        try
        {
            if (!Ours(__instance) && foreignState) return;
            int cells = RealCells(__instance);
            if (cells < 1)
            {
                pending = false;
                arrested = false;
                return;
            }
            bool swapped = swappedPrison == __instance && swapCell > NoCell;
            if (swapped)
            {
                heldCell = swapCell;
            }
            else if (!held)
            {
                int slot = Slot(cells);
                heldCell = slot >= 0 && SlotMine(__instance, slot) ? slot : Index(__instance);
            }
            held = heldCell > NoCell;
            pending = false;
            arrested = false;
            Retarget(__instance);
            if (!held) return;
            if (swapped)
            {
                Capture(__instance, heldCell);
                return;
            }
            if (Index(__instance) == heldCell)
            {
                Capture(__instance, heldCell);
                return;
            }
            __instance.ChangeCell(heldCell);
            if (Index(__instance) != heldCell)
            {
                heldCell = Index(__instance);
                held = heldCell > NoCell;
                Retarget(__instance);
                Capture(__instance, heldCell);
                CoopLog.Log.Error("coop: the game refused the prison cell change, the player keeps the cell it was given");
                return;
            }
            Capture(__instance, heldCell);
            Prison.Cell moved = CellAt(__instance, heldCell);
            if (moved != null && moved.playerSpawnpoint != null) Teleport.TeleportPlayerToTargetStatic(moved.playerSpawnpoint);
            FireImprisoned(moved);
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonLocal.ImprisonPostfix", e);
        }
    }

    public static void ImprisonFinalizer()
    {
        try
        {
            Prison prison = swappedPrison;
            if (prison == null) return;
            prison.prisonCells = swappedCells;
            if (held && heldCell > NoCell) Set(ActiveIndexField, prison, heldCell);
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonLocal.ImprisonFinalizer", e);
        }
        finally
        {
            swappedPrison = null;
            swappedCells = null;
            swapCell = NoCell;
            swapStorage = null;
        }
    }

    private static void Retarget(Prison prison)
    {
        if (prison == null || heldCell <= NoCell) return;
        for (int i = 0; i < deferred.Count; i++)
        {
            if (deferred[i].Target == prison) deferred[i].Keep = heldCell;
        }
    }

    public static void LoadingPostfix(Prison __instance)
    {
        if (!Active()) return;
        try
        {
            bool was = Bool(InPrisonField, __instance);
            int index = Index(__instance);
            int cells = CellCount(__instance);
            if (held)
            {
                Set(ActiveIndexField, __instance, heldCell);
                Set(ActiveCellField, __instance, CellAt(__instance, heldCell));
                EmptyCells(__instance, heldCell);
                DeferEmpty(__instance, heldCell);
                return;
            }
            if (!LoadedOurs(__instance))
            {
                ResetFields(__instance, was);
                EmptyCells(__instance, NoCell);
                DeferEmpty(__instance, NoCell);
                foreignState = was || index > NoCell;
                if (foreignState) CoopLog.Log.Info("coop: the prison state of the other player was cleared from this machine");
                return;
            }
            if (PendingNow())
            {
                ResetFields(__instance, was);
                int mine = SlotCell(__instance);
                EmptyCells(__instance, mine);
                DeferEmpty(__instance, mine);
                foreignState = was || index > NoCell;
                return;
            }
            int keep = index > NoCell && index < cells ? index : SlotCell(__instance);
            EmptyCells(__instance, keep);
            DeferEmpty(__instance, keep);
            if (was || index > NoCell) CoopLog.Log.Info("coop: the prison state of this player was kept from the loaded file");
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonLocal.LoadingPostfix", e);
        }
    }

    private static void ResetFields(Prison prison, bool was)
    {
        if (was) Detach(prison);
        if (was || Index(prison) > NoCell) ClearWakeup();
        Set(InPrisonField, prison, false);
        Set(ActiveIndexField, prison, NoCell);
        Set(ActiveCellField, prison, null);
        Set(ClearStorageField, prison, false);
    }

    private static bool LoadedOurs(Prison prison)
    {
        if (PendingNow() || held) return true;
        int cells = CellCount(prison);
        int index = Index(prison);
        if (index <= NoCell || index >= cells) return false;
        int slot = Slot(cells);
        return index == slot && SlotMine(prison, slot);
    }

    private static bool Ours(Prison prison)
    {
        if (PendingNow() || held) return true;
        if (Bool(InPrisonField, prison)) return true;
        return Cell(prison) != null;
    }

    private static bool PendingNow()
    {
        if (pending && Time.realtimeSinceStartup - pendingAt <= PendingSeconds) return true;
        pending = false;
        if (!arrested) return false;
        if (Time.realtimeSinceStartup - arrestAt <= PendingSeconds) return true;
        arrested = false;
        return false;
    }

    public static string SelfCheck()
    {
        if (AccessTools.Method(typeof(Crime), "TeleportToPrison", Type.EmptyTypes) == null) return "Crime.TeleportToPrison was not found";
        if (AccessTools.Method(typeof(Crime), "ConfiscateItems", new[] { typeof(Storage), typeof(bool), typeof(Transform) }) == null) return "Crime.ConfiscateItems was not found";
        if (AccessTools.Method(typeof(Prison), "Awake", Type.EmptyTypes) == null) return "Prison.Awake was not found";
        if (AccessTools.Method(typeof(Prison), "ImprisonPlayer", Type.EmptyTypes) == null) return "Prison.ImprisonPlayer was not found";
        if (AccessTools.Method(typeof(Prison), "OnLoadingGame", Type.EmptyTypes) == null) return "Prison.OnLoadingGame was not found";
        if (AccessTools.Method(typeof(Prison), "ChangeCell", new[] { typeof(int) }) == null) return "Prison.ChangeCell was not found";
        if (AccessTools.Method(typeof(Prison), "Release", Type.EmptyTypes) == null) return "Prison.Release was not found";
        if (AccessTools.Method(typeof(Prison), "Escape", Type.EmptyTypes) == null) return "Prison.Escape was not found";
        if (DeltaMethod == null) return "Prison.DeltaSeconds was not found";
        if (InPrisonField == null) return "Prison.inPrison was not found";
        if (ActiveIndexField == null) return "Prison.activeCellIndex was not found";
        if (ActiveCellField == null) return "Prison.activeCell was not found";
        if (ClearStorageField == null) return "Prison.clearStorage was not found";
        if (InPrisonField != null && InPrisonField.FieldType != typeof(bool)) return "Prison.inPrison is not a bool";
        if (ActiveIndexField != null && ActiveIndexField.FieldType != typeof(int)) return "Prison.activeCellIndex is not an int";
        if (ActiveCellField != null && ActiveCellField.FieldType != typeof(Prison.Cell)) return "Prison.activeCell is not a Prison.Cell";
        if (ClearStorageField != null && ClearStorageField.FieldType != typeof(bool)) return "Prison.clearStorage is not a bool";
        if (SecondsPassedEvent == null) return "TimeOfDayAzure.SecondsPassed was not found";
        if (AccessTools.Method(typeof(WaitingController), "SetOnWakeUpCallback", new[] { typeof(Action) }) == null) return "WaitingController.SetOnWakeUpCallback was not found";
        if (AccessTools.Method(typeof(Teleport), "TeleportPlayerToTargetStatic", new[] { typeof(Transform) }) == null) return "Teleport.TeleportPlayerToTargetStatic was not found";
        if (AccessTools.Method(typeof(Storage), "Empty", Type.EmptyTypes) == null) return "Storage.Empty was not found";
        if (AccessTools.Field(typeof(Storage), "storageSlots") == null) return "Storage.storageSlots was not found";
        if (AccessTools.Field(typeof(SlotController), "itemStack") == null) return "SlotController.itemStack was not found";
        return Ok;
    }
}
