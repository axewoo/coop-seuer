using System;
using System.Collections.Generic;
using System.Globalization;
using ObenseuerCore;
using OS.Items;
using UnityEngine;

internal sealed class ItemMsg
{
    public string Kind;
    public int ReqId;
    public bool Ok;
    public string Reason;
    public bool Real;
    public int Count;
    public string Scene;
    public string Id;
    public int ItemId;
    public int Amount;
    public float X;
    public float Y;
    public float Z;
    public string Meta;
    public int Seq;
    public int Idx;
    public bool Last;
    public bool Resync;
    public List<string> Ids;
}

internal static class ItemWire
{
    public const string Channel = "item";
    public const string NotYet = "Not in co-op yet";
    public const string PathMarker = "/path:";
    private const string DestructibleMarker = "/DestructibleList";
    private const int MaxScene = 64;
    private const int MaxId = 120;
    private const int MaxReason = 120;
    private const int MaxMeta = 600;
    private const int MaxReqId = 999999999;
    public const int MaxAmount = 100000;
    private const int MaxCount = 100000;
    private const int MaxKeys = 200;
    private const int MaxPackets = 4096;
    private const int MinItemId = 2;
    private const int MaxItemId = 1000000;
    private const float MaxCoord = 100000f;
    private const float MinMoney = 0.01f;
    public const float MaxMoney = 100000f;
    private const string ItemFormat = "{0}|{1}|{2}|{3}|{4:F4}|{5:F4}|{6:F4}";
    private const string DropFormat = "{0}|{1}|{2}|{3:F4}|{4:F4}|{5:F4}";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly char[] Bad = { '|', '\r', '\n' };
    private static float nextDropLog;

    public static string Hello(string scene)
    {
        return "HELLO|" + Clean(scene, MaxScene);
    }

    public static string HelloResync(string scene)
    {
        return "HELLO|" + Clean(scene, MaxScene) + "|1";
    }

    public static string Snap(string scene, int count)
    {
        return string.Format(Inv, "SNAP|{0}|{1}", Clean(scene, MaxScene), count);
    }

    public static string SnapItem(string scene, string id, int itemId, int amount, Vector3 position, string meta)
    {
        return "SNAPI|" + string.Format(Inv, ItemFormat, Clean(scene, MaxScene), Clean(id, MaxId), itemId, amount, position.x, position.y, position.z) + "|" + CleanMeta(meta);
    }

    public static string SnapEnd(string scene)
    {
        return "SNAPEND|" + Clean(scene, MaxScene);
    }

    public static string ReqPick(int reqId, string scene, string id)
    {
        return string.Format(Inv, "REQPICK|{0}|{1}|{2}", reqId, Clean(scene, MaxScene), Clean(id, MaxId));
    }

    public static string Give(int reqId, int itemId, int amount, string meta)
    {
        return string.Format(Inv, "IGIVE|{0}|{1}|{2}|", reqId, itemId, amount) + CleanMeta(meta);
    }

    public static string Money(int reqId, bool real, float amount)
    {
        return string.Format(Inv, "IMONEY|{0}|{1}|{2}", reqId, real ? 1 : 0, amount.ToString("F2", Inv));
    }

    public static string Gack(int reqId)
    {
        return string.Format(Inv, "GACK|{0}", reqId);
    }

    public static string Gone(string scene, string id)
    {
        return string.Format(Inv, "GONE|{0}|{1}", Clean(scene, MaxScene), Clean(id, MaxId));
    }

    public static string Manifest(string scene, int seq, int idx, bool last, string ids)
    {
        string head = string.Format(Inv, "IM|{0}|{1}|{2}|{3}", Clean(scene, MaxScene), seq, idx, last ? 1 : 0);
        return string.IsNullOrEmpty(ids) ? head : head + "|" + ids;
    }

    public static string ReqDrop(int reqId, string scene, int itemId, int amount, Vector3 position, string meta)
    {
        return string.Format(Inv, "REQDROP|{0}|", reqId) + string.Format(Inv, DropFormat, Clean(scene, MaxScene), itemId, amount, position.x, position.y, position.z) + "|" + CleanMeta(meta);
    }

    public static string Spawn(string scene, string id, int itemId, int amount, Vector3 position, string meta)
    {
        return "SPAWN|" + string.Format(Inv, ItemFormat, Clean(scene, MaxScene), Clean(id, MaxId), itemId, amount, position.x, position.y, position.z) + "|" + CleanMeta(meta);
    }

    public static string Ack(int reqId, bool ok, string reason)
    {
        return string.Format(Inv, "ACK|{0}|{1}|{2}", reqId, ok ? 1 : 0, Clean(reason ?? "", MaxReason));
    }

