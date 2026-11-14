using System;
using System.Collections.Generic;
using System.Globalization;
using ObenseuerCore;

internal sealed class Look
{
    private const int FieldCount = 37;
    private const int MaxIndex = 9999;
    private const float MaxValue = 2f;
    private const float MinHeight = 0f;
    private const float MaxHeight = 1.5f;

    public string Raw;
    public bool Male;
    public bool Advanced;
    public float Height;
    public float Shape;
    public float Gender;
    public float HeadSize;
    public float FaceSize;
    public float NeckLength;
    public float ShoulderWidth;
    public float BreastSize;
    public float HipWidth;
    public float HandSize;
    public float FootSize;
    public float LipThickness;
    public float JawPosition;
    public float ChinPosition;
    public float EyeSize;
    public float EyeDistance;
    public float EyePosition;
    public float EyeDepth;
    public float EarSize;
    public float EarRotation;
    public float NoseRootRotation;
    public float NoseTipRotation;
    public float NoseWidth;
    public int Head;
    public int Hat;
    public int Hair;
    public int Glasses;
    public int Beard;
    public int ShirtT;
    public int ShirtB;
    public int PantsT;
    public int PantsB;
    public int Socks;
    public int Shoes;

    public static Look FromCore(PlayerLook.Look core, Appearance ap)
    {
        Look look = new Look();
        look.Male = core.male;
        look.Advanced = core.advanced;
        look.Height = core.height;
        look.Shape = core.shape;
        look.Gender = core.gender;
        look.HeadSize = core.headSize;
        look.FaceSize = core.faceSize;
        look.NeckLength = core.neckLength;
        look.ShoulderWidth = core.shoulderWidth;
        look.BreastSize = core.breastSize;
        look.HipWidth = core.hipWidth;
        look.HandSize = core.handSize;
        look.FootSize = core.footSize;
        look.LipThickness = core.lipThickness;
        look.JawPosition = core.jawPosition;
        look.ChinPosition = core.chinPosition;
        look.EyeSize = core.eyeSize;
        look.EyeDistance = core.eyeDistance;
        look.EyePosition = core.eyePosition;
        look.EyeDepth = core.eyeDepth;
        look.EarSize = core.earSize;
        look.EarRotation = core.earRotation;
        look.NoseRootRotation = core.noseRootRotation;
        look.NoseTipRotation = core.noseTipRotation;
        look.NoseWidth = core.noseWidth;
        look.Head = IndexOf(ap, core.male, "Head", core.head);
        look.Hat = IndexOf(ap, core.male, "Hat", core.hat);
        look.Hair = IndexOf(ap, core.male, "Hair", core.hair);
        look.Glasses = IndexOf(ap, core.male, "Glasses", core.glasses);
        look.Beard = IndexOf(ap, core.male, "Beard", core.beard);
        look.ShirtT = IndexOf(ap, core.male, "Jacket", core.shirtT);
        look.ShirtB = IndexOf(ap, core.male, "Shirt", core.shirtB);
        look.PantsT = IndexOf(ap, core.male, "Pants", core.pantsT);
        look.PantsB = IndexOf(ap, core.male, "Leggings", core.pantsB);
        look.Socks = IndexOf(ap, core.male, "Socks", core.socks);
        look.Shoes = IndexOf(ap, core.male, "Shoes", core.shoes);
        return look;
    }

    public static bool Parse(string text, out Look look)
    {
        look = null;
        if (text == null) return false;
        string[] p = text.Split('|');
        if (p.Length != FieldCount || p[0] != "APP") return false;
        Look result = new Look();
        int k = 1;
        bool ok = ReadFlag(p[k++], out result.Male) && ReadFlag(p[k++], out result.Advanced)
            && ReadHeight(p[k++], out result.Height) && ReadNum(p[k++], out result.Shape) && ReadNum(p[k++], out result.Gender)
            && ReadNum(p[k++], out result.HeadSize) && ReadNum(p[k++], out result.FaceSize) && ReadNum(p[k++], out result.NeckLength)
            && ReadNum(p[k++], out result.ShoulderWidth) && ReadNum(p[k++], out result.BreastSize) && ReadNum(p[k++], out result.HipWidth)
            && ReadNum(p[k++], out result.HandSize) && ReadNum(p[k++], out result.FootSize) && ReadNum(p[k++], out result.LipThickness)
            && ReadNum(p[k++], out result.JawPosition) && ReadNum(p[k++], out result.ChinPosition) && ReadNum(p[k++], out result.EyeSize)
            && ReadNum(p[k++], out result.EyeDistance) && ReadNum(p[k++], out result.EyePosition) && ReadNum(p[k++], out result.EyeDepth)
            && ReadNum(p[k++], out result.EarSize) && ReadNum(p[k++], out result.EarRotation) && ReadNum(p[k++], out result.NoseRootRotation)
            && ReadNum(p[k++], out result.NoseTipRotation) && ReadNum(p[k++], out result.NoseWidth)
            && ReadIndex(p[k++], out result.Head) && ReadIndex(p[k++], out result.Hat) && ReadIndex(p[k++], out result.Hair)
            && ReadIndex(p[k++], out result.Glasses) && ReadIndex(p[k++], out result.Beard) && ReadIndex(p[k++], out result.ShirtT)
            && ReadIndex(p[k++], out result.ShirtB) && ReadIndex(p[k++], out result.PantsT) && ReadIndex(p[k++], out result.PantsB)
            && ReadIndex(p[k++], out result.Socks) && ReadIndex(p[k++], out result.Shoes);
        if (!ok) return false;
        result.Raw = text;
        look = result;
        return true;
    }

