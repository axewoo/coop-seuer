using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using ObenseuerCore;
using UnityEngine;

internal static class StationState
{
    private const string Mark = "COOPSTATION1\n";
    private static readonly FieldInfo SavedPlant = AccessTools.Field(typeof(GrowingSpot), "savedPlant");

    public static CraftingBase StationOf(Storage storage)
    {
        if (storage == null) return null;
        CraftingBase station = storage.GetComponent<CraftingBase>();
        if (station == null) station = storage.GetComponentInParent<CraftingBase>();
        return station != null && station.InputOutputStorage == storage ? station : null;
    }

    public static string Wrap(Storage storage, string key, string storageJson)
    {
        CraftingBase station = StationOf(storage);
        if (station == null || storageJson == null) return storageJson;
        try
        {
            List<string> parts = new List<string>();
            parts.Add(storageJson);
            parts.Add(Capture(station, key + "~station") ?? "");
            Growing bed = station as Growing;
            if (bed != null && bed.GrowingSlots != null)
            {
                for (int i = 0; i < bed.GrowingSlots.Count; i++) parts.Add(CaptureSpot(bed.GrowingSlots[i], key + "~spot" + i));
            }
            StringBuilder sb = new StringBuilder(Mark);
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(parts[i])));
            }
            return sb.ToString();
        }
        catch (Exception e)
        {
            CoopLog.Once("StationState.Wrap", e);
            return storageJson;
        }
    }

    public static string StoragePart(string json, out string[] rest)
    {
        rest = null;
        if (json == null || !json.StartsWith(Mark, StringComparison.Ordinal)) return json;
        string[] lines = json.Substring(Mark.Length).Split('\n');
        rest = lines;
        return lines.Length > 0 ? Decode(lines[0]) : null;
    }

    public static bool ApplyRest(Storage storage, string key, string[] rest)
    {
        if (rest == null || rest.Length < 2) return true;
        CraftingBase station = StationOf(storage);
        if (station == null) return true;
        try
        {
            string stationJson = Decode(rest[1]);
            if (!string.IsNullOrEmpty(stationJson))
            {
                string own = Capture(station, key + "~station");
                Detach(station);
                if (!Apply(station, key + "~station", stationJson))
                {
                    CoopLog.Log.Error("coop: a station state was refused for " + key + ", the whole hand over is refused");
                    if (own != null)
                    {
                        Detach(station);
                        Apply(station, key + "~station", own);
                    }
                    return false;
                }
            }
            Growing bed = station as Growing;
            if (bed == null || bed.GrowingSlots == null) return true;
            for (int i = 0; i < bed.GrowingSlots.Count && i + 2 < rest.Length; i++) ApplySpot(bed.GrowingSlots[i], key + "~spot" + i, Decode(rest[i + 2]));
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("StationState.ApplyRest", e);
            return false;
        }
    }

    public static void PruneTimers(manufacturingProcess station)
    {
        try
        {
            TimeOfDayAzure clock = TimeOfDayAzure.instance;
            if (clock == null || TimersField == null) return;
            System.Collections.IList timers = TimersField.GetValue(clock) as System.Collections.IList;
            if (timers == null) return;
            for (int i = timers.Count - 1; i >= 0; i--)
            {
                TimeOfDayAzure.Timer t = timers[i] as TimeOfDayAzure.Timer;
                if (t == null || t == station.process) continue;
                if ((t.Callback != null && t.Callback.Target == (object)station) || (t.Callback2 != null && t.Callback2.Target == (object)station)) timers.RemoveAt(i);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("StationState.PruneTimers", e);
        }
    }

    private static readonly FieldInfo TimersField = AccessTools.Field(typeof(TimeOfDayAzure), "timers");

    private static readonly EventInfo SecondsPassed = typeof(TimeOfDayAzure).GetEvent("SecondsPassed");
    private static readonly MethodInfo FuelTick = AccessTools.Method(typeof(Fuel), "DeltaSeconds");

    private static void Detach(CraftingBase station)
    {
        try
        {
            station.StopAllCoroutines();
            manufacturingProcess process = station as manufacturingProcess;
            if (process != null && process.process != null) process.process.Destroy();
            Fuel fuel = station._Power == null ? null : station._Power._Fuel;
            if (fuel != null)
            {
                if (SecondsPassed != null && FuelTick != null) SecondsPassed.RemoveEventHandler(null, Delegate.CreateDelegate(SecondsPassed.EventHandlerType, fuel, FuelTick));
                fuel.fuelCounterOn = false;
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("StationState.Detach", e);
        }
    }

    private static string Decode(string b64)
    {
        if (string.IsNullOrEmpty(b64)) return "";
        return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
    }

    private static string Surrogate(string key)
    {
        return "coop-" + CombatSync.Fnv(key).ToString("x16", System.Globalization.CultureInfo.InvariantCulture) + "-part";
    }

    private static string Capture(MonoBehaviour target, string key)
    {
        string real = SaveController.GetObjectGUID(target);
        SaveController.SetObjectGUID(target, Surrogate(key));
        try { return SaveData.Capture(target); }
        finally { SaveController.SetObjectGUID(target, real); }
    }

    private static bool Apply(MonoBehaviour target, string key, string json)
    {
        string real = SaveController.GetObjectGUID(target);
        SaveController.SetObjectGUID(target, Surrogate(key));
        try { return SaveData.Apply(target, json); }
        finally { SaveController.SetObjectGUID(target, real); }
    }

    private static string CaptureSpot(GrowingSpot spot, string key)
    {
        if (spot == null) return "";
        ItemReference saved = SavedPlant == null ? null : SavedPlant.GetValue(spot) as ItemReference;
        if (!spot.HasPlant() || saved == null || saved.Item == null) return "0";
        grow plant = spot.Plant == null ? null : spot.Plant.GetComponent<grow>();
        string json = plant == null ? null : Capture(plant, key);
        return saved.Item.ID.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\u0001" + (json ?? "");
    }

    private static void ApplySpot(GrowingSpot spot, string key, string text)
    {
        if (spot == null || text == null) return;
        int sep = text.IndexOf('\u0001');
        int id;
        if (!int.TryParse(sep < 0 ? text : text.Substring(0, sep), out id)) return;
        ItemReference saved = SavedPlant == null ? null : SavedPlant.GetValue(spot) as ItemReference;
        int have = spot.HasPlant() && saved != null && saved.Item != null ? saved.Item.ID : 0;
        if (id <= 0)
        {
            if (spot.HasPlant()) spot.HarvestPlant();
            return;
        }
        if (have != id)
        {
            Item item = ItemDatabase.instance.FetchItemByID(id);
            if (item == null || item.Prefab == null) return;
            spot.AddPlant(item, true);
        }
        grow plant = spot.Plant == null ? null : spot.Plant.GetComponent<grow>();
        string json = sep < 0 ? null : text.Substring(sep + 1);
        if (plant != null && !string.IsNullOrEmpty(json) && !Apply(plant, key, json)) CoopLog.Once("StationState.spot", "coop: a plant state was refused for " + key);
    }
}
