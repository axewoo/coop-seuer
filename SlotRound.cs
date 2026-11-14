using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

internal static class SlotRound
{
    public sealed class Ctx
    {
        public int Pot;
        public int StakesIndex;
        public int StakeIndex;
        public int StakeValue;
        public bool Payer;
        public bool Charged;
        public int RoundCounter;
        public bool PrevWon;
        public bool PrevUsedLocking;
        public int[] RunResults;
        public int[] RunResultsAfter;
        public int R0;
        public int R1;
        public int R2;
        public bool HasValues;
    }

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static FieldInfo stateField;
    private static FieldInfo potField;
    private static FieldInfo earningsField;
    private static FieldInfo stakesIndexField;
    private static FieldInfo stakesField;
    private static FieldInfo predetField;
    private static FieldInfo lineField;
    private static FieldInfo canDoubleField;
    private static FieldInfo flashField;
    private static FieldInfo rotateField;
    private static FieldInfo chosenField;
    private static FieldInfo cashField;
    private static FieldInfo buttonField;
    private static FieldInfo raiseField;
    private static FieldInfo audioField;
    private static FieldInfo runResultsField;
    private static FieldInfo canLockField;
    private static FieldInfo slotsField;
    private static FieldInfo roundCounterField;
    private static FieldInfo prevRunField;
    private static PropertyInfo prevWonField;
    private static PropertyInfo prevUsedLockingField;
    private static MethodInfo doublingMaterials;
    private static MethodInfo resetLockButtons;
    private static readonly Dictionary<SlotMachineGameplay, Ctx> prepared = new Dictionary<SlotMachineGameplay, Ctx>();
    private static readonly Dictionary<SlotMachineGameplay, Ctx> running = new Dictionary<SlotMachineGameplay, Ctx>();
    private static bool ready;

    public static bool Ready
    {
        get { return ready; }
    }

    public static string Missing { get; private set; }

    public static bool Init()
    {
        Type t = typeof(SlotMachineGameplay);
        stateField = AccessTools.Field(t, "_state");
        potField = AccessTools.Field(t, "_moneyInMachine");
        earningsField = AccessTools.Field(t, "_currentRunEarnings");
        stakesIndexField = AccessTools.Field(t, "_stakesIndex");
        stakesField = AccessTools.Field(t, "stakes");
        predetField = AccessTools.Field(t, "_predeterminedValues");
        lineField = AccessTools.Field(t, "_winningLine");
        canDoubleField = AccessTools.Field(t, "_canDouble");
        flashField = AccessTools.Field(t, "_shouldFlashDoublingButtons");
        rotateField = AccessTools.Field(t, "_shouldRotate");
        chosenField = AccessTools.Field(t, "_chosenDoublingValue");
        cashField = AccessTools.Field(t, "_cashOutState");
        buttonField = AccessTools.Field(t, "cashOutButton");
        raiseField = AccessTools.Field(t, "raiseStakesButton");
        audioField = AccessTools.Field(t, "slotMachineAudio");
        runResultsField = t.GetField("_runResults", BindingFlags.Static | BindingFlags.NonPublic);
        canLockField = AccessTools.Field(t, "_canLock");
        slotsField = AccessTools.Field(t, "_slots");
        roundCounterField = AccessTools.Field(t, "_roundCounterForLocking");
        prevRunField = AccessTools.Field(t, "_slotMachinePrevRun");
        prevWonField = AccessTools.Property(typeof(SlotMachinePrevRunInfo), "Won");
        prevUsedLockingField = AccessTools.Property(typeof(SlotMachinePrevRunInfo), "UsedLocking");
        doublingMaterials = AccessTools.Method(t, "SetMaterialsForDoublingButtons", new[] { typeof(bool), typeof(bool) });
        resetLockButtons = AccessTools.Method(t, "ResetLockButtons", Type.EmptyTypes);
        Missing = First();
        ready = Missing == null;
        if (!ready) CoopLog.Log.Error("coop: the slot machine round is off: " + Missing + " was not found");
        return ready;
    }

