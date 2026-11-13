using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;

internal static class SitSync
{
    public const string Channel = "sit";
    private const float CheckInterval = 0.1f;
    private const float ResendInterval = 1f;
    private const float StaleAfter = 3f;
    private static readonly Dictionary<ulong, SitWire.SitRecord> records = new Dictionary<ulong, SitWire.SitRecord>();
    private static float nextCheck;
    private static float nextSend;
    private static bool sitting;
    private static Vector3 lastPos;
    private static float lastYaw;

    public static void Tick()
    {
        float now = Time.unscaledTime;
        if (now < nextCheck || !Session.IsInLobby) return;
        nextCheck = now + CheckInterval;
        Vector3 pos;
        float yaw;
        if (LocalSeat(out pos, out yaw))
        {
            if (sitting && now < nextSend && pos == lastPos && yaw == lastYaw) return;
            nextSend = now + ResendInterval;
            sitting = true;
            lastPos = pos;
            lastYaw = yaw;
            Session.SendAll(Channel, SitWire.Seat(pos.x, pos.y, pos.z, yaw), false);
        }
        else if (sitting)
        {
            sitting = false;
            Session.SendAll(Channel, SitWire.Stand(), true);
        }
    }

    public static void Handle(ulong sender, string text)
    {
        SitWire.SitRecord record;
        if (!SitWire.Parse(text, out record))
        {
            CoopLog.Log.Debug("sit.parse", "ignored malformed sit from " + sender);
            return;
        }
        record.LastSeen = Time.unscaledTime;
        records[sender] = record;
    }

    public static SitWire.SitRecord Get(ulong peer)
    {
        SitWire.SitRecord record;
        if (!records.TryGetValue(peer, out record) || !record.Sitting || Time.unscaledTime - record.LastSeen > StaleAfter) return null;
        return record;
    }

    private static bool LocalSeat(out Vector3 pos, out float yaw)
    {
        pos = Vector3.zero;
        yaw = 0f;
        if (!InteractableChair.PlayerIsSitting || PlayerLocator.instance == null) return false;
        Transform parent = PlayerLocator.instance.transform.parent;
        InteractableChair chair = parent != null ? parent.GetComponent<InteractableChair>() : null;
        if (chair == null) return false;
        if (chair.animationNodes != null && chair.animationNodes.Count > 0 && chair.animationNodes[0] != null)
        {
            Transform node = chair.animationNodes[0].transform;
            pos = node.position;
            yaw = node.eulerAngles.y;
            return true;
        }
        pos = chair.transform.position;
        Transform view = chair.cameraPosition != null ? chair.cameraPosition : chair.transform;
        yaw = view.eulerAngles.y;
        return true;
    }
}
