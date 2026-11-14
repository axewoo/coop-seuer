using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using ObenseuerCore;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static class CoopDebug
{
    private const int LineCap = 20000;
    private const float RateWindow = 10f;
    private const int RateMax = 5;
    private const float SummarySeconds = 60f;
    private const float PerfSeconds = 30f;
    private const float GapSeconds = 1f;
    private const int MaxBuckets = 4096;
    private const int KeyChars = 24;
    private const int MaxValueChars = 600;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed class Bucket
    {
        public float WindowAt;
        public int Count;
        public int Suppressed;
    }

    private static readonly Dictionary<string, Bucket> buckets = new Dictionary<string, Bucket>();
    private static readonly HashSet<string> tags = new HashSet<string>();
    private static ConfigEntry<bool> enabled;
    private static ConfigEntry<bool> verbose;
    private static ConfigEntry<string> systems;
    private static ConfigEntry<KeyCode> toggleKey;
    private static ConfigFile file;
    private static bool installed;
    private static bool allTags = true;
    private static int lines;
    private static bool capSpent;
    private static float headerAt;
    private static float nextSummary;
    private static float nextPerf;
    private static float frameSum;
    private static float frameMax;
    private static int frameCount;
    private static bool wasInLobby;

    public static void Install(ConfigFile config)
    {
        try
        {
            if (installed) return;
            enabled = config.Bind("Debug", "Enabled", true, "Writes the co-op diagnostic lines to the log. The debug key flips it while the game runs. Keep it on for a playtest so the log proves what each system did.");
            verbose = config.Bind("Debug", "Verbose", false, "Adds the per event detail lines on top of the summary ones. The detail lines are the payload bodies and the per peer rows.");
            systems = config.Bind("Debug", "Systems", "all", "The debug tags that write lines, as a comma list, for example item,door,quest. all turns every tag on.");
            toggleKey = config.Bind("Debug", "ToggleKey", KeyCode.F10, "Flips the co-op debug lines on and off while the game runs.");
            file = config;
            installed = true;
            ParseTags();
            headerAt = Time.unscaledTime + 1f;
            CoopLog.Log.Info("coop: debug ready, enabled " + enabled.Value + ", verbose " + verbose.Value + ", systems " + systems.Value + ", toggle " + toggleKey.Value + ", cap " + LineCap + " lines");
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopDebug.Install", e);
        }
    }

    public static void Tick()
    {
        try
        {
            if (!installed) return;
            Key();
            if (headerAt > 0f && Time.unscaledTime >= headerAt)
            {
                headerAt = 0f;
                Header("game start");
            }
            WatchLobby();
            Frame();
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopDebug.Tick", e);
        }
    }

    public static bool On(string tag)
    {
        if (!installed || enabled == null || !enabled.Value) return false;
        if (capSpent) return false;
        return allTags || tags.Contains(tag);
    }

    public static void Log(string tag, string msg)
    {
        if (msg == null) return;
        if (!On(tag)) return;
        Line(tag, Prefix(msg), msg);
    }

    public static void LogV(string tag, string msg)
    {
        if (verbose == null || !verbose.Value) return;
        Log(tag, msg);
    }

    public static void Event(string tag, string key, string msg)
    {
        if (msg == null) return;
        if (!On(tag)) return;
        Line(tag, key == null ? Prefix(msg) : key, msg);
    }

    public static void Error(string tag, string msg)
    {
        if (msg == null) return;
        CoopLog.Log.Error("[dbg:" + (tag == null ? "?" : tag) + "] " + msg);
    }

    public static void Header(string reason)
    {
        if (!On("sess")) return;
        Write("[dbg:sess] ===== co-op session header: " + reason + " =====");
        Write("[dbg:sess] mod " + Plugin.Version + " on " + Loader() + ", game build " + Core.GameBuild + ", scene " + Scene() + ", area " + Area());
        Write("[dbg:sess] steam " + Session.Me + ", role " + Role() + ", host " + Session.HostId + ", peers " + Peers());
        Write("[dbg:sess] debug enabled " + (enabled == null ? "?" : enabled.Value.ToString()) + ", verbose " + (verbose == null ? "?" : verbose.Value.ToString()) + ", systems " + (systems == null ? "?" : systems.Value) + ", toggle " + (toggleKey == null ? "?" : toggleKey.Value.ToString()) + ", cap " + LineCap);
        Write("[dbg:sess] timer scale " + TimerScale.Factor.ToString("0.###", Inv) + ", ready " + TimerScale.Ready);
        Write("[dbg:sess] mods " + CoopUi.ModList());
        Write("[dbg:sess] config " + ConfigSummary());
    }

    private static void Key()
    {
        if (toggleKey == null || toggleKey.Value == KeyCode.None) return;
        if (!Input.GetKeyDown(toggleKey.Value)) return;
        enabled.Value = !enabled.Value;
        buckets.Clear();
        ParseTags();
        string state = enabled.Value ? "on" : "off";
        CoopNotice.Show("debug|toggle", "Co-op debug " + state, 3f);
        CoopLog.Log.Info("coop: debug " + state + ", the key is " + toggleKey.Value);
    }

    private static void WatchLobby()
    {
        if (!On("sess")) return;
        bool inLobby = Session.IsInLobby;
        if (inLobby && !wasInLobby)
        {
            nextSummary = Time.unscaledTime + SummarySeconds;
            Header(Session.IsHost ? "lobby hosted" : "lobby joined");
        }
        if (!inLobby && wasInLobby) Line("sess", "lobbyend", "the lobby ended, scene " + Scene() + ", role " + Role() + ", deliberate leave " + Session.LeftDeliberately);
        wasInLobby = inLobby;
        if (!inLobby) return;
        if (Time.unscaledTime < nextSummary) return;
        nextSummary = Time.unscaledTime + SummarySeconds;
        Line("sess", "summary", Summary());
    }

    private static void Frame()
    {
        float dt = Time.unscaledDeltaTime;
        frameSum += dt;
        frameCount++;
        if (dt > frameMax) frameMax = dt;
        if (dt > GapSeconds && On("perf"))
        {
            Event("perf", "gap", "a frame of " + dt.ToString("0.00", Inv) + " s, scene " + Scene() + ", area " + Area() + ", SaveData busy " + SaveData.Busy + ", lobby " + Session.IsInLobby);
        }
        if (!Session.IsInLobby || !On("perf"))
        {
            frameSum = 0f;
            frameMax = 0f;
            frameCount = 0;
            nextPerf = 0f;
            return;
        }
        float now = Time.unscaledTime;
        if (nextPerf <= 0f) nextPerf = now + PerfSeconds;
        if (now < nextPerf) return;
        nextPerf = now + PerfSeconds;
        float avg = frameCount > 0 ? frameSum / frameCount : 0f;
        Event("perf", "frame", "frames " + frameCount + ", avg " + (avg * 1000f).ToString("0.0", Inv) + " ms, max " + (frameMax * 1000f).ToString("0.0", Inv) + " ms, lobby " + Session.IsInLobby);
        frameSum = 0f;
        frameMax = 0f;
        frameCount = 0;
    }

    private static void ParseTags()
    {
        tags.Clear();
        if (systems == null || systems.Value == null)
        {
            allTags = true;
            return;
        }
        string[] parts = systems.Value.Split(',');
        bool any = false;
        bool every = false;
        for (int i = 0; i < parts.Length; i++)
        {
            string tag = parts[i].Trim().ToLowerInvariant();
            if (tag.Length == 0) continue;
            if (tag == "all")
            {
                every = true;
                continue;
            }
            any = true;
            tags.Add(tag);
        }
        if (every) tags.Clear();
        allTags = every || !any;
    }

    private static void Line(string tag, string key, string msg)
    {
        float now = Time.unscaledTime;
        string full = tag + "|" + key;
        Bucket bucket;
        if (!buckets.TryGetValue(full, out bucket))
        {
            if (buckets.Count >= MaxBuckets) buckets.Clear();
            bucket = new Bucket();
            bucket.WindowAt = now;
            buckets[full] = bucket;
        }
        else if (now - bucket.WindowAt >= RateWindow)
        {
            int lost = bucket.Suppressed;
            bucket.WindowAt = now;
            bucket.Count = 0;
            bucket.Suppressed = 0;
            if (lost > 0) Write("[dbg:" + tag + "] " + lost + " lines suppressed");
        }
        if (bucket.Count >= RateMax)
        {
            bucket.Suppressed++;
            return;
        }
        bucket.Count++;
        Write("[dbg:" + tag + "] " + msg);
    }

    private static void Write(string text)
    {
        if (lines < LineCap)
        {
            lines++;
            CoopLog.Log.Info(text);
            return;
        }
        if (capSpent) return;
        capSpent = true;
        CoopLog.Log.Info("coop: the debug line cap of " + LineCap + " is reached, only the debug error lines keep logging until the game restarts");
    }

    private static string Prefix(string msg)
    {
        int end = msg.Length;
        for (int i = 0; i < msg.Length; i++)
        {
            char c = msg[i];
            if (c == ' ' || c == '=' || c == ':') { end = i; break; }
        }
        if (end > KeyChars) end = KeyChars;
        if (end <= 0) end = msg.Length < KeyChars ? msg.Length : KeyChars;
        return msg.Substring(0, end);
    }

    private static string Summary()
    {
        StringBuilder b = new StringBuilder();
        b.Append("scene ").Append(Scene());
        b.Append(", area ").Append(Area());
        b.Append(", me ").Append(Session.Me);
        b.Append(", authority ").Append(FreeRoam.Authority);
        b.Append(", role ").Append(Role());
        b.Append(", peers ").Append(Session.Peers.Count);
        b.Append(", in scene ").Append(ScenePeers());
        b.Append(", items with an id ").Append(ItemHost.Advertised);
        b.Append(", items mirrored ").Append(ItemGuest.Mirrored);
        b.Append(", doors indexed ").Append(DoorSync.Indexed);
        b.Append(", elevators ").Append(ElevatorHost.Tracked + ElevatorGuest.Tracked);
        b.Append(", join pending ").Append(Session.JoinPending);
        b.Append(", host in world ").Append(Session.HostInWorld);
        return b.ToString();
    }

    private static string Peers()
    {
        List<ulong> peers = Session.Peers;
        if (peers == null || peers.Count == 0) return "none";
        StringBuilder b = new StringBuilder();
        for (int i = 0; i < peers.Count; i++)
        {
            if (i > 0) b.Append(',');
            b.Append(peers[i]);
        }
        return b.ToString();
    }

    private static int ScenePeers()
    {
        string area = Area();
        if (area.Length == 0) return 0;
        List<ulong> peers = Session.PeersInArea(area);
        return peers == null ? 0 : peers.Count;
    }

    private static string Role()
    {
        if (Session.IsHost) return "host";
        if (Session.IsGuest) return "guest";
        return "solo";
    }

    private static string Scene()
    {
        Scene scene = SceneManager.GetActiveScene();
        string name = scene.name;
        return string.IsNullOrEmpty(name) ? "none" : name;
    }

    private static string Area()
    {
        string area = GameRefs.AreaId;
        return string.IsNullOrEmpty(area) ? "none" : area;
    }

    private static string Loader()
    {
#if BEPINEX5
        return "BepInEx 5";
#else
        return "BepInEx 6";
#endif
    }

    private static string ConfigSummary()
    {
        if (file == null) return "unavailable";
        StringBuilder b = new StringBuilder();
        try
        {
            foreach (KeyValuePair<ConfigDefinition, ConfigEntryBase> row in file)
            {
                if (row.Key == null || row.Value == null) continue;
                if (b.Length > 0) b.Append("; ");
                b.Append(row.Key.Section).Append('/').Append(row.Key.Key).Append('=').Append(Shown(row.Value.BoxedValue));
                if (b.Length >= MaxValueChars)
                {
                    b.Append(" ...");
                    break;
                }
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopDebug.ConfigSummary", e);
            return "unavailable";
        }
        return b.Length == 0 ? "none" : b.ToString();
    }

    private static string Shown(object value)
    {
        if (value == null) return "null";
        string text = value.ToString();
        if (text == null) return "null";
        if (text.Length > 40) text = text.Substring(0, 40);
        return text;
    }

    public static string LobbyLine()
    {
        StringBuilder b = new StringBuilder();
        b.Append("status ").Append(Lobby.Status);
        b.Append(", owner ").Append(Lobby.Owner);
        b.Append(", me ").Append(Session.Me);
        b.Append(", members ").Append(Lobby.Members == null ? 0 : Lobby.Members.Count).Append(" [");
        if (Lobby.Members != null)
        {
            for (int i = 0; i < Lobby.Members.Count; i++)
            {
                ulong id = Lobby.Members[i];
                if (i > 0) b.Append(", ");
                b.Append(Lobby.MemberName(id)).Append('/').Append(id);
            }
        }
        b.Append(']');
        return b.ToString();
    }

    public static string Age(float at)
    {
        if (at <= -999f) return "never";
        return (Time.unscaledTime - at).ToString("0.0", Inv) + " s";
    }
}