    public static string SelfCheck()
    {
        try
        {
            if (!Init()) return Missing;
        }
        catch (Exception e)
        {
            return "exception: " + e.Message;
        }
        return "slot machine round fields ok";
    }

    private static string First()
    {
        if (stateField == null) return "_state";
        if (potField == null) return "_moneyInMachine";
        if (earningsField == null) return "_currentRunEarnings";
        if (stakesIndexField == null) return "_stakesIndex";
        if (stakesField == null) return "stakes";
        if (predetField == null) return "_predeterminedValues";
        if (lineField == null) return "_winningLine";
        if (canDoubleField == null) return "_canDouble";
        if (flashField == null) return "_shouldFlashDoublingButtons";
        if (rotateField == null) return "_shouldRotate";
        if (chosenField == null) return "_chosenDoublingValue";
        if (cashField == null) return "_cashOutState";
        if (buttonField == null) return "cashOutButton";
        if (raiseField == null) return "raiseStakesButton";
        if (audioField == null) return "slotMachineAudio";
        if (runResultsField == null) return "_runResults";
        if (canLockField == null) return "_canLock";
        if (slotsField == null) return "_slots";
        if (roundCounterField == null) return "_roundCounterForLocking";
        if (prevRunField == null) return "_slotMachinePrevRun";
        if (prevWonField == null) return "SlotMachinePrevRunInfo.Won";
        if (prevUsedLockingField == null) return "SlotMachinePrevRunInfo.UsedLocking";
        if (doublingMaterials == null) return "SetMaterialsForDoublingButtons";
        if (resetLockButtons == null) return "ResetLockButtons";
        return null;
    }

    public static string StateOf(SlotMachineGameplay machine)
    {
        object value = stateField.GetValue(machine);
        return value == null ? "" : value.ToString();
    }

    public static void SetState(SlotMachineGameplay machine, string name)
    {
        stateField.SetValue(machine, Enum.Parse(stateField.FieldType, name));
    }

    public static int[] Stakes(SlotMachineGameplay machine)
    {
        return (int[])stakesField.GetValue(machine);
    }

    public static int StakesIndexOf(SlotMachineGameplay machine)
    {
        return (int)stakesIndexField.GetValue(machine);
    }

    public static int MoneyOf(SlotMachineGameplay machine)
    {
        return (int)potField.GetValue(machine);
    }

    public static int EarningsOf(SlotMachineGameplay machine)
    {
        return (int)earningsField.GetValue(machine);
    }

    public static bool CanDoubleOf(SlotMachineGameplay machine)
    {
        return (bool)canDoubleField.GetValue(machine);
    }

