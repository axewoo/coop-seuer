using System;
using System.Globalization;

internal static class SitWire
{
    private const float MaxCoordinate = 100000f;
    private const float MaxYaw = 360f;
    private const int FieldCount = 5;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal sealed class SitRecord
    {
        public bool Sitting;
        public float X;
        public float Y;
        public float Z;
        public float Yaw;
        public float LastSeen;
    }

    public static string Stand()
    {
        return "0";
    }

    public static string Seat(float x, float y, float z, float yaw)
    {
        return string.Format(Inv, "1|{0:F2}|{1:F2}|{2:F2}|{3:F2}", x, y, z, yaw);
    }

    public static bool Parse(string text, out SitRecord record)
    {
        record = new SitRecord();
        string[] parts = text.Split('|');
        if (parts.Length == 1 && parts[0] == "0") return true;
        if (parts.Length != FieldCount || parts[0] != "1") return false;
        float x;
        float y;
        float z;
        float yaw;
        if (!ReadValue(parts[1], MaxCoordinate, out x) || !ReadValue(parts[2], MaxCoordinate, out y) || !ReadValue(parts[3], MaxCoordinate, out z) || !ReadValue(parts[4], MaxYaw, out yaw)) return false;
        record.Sitting = true;
        record.X = x;
        record.Y = y;
        record.Z = z;
        record.Yaw = yaw;
        return true;
    }

    private static bool ReadValue(string text, float max, out float value)
    {
        return float.TryParse(text, NumberStyles.Float, Inv, out value) && !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= max;
    }
}
