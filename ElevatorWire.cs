using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

internal sealed class ElevMsg
{
    public string Kind;
    public string Scene;
    public string Key;
    public float Position;
    public int Moving;
    public bool Direction;
    public bool DoorsClosed;
    public bool Powered;
    public int Mask;
    public string[] Queue;
    public string QueueRaw;
    public string Op;
    public int Arg;
    public bool Ok;
    public string Reason;
    public int Seq = -1;
}

internal static class ElevatorWire
{
    public const string Channel = "elev";
    public const int MaxFloors = 32;
    public const int MaxQueue = 16;
    private const int MaxPayload = 880;
    private const int MaxScene = 80;
    private const int MaxKey = 200;
    private const int MaxReason = 120;
    private const int MaxQueueText = 240;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly char[] Bad = { '|', '\r', '\n' };
    private static readonly string[] Empty = new string[0];

    public static string State(string scene, string key, float position, int moving, bool direction, bool doorsClosed, bool powered, int mask, string queue, int seq)
    {
        return string.Format(Inv, "ELE|{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}",
            scene, key, Num(position), moving, Flag(direction), Flag(doorsClosed), Flag(powered), mask, queue, seq);
    }

    public static string Request(string scene, string key, string op, int arg)
    {
        return string.Format(Inv, "ELQ|{0}|{1}|{2}|{3}", scene, key, op, arg);
    }

    public static string Answer(string scene, string key, bool ok, string reason)
    {
        return string.Format(Inv, "ELA|{0}|{1}|{2}|{3}", scene, key, Flag(ok), Clean(reason));
    }

    public static bool IsOp(string op)
    {
        return op == "call" || op == "up" || op == "down" || op == "enable" || op == "disable" || op == "power";
    }

    public static bool InScene(string scene)
    {
        string here = GameRefs.AreaId;
        return scene != null && here != null && scene == here;
    }

    public static bool TryParse(string payload, out ElevMsg msg)
    {
        msg = null;
        try
        {
            if (payload == null) return false;
            if (payload.Length > MaxPayload || Encoding.UTF8.GetByteCount(payload) > MaxPayload) return false;
            string[] f = payload.Split('|');
            ElevMsg m = new ElevMsg();
            m.Kind = f[0];
            if (!Fields(f, m)) return false;
            msg = m;
            return true;
        }
        catch (Exception)
        {
            msg = null;
            return false;
        }
    }

    private static bool Fields(string[] f, ElevMsg m)
    {
        switch (m.Kind)
        {
            case "ELE":
                if (f.Length != 10 && f.Length != 11) return false;
                if (!Text(f[1], MaxScene, out m.Scene)) return false;
                if (!Text(f[2], MaxKey, out m.Key)) return false;
                if (!Num(f[3], out m.Position)) return false;
                if (!Move(f[4], out m.Moving)) return false;
                if (!Flag(f[5], out m.Direction)) return false;
                if (!Flag(f[6], out m.DoorsClosed)) return false;
                if (!Flag(f[7], out m.Powered)) return false;
                if (!Count(f[8], out m.Mask)) return false;
                if (!Queue(f[9], out m.Queue, out m.QueueRaw)) return false;
                if (f.Length == 11 && !Seq(f[10], out m.Seq)) return false;
                return true;
            case "ELQ":
                if (f.Length != 5) return false;
                if (!Text(f[1], MaxScene, out m.Scene)) return false;
                if (!Text(f[2], MaxKey, out m.Key)) return false;
                if (!IsOp(f[3])) return false;
                m.Op = f[3];
                return Floor(f[4], out m.Arg);
            case "ELA":
                return f.Length == 5 && Text(f[1], MaxScene, out m.Scene) && Text(f[2], MaxKey, out m.Key)
                    && Flag(f[3], out m.Ok) && Note(f[4], out m.Reason);
            default:
                return false;
        }
    }

    private static string Num(float value)
    {
        return value.ToString("R", Inv);
    }

    private static string Flag(bool value)
    {
        return value ? "1" : "0";
    }

    private static string Clean(string text)
    {
        string plain = (text ?? "").Replace("|", "").Replace("\r", "").Replace("\n", "");
        return plain.Length > MaxReason ? plain.Substring(0, MaxReason) : plain;
    }

    private static bool Flag(string text, out bool value)
    {
        value = text == "1";
        return text == "0" || text == "1";
    }

    private static bool Move(string text, out int value)
    {
        return Int(text, -1, 1, out value);
    }

    private static bool Count(string text, out int value)
    {
        return Int(text, 0, int.MaxValue, out value);
    }