    public static bool LockingInPlay(SlotMachineGameplay machine)
    {
        if ((bool)canLockField.GetValue(machine)) return true;
        SlotMachineSlot[] slots = (SlotMachineSlot[])slotsField.GetValue(machine);
        if (slots == null) return false;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] != null && slots[i].LockStatus != SlotMachineHelper.LockStatuses.None) return true;
        }
        return false;
    }

    public static int LineOf(SlotMachineGameplay machine)
    {
        return Convert.ToInt32(lineField.GetValue(machine));
    }

    public static void SetLine(SlotMachineGameplay machine, int line)
    {
        lineField.SetValue(machine, Enum.ToObject(lineField.FieldType, line));
    }

    public static bool ReadPredetermined(SlotMachineGameplay machine, out int r0, out int r1, out int r2)
    {
        r0 = 0;
        r1 = 0;
        r2 = 0;
        int?[] values = (int?[])predetField.GetValue(machine);
        if (values == null) return false;
        int[] kept = (int[])runResultsField.GetValue(null);
        bool exact = true;
        for (int i = 0; i < 3 && i < values.Length; i++)
        {
            int value;
            if (values[i].HasValue) value = values[i].Value;
            else
            {
                exact = false;
                value = kept != null && i < kept.Length ? kept[i] : 0;
            }
            if (i == 0) r0 = value;
            else if (i == 1) r1 = value;
            else r2 = value;
        }
        return exact;
    }

    public static void WritePredetermined(SlotMachineGameplay machine, int r0, int r1, int r2)
    {
        int?[] values = (int?[])predetField.GetValue(machine);
        if (values == null) return;
        for (int i = 0; i < values.Length; i++)
        {
            if (i == 0) values[i] = r0;
            else if (i == 1) values[i] = r1;
            else if (i == 2) values[i] = r2;
        }
    }

    public static Ctx Begin(SlotMachineGameplay machine, int stakeIndex, bool payer, int line)
    {
        int[] stakes = Stakes(machine);
        if (stakes == null || stakeIndex < 0 || stakeIndex >= stakes.Length) return null;
        Abort(machine);
        Ctx ctx = new Ctx();
        ctx.Pot = MoneyOf(machine);
        ctx.StakesIndex = StakesIndexOf(machine);
        ctx.StakeIndex = stakeIndex;
        ctx.StakeValue = stakes[stakeIndex];
        ctx.Payer = payer;
        ctx.RoundCounter = (int)roundCounterField.GetValue(machine);
        object prev = prevRunField.GetValue(machine);
        if (prev != null)
        {
            ctx.PrevWon = (bool)prevWonField.GetValue(prev, null);
            ctx.PrevUsedLocking = (bool)prevUsedLockingField.GetValue(prev, null);
        }
        int[] kept = (int[])runResultsField.GetValue(null);
        ctx.RunResults = kept == null ? null : (int[])kept.Clone();
        ctx.RunResultsAfter = kept;
        prepared[machine] = ctx;
        stakes[stakeIndex] = payer ? ctx.StakeValue : 0;
        stakesIndexField.SetValue(machine, stakeIndex);
        potField.SetValue(machine, ctx.Pot + ctx.StakeValue);
        SetLine(machine, line);
        return ctx;
    }

    public static void SetStakes(SlotMachineGameplay machine, int index)
    {
        stakesIndexField.SetValue(machine, index);
        int[] stakes = Stakes(machine);
        Button button = raiseField.GetValue(machine) as Button;
        if (button != null && stakes != null && index >= 0 && index < stakes.Length) button.useMessage = stakes[index].ToString();
    }

    public static void SetValues(SlotMachineGameplay machine, int r0, int r1, int r2)
    {
        Ctx ctx;
        if (!prepared.TryGetValue(machine, out ctx)) return;
        ctx.R0 = r0;
        ctx.R1 = r1;
        ctx.R2 = r2;
        ctx.HasValues = true;
    }

    public static bool MarkRunning(SlotMachineGameplay machine)
    {
        Ctx ctx;
        if (!prepared.TryGetValue(machine, out ctx)) return false;
        prepared.Remove(machine);
        if (ctx.HasValues) WritePredetermined(machine, ctx.R0, ctx.R1, ctx.R2);
        running[machine] = ctx;
        return true;
    }

    public static bool IsRunning(SlotMachineGameplay machine)
    {
        return running.ContainsKey(machine);
    }

    public static bool OtherRoundInProgress(SlotMachineGameplay machine)
    {
        try
        {
            SlotMachineGameplay[] all = UnityEngine.Object.FindObjectsByType<SlotMachineGameplay>(FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                SlotMachineGameplay other = all[i];
                if (other == null || other == machine) continue;
                if (running.ContainsKey(other)) return true;
                if (StateOf(other) == "Playing") return true;
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("SlotRound.OtherRoundInProgress", e);
        }
        return false;
    }

    public static bool PayerOf(SlotMachineGameplay machine)
    {
        Ctx ctx;
        return running.TryGetValue(machine, out ctx) && ctx.Payer;
    }

    public static bool IsRunningPayer(SlotMachineGameplay machine)
    {
        Ctx ctx;
        return running.TryGetValue(machine, out ctx) && ctx.Payer;
    }

    public static void MarkCharged(SlotMachineGameplay machine)
    {
        Ctx ctx;
        if (running.TryGetValue(machine, out ctx)) ctx.Charged = true;
    }

    public static void Abort(SlotMachineGameplay machine)
    {
        Ctx ctx;
        if (!prepared.TryGetValue(machine, out ctx)) return;
        prepared.Remove(machine);
        Restore(machine, ctx);
    }

    public static void Finish(SlotMachineGameplay machine)
    {
        Ctx ctx;
        if (!running.TryGetValue(machine, out ctx)) return;
        running.Remove(machine);
        Restore(machine, ctx);
    }

    public static void Reset()
    {
        List<KeyValuePair<SlotMachineGameplay, Ctx>> live = new List<KeyValuePair<SlotMachineGameplay, Ctx>>(running);
        List<KeyValuePair<SlotMachineGameplay, Ctx>> held = new List<KeyValuePair<SlotMachineGameplay, Ctx>>(prepared);
        running.Clear();
        prepared.Clear();
        try
        {
            for (int i = 0; i < live.Count; i++) RestoreQuiet(live[i].Key, live[i].Value, true);
            for (int i = 0; i < held.Count; i++) RestoreQuiet(held[i].Key, held[i].Value, false);
        }
        catch (Exception e)
        {
            CoopLog.Once("SlotRound.Reset", e);
        }
    }

    private static void RestoreQuiet(SlotMachineGameplay machine, Ctx ctx, bool live)
    {
        if (live)
        {
            try
            {
                RefundStake(ctx);
            }
            catch (Exception e)
            {
                CoopLog.Once("SlotRound.RefundQuiet", e);
            }
        }
        if (machine == null) return;
        try
        {
            Restore(machine, ctx);
        }
        catch (Exception e)
        {
            CoopLog.Once("SlotRound.RestoreQuiet", e);
        }
    }

    private static void RefundStake(Ctx ctx)
    {
        if (!ctx.Charged) return;
        if (Money.instance == null)
        {
            CoopLog.Log.Info("slot: a charged round was abandoned with no Money instance, so its " + ctx.StakeValue + " coin stake could not be given back");
            return;
        }
        Money.instance.AddMoney(ctx.StakeValue, false);
        CoopLog.Log.Info("slot: refunded the " + ctx.StakeValue + " coin stake of an abandoned round, the round never finished");
    }

    private static void Restore(SlotMachineGameplay machine, Ctx ctx)
    {
        int[] stakes = Stakes(machine);
        if (stakes != null && ctx.StakeIndex >= 0 && ctx.StakeIndex < stakes.Length) stakes[ctx.StakeIndex] = ctx.StakeValue;
        potField.SetValue(machine, ctx.Pot);
        earningsField.SetValue(machine, 0);
        stakesIndexField.SetValue(machine, ctx.StakesIndex);
        canDoubleField.SetValue(machine, false);
        flashField.SetValue(machine, false);
        rotateField.SetValue(machine, false);
        chosenField.SetValue(machine, Enum.Parse(chosenField.FieldType, "Inactive"));
        cashField.SetValue(machine, Enum.Parse(cashField.FieldType, "CashOut"));
        SetState(machine, "Standby");
        Button button = buttonField.GetValue(machine) as Button;
        if (button != null) button.useMessage = "Money in machine: " + ctx.Pot;
        try
        {
            roundCounterField.SetValue(machine, ctx.RoundCounter);
            object prev = prevRunField.GetValue(machine);
            if (prev != null)
            {
                prevWonField.SetValue(prev, ctx.PrevWon, null);
                prevUsedLockingField.SetValue(prev, ctx.PrevUsedLocking, null);
            }
            int[] kept = (int[])runResultsField.GetValue(null);
            if (kept != null && ctx.RunResults != null && ReferenceEquals(kept, ctx.RunResultsAfter))
            {
                int count = kept.Length < ctx.RunResults.Length ? kept.Length : ctx.RunResults.Length;
                Array.Copy(ctx.RunResults, kept, count);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("SlotRound.RestoreState", e);
        }
        try
        {
            if (resetLockButtons != null) resetLockButtons.Invoke(machine, null);
        }
        catch (Exception e)
        {
            CoopLog.Once("SlotRound.RestoreLocks", e);
        }
        try
        {
            SlotMachineAudio audio = audioField.GetValue(machine) as SlotMachineAudio;
            if (audio != null) audio.StopDoublingSound();
            doublingMaterials.Invoke(machine, new object[] { false, false });
        }
        catch (Exception e)
        {
            CoopLog.Once("SlotRound.Restore", e);
        }
    }
}
