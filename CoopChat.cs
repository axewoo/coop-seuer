using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using ObenseuerCore;
using UnityEngine;
using UnityEngine.EventSystems;

internal static partial class CoopChat
{
    public const string Channel = "chat";
    internal const int MaxChars = 200;
    private const int MaxLines = 100;
    private const int ClosedLines = 6;
    private const int OpenLines = 12;
    private const float LineHeight = 20f;
    private const float FieldHeight = 26f;
    private const float HoldSeconds = 12f;
    private const float FadeSeconds = 3f;
    private const float RateWindow = 5f;
    private const int RateCount = 5;
    private const float DuplicateSeconds = 2f;
    private const string FieldName = "CoopChatInput";

    internal sealed class Line
    {
        public string Chip;
        public string Body;
        public bool Grey;
        public float At;
    }

    private static readonly List<Line> lines = new List<Line>();
    private static readonly Dictionary<ulong, float> windowStart = new Dictionary<ulong, float>();
    private static readonly Dictionary<ulong, int> windowCount = new Dictionary<ulong, int>();
    private static readonly Dictionary<ulong, string> lastText = new Dictionary<ulong, string>();
    private static readonly Dictionary<ulong, float> lastAt = new Dictionary<ulong, float>();
    private static readonly Dictionary<ulong, string> names = new Dictionary<ulong, string>();
    private static readonly HashSet<ulong> muted = new HashSet<ulong>();
    private static ConfigEntry<KeyCode> openKey;
    private static ConfigEntry<KeyCode> slashKey;
    private static GameObject holder;
    private static bool open;
    private static bool grabbed;
    private static GameController grabbedOn;
    private static EventSystem navSystem;
    private static bool navWasOn;
    private static bool menuWasVisible;
    private static int openFrame = -1;
    private static string initial = "";
    private static string input = "";
    private static Rect inputRect;
    private static GUIStyle box;
    private static GUIStyle label;
    private static GUIStyle plain;
    private static GUIStyle grey;
    private static GUIStyle field;

    public static void Install(ConfigFile config)
    {
        try
        {
            openKey = config.Bind("Chat", "OpenKey", KeyCode.T, "Opens the co-op chat input. The game uses T for Tasks, so the chat closes that menu again when its own key press opened it.");
            slashKey = config.Bind("Chat", "OpenKeyAlt", KeyCode.Slash, "A second key that opens the chat with a slash already typed, for a command.");
            Lobby.MemberJoined += OnMemberJoined;
            Lobby.MemberLeft += OnMemberLeft;
            Events.SceneLoaded += scene => Close("the scene changed");
            CoopLog.Log.Info("coop: text chat ready, the open key is " + openKey.Value);
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopChat.Install", e);
        }
    }

    public static void Reset()
    {
        lines.Clear();
        windowStart.Clear();
        windowCount.Clear();
        lastText.Clear();
        lastAt.Clear();
        names.Clear();
        muted.Clear();
        CoopChatHistory.Clear();
        menuWasVisible = false;
        Close("the lobby ended");
    }

    public static void Tick()
    {
        try
        {
            if (!Session.IsInLobby)
            {
                if (open || grabbed) Close("the lobby ended");
                if (lines.Count > 0) lines.Clear();
                return;
            }
            if (open)
            {
                if (BadState()) Close("the game state changed");
                return;
            }
            bool plainKey = Key(openKey);
            bool slash = !plainKey && slashKey != null && slashKey.Value != KeyCode.None && Input.GetKeyDown(slashKey.Value);
            if (!plainKey && !slash)
            {
                menuWasVisible = MenuVisible();
                return;
            }
            if (!menuWasVisible && MenuVisible() && GameUIController.instance != null) GameUIController.instance.HideGameMenu(false);
            if (!menuWasVisible && !Blocked()) Open(slash);
            menuWasVisible = MenuVisible();
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopChat.Tick", e);
        }
    }

