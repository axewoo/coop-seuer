using System;
using System.Globalization;
using System.Text;
using PixelCrushers.DialogueSystem;
using UnityEngine;

internal sealed class TalkMsg
{
    public string Kind;
    public string Raw;
    public int ReqId;
    public string Npc;
    public string Reason;
    public ulong Holder;
    public string Table;
    public string Element;
    public string Field;
    public string Type;
    public string Value;
}

internal static class TalkWire
{
    public const string Channel = "talk";
    private const int MaxReqId = 999999999;
    private const int MaxNpc = 200;
    private const int MaxText = 200;
    private const int MaxReason = 120;
    private const int MaxValue = 200;
    private const int MaxNewVariable = 128;
    private const string VarKind = "V";
    private const string FieldKind = "F";
    private const string HostVarKind = "THVAR";
    private const string HostWantKind = "THWANT";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly char[] Bad = { '|', '\r', '\n' };
    private static readonly string[] Tables = { "Variable", "Actor", "Item", "Location", "Quest" };
    private static readonly string[] BlockedWords = { "money", "bank", "crime", "fine", "wallet", "cash", "bribe", "debt", "wanted", "criminal", "arrest", "arrested" };
    private static readonly string[] PerPlayer = { "Actor", "Conversant", "Speaker", "Player", "Current", "Saved", "Random", "Currency", "Price", "Day", "Intro", "BusIntro", "Soundscape", "WakeUp", "Alert" };

    public static string Req(int reqId, string npc)
    {
        return string.Format(Inv, "TREQ|{0}|{1}", reqId, npc);
    }

    public static string Ok(int reqId, string npc)
    {
        return string.Format(Inv, "TOK|{0}|{1}", reqId, npc);
    }

    public static string Deny(int reqId, string reason)
    {
        return string.Format(Inv, "TDENY|{0}|{1}", reqId, Clean(reason, MaxReason));
    }

    public static string End(string npc)
    {
        return "TEND|" + npc;
    }

    public static string Revoke(string npc)
    {
        return "TREV|" + npc;
    }

    public static string Lock(string npc, ulong holder)
    {
        return string.Format(Inv, "TLOCK|{0}|{1}", npc, holder);
    }

    public static string Var(string npc, string name, object value)
    {
        string type;
        string text = Encode(value, out type);
        return string.Format(Inv, "TVARS|{0}|{1}|{2}|{3}|{4}", npc, VarKind, Clean(name, MaxText), type, text);
    }

    public static string Field(string npc, string table, string element, string field, object value)
    {
        string type;
        string text = Encode(value, out type);
        return string.Format(Inv, "TVARS|{0}|{1}|{2}|{3}|{4}|{5}|{6}", npc, FieldKind, Clean(table, MaxText), Clean(element, MaxText), Clean(field, MaxText), type, text);
    }

    public static string HostVar(string name, object value)
    {
        string type;
        string text = Encode(value, out type);
        return string.Format(Inv, "{0}|{1}|{2}|{3}", HostVarKind, Clean(name, MaxText), type, text);
    }

    public static string HostVarWant()
    {
        return HostWantKind;
    }

    public static bool HostVarOk(TalkMsg m)
    {
        string reason;
        return m != null && m.Kind == HostVarKind && KnownType(m.Type) && Shared(m.Table) && ValueOk(m.Type, m.Value, out reason);
    }

    public static void ApplyHostVar(TalkMsg m)
    {
        DialogueLua.SetVariable(m.Table, Decode(m.Type, m.Value));
    }

