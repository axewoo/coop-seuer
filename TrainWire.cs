using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

internal sealed class TrainMsg
{
    public string Kind;
    public int ReqId;
    public string Scene;
    public string Guid;
    public bool On;
    public bool Braking;
    public float Speed;
    public float Target;
    public string FrontSeg;
    public float FrontPos;
    public int FrontDir;
    public string BackSeg;
    public float BackPos;
    public int BackDir;
    public ulong Driver;
    public string Cmd;
    public float Seconds;
    public int Index;
    public bool Ok;
    public string Reason;
}

internal static class TrainWire
{
    public const string Channel = "train";
    private const int MaxScene = 80;
    private const int MaxText = 200;
    private const int MaxReason = 120;
    public const float MaxCommandSeconds = 0.2f;
    private const int MaxReqId = 999999999;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly char[] Bad = { '|', '\r', '\n' };

    public static string Snapshot(string scene, string guid, bool on, bool braking, float speed, float target, string frontSeg, float frontPos, int frontDir, string backSeg, float backPos, int backDir, ulong driver)
    {
        return string.Format(Inv, "TRN|{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}|{10}|{11}|{12}",
            scene, guid, Flag(on), Flag(braking), Num(speed), Num(target), frontSeg, Num(frontPos), frontDir, backSeg, Num(backPos), backDir, driver);
    }

    public static string ReqStart(int reqId, string scene, string guid, bool on)
    {
        return string.Format(Inv, "REQSTART|{0}|{1}|{2}|{3}", reqId, scene, guid, Flag(on));
    }

    public static string StartAck(int reqId, bool ok, string reason)
    {
        return string.Format(Inv, "STARTACK|{0}|{1}|{2}", reqId, Flag(ok), Clean(reason));
    }

    public static string ReqDrv(int reqId, string scene, string guid)
    {
        return string.Format(Inv, "REQDRV|{0}|{1}|{2}", reqId, scene, guid);
    }

    public static string DrvAck(int reqId, bool ok, string reason)
    {
        return string.Format(Inv, "DRVACK|{0}|{1}|{2}", reqId, Flag(ok), Clean(reason));
    }

    public static string Rel(string scene, string guid)
    {
        return string.Format(Inv, "REL|{0}|{1}", scene, guid);
    }

    public static string Drv(string scene, string guid, string cmd, float seconds)
    {
        return string.Format(Inv, "DRV|{0}|{1}|{2}|{3}", scene, guid, cmd, Num(seconds));
    }

    public static string Swt(string scene, string guid, int index)
    {
        return string.Format(Inv, "SWT|{0}|{1}|{2}", scene, guid, index);
    }

    public static string ReqSwt(int reqId, string scene, string guid, int index)
    {
        return string.Format(Inv, "REQSWT|{0}|{1}|{2}|{3}", reqId, scene, guid, index);
    }

    public static string SwtAck(int reqId, bool ok, string reason)
    {
        return string.Format(Inv, "SWTACK|{0}|{1}|{2}", reqId, Flag(ok), Clean(reason));
    }

    public static bool IsCmd(string cmd)
    {
        return cmd == "F" || cmd == "B" || cmd == "N" || cmd == "K" || cmd == "R";
    }