    private static void Open(bool slash)
    {
        open = true;
        initial = slash ? "/" : "";
        input = initial;
        openFrame = Time.frameCount;
        NavHold(true);
        Grab();
        CoopLog.Log.Info("coop: the chat input is open");
    }

    private static void Close(string why)
    {
        bool wasOpen = open;
        open = false;
        openFrame = -1;
        input = "";
        initial = "";
        NavHold(false);
        Release();
        if (wasOpen)
        {
            GUIUtility.keyboardControl = 0;
            CoopLog.Log.Info("coop: the chat input is closed: " + why);
        }
    }

    private static void NavHold(bool on)
    {
        try
        {
            if (on)
            {
                if (navSystem != null) return;
                EventSystem system = EventSystem.current;
                if (system == null) return;
                navWasOn = system.sendNavigationEvents;
                system.sendNavigationEvents = false;
                navSystem = system;
                return;
            }
            if (navSystem == null) return;
            navSystem.sendNavigationEvents = navWasOn;
            navSystem = null;
        }
        catch (Exception e)
        {
            navSystem = null;
            CoopLog.Once("CoopChat.NavHold", e);
        }
    }

    private static void Grab()
    {
        try
        {
            GameController game = GameController.instance;
            if (game == null) return;
            if (holder == null)
            {
                holder = new GameObject("CoopChatInput");
                UnityEngine.Object.DontDestroyOnLoad(holder);
            }
            grabbed = true;
            grabbedOn = game;
            game.ControlsDisabled(holder, true);
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopChat.Grab", e);
        }
    }

    private static void Release()
    {
        if (!grabbed) return;
        grabbed = false;
        GameController game = grabbedOn;
        grabbedOn = null;
        try
        {
            if (game == null || holder == null || GameController.instance != game) return;
            game.ControlsEnabled(holder);
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopChat.Release", e);
        }
    }

    public static void Handle(ulong from, string text)
    {
        try
        {
            if (!Session.IsInLobby || text == null || !Session.Peers.Contains(from)) return;
            if (text.StartsWith(CoopChatHistory.Prefix, StringComparison.Ordinal))
            {
                CoopChatHistory.Handle(from, text);
                return;
            }
            if (text.StartsWith("CHATR|", StringComparison.Ordinal))
            {
                if (from != Session.HostId) return;
                int sep = text.IndexOf('|', 6);
                if (sep < 0) return;
                ulong sender;
                if (!ulong.TryParse(text.Substring(6, sep - 6), NumberStyles.None, CultureInfo.InvariantCulture, out sender)) return;
                if (sender == 0UL || sender == Session.Me || !Session.Peers.Contains(sender)) return;
                string relayed = Clean(text.Substring(sep + 1));
                if (relayed.Length == 0 || relayed.Length > MaxChars) return;
                Show(sender, relayed);
                if (CoopDebug.On("chat")) CoopDebug.Event("chat", "recv", "a relayed chat of " + relayed.Length + " chars from " + sender + " was accepted");
                return;
            }
            if (!text.StartsWith("CHAT|", StringComparison.Ordinal)) return;
            if (!Session.IsHost) return;
            string body = text.Substring(5);
            string clean;
            if (!Validate(from, body, out clean)) return;
            Relay(from, clean);
            Show(from, clean);
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopChat.Handle", e);
        }
    }

    private static bool Validate(ulong sender, string body, out string clean)
    {
        clean = Clean(body);
        if (clean.Length == 0 || clean.Length > MaxChars)
        {
            if (CoopDebug.On("chat")) CoopDebug.Event("chat", "drop", "a chat from " + sender + " was dropped, its length is " + clean.Length + " of at most " + MaxChars);
            return false;
        }
        if (!Admit(sender))
        {
            if (CoopDebug.On("chat")) CoopDebug.Event("chat", "drop", "a chat from " + sender + " was dropped, over the rate limit of " + RateCount + " per " + RateWindow + " s");
            return false;
        }
        if (Duplicate(sender, clean))
        {
            if (CoopDebug.On("chat")) CoopDebug.Event("chat", "drop", "a chat from " + sender + " was dropped as a duplicate of its last message");
            return false;
        }
        return true;
    }

