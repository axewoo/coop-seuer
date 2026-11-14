using System;
using System.Globalization;
using System.Reflection;
using UnityEngine;

internal sealed class SaunaMsg
{
    public string Kind;
    public int ReqId;
    public string Scene;
    public string Guid;
    public string Op;
    public string Reason;
    public int Logs;
    public bool On;
    public bool Ok;
    public float Temp;
    public float Steam;
}

internal static class SaunaWire
{
    public const string Channel = "sauna";
    public const string ReqLight = "REQLIGHT";
    public const string LightAck = "LIGHTACK";
    public const string Lit = "LIT";
    public const string State = "SAUNA";
    public const string ReqWater = "REQWATER";
    public const string WaterAck = "WATERACK";
    public const string OpOn = "on";
    public const string OpOff = "off";
    private const int MaxText = 200;
    private const int MaxReason = 120;
    private const int MaxReqId = 999999999;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly char[] Bad = { '|', '\r', '\n' };
    private static readonly FieldInfo SteamField = typeof(Sauna).GetField("l\u00F6yly", BindingFlags.Instance | BindingFlags.Public);

    public static string Req(int reqId, string scene, string guid, string op, int logs)
    {
        return string.Format(Inv, "REQLIGHT|{0}|{1}|{2}|{3}|{4}", reqId, scene, guid, op, logs);
    }

    public static string Ack(int reqId, bool ok, string reason)
    {
        return string.Format(Inv, "LIGHTACK|{0}|{1}|{2}", reqId, ok ? "1" : "0", Clean(reason));
    }

    public static string Fire(string scene, string guid, bool on)
    {
        return string.Format(Inv, "LIT|{0}|{1}|{2}", scene, guid, on ? "1" : "0");
    }

    public static string Values(string scene, string guid, bool on, float temp, float steam)
    {
        return string.Format(Inv, "SAUNA|{0}|{1}|{2}|{3}|{4}", scene, guid, on ? "1" : "0", Num(temp), Num(steam));
    }

    public static string Water(int reqId, string scene, string guid)
    {
        return string.Format(Inv, "REQWATER|{0}|{1}|{2}", reqId, scene, guid);
    }

    public static string WaterOk(int reqId, bool ok, string reason)
    {
        return string.Format(Inv, "WATERACK|{0}|{1}|{2}", reqId, ok ? "1" : "0", Clean(reason));
    }

    public static float Steam(Sauna sauna)
    {
        return SteamField == null ? 0f : (float)SteamField.GetValue(sauna);
    }

    public static void SetSteam(Sauna sauna, float value)
    {
        if (SteamField != null) SteamField.SetValue(sauna, value);
    }

    public static Sauna Find(string scene, string guid)
    {
        if (scene == null || guid == null || scene != GameRefs.AreaId) return null;
        return SaveController.GetObjectByGUID(guid, typeof(Sauna)) as Sauna;
    }

    private static string Num(float value)
    {
        return value.ToString("F2", Inv);
    }

    private static string Clean(string text)
    {
        string plain = (text ?? "").Replace("|", "").Replace("\r", "").Replace("\n", "");
        return plain.Length > MaxReason ? plain.Substring(0, MaxReason) : plain;
    }

    public static bool TryParse(string payload, out SaunaMsg msg)
    {
        msg = null;
        try
        {
            if (payload == null) return false;
            string[] f = payload.Split('|');
            SaunaMsg m = new SaunaMsg();
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

    private static bool Fields(string[] f, SaunaMsg m)
    {
        switch (m.Kind)
        {
            case "REQLIGHT":
                return f.Length == 6 && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[2], out m.Scene) && Text(f[3], out m.Guid)
                    && Op(f[4], out m.Op) && Int(f[5], 0, 999, out m.Logs);
            case "LIGHTACK":
            case "WATERACK":
                return f.Length == 4 && Int(f[1], 1, MaxReqId, out m.ReqId) && Flag(f[2], out m.Ok) && Note(f[3], out m.Reason);
            case "LIT":
                return f.Length == 4 && Text(f[1], out m.Scene) && Text(f[2], out m.Guid) && Flag(f[3], out m.On);
            case "SAUNA":
                return f.Length == 6 && Text(f[1], out m.Scene) && Text(f[2], out m.Guid) && Flag(f[3], out m.On)
                    && Float(f[4], out m.Temp) && Float(f[5], out m.Steam);
            case "REQWATER":
                return f.Length == 4 && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[2], out m.Scene) && Text(f[3], out m.Guid);
            default:
                return false;
        }
    }

    private static bool Op(string text, out string value)
    {
        value = text;
        return text == OpOn || text == OpOff;
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

    private static bool Float(string text, out float value)
    {
        return float.TryParse(text, NumberStyles.Float, Inv, out value);
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
