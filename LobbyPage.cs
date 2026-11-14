using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using ObenseuerCore;
using Steamworks;
using UnityEngine;

public class PageRequest
{
    public string type;
    public string label;
}

public class PageButton
{
    public string label;
    public bool enabled;
    public string reason;
}

public class PageMember
{
    public string id;
    public string name;
    public bool host;
    public bool you;
    public bool inWorld;
    public string place;
}

public class PageState
{
    public string type;
    public string status;
    public string strip;
    public bool stripOn;
    public string message;
    public bool hostInWorld;
    public bool emptyInvite;
    public bool joining;
    public bool waiting;
    public string joinText;
    public PageMember[] members;
    public PageButton[] buttons;
}

public class PageAvatar
{
    public string type;
    public string id;
    public string data;
}

internal static class LobbyPage
{
    private const string FolderName = "lobby";
    private const string PageFile = "index.html";
    private const float PushInterval = 0.5f;
    private const float ReadyTimeout = 10f;
    private const float OverlayWait = 3f;
    private const int EscapeKey = 27;
    private const string OverlayReason = "obenseuer.coop overlay";
    private const string OverlayMissing = "Steam overlay did not open. Turn it on in Steam settings, In-Game.";
    private static Callback<GameOverlayActivated_t> overlay;
    private static WebView view;
    private static bool ready;
    private static float openedAt;
    private static float nextPush;
    private static bool overlayHeld;
    private static bool overlaySuspended;
    private static bool overlayActiveSeen;
    private static bool showFailed;
    private static float keySeenAt = -1000f;
    private static float overlayAskedAt;
    private static string overlayNotice = "";
    private static FieldInfo lobbyIdField;
    private static bool lobbyIdSearched;
    private static readonly HashSet<ulong> sent = new HashSet<ulong>();
    private static string lastState = "";

    internal static bool IsOpen
    {
        get { return view != null; }
    }

    internal static bool OverlayHeld
    {
        get { return overlayHeld; }
    }

    internal static string Notice
    {
        get { return overlayNotice; }
    }

    internal static void ClearNotice()
    {
        overlayNotice = "";
    }

    internal static bool PageSawEscape(float within)
    {
        return Time.unscaledTime - keySeenAt < within;
    }

