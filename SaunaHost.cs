using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;

internal static class SaunaHost
{
    private const float StateInterval = 1f;
    private static readonly Dictionary<string, bool> lastOn = new Dictionary<string, bool>();
    private static float nextState;

    public static void Init()
    {
    }

    public static void Reset()
    {
        lastOn.Clear();
        nextState = 0f;
    }

    public static void OnSceneChanged()
    {
        lastOn.Clear();
    }

    public static void Tick()
    {
        try
        {
            if (!Session.IsHost) return;
            float now = Time.unscaledTime;
            if (now < nextState) return;
            nextState = now + StateInterval;
            if (!Session.IsInLobby || GameRefs.AreaId == null) return;
            List<ulong> peers = Session.PeersInArea(GameRefs.AreaId);
            if (peers.Count == 0) return;
            Sauna[] all = UnityEngine.Object.FindObjectsByType<Sauna>(FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++) Publish(all[i], peers);
        }
        catch (Exception e)
        {
            CoopLog.Once("SaunaHost.Tick", e);
        }
    }

    public static void OnLocalChange(Sauna sauna)
    {
        try
        {
            if (!Session.IsHost || Shared.Applying) return;
            if (!Session.IsInLobby) return;
            string scene = GameRefs.AreaId;
            string guid = Guid(sauna);
            if (scene == null || guid == null) return;
            List<ulong> peers = Session.PeersInArea(scene);
            if (peers.Count == 0) return;
            lastOn[guid] = sauna.On;
            Send(peers, SaunaWire.Fire(scene, guid, sauna.On), true);
            Send(peers, SaunaWire.Values(scene, guid, sauna.On, sauna.temperature, SaunaWire.Steam(sauna)), true);
        }
        catch (Exception e)
        {
            CoopLog.Once("SaunaHost.OnLocalChange", e);
        }
    }

    public static void Handle(ulong from, SaunaMsg m)
    {
        try
        {
            if (!Session.Peers.Contains(from)) return;
            if (m.Kind == SaunaWire.ReqLight) OnLight(from, m);
            else if (m.Kind == SaunaWire.ReqWater) OnWater(from, m);
        }
        catch (Exception e)
        {
            CoopLog.Once("SaunaHost.Handle", e);
        }
    }

    private static void Publish(Sauna sauna, List<ulong> peers)
    {
        string scene = GameRefs.AreaId;
        string guid = Guid(sauna);
        if (guid == null) return;
        bool known;
        if (!lastOn.TryGetValue(guid, out known) || known != sauna.On)
        {
            lastOn[guid] = sauna.On;
            Send(peers, SaunaWire.Fire(scene, guid, sauna.On), true);
            Send(peers, SaunaWire.Values(scene, guid, sauna.On, sauna.temperature, SaunaWire.Steam(sauna)), true);
            return;
        }
        Send(peers, SaunaWire.Values(scene, guid, sauna.On, sauna.temperature, SaunaWire.Steam(sauna)), false);
    }

    private static void OnLight(ulong from, SaunaMsg m)
    {
        if (!Shared.Admit(from, SaunaWire.Channel, m.ReqId, SaunaWire.ReqLight)) return;
        string reason = "";
        bool light = m.Op == SaunaWire.OpOn;
        if (m.Scene != GameRefs.AreaId) reason = "not in the host world";
        Sauna sauna = reason.Length == 0 ? SaunaWire.Find(m.Scene, m.Guid) : null;
        if (reason.Length == 0 && sauna == null) reason = "not found";
        if (reason.Length == 0 && light == sauna.On) reason = light ? "already lit" : "already off";
        if (reason.Length == 0)
        {
            string scene = GameRefs.AreaId;
            Shared.Apply(delegate { if (light) sauna.TurnOn(); else sauna.TurnOff(); });
            lastOn[sauna.GUID] = sauna.On;
            List<ulong> peers = Session.PeersInArea(scene);
            Send(peers, SaunaWire.Fire(scene, sauna.GUID, sauna.On), true);
            Send(peers, SaunaWire.Values(scene, sauna.GUID, sauna.On, sauna.temperature, SaunaWire.Steam(sauna)), true);
            CoopLog.Log.Info("sauna.host: " + (light ? "lit" : "turned off") + " " + sauna.GUID + " for " + Session.DisplayName(from) + ", " + m.Logs + " log(s) reported");
        }
        Shared.Answer(from, SaunaWire.Channel, m.ReqId, SaunaWire.ReqLight, SaunaWire.Ack(m.ReqId, reason.Length == 0, reason));
    }

    private static void OnWater(ulong from, SaunaMsg m)
    {
        if (!Shared.Admit(from, SaunaWire.Channel, m.ReqId, SaunaWire.ReqWater)) return;
        string reason = "";
        if (m.Scene != GameRefs.AreaId) reason = "not in the host world";
        Sauna sauna = reason.Length == 0 ? SaunaWire.Find(m.Scene, m.Guid) : null;
        if (reason.Length == 0 && sauna == null) reason = "not found";
        if (reason.Length == 0 && sauna.passive) reason = "a passive sauna takes no water";
        if (reason.Length == 0)
        {
            Shared.Apply(delegate { sauna.ThrowWater(); });
            string scene = GameRefs.AreaId;
            Send(Session.PeersInArea(scene), SaunaWire.Values(scene, sauna.GUID, sauna.On, sauna.temperature, SaunaWire.Steam(sauna)), true);
        }
        Shared.Answer(from, SaunaWire.Channel, m.ReqId, SaunaWire.ReqWater, SaunaWire.WaterOk(m.ReqId, reason.Length == 0, reason));
    }

    private static string Guid(Sauna sauna)
    {
        if (sauna == null || string.IsNullOrEmpty(sauna.GUID))
        {
            CoopLog.Once("SaunaHost.guid", "a sauna has no GUID and is not shared");
            return null;
        }
        return sauna.GUID;
    }

    private static void Send(List<ulong> peers, string text, bool reliable)
    {
        for (int i = 0; i < peers.Count; i++) Session.Send(peers[i], SaunaWire.Channel, text, reliable);
    }
}