    public static bool TryParse(string payload, out TrainMsg msg)
    {
        msg = null;
        try
        {
            if (payload == null) return false;
            string[] f = payload.Split('|');
            TrainMsg m = new TrainMsg();
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

    private static bool Fields(string[] f, TrainMsg m)
    {
        switch (m.Kind)
        {
            case "TRN":
                return f.Length == 14 && Text(f[1], out m.Scene) && Text(f[2], out m.Guid) && Flag(f[3], out m.On) && Flag(f[4], out m.Braking)
                    && Num(f[5], out m.Speed) && Num(f[6], out m.Target)
                    && Text(f[7], out m.FrontSeg) && Num(f[8], out m.FrontPos) && Dir(f[9], out m.FrontDir)
                    && Text(f[10], out m.BackSeg) && Num(f[11], out m.BackPos) && Dir(f[12], out m.BackDir) && ULong(f[13], out m.Driver);
            case "REQSTART":
                return f.Length == 5 && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[2], out m.Scene) && Text(f[3], out m.Guid) && Flag(f[4], out m.On);
            case "STARTACK":
            case "DRVACK":
            case "SWTACK":
                return f.Length == 4 && Int(f[1], 1, MaxReqId, out m.ReqId) && Flag(f[2], out m.Ok) && Note(f[3], out m.Reason);
            case "REQDRV":
                return f.Length == 4 && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[2], out m.Scene) && Text(f[3], out m.Guid);
            case "REL":
                return f.Length == 3 && Text(f[1], out m.Scene) && Text(f[2], out m.Guid);
            case "DRV":
                return f.Length == 5 && Text(f[1], out m.Scene) && Text(f[2], out m.Guid) && Text(f[3], out m.Cmd) && IsCmd(m.Cmd) && Secs(f[4], out m.Seconds);
            case "SWT":
                return f.Length == 4 && Text(f[1], out m.Scene) && Text(f[2], out m.Guid) && Int(f[3], 0, 64, out m.Index);
            case "REQSWT":
                return f.Length == 5 && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[2], out m.Scene) && Text(f[3], out m.Guid) && Int(f[4], 0, 64, out m.Index);
            default:
                return false;
        }
    }

    public static bool InScene(string scene)
    {
        string here = GameRefs.AreaId;
        return scene != null && here != null && scene == here;
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

    private static bool Dir(string text, out int value)
    {
        value = text == "1" ? 1 : 0;
        return text == "0" || text == "1";
    }

    private static bool Int(string text, int min, int max, out int value)
    {
        return int.TryParse(text, NumberStyles.Integer, Inv, out value) && value >= min && value <= max;
    }

    private static bool ULong(string text, out ulong value)
    {
        return ulong.TryParse(text, NumberStyles.None, Inv, out value);
    }

    private static bool Secs(string text, out float value)
    {
        value = 0f;
        if (!Num(text, out value)) return false;
        if (value < 0f) return false;
        if (value > MaxCommandSeconds) value = MaxCommandSeconds;
        return true;
    }

    private static bool Num(string text, out float value)
    {
        value = 0f;
        if (!float.TryParse(text, NumberStyles.Float, Inv, out value)) return false;
        if (float.IsNaN(value) || float.IsInfinity(value)) return false;
        return value >= -10000f && value <= 10000f;
    }

    private static bool Text(string text, out string value)
    {
        value = text;
        return text != null && text.Length >= 1 && text.Length <= MaxText && text.IndexOfAny(Bad) < 0;
    }

    private static bool Note(string text, out string value)
    {
        value = text;
        return text != null && text.Length <= MaxReason;
    }
}

internal static class TrainRefs
{
    private const float ScanInterval = 1f;
    private static readonly Dictionary<string, Minitrain> minitrains = new Dictionary<string, Minitrain>();
    private static readonly Dictionary<string, MinitrainSwitch> switches = new Dictionary<string, MinitrainSwitch>();
    private static FieldInfo rotations;
    private static float nextScan;

    public static void Reset()
    {
        minitrains.Clear();
        switches.Clear();
        nextScan = 0f;
    }

    public static Minitrain MinitrainOf(string trainGuid)
    {
        if (trainGuid == null) return null;
        Scan();
        Minitrain found;
        return minitrains.TryGetValue(trainGuid, out found) ? found : null;
    }

    public static MinitrainSwitch SwitchOf(string guid)
    {
        if (guid == null) return null;
        Scan();
        MinitrainSwitch found;
        return switches.TryGetValue(guid, out found) ? found : null;
    }

    public static int BranchCount(MinitrainSwitch entry)
    {
        if (entry == null) return 0;
        if (rotations == null) rotations = typeof(MinitrainSwitch).GetField("branchRotations", BindingFlags.Instance | BindingFlags.NonPublic);
        if (rotations == null) return 0;
        Vector3[] value = rotations.GetValue(entry) as Vector3[];
        return value == null ? 0 : value.Length;
    }

    public static string TrainGuidOf(Minitrain minitrain)
    {
        TrackTrain train = minitrain == null ? null : minitrain.GetTrain();
        return train == null ? null : train.GUID;
    }

    private static void Scan()
    {
        float now = Time.unscaledTime;
        if (now < nextScan) return;
        nextScan = now + ScanInterval;
        minitrains.Clear();
        switches.Clear();
        UnityEngine.Object[] found = UnityEngine.Object.FindObjectsByType(typeof(Minitrain), FindObjectsSortMode.None);
        for (int i = 0; i < found.Length; i++)
        {
            Minitrain minitrain = found[i] as Minitrain;
            if (minitrain == null) continue;
            string guid = TrainGuidOf(minitrain);
            if (guid != null) minitrains[guid] = minitrain;
        }
        UnityEngine.Object[] all = UnityEngine.Object.FindObjectsByType(typeof(MinitrainSwitch), FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            MinitrainSwitch entry = all[i] as MinitrainSwitch;
            if (entry != null && !string.IsNullOrEmpty(entry.GUID)) switches[entry.GUID] = entry;
        }
    }
}