    public static bool Shared(string name)
    {
        if (name == null || name.Length == 0 || name.Length > MaxText) return false;
        if (Blocked(name)) return false;
        for (int i = 0; i < PerPlayer.Length; i++)
        {
            if (name.StartsWith(PerPlayer[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    public static void Apply(TalkMsg m)
    {
        object value = Decode(m.Type, m.Value);
        if (m.Kind == VarKind) DialogueLua.SetVariable(m.Table, value);
        else DialogueLua.SetTableField(m.Table, m.Element, m.Field, value);
    }

    public static bool IsWrite(TalkMsg m)
    {
        return m.Kind == VarKind || m.Kind == FieldKind;
    }

    public static bool Valid(TalkMsg m, out string reason)
    {
        reason = "not a write";
        try
        {
            if (m == null || !IsWrite(m)) return false;
            if (!KnownType(m.Type))
            {
                reason = "unknown type";
                return false;
            }
            if (m.Kind == VarKind)
            {
                if (m.Table == null || m.Table.Length == 0)
                {
                    reason = "empty name";
                    return false;
                }
                if (Blocked(m.Table))
                {
                    reason = "blocked name";
                    return false;
                }
                if (!DialogueLua.DoesVariableExist(m.Table) && m.Table.Length > MaxNewVariable)
                {
                    reason = "new name too long";
                    return false;
                }
            }
            else
            {
                if (!TableOk(m.Table))
                {
                    reason = "table not allowed";
                    return false;
                }
                if (Blocked(m.Element + "." + m.Field))
                {
                    reason = "blocked name";
                    return false;
                }
                if (!DialogueLua.DoesTableElementExist(m.Table, m.Element))
                {
                    reason = "no such element";
                    return false;
                }
                if (!DialogueLua.GetTableField(m.Table, m.Element, m.Field).hasReturnValue)
                {
                    reason = "no such field";
                    return false;
                }
            }
            return ValueOk(m.Type, m.Value, out reason);
        }
        catch (Exception e)
        {
            CoopLog.Once("TalkWire.Valid", e);
            reason = "check failed";
            return false;
        }
    }

    public static bool NewVariable(TalkMsg m)
    {
        try
        {
            return m != null && m.Kind == VarKind && m.Table != null && !DialogueLua.DoesVariableExist(m.Table);
        }
        catch (Exception e)
        {
            CoopLog.Once("TalkWire.NewVariable", e);
            return false;
        }
    }

    private static bool ValueOk(string type, string text, out string reason)
    {
        reason = "bad value";
        bool ok = false;
        if (text != null)
        {
            if (type == "n") ok = text.Length == 0;
            else if (type == "b") ok = text == "0" || text == "1";
            else if (type == "s") ok = true;
            else if (type == "i")
            {
                long whole;
                ok = long.TryParse(text, NumberStyles.Integer, Inv, out whole);
            }
            else if (type == "f")
            {
                float small;
                ok = float.TryParse(text, NumberStyles.Float, Inv, out small) && !float.IsNaN(small) && !float.IsInfinity(small);
            }
            else if (type == "d")
            {
                double wide;
                ok = double.TryParse(text, NumberStyles.Float, Inv, out wide) && !double.IsNaN(wide) && !double.IsInfinity(wide);
            }
        }
        if (ok) reason = "";
        return ok;
    }

    private static bool KnownType(string type)
    {
        return type == "n" || type == "b" || type == "i" || type == "f" || type == "d" || type == "s";
    }

    private static bool TableOk(string table)
    {
        if (table == null) return false;
        for (int i = 0; i < Tables.Length; i++)
        {
            if (Tables[i] == table) return true;
        }
        return false;
    }

    public static bool Blocked(string name)
    {
        if (name == null) return false;
        int start = 0;
        for (int i = 0; i <= name.Length; i++)
        {
            if (i == name.Length || !char.IsLetterOrDigit(name[i]))
            {
                if (i > start && BlockedWord(name.Substring(start, i - start))) return true;
                start = i + 1;
            }
            else if (i > start && char.IsUpper(name[i]))
            {
                bool afterLower = !char.IsUpper(name[i - 1]);
                bool beforeLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (afterLower || beforeLower)
                {
                    if (BlockedWord(name.Substring(start, i - start))) return true;
                    start = i;
                }
            }
        }
        return false;
    }

    private static bool BlockedWord(string word)
    {
        string lower = word.ToLowerInvariant();
        for (int i = 0; i < BlockedWords.Length; i++)
        {
            if (BlockedWords[i] == lower) return true;
        }
        return false;
    }

    public static string Encode(object value, out string type)
    {
        if (value == null)
        {
            type = "n";
            return "";
        }
        string text = value as string;
        if (text != null)
        {
            type = "s";
            return Clean(text, MaxValue);
        }
        if (value is bool)
        {
            type = "b";
            return ((bool)value) ? "1" : "0";
        }
        if (value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint)
        {
            type = "i";
            return Convert.ToInt64(value).ToString(Inv);
        }
        if (value is long || value is ulong)
        {
            type = "d";
            return Convert.ToDouble(value).ToString("R", Inv);
        }
        if (value is float)
        {
            type = "f";
            return ((float)value).ToString("R", Inv);
        }
        if (value is double)
        {
            type = "d";
            return ((double)value).ToString("R", Inv);
        }
        type = "s";
        return Clean(value.ToString(), MaxValue);
    }

    public static object Decode(string type, string text)
    {
        if (type == "n") return null;
        if (type == "b") return text == "1";
        if (type == "i")
        {
            int whole;
            if (int.TryParse(text, NumberStyles.Integer, Inv, out whole)) return whole;
            double wide;
            return double.TryParse(text, NumberStyles.Float, Inv, out wide) ? (object)wide : null;
        }
        if (type == "f")
        {
            float small;
            return float.TryParse(text, NumberStyles.Float, Inv, out small) ? (object)small : null;
        }
        if (type == "d")
        {
            double wide;
            return double.TryParse(text, NumberStyles.Float, Inv, out wide) ? (object)wide : null;
        }
        return text;
    }

    public static string NpcKey(InteractableTalk talk)
    {
        if (talk == null) return null;
        string area = GameRefs.AreaId;
        string path = Path(talk.transform);
        if (path.Length == 0) return null;
        return "n-" + Tag((area == null ? "?" : area) + "|" + path);
    }

    public static bool TryParse(string payload, out TalkMsg msg)
    {
        msg = null;
        try
        {
            if (payload == null) return false;
            string[] f = payload.Split('|');
            TalkMsg m = new TalkMsg();
            m.Kind = f[0];
            m.Raw = payload;
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

    private static bool Fields(string[] f, TalkMsg m)
    {
        switch (m.Kind)
        {
            case "TREQ":
                return f.Length == 3 && Id(f[1], out m.ReqId) && Text(f[2], out m.Npc);
            case "TOK":
                return f.Length == 3 && Id(f[1], out m.ReqId) && Text(f[2], out m.Npc);
            case "TDENY":
                return f.Length == 3 && Id(f[1], out m.ReqId) && Note(f[2], out m.Reason);
            case "TEND":
                return f.Length == 2 && Text(f[1], out m.Npc);
            case "TREV":
                return f.Length == 2 && Text(f[1], out m.Npc);
            case "TLOCK":
                return f.Length == 3 && Text(f[1], out m.Npc) && ulong.TryParse(f[2], NumberStyles.None, Inv, out m.Holder);
            case "THWANT":
                return f.Length == 1;
            case "THVAR":
                if (f.Length != 4 || !Joined(f[1]) || f[2].Length != 1 || f[3].Length > MaxValue) return false;
                m.Table = f[1];
                m.Element = null;
                m.Field = null;
                m.Type = f[2];
                m.Value = f[3];
                return true;
            case "TVARS":
                if (f.Length == 6 && Text(f[1], out m.Npc) && f[2] == VarKind)
                {
                    m.Table = f[3];
                    m.Element = null;
                    m.Field = null;
                    m.Type = f[4];
                    m.Value = f[5];
                    return Joined(f[3]) && m.Type.Length == 1 && m.Value.Length <= MaxValue;
                }
                if (f.Length == 8 && Text(f[1], out m.Npc) && f[2] == FieldKind)
                {
                    m.Table = f[3];
                    m.Element = f[4];
                    m.Field = f[5];
                    m.Type = f[6];
                    m.Value = f[7];
                    return Joined(f[3]) && Joined(f[4]) && Joined(f[5]) && m.Type.Length == 1 && m.Value.Length <= MaxValue;
                }
                return false;
            default:
                return false;
        }
    }

    private static bool Joined(string text)
    {
        return text != null && text.Length >= 1 && text.Length <= MaxText;
    }

    private static string Path(Transform step)
    {
        StringBuilder text = new StringBuilder();
        while (step != null)
        {
            if (text.Length > 0) text.Insert(0, '/');
            text.Insert(0, Ordinal(step).ToString(Inv) + ":" + step.name);
            step = step.parent;
        }
        return text.ToString();
    }

    private static int Ordinal(Transform step)
    {
        Transform parent = step.parent;
        if (parent == null) return 0;
        int index = 0;
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child == step) break;
            if (string.Equals(child.name, step.name, StringComparison.Ordinal)) index++;
        }
        return index;
    }

    private static string Tag(string key)
    {
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < key.Length; i++)
        {
            hash ^= key[i];
            hash *= 1099511628211UL;
        }
        char[] hex = new char[16];
        for (int i = hex.Length - 1; i >= 0; i--)
        {
            hex[i] = Hex(hash & 0xF);
            hash >>= 4;
        }
        return new string(hex);
    }

    private static char Hex(ulong value)
    {
        return value < 10 ? (char)(48 + value) : (char)(97 + (value - 10));
    }

    private static string Clean(string text, int max)
    {
        string plain = (text ?? "").Replace("|", "/").Replace("\r", "").Replace("\n", "");
        return plain.Length > max ? plain.Substring(0, max) : plain;
    }

    private static bool Id(string text, out int value)
    {
        return int.TryParse(text, NumberStyles.Integer, Inv, out value) && value >= 1 && value <= MaxReqId;
    }

    private static bool Text(string text, out string value)
    {
        value = text;
        return text != null && text.Length >= 1 && text.Length <= MaxNpc && text.IndexOfAny(Bad) < 0;
    }

    private static bool Note(string text, out string value)
    {
        value = text;
        return text != null && text.Length <= MaxReason;
    }
}