    public static bool Open()
    {
        if (view != null) return true;
        string folder = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "web", FolderName);
        view = WebUi.Open(Plugin.PluginGuid, folder, PageFile, false);
        if (view == null)
        {
            CoopLog.Log.Info("lobby page did not open: " + WebUi.LastError);
            return false;
        }
        view.Message += OnMessage;
        view.KeyPressed += OnKey;
        view.EscapeHides = false;
        ready = false;
        openedAt = Time.unscaledTime;
        nextPush = 0f;
        if (overlaySuspended) WebUi.ResumeAll(OverlayReason);
        overlayHeld = false;
        overlaySuspended = false;
        overlayActiveSeen = false;
        showFailed = false;
        keySeenAt = -1000f;
        overlayNotice = "";
        sent.Clear();
        lastState = "";
        return true;
    }

    public static void WatchOverlay()
    {
        if (overlay != null || !Steam.IsReady) return;
        overlay = Callback<GameOverlayActivated_t>.Create(OnOverlay);
        CoopLog.Log.Info("overlay watch registered, overlay enabled " + Steam.OverlayEnabled);
    }

    private static void OnOverlay(GameOverlayActivated_t active)
    {
        try
        {
            bool on = active.m_bActive != 0;
            if (on) overlayActiveSeen = true;
            CoopLog.Log.Info("overlay callback active " + (on ? 1 : 0) + ", overlay enabled " + Steam.OverlayEnabled + ", lobby " + LobbyId() + ", owner " + Lobby.Owner + ", page hidden " + overlayHeld);
            if (!on && overlayHeld) ReleaseOverlay(true);
        }
        catch (Exception e)
        {
            CoopLog.Once("LobbyPage.overlay", e);
        }
    }

    public static bool Failed()
    {
        if (view == null) return false;
        if (showFailed) return true;
        if (view.IsClosed) return true;
        return !ready && Time.unscaledTime - openedAt > ReadyTimeout;
    }

    public static void Close()
    {
        ReleaseOverlay(false);
        overlayNotice = "";
        if (view == null) return;
        WebView closing = view;
        view = null;
        ready = false;
        lastState = "";
        closing.Close();
    }

    public static void Tick()
    {
        OverlayTick();
        if (view == null) return;
        if (!overlayHeld) LobbyInput.Forward(view, ready);
        if (!ready || Time.unscaledTime < nextPush) return;
        nextPush = Time.unscaledTime + PushInterval;
        Push(false);
    }

    internal static void HoldOverlay()
    {
        if (overlayHeld) return;
        overlayHeld = true;
        overlayActiveSeen = false;
        overlayAskedAt = Time.unscaledTime;
        overlayNotice = "";
        CoopLog.Log.Info("overlay hold: overlay enabled " + Steam.OverlayEnabled + ", lobby " + LobbyId() + ", owner " + Lobby.Owner);
        if (view == null) return;
        overlaySuspended = true;
        WebUi.SuspendAll(OverlayReason);
        view.Hide();
    }

    internal static void ReleaseOverlay()
    {
        ReleaseOverlay(true);
    }

    private static void ReleaseOverlay(bool show)
    {
        if (!overlayHeld) return;
        overlayHeld = false;
        overlayActiveSeen = false;
        if (overlaySuspended)
        {
            overlaySuspended = false;
            WebUi.ResumeAll(OverlayReason);
        }
        if (show && view != null && !view.Show())
        {
            showFailed = true;
            CoopLog.Log.Error("LobbyPage.show: the page did not come back after the overlay hold, the panel takes over");
        }
        CoopLog.Log.Info("overlay hold released, page shown " + show);
    }

    internal static void OverlayTick()
    {
        if (!overlayHeld) return;
        if (!Steam.OverlayEnabled)
        {
            CoopLog.Log.Info("overlay hold released: the overlay is off in Steam settings");
            ReleaseOverlay(true);
            return;
        }
        if (overlayActiveSeen) return;
        if (Time.unscaledTime - overlayAskedAt < OverlayWait) return;
        overlayNotice = OverlayMissing;
        CoopLog.Log.Info("overlay hold released: no overlay callback within " + OverlayWait + " s");
        ReleaseOverlay(true);
    }

    internal static void PushNow()
    {
        Push(true);
    }

    private static void Push(bool force)
    {
        if (view == null) return;
        if (force) lastState = "";
        string json = JsonConvert.SerializeObject(State());
        if (json != lastState && view.Post(json)) lastState = json;
        foreach (ulong id in Lobby.Members) SendAvatar(id);
    }

    private static void SendAvatar(ulong id)
    {
        if (view == null || sent.Contains(id)) return;
        string data = LobbyAvatars.Get(id);
        if (data == null) return;
        if (view.Post(JsonConvert.SerializeObject(new PageAvatar { type = "avatar", id = id.ToString(), data = data }))) sent.Add(id);
    }

    private static PageState State()
    {
        List<string> labels = new List<string>();
        List<Func<bool>> actions = new List<Func<bool>>();
        MainMenuButton.Layout(Lobby.Status, Session.HostInWorld, labels, actions);
        PageState state = new PageState();
        state.type = "state";
        state.status = Session.StatusText;
        state.stripOn = StripOn();
        state.strip = Session.IsInLobby ? (state.stripOn ? "Host is in world" : "Host is in the menu") : "";
        state.message = MainMenuButton.Message();
        state.hostInWorld = Session.HostInWorld;
        state.emptyInvite = labels.Contains(MainMenuButton.InviteLabel);
        state.joining = Session.JoinPending;
        state.waiting = Session.HostReload;
        state.joinText = Session.HostReload ? Session.HostReloadText : Session.JoinPendingText;
        state.members = Members();
        state.buttons = Buttons(labels);
        return state;
    }

    private static bool StripOn()
    {
        return Session.IsGuest ? Session.HostInWorld : Session.LocalInWorld;
    }

    private static PageMember[] Members()
    {
        List<PageMember> rows = new List<PageMember>();
        foreach (ulong id in Lobby.Members)
        {
            PageMember row = new PageMember();
            row.id = id.ToString();
            row.name = Session.DisplayName(id);
            row.host = id == Lobby.Owner;
            row.you = id == Steam.LocalId;
            row.inWorld = row.you ? Session.LocalInWorld : Session.AreaOf(id) != null;
            row.place = row.inWorld ? "In world" : "In menu";
            rows.Add(row);
        }
        return rows.ToArray();
    }

    private static PageButton[] Buttons(List<string> labels)
    {
        PageButton[] buttons = new PageButton[labels.Count];
        for (int i = 0; i < labels.Count; i++)
        {
            buttons[i] = new PageButton();
            buttons[i].label = labels[i];
            buttons[i].reason = MainMenuButton.DisabledReason(labels[i]);
            buttons[i].enabled = buttons[i].reason.Length == 0;
        }
        return buttons;
    }

    private static void OnMessage(string json)
    {
        try
        {
            PageRequest request = JsonConvert.DeserializeObject<PageRequest>(json);
            if (request == null || request.type == null) return;
            if (request.type == "ready")
            {
                ready = true;
                openedAt = Time.unscaledTime;
                Push(true);
            }
            else if (request.type == "press" && request.label != null)
            {
                MainMenuButton.PagePress(request.label);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("LobbyPage.message", e);
        }
    }

    private static void OnKey(int key)
    {
        if (key != EscapeKey) return;
        keySeenAt = Time.unscaledTime;
        try
        {
            MainMenuButton.PagePress("Back");
        }
        catch (Exception e)
        {
            CoopLog.Once("LobbyPage.key", e);
        }
    }

    private static string LobbyId()
    {
        if (!lobbyIdSearched)
        {
            lobbyIdSearched = true;
            lobbyIdField = typeof(Lobby).GetField("lobbyId", BindingFlags.Static | BindingFlags.NonPublic);
            if (lobbyIdField == null) CoopLog.Log.Info("lobby id is not exposed by the framework");
        }
        if (lobbyIdField == null) return "unknown";
        object value = lobbyIdField.GetValue(null);
        return value == null ? "unknown" : value.ToString();
    }
}