    private static bool Floor(string text, out int value)
    {
        return Int(text, -1, MaxFloors, out value);
    }

    private static bool Seq(string text, out int value)
    {
        return Int(text, 0, int.MaxValue, out value);
    }

    private static bool Int(string text, int min, int max, out int value)
    {
        return int.TryParse(text, NumberStyles.Integer, Inv, out value) && value >= min && value <= max;
    }

    private static bool Num(string text, out float value)
    {
        value = 0f;
        if (!float.TryParse(text, NumberStyles.Float, Inv, out value)) return false;
        if (float.IsNaN(value) || float.IsInfinity(value)) return false;
        return value >= -10000f && value <= 10000f;
    }

    private static bool Queue(string text, out string[] value, out string raw)
    {
        value = Empty;
        raw = text;
        if (text == null || text.Length > MaxQueueText) return false;
        if (text.Length == 0) return true;
        string[] parts = text.Split(',');
        if (parts.Length > MaxQueue) return false;
        for (int i = 0; i < parts.Length; i++)
        {
            float n;
            if (!Num(parts[i], out n)) return false;
        }
        value = parts;
        return true;
    }

    private static bool Text(string text, int max, out string value)
    {
        value = text;
        return text != null && text.Length >= 1 && text.Length <= max && text.IndexOfAny(Bad) < 0;
    }

    private static bool Note(string text, out string value)
    {
        value = text;
        return text != null && text.Length <= MaxReason;
    }
}

internal static class ElevatorRefs
{
    private const float ScanInterval = 5f;
    private const float MissInterval = 1f;
    private const int MaxKeyLen = 200;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Dictionary<string, Elevator> cars = new Dictionary<string, Elevator>();
    private static readonly Dictionary<Elevator, string> keys = new Dictionary<Elevator, string>();
    private static readonly List<Elevator> list = new List<Elevator>();
    private static FieldInfo movingField;
    private static FieldInfo directionField;
    private static FieldInfo doorsField;
    private static FieldInfo queueField;
    private static FieldInfo distancesField;
    private static FieldInfo directionsField;
    private static MethodInfo counterWeight;
    private static float nextScan;
    private static float nextMiss;
    private static bool scanned;

    public static List<Elevator> Cars
    {
        get { return list; }
    }

    public static void Reset()
    {
        cars.Clear();
        keys.Clear();
        list.Clear();
        nextScan = 0f;
        nextMiss = 0f;
        scanned = false;
    }

    public static bool Scan()
    {
        float now = Time.unscaledTime;
        if (scanned && now < nextScan) return false;
        ScanNow();
        return true;
    }

    private static void ScanNow()
    {
        nextScan = Time.unscaledTime + ScanInterval;
        scanned = true;
        cars.Clear();
        keys.Clear();
        list.Clear();
        UnityEngine.Object[] found = UnityEngine.Object.FindObjectsByType(typeof(Elevator), FindObjectsSortMode.None);
        for (int i = 0; i < found.Length; i++)
        {
            Elevator car = found[i] as Elevator;
            if (car == null) continue;
            string key = KeyOf(car);
            if (key == null || cars.ContainsKey(key)) continue;
            cars[key] = car;
            list.Add(car);
        }
    }

    public static Elevator CarOf(string key)
    {
        if (key == null) return null;
        Elevator found;
        if (cars.TryGetValue(key, out found) && found != null) return found;
        float now = Time.unscaledTime;
        if (now < nextMiss) return null;
        nextMiss = now + MissInterval;
        ScanNow();
        return cars.TryGetValue(key, out found) ? found : null;
    }

    public static void Note(Elevator car)
    {
        if (car == null) return;
        string key = KeyOf(car);
        if (key == null) return;
        if (!cars.ContainsKey(key)) cars[key] = car;
        if (!list.Contains(car)) list.Add(car);
    }

    public static string KeyOf(Elevator car)
    {
        if (car == null) return null;
        string known;
        if (keys.TryGetValue(car, out known)) return known;
        string key = null;
        try
        {
            key = !string.IsNullOrEmpty(car.GUID) ? car.GUID : CombatSync.KeyOf(car);
        }
        catch (Exception e)
        {
            CoopLog.Once("ElevatorRefs.KeyOf", e);
            return null;
        }
        if (key != null && key.Length > MaxKeyLen) key = "p:" + CombatSync.Fnv(key).ToString("x16", Inv);
        if (key != null) keys[car] = key;
        return key;
    }

    public static int Moving(Elevator car)
    {
        object value = Field(ref movingField, car, "moving");
        return value is int ? (int)value : 0;
    }