    private static void Show(ulong sender, string clean)
    {
        CoopChatHistory.Record(sender, clean);
        if (muted.Contains(sender)) return;
        Append(sender, clean, true);
    }

    private static void Relay(ulong sender, string text)
    {
        string packet = "CHATR|" + sender.ToString(CultureInfo.InvariantCulture) + "|" + text;
        foreach (ulong peer in Session.Peers)
        {
            if (peer == sender) continue;
            Session.Send(peer, Channel, packet, true);
        }
        if (CoopDebug.On("chat")) CoopDebug.Event("chat", "relay", "a chat of " + text.Length + " chars from " + sender + " was relayed, the lobby has " + Session.Peers.Count + " peers");
    }

    private static bool Admit(ulong sender)
    {
        float now = Time.unscaledTime;
        float start;
        if (!windowStart.TryGetValue(sender, out start) || now - start > RateWindow)
        {
            windowStart[sender] = now;
            windowCount[sender] = 1;
            return true;
        }
        int count;
        windowCount.TryGetValue(sender, out count);
        if (count >= RateCount) return false;
        windowCount[sender] = count + 1;
        return true;
    }

    private static bool Duplicate(ulong sender, string body)
    {
        float now = Time.unscaledTime;
        string old;
        float at;
        if (lastText.TryGetValue(sender, out old) && lastAt.TryGetValue(sender, out at) && old == body && now - at < DuplicateSeconds) return true;
        lastText[sender] = body;
        lastAt[sender] = now;
        return false;
    }