    public static bool TryParse(string payload, out ItemMsg msg)
    {
        msg = null;
        try
        {
            if (payload == null) return false;
            string[] f = payload.Split('|');
            ItemMsg m = new ItemMsg();
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

    public static string StrongId(Collectible item)
    {
        if (item == null) return null;
        string id = WorldItems.IdOf(item.gameObject);
        if (string.IsNullOrEmpty(id) || id.Length > MaxId) return null;
        if (id.IndexOf(PathMarker, StringComparison.Ordinal) >= 0) return null;
        if (id.IndexOf(DestructibleMarker, StringComparison.Ordinal) >= 0) return null;
        return HasBad(id) ? null : id;
    }

    public static bool HasBad(string text)
    {
        return text != null && text.IndexOfAny(Bad) >= 0;
    }

    private static string Clean(string text, int max)
    {
        string plain = (text ?? "").Replace("|", "").Replace("\r", "").Replace("\n", "");
        return plain.Length > max ? plain.Substring(0, max) : plain;
    }

    private static string CleanMeta(string meta)
    {
        return Clean(meta ?? "", MaxMeta);
    }

    private static bool Fields(string[] f, ItemMsg m)
    {
        switch (m.Kind)
        {
            case "HELLO":
                return (f.Length == 2 || f.Length == 3) && Text(f[1], MaxScene, out m.Scene) && (f.Length == 2 || Flag(f[2], out m.Resync));
            case "SNAP":
                return f.Length == 3 && Text(f[1], MaxScene, out m.Scene) && Int(f[2], 0, MaxCount, out m.Count);
            case "SNAPEND":
                return f.Length == 2 && Text(f[1], MaxScene, out m.Scene);
            case "SNAPI":
            case "SPAWN":
                return f.Length == 9 && Spawn(f, 1, true, m);
            case "REQPICK":
                return f.Length == 4 && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[2], MaxScene, out m.Scene) && Text(f[3], MaxId, out m.Id);
            case "REQDROP":
                return f.Length == 9 && Int(f[1], 1, MaxReqId, out m.ReqId) && Spawn(f, 2, false, m);
            case "IGIVE":
                return f.Length == 5 && Int(f[1], 1, MaxReqId, out m.ReqId) && Int(f[2], MinItemId, MaxItemId, out m.ItemId) && Int(f[3], 1, MaxAmount, out m.Amount) && Meta(f[4], out m.Meta);
            case "IMONEY":
                return f.Length == 4 && Int(f[1], 1, MaxReqId, out m.ReqId) && Flag(f[2], out m.Real) && Cash(f[3], out m.X);
            case "GACK":
                return f.Length >= 2 && Int(f[1], 1, MaxReqId, out m.ReqId);
            case "GONE":
                return f.Length == 3 && Text(f[1], MaxScene, out m.Scene) && Text(f[2], MaxId, out m.Id);
            case "ACK":
                return f.Length == 4 && Int(f[1], 1, MaxReqId, out m.ReqId) && Flag(f[2], out m.Ok) && Note(f[3], out m.Reason);
            case "IM":
                return ManifestFields(f, m);
            default:
                return false;
        }
    }

    private static bool ManifestFields(string[] f, ItemMsg m)
    {
        m.Ids = new List<string>();
        if (f.Length < 5 || f.Length > MaxKeys + 5)
        {
            Drop("the packet carried too many ids or was not a manifest");
            return false;
        }
        if (!Text(f[1], MaxScene, out m.Scene) || !Int(f[2], 0, MaxCount, out m.Seq) || !Int(f[3], 0, MaxPackets, out m.Idx) || !Flag(f[4], out m.Last))
        {
            Drop("the packet header was malformed or named no usable index");
            return false;
        }
        for (int i = 5; i < f.Length; i++)
        {
            string[] ids = f[i].Split(',');
            for (int j = 0; j < ids.Length; j++)
            {
                if (!ManifestId(ids[j]))
                {
                    Drop("the packet carried an id that cannot be used");
                    return false;
                }
                if (m.Ids.Count >= MaxKeys)
                {
                    Drop("the packet carried more ids than one packet may hold");
                    return false;
                }
                m.Ids.Add(ids[j]);
            }
        }
        return true;
    }

    private static bool ManifestId(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxId || HasBad(text) || text.IndexOf(',') >= 0) return false;
        if (text.IndexOf(PathMarker, StringComparison.Ordinal) >= 0) return false;
        return text.IndexOf(DestructibleMarker, StringComparison.Ordinal) < 0;
    }

    private static void Drop(string why)
    {
        float now = Time.unscaledTime;
        if (now < nextDropLog) return;
        nextDropLog = now + 10f;
        CoopLog.Log.Info("coop: an item manifest packet was dropped, " + why);
        if (CoopDebug.On("item")) CoopDebug.Event("item", "manifestbad", why);
    }

    private static bool Spawn(string[] f, int at, bool withId, ItemMsg m)
    {
        if (!Text(f[at], MaxScene, out m.Scene)) return false;
        int next = at + 1;
        if (withId)
        {
            if (!Text(f[next], MaxId, out m.Id)) return false;
            next++;
        }
        return Int(f[next], MinItemId, MaxItemId, out m.ItemId) && Int(f[next + 1], 1, MaxAmount, out m.Amount)
            && Float(f[next + 2], out m.X) && Float(f[next + 3], out m.Y) && Float(f[next + 4], out m.Z)
            && Meta(f[next + 5], out m.Meta);
    }

    private static bool Cash(string text, out float value)
    {
        return Float(text, out value) && value >= MinMoney && value <= MaxMoney;
    }

    private static bool Float(string text, out float value)
    {
        return float.TryParse(text, NumberStyles.Float, Inv, out value) && !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= MaxCoord;
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

    private static bool Text(string text, int max, out string value)
    {
        value = text;
        return !string.IsNullOrEmpty(text) && text.Length <= max && !HasBad(text);
    }

    private static bool Meta(string text, out string value)
    {
        value = text;
        return text != null && text.Length <= MaxMeta && !HasBad(text);
    }

    private static bool Note(string text, out string value)
    {
        value = text;
        return text != null && text.Length <= MaxReason && !HasBad(text);
    }
}