    public static void SetMoving(Elevator car, int value)
    {
        Field(ref movingField, car, "moving");
        if (movingField != null) movingField.SetValue(car, value);
    }

    public static bool Direction(Elevator car)
    {
        object value = Field(ref directionField, car, "direction");
        return value is bool && (bool)value;
    }

    public static void SetDirection(Elevator car, bool value)
    {
        Field(ref directionField, car, "direction");
        if (directionField != null) directionField.SetValue(car, value);
    }

    public static bool DoorsClosed(Elevator car)
    {
        object value = Field(ref doorsField, car, "doorsclosed");
        return !(value is bool) || (bool)value;
    }

    public static void SetDoorsClosed(Elevator car, bool value)
    {
        Field(ref doorsField, car, "doorsclosed");
        if (doorsField != null) doorsField.SetValue(car, value);
    }

    public static List<float> QueueOf(Elevator car)
    {
        object value = Field(ref queueField, car, "Queue");
        return value as List<float>;
    }

    public static string QueueText(Elevator car)
    {
        List<float> queue = QueueOf(car);
        if (queue == null || queue.Count == 0) return "";
        int count = queue.Count > ElevatorWire.MaxQueue ? ElevatorWire.MaxQueue : queue.Count;
        string text = "";
        for (int i = 0; i < count; i++) text += (i == 0 ? "" : ",") + queue[i].ToString("R", Inv);
        return text;
    }

    public static void SetQueue(Elevator car, string[] parts)
    {
        List<float> queue = QueueOf(car);
        if (queue == null) return;
        queue.Clear();
        if (parts == null) return;
        for (int i = 0; i < parts.Length && i < ElevatorWire.MaxQueue; i++)
        {
            float value;
            if (float.TryParse(parts[i], NumberStyles.Float, Inv, out value)) queue.Add(value);
        }
    }

    public static float[] Distances(Elevator car)
    {
        object value = Field(ref distancesField, car, "DistanceBetweenFloors");
        return value as float[];
    }

    public static Vector3[] Directions(Elevator car)
    {
        object value = Field(ref directionsField, car, "DirectionBetweenFloors");
        return value as Vector3[];
    }

    public static int Segment(float pos, int floors, int count)
    {
        int segment;
        if (pos >= floors - 1) segment = (int)Mathf.Floor(pos - 0.5f);
        else if (pos <= 0.25f) segment = 0;
        else segment = (int)Mathf.Floor(pos);
        if (segment < 0) segment = 0;
        if (segment > count - 1) segment = count - 1;
        return segment;
    }

    public static void Move(Elevator car, float pos, float[] distances, Vector3[] directions)
    {
        try
        {
            if (car.Floors == null || car.Floors.Length == 0 || distances == null || directions == null) return;
            if (distances.Length == 0) return;
            int floor = Mathf.FloorToInt(pos);
            if (floor < 0) floor = 0;
            if (floor > car.Floors.Length - 1) floor = car.Floors.Length - 1;
            Elevator.FloorData data = car.Floors[floor];
            if (data == null || data.position == null) return;
            int segment = Segment(pos, car.Floors.Length, distances.Length);
            car.transform.position = data.position.position + directions[segment] * distances[segment] * (pos - floor);
            CounterWeight(car);
        }
        catch (Exception e)
        {
            CoopLog.Once("ElevatorRefs.Move", e);
        }
    }

    private static void CounterWeight(Elevator car)
    {
        if (counterWeight == null) counterWeight = AccessTools.Method(typeof(Elevator), "CounterWeightMovement", Type.EmptyTypes);
        if (counterWeight == null) return;
        try
        {
            counterWeight.Invoke(car, null);
        }
        catch (Exception e)
        {
            CoopLog.Once("ElevatorRefs.CounterWeight", e);
        }
    }

    private static object Field(ref FieldInfo cache, Elevator car, string name)
    {
        if (cache == null) cache = AccessTools.Field(typeof(Elevator), name);
        return cache == null || car == null ? null : cache.GetValue(car);
    }
}

internal static class ElevatorLog
{
    private const float MinInterval = 1f;
    private const int MaxSites = 48;
    private static readonly Dictionary<string, float> next = new Dictionary<string, float>();

    public static void Info(string site, string message)
    {
        try
        {
            float now = Time.unscaledTime;
            float due;
            if (next.TryGetValue(site, out due) && now < due) return;
            if (next.Count > MaxSites) next.Clear();
            next[site] = now + MinInterval;
            CoopLog.Log.Info("coop: " + message);
        }
        catch (Exception)
        {
        }
    }

    public static void Reset()
    {
        next.Clear();
    }
}
