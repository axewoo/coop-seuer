using System;
using System.Globalization;
using UnityEngine;

internal sealed class SlotMsg
{
    public string Kind;
    public int ReqId;
    public string Scene;
    public string Key;
    public string Reason;
    public bool Ok;
    public int SpinId;
    public ulong Player;
    public int StakeIndex;
    public int Line;
    public int R0;
    public int R1;
    public int R2;
}

internal static class SlotWire
{
    public const string Channel = "slot";
    public const string ReqSpin = "REQSPIN";
    public const string SpinAck = "SPINACK";
    public const string SpinKind = "SPIN";
    private const int MaxText = 200;
    private const int MaxKey = 160;
    private const int MaxReason = 120;
    private const int MaxReqId = 999999999;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly char[] Bad = { '|', '\r', '\n' };

    public static string Req(int reqId, string scene, string key, int stakeIndex)
    {
        return string.Format(Inv, "REQSPIN|{0}|{1}|{2}|{3}", reqId, scene, key, stakeIndex);
    }

    public static string Ack(int reqId, bool ok, string reason)
    {
        return string.Format(Inv, "SPINACK|{0}|{1}|{2}", reqId, ok ? "1" : "0", Clean(reason));
    }

    public static string Spin(string scene, string key, int spinId, ulong player, int stakeIndex, int line, int r0, int r1, int r2)
    {
        return string.Format(Inv, "SPIN|{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}", scene, key, spinId, player, stakeIndex, line, r0, r1, r2);
    }

    public static string KeyOf(SlotMachineGameplay machine)
    {
        if (machine == null) return null;
        string path = Path(machine.transform);
        return path.Length <= MaxKey && path.IndexOfAny(Bad) < 0 ? path : null;
    }

    public static SlotMachineGameplay Find(string scene, string key)
    {
        if (scene == null || key == null || scene != GameRefs.AreaId) return null;
        SlotMachineGameplay[] all = UnityEngine.Object.FindObjectsByType<SlotMachineGameplay>(FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && KeyOf(all[i]) == key) return all[i];
        }
        return null;
    }

    private static string Path(Transform step)
    {
        return step.parent == null ? step.name : Path(step.parent) + "/" + step.name;
    }

    public static bool TryParse(string payload, out SlotMsg msg)
    {
        msg = null;
        try
        {
            if (payload == null) return false;
            string[] f = payload.Split('|');
            SlotMsg m = new SlotMsg();
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

    private static bool Fields(string[] f, SlotMsg m)
    {
        switch (m.Kind)
        {
            case "REQSPIN":
                return f.Length == 5 && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[2], MaxText, out m.Scene) && Text(f[3], MaxKey, out m.Key) && Int(f[4], 0, 99, out m.StakeIndex);
            case "SPINACK":
                return f.Length == 4 && Int(f[1], 1, MaxReqId, out m.ReqId) && Flag(f[2], out m.Ok) && Note(f[3], out m.Reason);
            case "SPIN":
                return f.Length == 10 && Text(f[1], MaxText, out m.Scene) && Text(f[2], MaxKey, out m.Key) && Int(f[3], 1, MaxReqId, out m.SpinId)
                    && ULong(f[4], out m.Player) && Int(f[5], 0, 99, out m.StakeIndex) && Int(f[6], 0, 2, out m.Line)
                    && Int(f[7], 0, 99, out m.R0) && Int(f[8], 0, 99, out m.R1) && Int(f[9], 0, 99, out m.R2);
            default:
                return false;
        }
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

    private static bool Int(string text, int min, int max, out int value)
    {
        return int.TryParse(text, NumberStyles.Integer, Inv, out value) && value >= min && value <= max;
    }

    private static bool ULong(string text, out ulong value)
    {
        return ulong.TryParse(text, NumberStyles.None, Inv, out value);
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
