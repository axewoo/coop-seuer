using System;
using System.Globalization;

internal sealed class VendMsg
{
    public string Kind;
    public int ReqId;
    public string Guid;
    public int Index = -1;
    public bool Ok;
    public string Reason;
    public int Stock = -1;
}

internal static class VendWire
{
    public const string Channel = "vend";
    private const int MaxReqId = 999999999;
    private const int MaxText = 120;
    private const int MaxReason = 120;
    private const int MaxIndex = 4096;
    private const int MaxStock = 100000;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly char[] Bad = { '|', '\r', '\n' };

    public static string Req(int reqId, string guid, int index)
    {
        return string.Format(Inv, "VENDREQ|{0}|{1}|{2}", reqId, Clean(guid), index);
    }

    public static string Ack(int reqId, bool ok, string reason, int stock)
    {
        return string.Format(Inv, "VACK|{0}|{1}|{2}|{3}", reqId, ok ? 1 : 0, Clean(reason), stock < 0 ? -1 : stock);
    }

    public static string Nack(int reqId, string guid, int index)
    {
        return string.Format(Inv, "VNACK|{0}|{1}|{2}", reqId, Clean(guid), index);
    }

    public static bool TryParse(string payload, out VendMsg msg)
    {
        msg = null;
        try
        {
            if (payload == null) return false;
            string[] f = payload.Split('|');
            VendMsg m = new VendMsg();
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

    public static VendingMachine.VendingMachineItem Item(VendingMachine machine, int index)
    {
        if (machine == null || machine.sellableItems == null) return null;
        if (index < 0 || index >= machine.sellableItems.Length) return null;
        return machine.sellableItems[index];
    }

    public static int IndexOf(VendingMachine machine, VendingMachine.VendingMachineItem item)
    {
        if (machine == null || machine.sellableItems == null || item == null) return -1;
        return Array.IndexOf(machine.sellableItems, item);
    }

    private static bool Fields(string[] f, VendMsg m)
    {
        switch (m.Kind)
        {
            case "VENDREQ":
                return f.Length == 4 && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[2], out m.Guid) && Int(f[3], 0, MaxIndex, out m.Index);
            case "VACK":
                return f.Length == 5 && Int(f[1], 1, MaxReqId, out m.ReqId) && Flag(f[2], out m.Ok) && Note(f[3], out m.Reason) && Int(f[4], -1, MaxStock, out m.Stock);
            case "VNACK":
                return f.Length == 4 && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[2], out m.Guid) && Int(f[3], 0, MaxIndex, out m.Index);
            default:
                return false;
        }
    }

    private static string Clean(string text)
    {
        string plain = (text ?? "").Replace("|", "").Replace("\r", "").Replace("\n", "");
        return plain.Length > MaxText ? plain.Substring(0, MaxText) : plain;
    }

    private static bool Int(string text, int min, int max, out int value)
    {
        return int.TryParse(text, NumberStyles.Integer, Inv, out value) && value >= min && value <= max;
    }

    private static bool Flag(string text, out bool value)
    {
        value = text == "1";
        return text == "1" || text == "0";
    }

    private static bool Text(string text, out string value)
    {
        value = text;
        return !string.IsNullOrEmpty(text) && text.Length <= MaxText && text.IndexOfAny(Bad) < 0;
    }

    private static bool Note(string text, out string value)
    {
        value = text;
        return text != null && text.IndexOfAny(Bad) < 0;
    }
}
