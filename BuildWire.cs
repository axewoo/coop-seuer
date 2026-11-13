using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

internal sealed class Piece
{
    public string Scene;
    public string Manager;
    public string SyncId;
    public string FurnitureId;
    public Vector3 Position;
    public Quaternion Rotation;
    public Quaternion ChildRotation;
    public int Skin;
    public int Alt;
}

internal sealed class BuildMsg
{
    public string Kind;
    public int ReqId;
    public bool Ok;
    public string Reason;
    public int Count;
    public string Sum;
    public string Scene;
    public string Manager;
    public string SyncId;
    public Vector3 Position;
    public Quaternion Rotation;
    public Quaternion RootRotation;
    public Piece Piece;
    public int Seq;
    public int Idx;
    public bool Last;
    public List<string> Keys;
    public bool Disabled;
    public bool ManagerDisabled;
    public bool NotFullyExamined;
    public bool TaskOk;
    public int Examine;
    public int ExamineMax;
}

internal static class BuildWire
{
    public const string Channel = "build";
    private const int MaxText = 200;
    private const int MaxReason = 120;
    private const int MaxReqId = 999999999;
    private const int MaxByte = 255;
    private const int MaxCount = 100000;
    private const int MaxKeys = 200;
    private const int MaxPackets = 4096;
    private const int PieceFields = 18;
    private const int PoseAt = 5;
    private const float MaxValue = 100000f;
    private const float MinNorm = 0.5f;
    private const string PoseFormat = "{0:F4}|{1:F4}|{2:F4}|{3:F4}|{4:F4}|{5:F4}|{6:F4}|{7:F4}|{8:F4}|{9:F4}|{10:F4}|{11}|{12}";
    private const string MoveFormat = "{0:F4}|{1:F4}|{2:F4}|{3:F4}|{4:F4}|{5:F4}|{6:F4}";
    private const string MoveRootFormat = MoveFormat + "|{7:F4}|{8:F4}|{9:F4}|{10:F4}";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly char[] Bad = { '|', '\r', '\n' };
    private static float nextDropLog;

    public static string Hello(string scene)
    {
        return "HELLO|" + scene;
    }

    public static string Snap(string scene, string manager, int count)
    {
        return string.Format(Inv, "SNAP|{0}|{1}|{2}", scene, manager, count);
    }

    public static string SnapItem(Piece p)
    {
        return "SNAPI|" + Body(p, true);
    }

    public static string SnapEnd(string scene, string manager)
    {
        return string.Format(Inv, "SNAPEND|{0}|{1}", scene, manager);
    }

    public static string Add(Piece p)
    {
        return "ADD|" + Body(p, true);
    }

    public static string Del(string scene, string syncId)
    {
        return string.Format(Inv, "DEL|{0}|{1}", scene, syncId);
    }

    public static string Ack(int reqId, bool ok, string reason)
    {
        return string.Format(Inv, "ACK|{0}|{1}|{2}", reqId, ok ? 1 : 0, Clean(reason));
    }

    public static string Give(int reqId, string furnitureId, int skin, int alt)
    {
        return string.Format(Inv, "GIVE|{0}|{1}|{2}|{3}", reqId, furnitureId, skin, alt);
    }

    public static string ReqPlace(int reqId, Piece p)
    {
        return string.Format(Inv, "REQP|{0}|", reqId) + Body(p, false);
    }

    public static string ReqTake(int reqId, string scene, string syncId)
    {
        return string.Format(Inv, "REQT|{0}|{1}|{2}", reqId, scene, syncId);
    }

    public static string ReqMove(int reqId, string scene, string syncId, Vector3 position, Quaternion rotation, Quaternion rootRotation)
    {
        return string.Format(Inv, "REQM|{0}|{1}|{2}|", reqId, scene, syncId) + Pose(position, rotation, rootRotation);
    }

    public static string Move(string scene, string syncId, Vector3 position, Quaternion rotation, Quaternion rootRotation)
    {
        return string.Format(Inv, "MOVE|{0}|{1}|", scene, syncId) + Pose(position, rotation, rootRotation);
    }

