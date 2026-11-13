using System;
using System.Globalization;

internal sealed class CraftMsg
{
    public string Kind;
    public int ReqId;
    public string Scene;
    public string Key;
    public int RecipeId;
    public bool Ok;
    public bool Progress;
    public string Reason;
    public int Skill = -1;
    public int SkillType;
    public float Amount;
    public float Effect = -1f;
}

internal static class CraftWire
{
    public const string Channel = "craft";
    public const string ModId = ContainerWire.ModId;
    public const string BlobPrefix = "craft-state-";
    private const int MaxScene = 64;
    private const int MaxKey = 200;
    private const int MaxReason = 120;
    private const int MaxReqId = 999999999;
    private const int MaxRecipeId = 1000000;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly char[] Bad = { '|', '\r', '\n' };

    public static string ReqCraft(int reqId, string scene, string key, int recipeId, int skill = -1, int skillType = 0, float effect = -1f)
    {
        if (skill < 0) return string.Format(Inv, "REQCRAFT|{0}|{1}|{2}|{3}", reqId, Clean(scene, MaxScene), Clean(key, MaxKey), recipeId);
        string head = string.Format(Inv, "REQCRAFT|{0}|{1}|{2}|{3}|{4}|{5}", reqId, Clean(scene, MaxScene), Clean(key, MaxKey), recipeId, skill, skillType);
        return effect < 0f ? head : head + "|" + effect.ToString("R", Inv);
    }

    public static string SkillGain(int skillType, float amount)
    {
        return string.Format(Inv, "CRAFTSKILL|{0}|{1}", skillType, amount.ToString("R", Inv));
    }

    public static string Ack(int reqId, bool ok, string reason)
    {
        return string.Format(Inv, "CRAFTACK|{0}|{1}|{2}", reqId, ok ? 1 : 0, Clean(reason, MaxReason));
    }

    public static string Station(string scene, string key, bool progress)
    {
        return string.Format(Inv, "STATION|{0}|{1}|{2}", Clean(scene, MaxScene), Clean(key, MaxKey), progress ? 1 : 0);
    }

    public static string BlobNameOf(string key)
    {
        return key == null ? BlobPrefix : BlobPrefix + ContainerWire.Tag(key);
    }

    public static bool IsBlobName(string name)
    {
        return name != null && name.StartsWith(BlobPrefix, StringComparison.Ordinal);
    }

    public static bool TryParse(string payload, out CraftMsg msg)
    {
        msg = null;
        try
        {
            if (payload == null) return false;
            string[] f = payload.Split('|');
            CraftMsg m = new CraftMsg();
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

    private static bool Fields(string[] f, CraftMsg m)
    {
        switch (m.Kind)
        {
            case "REQCRAFT":
                if (f.Length == 5) return Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[2], MaxScene, out m.Scene)
                    && Text(f[3], MaxKey, out m.Key) && Int(f[4], 0, MaxRecipeId, out m.RecipeId);
                if (f.Length != 7 && f.Length != 8) return false;
                return Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[2], MaxScene, out m.Scene)
                    && Text(f[3], MaxKey, out m.Key) && Int(f[4], 0, MaxRecipeId, out m.RecipeId)
                    && Int(f[5], 0, 1000, out m.Skill) && Int(f[6], 0, 1000, out m.SkillType)
                    && (f.Length == 7 || Flt(f[7], out m.Effect));
            case "CRAFTSKILL":
                return f.Length == 3 && Int(f[1], 0, 1000, out m.SkillType) && Flt(f[2], out m.Amount);
            case "CRAFTACK":
                return f.Length == 4 && Int(f[1], 1, MaxReqId, out m.ReqId) && Flag(f[2], out m.Ok) && Note(f[3], out m.Reason);
            case "STATION":
                return f.Length == 4 && Text(f[1], MaxScene, out m.Scene) && Text(f[2], MaxKey, out m.Key) && Flag(f[3], out m.Progress);
            default:
                return false;
        }
    }

    private static string Clean(string text, int max)
    {
        string plain = (text ?? "").Replace("|", "").Replace("\r", "").Replace("\n", "");
        return plain.Length > max ? plain.Substring(0, max) : plain;
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

    private static bool Flt(string text, out float value)
    {
        return float.TryParse(text, NumberStyles.Float, Inv, out value) && value >= 0f && value <= 1000f;
    }

    private static bool Text(string text, int max, out string value)
    {
        value = text;
        return !string.IsNullOrEmpty(text) && text.Length <= max && text.IndexOfAny(Bad) < 0;
    }

    private static bool Note(string text, out string value)
    {
        value = text;
        return text != null && text.Length <= MaxReason;
    }
}
