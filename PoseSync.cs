using System;
using System.Collections.Generic;
using System.Globalization;
using ObenseuerCore;
using UnityEngine;

internal static class PoseSync
{
    public const string Channel = "pose";
    private const float SendInterval = 0.1f;
    private const float StaleAfter = 1f;
    private const float MaxValue = 1000f;
    private const int FieldCount = 4;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Dictionary<ulong, PoseRecord> records = new Dictionary<ulong, PoseRecord>();
    private static float nextSend;

    internal sealed class PoseRecord
    {
        public float? Speed;
        public bool? Crouching;
        public bool? Grounded;
        public float? VerticalSpeed;
        public float LastSeen;
    }

    public static void Tick()
    {
        try
        {
            SitSync.Tick();
        }
        catch (Exception e)
        {
            CoopLog.Once("SitSync.Tick", e);
        }
        float now = Time.unscaledTime;
        if (now < nextSend || !Session.IsInLobby || GameRefs.LocalPlayer == null) return;
        OpenSewerCharacterController controller = OpenSewerCharacterController.instance;
        if (controller == null || controller.movement == null) return;
        nextSend = now + SendInterval;
        Vector3 velocity = controller.movement.velocity;
        string text = string.Format(Inv, "{0:F2}|{1}|{2}|{3:F2}", new Vector2(velocity.x, velocity.z).magnitude, controller.isCrouching ? 1 : 0, controller.isGrounded ? 1 : 0, velocity.y);
        Session.SendAll(Channel, text, false);
    }

    public static void Handle(ulong sender, string text)
    {
        PoseRecord record;
        if (!Parse(text, out record))
        {
            CoopLog.Log.Debug("pose.parse", "ignored malformed pose from " + sender);
            return;
        }
        record.LastSeen = Time.unscaledTime;
        records[sender] = record;
    }

    public static PoseRecord Get(ulong peer)
    {
        PoseRecord record;
        if (!records.TryGetValue(peer, out record) || Time.unscaledTime - record.LastSeen > StaleAfter) return null;
        return record;
    }

    internal static bool Parse(string text, out PoseRecord record)
    {
        record = new PoseRecord();
        string[] parts = text.Split('|');
        if (parts.Length > FieldCount) return false;
        float number;
        bool flag;
        if (Has(parts, 0))
        {
            if (!ReadValue(parts[0], out number) || number < 0f) return false;
            record.Speed = number;
        }
        if (Has(parts, 1))
        {
            if (!ReadFlag(parts[1], out flag)) return false;
            record.Crouching = flag;
        }
        if (Has(parts, 2))
        {
            if (!ReadFlag(parts[2], out flag)) return false;
            record.Grounded = flag;
        }
        if (Has(parts, 3))
        {
            if (!ReadValue(parts[3], out number)) return false;
            record.VerticalSpeed = number;
        }
        return true;
    }

    private static bool Has(string[] parts, int index)
    {
        return index < parts.Length && parts[index].Length > 0;
    }

    private static bool ReadValue(string text, out float value)
    {
        return float.TryParse(text, NumberStyles.Float, Inv, out value) && !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= MaxValue;
    }

    private static bool ReadFlag(string text, out bool value)
    {
        value = text == "1";
        return text == "0" || text == "1";
    }
}
