using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ObenseuerCore;
using Outputs;
using UnityEngine;

internal static partial class PrisonLocal
{
    private static void FireImprisoned(Prison.Cell cell)
    {
        if (cell == null || cell.OnImprisoned == null) return;
        for (int i = 0; i < cell.OnImprisoned.Length; i++)
        {
            output fired = cell.OnImprisoned[i];
            if (fired != null) fired.Fire();
        }
    }

    private static void Detach(Prison prison)
    {
        try
        {
            if (SecondsPassedEvent == null || DeltaMethod == null || SecondsPassedEvent.EventHandlerType == null) return;
            Delegate handler = Delegate.CreateDelegate(SecondsPassedEvent.EventHandlerType, prison, DeltaMethod);
            SecondsPassedEvent.RemoveEventHandler(null, handler);
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonLocal.Detach", e);
        }
    }

    private static void ClearWakeup()
    {
        WaitingController controller = WaitingController.instance;
        if (controller != null) controller.SetOnWakeUpCallback(null);
    }

    private static void EmptyCells(Prison prison, int keep)
    {
        if (prison == null) return;
        Prison.Cell[] cells = Cells(prison);
        if (cells == null) return;
        for (int i = 0; i < cells.Length; i++)
        {
            if (i == keep || cells[i] == null || cells[i].confiscatedStorage == null) continue;
            EmptyStorage(cells[i].confiscatedStorage);
        }
    }

    private static void EmptyStorage(Storage storage)
    {
        if (storage == null || storage.IsEmpty()) return;
        StorageSlot[] data = storage.storageSlots;
        SlotController[] built = storage.Slots;
        if (data != null && built != null && built.Length == data.Length)
        {
            for (int i = 0; i < data.Length; i++)
            {
                Zero(data[i]);
                if (built[i] != null && !ReferenceEquals(built[i].itemStack, data[i])) Zero(built[i].itemStack);
            }
            return;
        }
        storage.Empty();
    }

    private static void Zero(ItemStack stack)
    {
        if (stack == null) return;
        stack.itemId = -1;
        stack.itemAmount = 0;
    }

    private static Prison PrisonOf(Storage storage)
    {
        if (cached != null) return cached;
        if (searched) return null;
        if (storage != null)
        {
            Prison parent = storage.GetComponentInParent<Prison>();
            if (parent != null)
            {
                cached = parent;
                searched = true;
                return parent;
            }
        }
        searched = true;
        Prison found = UnityEngine.Object.FindFirstObjectByType<Prison>();
        if (found != null) cached = found;
        return found;
    }

    private static Prison.Cell[] Cells(Prison prison)
    {
        return prison == null ? null : prison.prisonCells;
    }

    private static int CellCount(Prison prison)
    {
        Prison.Cell[] cells = Cells(prison);
        return cells == null ? 0 : cells.Length;
    }

    private static int RealCells(Prison prison)
    {
        if (prison != null && swappedPrison == prison && swappedCells != null) return swappedCells.Length;
        return CellCount(prison);
    }

    private static Prison.Cell CellAt(Prison prison, int index)
    {
        Prison.Cell[] cells = Cells(prison);
        return cells == null || index < 0 || index >= cells.Length ? null : cells[index];
    }

    private static int CellIndex(Prison prison, Storage storage)
    {
        Prison.Cell[] cells = Cells(prison);
        if (cells == null || storage == null) return NoCell;
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i] != null && cells[i].confiscatedStorage == storage) return i;
        }
        return NoCell;
    }

    private static int Index(Prison prison)
    {
        if (ActiveIndexField == null || prison == null) return NoCell;
        return (int)ActiveIndexField.GetValue(prison);
    }

    private static object Cell(Prison prison)
    {
        return ActiveCellField == null || prison == null ? null : ActiveCellField.GetValue(prison);
    }

    private static bool Bool(FieldInfo field, object target)
    {
        return field != null && target != null && (bool)field.GetValue(target);
    }

    private static void Set(FieldInfo field, object target, object value)
    {
        if (field == null || target == null) return;
        field.SetValue(target, value);
    }

    private static void Patch(Harmony harmony, Type type, string name, Type[] args, string prefix, string postfix, string finalizer)
    {
        MethodBase original = AccessTools.Method(type, name, args);
        if (original == null) throw new MissingMethodException(type.Name, name);
        harmony.Patch(original,
            prefix: prefix == null ? null : new HarmonyMethod(typeof(PrisonLocal), prefix),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(PrisonLocal), postfix),
            finalizer: finalizer == null ? null : new HarmonyMethod(typeof(PrisonLocal), finalizer));
    }

    internal static Storage OwnCellStorage()
    {
        try
        {
            if (!Ready || !Active()) return null;
            Prison prison = cached;
            if (prison == null) return null;
            int own = OwnCell(prison);
            if (own <= NoCell) return null;
            Prison.Cell cell = CellAt(prison, own);
            return cell == null ? null : cell.confiscatedStorage;
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonLocal.OwnCellStorage", e);
            return null;
        }
    }

    private static Storage ConfiscatedInto(Prison prison, int cell)
    {
        if (prison == null || cell <= NoCell) return null;
        if (swappedPrison == prison && swappedCells != null) return swapStorage;
        Prison.Cell at = CellAt(prison, cell);
        return at == null ? null : at.confiscatedStorage;
    }

    private static void Capture(Prison prison, int cell)
    {
        if (prison == null || cell <= NoCell) return;
        if (SlotMine(prison, cell))
        {
            PrisonStore.Captured(ConfiscatedInto(prison, cell), cell);
            return;
        }
        if (sharedCaptureLogged) return;
        sharedCaptureLogged = true;
        CoopLog.Log.Info("coop: prison: the cell of this player is shared with another member, the confiscated items are not held by the durable store");
        if (CoopDebug.On("prison")) CoopDebug.Event("prison", "shared", "the held cell is shared with another member, the durable store keeps no record of this arrest");
    }

    private static void NoteCrowding(Prison prison)
    {
        try
        {
            if (sharedLogged) return;
            int cells = RealCells(prison);
            List<ulong> members = Lobby.Members;
            int players = members == null ? 0 : members.Count;
            if (cells < 1 || players <= cells) return;
            sharedLogged = true;
            CoopLog.Log.Info("coop: prison: more players than cells, cell shared (" + players + " players, " + cells + " cells), the items stay private through the durable store");
            if (CoopDebug.On("prison")) CoopDebug.Event("prison", "shared", "more players than cells (" + players + " players, " + cells + " cells), the cell is shared and the durable store keeps the items private");
        }
        catch (Exception e)
        {
            CoopLog.Once("PrisonLocal.NoteCrowding", e);
        }
    }
}