    public static string Sum(string scene, int count, string sum)
    {
        return string.Format(Inv, "SUM|{0}|{1}|{2}", scene, count, sum);
    }

    public static string Lock(string scene, string manager, bool disabled, bool managerDisabled, bool notFullyExamined, bool taskOk, int examine, int examineMax)
    {
        return string.Format(Inv, "LOCK|{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}", scene, manager, disabled ? 1 : 0, managerDisabled ? 1 : 0, notFullyExamined ? 1 : 0, taskOk ? 1 : 0, examine, examineMax);
    }

    public static string Sum(List<string> ids)
    {
        int total = 0;
        for (int i = 0; i < ids.Count; i++) total += HashOf(ids[i]);
        return (total & 0x7fffffff).ToString("x8", Inv);
    }

    public static bool TryParse(string payload, out BuildMsg msg)
    {
        msg = null;
        try
        {
            if (payload == null) return false;
            string[] f = payload.Split('|');
            BuildMsg m = new BuildMsg();
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

    private static int HashOf(string text)
    {
        if (text == null) return 0;
        int hash = unchecked((int)2166136261);
        for (int i = 0; i < text.Length; i++) hash = unchecked((hash ^ text[i]) * 16777619);
        return hash;
    }

    private static string Body(Piece p, bool withSync)
    {
        string sync = withSync ? p.SyncId + "|" : "";
        return p.Scene + "|" + p.Manager + "|" + sync + p.FurnitureId + "|" + Tail(p);
    }

    private static string Tail(Piece p)
    {
        return string.Format(Inv, PoseFormat, p.Position.x, p.Position.y, p.Position.z, p.Rotation.x, p.Rotation.y, p.Rotation.z, p.Rotation.w, p.ChildRotation.x, p.ChildRotation.y, p.ChildRotation.z, p.ChildRotation.w, p.Skin, p.Alt);
    }

    private static string Pose(Vector3 position, Quaternion rotation, Quaternion rootRotation)
    {
        return string.Format(Inv, MoveRootFormat, position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w,
            rootRotation.x, rootRotation.y, rootRotation.z, rootRotation.w);
    }

    private static string Clean(string text)
    {
        string plain = (text ?? "").Replace("|", "").Replace("\r", "").Replace("\n", "");
        return plain.Length > MaxReason ? plain.Substring(0, MaxReason) : plain;
    }

    private static bool Fields(string[] f, BuildMsg m)
    {
        switch (m.Kind)
        {
            case "HELLO":
                return f.Length == 2 && Text(f[1], out m.Scene);
            case "SNAP":
                return f.Length == 4 && Text(f[1], out m.Scene) && Text(f[2], out m.Manager) && Int(f[3], 0, MaxCount, out m.Count);
            case "SNAPEND":
                return f.Length == 3 && Text(f[1], out m.Scene) && Text(f[2], out m.Manager);
            case "SNAPI":
            case "ADD":
                return f.Length == PieceFields && ReadPiece(f, 1, true, out m.Piece);
            case "REQP":
                return f.Length == PieceFields && Int(f[1], 1, MaxReqId, out m.ReqId) && ReadPiece(f, 2, false, out m.Piece);
            case "REQT":
                return f.Length == 4 && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[2], out m.Scene) && Text(f[3], out m.SyncId);
            case "REQM":
                return f.Length == 15 && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[2], out m.Scene) && Text(f[3], out m.SyncId)
                    && Vec(f, 4, out m.Position) && Quat(f, 7, out m.Rotation) && Quat(f, 11, out m.RootRotation);
            case "DEL":
                return f.Length == 3 && Text(f[1], out m.Scene) && Text(f[2], out m.SyncId);
            case "MOVE":
                return f.Length == 14 && Text(f[1], out m.Scene) && Text(f[2], out m.SyncId)
                    && Vec(f, 3, out m.Position) && Quat(f, 6, out m.Rotation) && Quat(f, 10, out m.RootRotation);
            case "SUM":
                return f.Length == 4 && Text(f[1], out m.Scene) && Int(f[2], 0, MaxCount, out m.Count) && Text(f[3], out m.Sum);
            case "BM":
                return ManifestFields(f, m);
            case "LOCK":
                return f.Length == 9 && Text(f[1], out m.Scene) && Text(f[2], out m.Manager) && Flag(f[3], out m.Disabled) && Flag(f[4], out m.ManagerDisabled)
                    && Flag(f[5], out m.NotFullyExamined) && Flag(f[6], out m.TaskOk) && Int(f[7], -1, MaxCount, out m.Examine) && Int(f[8], -1, MaxCount, out m.ExamineMax);
            case "ACK":
                return f.Length == 4 && Int(f[1], 1, MaxReqId, out m.ReqId) && Flag(f[2], out m.Ok) && Note(f[3], out m.Reason);
            case "GIVE":
                m.Piece = new Piece();
                return f.Length == 5 && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[2], out m.Piece.FurnitureId) && Int(f[3], 0, MaxByte, out m.Piece.Skin) && Int(f[4], 0, MaxByte, out m.Piece.Alt);
            default:
                return false;
        }
    }

    private static bool ManifestFields(string[] f, BuildMsg m)
    {
        m.Keys = new List<string>();
        if (f.Length < 5 || f.Length > MaxKeys + 5)
        {
            Drop("the packet carried " + (f.Length - 5) + " keys or was not a manifest");
            return false;
        }
        if (!Text(f[1], out m.Scene) || !Int(f[2], 0, MaxCount, out m.Seq) || !Int(f[3], 0, MaxPackets, out m.Idx) || !Flag(f[4], out m.Last))
        {
            Drop("the packet header was malformed or named no usable index");
            return false;
        }
        for (int i = 5; i < f.Length; i++)
        {
            string key;
            if (!Text(f[i], out key))
            {
                Drop("the packet carried a key that cannot be used");
                return false;
            }
            m.Keys.Add(key);
        }
        return true;
    }

    private static void Drop(string why)
    {
        float now = Time.unscaledTime;
        if (now < nextDropLog) return;
        nextDropLog = now + 10f;
        CoopLog.Log.Info("coop: a build manifest packet was dropped, " + why);
    }

    private static bool ReadPiece(string[] f, int scene, bool withSync, out Piece p)
    {
        p = new Piece();
        int furniture = withSync ? scene + 3 : scene + 2;
        if (!Text(f[scene], out p.Scene) || !Text(f[scene + 1], out p.Manager) || !Text(f[furniture], out p.FurnitureId)) return false;
        if (withSync && !Text(f[scene + 2], out p.SyncId)) return false;
        return Vec(f, PoseAt, out p.Position) && Quat(f, PoseAt + 3, out p.Rotation) && Quat(f, PoseAt + 7, out p.ChildRotation)
            && Int(f[PoseAt + 11], 0, MaxByte, out p.Skin) && Int(f[PoseAt + 12], 0, MaxByte, out p.Alt);
    }

    private static bool Vec(string[] f, int at, out Vector3 v)
    {
        float x, y, z;
        v = Vector3.zero;
        if (!Float(f[at], out x) || !Float(f[at + 1], out y) || !Float(f[at + 2], out z)) return false;
        v = new Vector3(x, y, z);
        return true;
    }

    private static bool Quat(string[] f, int at, out Quaternion q)
    {
        float x, y, z, w;
        q = Quaternion.identity;
        if (!Float(f[at], out x) || !Float(f[at + 1], out y) || !Float(f[at + 2], out z) || !Float(f[at + 3], out w)) return false;
        float length = Mathf.Sqrt(x * x + y * y + z * z + w * w);
        if (length < MinNorm) return false;
        q = new Quaternion(x / length, y / length, z / length, w / length);
        return true;
    }

    private static bool Float(string text, out float value)
    {
        return float.TryParse(text, NumberStyles.Float, Inv, out value) && !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= MaxValue;
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
        return text != null && text.Length >= 1 && text.Length <= MaxText && text.IndexOfAny(Bad) < 0;
    }

    private static bool Note(string text, out string value)
    {
        value = text;
        return text != null && text.Length <= MaxReason;
    }
}