    private static string Clean(string text)
    {
        if (text == null) return "";
        StringBuilder b = new StringBuilder(text.Length);
        bool space = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsControl(c) || Format(c) || c == ' ')
            {
                if (b.Length > 0) space = true;
                continue;
            }
            if (space)
            {
                b.Append(' ');
                space = false;
            }
            b.Append(c);
        }
        return b.ToString();
    }

    private static bool Format(char c)
    {
        if (c >= '\u200B' && c <= '\u200F') return true;
        if (c >= '\u202A' && c <= '\u202E') return true;
        if (c >= '\u2060' && c <= '\u2064') return true;
        if (c >= '\u2066' && c <= '\u2069') return true;
        return c == '\uFEFF';
    }

    private static string Shown(ulong sender, bool known)
    {
        string name = known ? Clean(Session.DisplayName(sender)) : "A player";
        if (name.Length == 0) name = "Player";
        name = name.Replace("<", "").Replace(">", "");
        return "<color=" + (sender == Session.HostId ? "#ffd479" : "#9fd4ff") + ">" + name + "</color>";
    }

    private static void Add(ulong sender, string text)
    {
        CoopChatHistory.Record(sender, text);
        Append(sender, text, true);
    }

    internal static Line Append(ulong sender, string text, bool known)
    {
        Line line = new Line();
        if (text.StartsWith("* ", StringComparison.Ordinal))
        {
            line.Chip = "* " + Shown(sender, known) + " ";
            line.Body = text.Substring(2);
        }
        else
        {
            line.Chip = "[" + Shown(sender, known) + "] ";
            line.Body = text;
        }
        line.At = Time.unscaledTime;
        lines.Add(line);
        if (lines.Count > MaxLines) lines.RemoveRange(0, lines.Count - MaxLines);
        return line;
    }

    internal static void Note(string text)
    {
        Line line = new Line();
        line.Chip = "";
        line.Body = text;
        line.Grey = true;
        line.At = Time.unscaledTime;
        lines.Add(line);
        if (lines.Count > MaxLines) lines.RemoveRange(0, lines.Count - MaxLines);
    }

    private static void Submit()
    {
        string typed = Clean(input);
        input = "";
        if (typed.Length == 0) return;
        if (typed[0] == '/')
        {
            Command(typed);
            return;
        }
        Send(typed, false);
    }

    private static void Command(string body)
    {
        int sep = body.IndexOf(' ');
        string name = (sep < 0 ? body : body.Substring(0, sep)).ToLowerInvariant();
        string rest = sep < 0 ? "" : body.Substring(sep + 1).Trim();
        if (name == "/help")
        {
            Note("Chat: Enter sends, Escape closes. Commands: /me text, /mute name (hides their lines on your screen only), /unmute name, /clear.");
            return;
        }
        if (name == "/clear")
        {
            lines.Clear();
            return;
        }
        if (name == "/me")
        {
            if (rest.Length == 0)
            {
                Note("Usage: /me text.");
                return;
            }
            Send(rest, true);
            return;
        }
        if (name == "/mute" || name == "/unmute")
        {
            Mute(rest, name == "/mute");
            return;
        }
        Note("Unknown command. Try /help.");
    }

    private static void Mute(string who, bool mute)
    {
        if (who.Length == 0)
        {
            Note("Usage: " + (mute ? "/mute" : "/unmute") + " name.");
            return;
        }
        int found = 0;
        foreach (ulong peer in Session.Peers)
        {
            string name = Clean(Session.DisplayName(peer));
            if (!string.Equals(name, who, StringComparison.OrdinalIgnoreCase) && !IdSuffix(peer, who)) continue;
            found++;
            if (mute) muted.Add(peer);
            else muted.Remove(peer);
        }
        if (found == 0)
        {
            Note("No player called " + who + " is in the lobby.");
            return;
        }
        string tail = mute ? " muted for this session, on your screen only." : " unmuted.";
        if (found == 1) Note(who + " is" + tail);
        else Note(found + " players are" + tail);
    }

    private static bool IdSuffix(ulong peer, string who)
    {
        string digits = who;
        if (digits.StartsWith("#", StringComparison.Ordinal)) digits = digits.Substring(1);
        if (digits.Length < 3 || digits.Length > 4) return false;
        for (int i = 0; i < digits.Length; i++)
        {
            if (digits[i] < '0' || digits[i] > '9') return false;
        }
        return (peer % 10000UL).ToString("0000", CultureInfo.InvariantCulture).EndsWith(digits, StringComparison.Ordinal);
    }

    private static void Send(string clean, bool action)
    {
        clean = Clean(clean);
        string wire = action ? "* " + clean : clean;
        if (wire.Length == 0) return;
        if (wire.Length > MaxChars)
        {
            Note("The message is too long (" + MaxChars + " characters at most).");
            return;
        }
        if (!Admit(Session.Me))
        {
            Note("Slow down.");
            return;
        }
        if (Duplicate(Session.Me, wire)) return;
        if (Session.IsHost)
        {
            Add(Session.Me, wire);
            Relay(Session.Me, wire);
            return;
        }
        if (!Session.Send(Session.HostId, Channel, "CHAT|" + wire, true)) return;
        Add(Session.Me, wire);
    }

    private static void OnMemberJoined(ulong peer)
    {
        try
        {
            string name = Clean(Session.DisplayName(peer));
            if (name.Length == 0) name = "A player";
            names[peer] = name;
            if (peer != Session.Me) Note(name + " joined the lobby");
            CoopChatHistory.Send(peer);
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopChat.OnMemberJoined", e);
        }
    }

    private static void OnMemberLeft(ulong peer)
    {
        try
        {
            if (Lobby.Members.Contains(peer)) return;
            CoopChatHistory.Leave(peer);
            string name;
            if (!names.TryGetValue(peer, out name)) name = "";
            names.Remove(peer);
            if (name.Length == 0) name = Clean(Session.DisplayName(peer));
            if (name.Length == 0) return;
            if (peer != Session.Me) Note(name + " left the lobby");
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopChat.OnMemberLeft", e);
        }
    }
}