    public string Text()
    {
        return string.Join("|", new string[]
        {
            "APP", Flag(Male), Flag(Advanced),
            Fmt(Height), Fmt(Shape), Fmt(Gender), Fmt(HeadSize), Fmt(FaceSize), Fmt(NeckLength), Fmt(ShoulderWidth), Fmt(BreastSize),
            Fmt(HipWidth), Fmt(HandSize), Fmt(FootSize), Fmt(LipThickness), Fmt(JawPosition), Fmt(ChinPosition), Fmt(EyeSize),
            Fmt(EyeDistance), Fmt(EyePosition), Fmt(EyeDepth), Fmt(EarSize), Fmt(EarRotation), Fmt(NoseRootRotation), Fmt(NoseTipRotation), Fmt(NoseWidth),
            Idx(Head), Idx(Hat), Idx(Hair), Idx(Glasses), Idx(Beard), Idx(ShirtT), Idx(ShirtB), Idx(PantsT), Idx(PantsB), Idx(Socks), Idx(Shoes)
        });
    }

    public PlayerLook.Look ToCore(Appearance ap)
    {
        PlayerLook.Look core = new PlayerLook.Look();
        core.male = Male;
        core.advanced = Advanced;
        core.height = Height;
        core.shape = Shape;
        core.gender = Gender;
        core.headSize = HeadSize;
        core.faceSize = FaceSize;
        core.neckLength = NeckLength;
        core.shoulderWidth = ShoulderWidth;
        core.breastSize = BreastSize;
        core.hipWidth = HipWidth;
        core.handSize = HandSize;
        core.footSize = FootSize;
        core.lipThickness = LipThickness;
        core.jawPosition = JawPosition;
        core.chinPosition = ChinPosition;
        core.eyeSize = EyeSize;
        core.eyeDistance = EyeDistance;
        core.eyePosition = EyePosition;
        core.eyeDepth = EyeDepth;
        core.earSize = EarSize;
        core.earRotation = EarRotation;
        core.noseRootRotation = NoseRootRotation;
        core.noseTipRotation = NoseTipRotation;
        core.noseWidth = NoseWidth;
        core.head = NameOf(ap, Male, "Head", Head);
        core.hat = NameOf(ap, Male, "Hat", Hat);
        core.hair = NameOf(ap, Male, "Hair", Hair);
        core.glasses = NameOf(ap, Male, "Glasses", Glasses);
        core.beard = NameOf(ap, Male, "Beard", Beard);
        core.shirtT = NameOf(ap, Male, "Jacket", ShirtT);
        core.shirtB = NameOf(ap, Male, "Shirt", ShirtB);
        core.pantsT = NameOf(ap, Male, "Pants", PantsT);
        core.pantsB = NameOf(ap, Male, "Leggings", PantsB);
        core.socks = NameOf(ap, Male, "Socks", Socks);
        core.shoes = NameOf(ap, Male, "Shoes", Shoes);
        return core;
    }

    private static int IndexOf(Appearance ap, bool male, string slot, string name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        return PlayerLook.Options(slot, male, ap).IndexOf(name);
    }

    private static string NameOf(Appearance ap, bool male, string slot, int index)
    {
        IList<string> names = PlayerLook.Options(slot, male, ap);
        return index >= 0 && index < names.Count ? names[index] : "";
    }

    private static bool ReadFlag(string text, out bool value)
    {
        value = text == "1";
        return text == "1" || text == "0";
    }

    private static bool ReadNum(string text, out float value)
    {
        return ReadClamped(text, -MaxValue, MaxValue, out value);
    }

    private static bool ReadHeight(string text, out float value)
    {
        return ReadClamped(text, MinHeight, MaxHeight, out value);
    }

    private static bool ReadClamped(string text, float min, float max, out float value)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || float.IsNaN(value)) return false;
        value = Math.Max(min, Math.Min(max, value));
        return true;
    }

    private static bool ReadIndex(string text, out int value)
    {
        return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) && value >= -1 && value <= MaxIndex;
    }

    private static string Flag(bool value)
    {
        return value ? "1" : "0";
    }

    private static string Fmt(float value)
    {
        return value.ToString("F3", CultureInfo.InvariantCulture);
    }

    private static string Idx(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
