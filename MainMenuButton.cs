using System;
using System.Collections.Generic;
using System.Reflection;
using ObenseuerCore;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

internal static partial class MainMenuButton
{
    private const float RetryInterval = 1f;
    private const float PanelWidth = 420f;
    private const float PanelHeight = 700f;
    private const float FirstButtonY = -70f;
    private const float ButtonStep = 60f;
    private const float StatusRefreshInterval = 0.5f;
    private const float PressGuard = 0.3f;
    private const int MemberRows = 4;
    private const int PanelButtons = 5;
    private const string ButtonId = "multiplayer";
    private const string ButtonName = "Mod:multiplayer";
    internal const string JoinLabel = "Join world";
    internal const string InviteLabel = "Invite friends";
    internal const string LoadWorldLabel = "Load world";
    private const int LoadCall = 1;
    private const string HostHint = "Invite friends, then Load world.";
    private const string GuestHint = "You play in the host's world. Join when the host is in game.";
    private const string OfflineHint = "Host a lobby, press Load world to play your own save, or accept a Steam invite.";
    private const string PendingHint = "Steam is setting up the lobby. This can take a few seconds.";
    private const string LoadWorldFailed = "The main menu is not open, so the load game menu cannot open.";
    private const string OverlayOff = "The Steam overlay is off. Friends can still join from your name in their Steam friends list.";
    private const string InviteFailed = "The Steam invite dialog did not open.";
    private const string KeepWindowedNotice = "The co-op page needs Borderless or Windowed screen mode (Options, Display).";
    private static readonly string[] GridFields = { "newGame", "loadGame", "options", "unknown", "credits", "quit" };
    private static readonly Dictionary<string, FieldInfo> GridInfos = GridFieldInfos();
    private static readonly List<Transform> hidden = new List<Transform>();
    private static readonly TMP_Text[] rows = new TMP_Text[MemberRows];
    private static readonly UnityEngine.UI.Button[] buttons = new UnityEngine.UI.Button[PanelButtons];
    private static MainMenu attached;
    private static GameObject panel;
    private static GameObject blocker;
    private static TMP_Text status;
    private static TMP_Text message;
    private static int shownLayout = -1;
    private static int joinSlot = -1;
    private static string notice = "";
    private static float nextAttempt;
    private static float nextStatusRefresh;
    private static bool controlsHeld;
    private static bool closePending;
    private static int pendingCall;
    private static bool releasePending;
    private static bool loadClosed;
    private static string lastLabel = "";
    private static float lastPressAt = -1000f;
    private static int closedFrame = -1;
    private static bool pageUp;

    public static void Init()
    {
        if (!MainMenuButtons.Add(ButtonId, "MULTIPLAYER", OpenPanel)) CoopLog.Log.Error("main menu button " + ButtonId + " not added: the id is taken");
    }

    public static void Tick()
    {
        try
        {
            if (releasePending)
            {
                releasePending = false;
                Release();
                if (loadClosed)
                {
                    loadClosed = false;
                    if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
                }
            }
            if (closePending)
            {
                closePending = false;
                int call = pendingCall;
                pendingCall = 0;
                releasePending = call != 0;
                loadClosed = call != 0;
                ClosePanel();
                if (call == LoadCall) LoadWorld();
                else if (call == NewCall) NewWorld();
            }
            MainMenu menu = MainMenu.Instance;
            if (menu == null)
            {
                attached = null;
                Restore();
                LobbyAvatars.Clear();
                return;
            }
            if (menu == attached && panel != null)
            {
                if (pageUp)
                {
                    PageTick();
                }
                else
                {
                    LobbyPage.OverlayTick();
                    if (panel.activeSelf && CloseRequested()) Press("Back", BackPressed);
                }
                if (panel.activeSelf) KeepPanelSelected();
                if (!Showing() && controlsHeld && !releasePending) Release();
                UpdateStatus();
                return;
            }
            if (Time.unscaledTime < nextAttempt) return;
            nextAttempt = Time.unscaledTime + RetryInterval;
            Attach(menu);
        }
        catch (Exception e)
        {
            CoopLog.Once("MainMenuButton.Tick", e);
        }
    }

    private static void Attach(MainMenu menu)
    {
        if (panel == null) BuildPanel(FindTemplate());
        if (panel == null) return;
        attached = menu;
        CoopLog.Log.Info("Multiplayer panel ready on the main menu");
    }

    private static UnityEngine.UI.Button FindTemplate()
    {
        MainMenu menu = MainMenu.Instance;
        if (menu == null) return null;
        UnityEngine.UI.Button fallback = null;
        foreach (UnityEngine.UI.Button button in menu.GetComponentsInChildren<UnityEngine.UI.Button>(true))
        {
            for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
            {
                if (button.onClick.GetPersistentTarget(i) != menu) continue;
                if (button.onClick.GetPersistentMethodName(i) == "LoadGame") return button;
                if (fallback == null) fallback = button;
            }
        }
        return fallback;
    }

    private static UnityEngine.UI.Button OwnButton()
    {
        MainMenu menu = MainMenu.Instance;
        if (menu == null) return null;
        foreach (UnityEngine.UI.Button button in menu.GetComponentsInChildren<UnityEngine.UI.Button>(true))
        {
            if (button.name == ButtonName) return button;
        }
        return null;
    }

    private static UnityEngine.UI.Button CloneButton(UnityEngine.UI.Button template, Transform parent, string name, string label, Action onClick)
    {
        GameObject copy = (GameObject)UnityEngine.Object.Instantiate(template.gameObject, parent, false);
        StripLocalization(copy);
        TMP_Text[] texts = copy.GetComponentsInChildren<TMP_Text>(true);
        if (texts.Length == 0)
        {
            UnityEngine.Object.Destroy(copy);
            CoopLog.Once("MainMenuButton.label", "template button has no TMP label");
            return null;
        }
        foreach (TMP_Text text in texts)
        {
            text.text = label;
        }
        copy.name = name;
        UnityEngine.UI.Button button = copy.GetComponent<UnityEngine.UI.Button>();
        button.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        button.onClick.AddListener(() => onClick());
        copy.SetActive(true);
        return button;
    }

    private static void StripLocalization(GameObject root)
    {
        foreach (Component component in root.GetComponentsInChildren<Component>(true))
        {
            if (component != null && component.GetType().FullName.StartsWith("UnityEngine.Localization", StringComparison.Ordinal))
            {
                UnityEngine.Object.DestroyImmediate(component);
            }
        }
    }

    private static List<Transform> GridButtons()
    {
        List<Transform> cells = new List<Transform>();
        foreach (MainMenuNavigationHelper helper in UnityEngine.Object.FindObjectsByType<MainMenuNavigationHelper>(FindObjectsSortMode.None))
        {
            foreach (string field in GridFields)
            {
                UnityEngine.UI.Button cell = GridButton(helper, field);
                if (cell != null) cells.Add(cell.transform);
            }
        }
        return cells;
    }

    private static Dictionary<string, FieldInfo> GridFieldInfos()
    {
        Dictionary<string, FieldInfo> infos = new Dictionary<string, FieldInfo>();
        foreach (string field in GridFields)
        {
            infos[field] = typeof(MainMenuNavigationHelper).GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }
        return infos;
    }

    private static UnityEngine.UI.Button GridButton(MainMenuNavigationHelper helper, string field)
    {
        FieldInfo info = GridInfos[field];
        return info == null ? null : info.GetValue(helper) as UnityEngine.UI.Button;
    }

    private static void BuildPanel(UnityEngine.UI.Button template)
    {
        if (template == null) return;
        Canvas canvas = template.GetComponentInParent<Canvas>();
        Transform root = canvas != null ? canvas.transform : template.transform.root;
        panel = new GameObject("Mod:Multiplayer panel", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(root, false);
        RectTransform rect = (RectTransform)panel.transform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
        rect.anchoredPosition = Vector2.zero;
        panel.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.85f);
        JoinBlocker(root);
        CloneLabel(template, new Vector2(0f, 200f), 40f).text = "MULTIPLAYER";
        status = CloneLabel(template, new Vector2(0f, 155f), 30f);
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = CloneLabel(template, new Vector2(0f, 118f - 30f * i), 26f);
        }
        message = CloneLabel(template, new Vector2(0f, -10f), 50f);
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i] = AddPanelButton(template, "Lobby " + (i + 1), FirstButtonY - ButtonStep * i, () => { });
        }
        panel.SetActive(false);
        shownLayout = -1;
    }

    private static void JoinBlocker(Transform root)
    {
        blocker = new GameObject("Mod:Multiplayer blocker", typeof(RectTransform), typeof(Image));
        blocker.transform.SetParent(root, false);
        RectTransform rect = (RectTransform)blocker.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        Image image = blocker.GetComponent<Image>();
        image.color = new Color(0f, 0f, 0f, 0.6f);
        image.raycastTarget = true;
        blocker.transform.SetSiblingIndex(panel.transform.GetSiblingIndex());
        blocker.SetActive(false);
    }

    private static UnityEngine.UI.Button AddPanelButton(UnityEngine.UI.Button template, string label, float y, Action onClick)
    {
        UnityEngine.UI.Button button = CloneButton(template, panel.transform, "Mod:" + label, label, onClick);
        if (button == null) return null;
        RectTransform rect = (RectTransform)button.transform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(0f, y);
        return button;
    }

    private static TMP_Text CloneLabel(UnityEngine.UI.Button template, Vector2 position, float height)
    {
        TMP_Text source = template.GetComponentInChildren<TMP_Text>(true);
        if (source == null) return null;
        GameObject copy = (GameObject)UnityEngine.Object.Instantiate(source.gameObject, panel.transform, false);
        StripLocalization(copy);
        copy.name = "Mod:label";
        RectTransform rect = (RectTransform)copy.transform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(PanelWidth - 40f, height);
        rect.anchoredPosition = position;
        TMP_Text text = copy.GetComponent<TMP_Text>();
        text.enableAutoSizing = true;
        text.fontSizeMax = text.fontSize;
        return text;
    }

    private static void OpenPanel()
    {
        MainMenu menu = MainMenu.Instance;
        if (menu == null || menu.currentMenu != MainMenu.CurrentWindow.Main)
        {
            CoopLog.Log.Info("Multiplayer button ignored: the main menu is not on the main screen");
            return;
        }
        UnityEngine.UI.Button own = OwnButton();
        if (panel == null || Showing() || own == null || closedFrame == Time.frameCount) return;
        Transform parent = own.transform.parent;
        foreach (Transform cell in GridButtons())
        {
            Hide(cell);
        }
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform sibling = parent.GetChild(i);
            if (sibling != panel.transform && sibling.name.StartsWith("Mod:", StringComparison.Ordinal)) Hide(sibling);
        }
        notice = "";
        releasePending = false;
        Hold();
        if (OpenPage()) return;
        ShowPanel();
    }

    private static bool OpenPage()
    {
        if (Screen.fullScreenMode == FullScreenMode.ExclusiveFullScreen)
        {
            notice = KeepWindowedNotice;
            CoopLog.Log.Info("lobby page not opened: WebView2 is not drawn over exclusive fullscreen, showing the panel");
            return false;
        }
        try
        {
            pageUp = LobbyPage.Open();
        }
        catch (Exception e)
        {
            CoopLog.Once("MainMenuButton.page", e);
        }
        if (pageUp && EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        return pageUp;
    }

    private static void ShowPanel()
    {
        if (blocker != null) blocker.SetActive(true);
        panel.SetActive(true);
        shownLayout = -1;
        RefreshPanel();
        Select(FirstUsable());
    }

    private static void PageTick()
    {
        if (!LobbyPage.Failed() && Screen.fullScreenMode != FullScreenMode.ExclusiveFullScreen)
        {
            LobbyPage.Tick();
            return;
        }
        if (LobbyPage.Failed())
        {
            CoopLog.Log.Info("lobby page closed unexpectedly, showing the panel");
        }
        else
        {
            notice = KeepWindowedNotice;
            CoopLog.Log.Info("lobby page closed: WebView2 is not drawn over exclusive fullscreen, showing the panel");
        }
        pageUp = false;
        LobbyPage.Close();
        ShowPanel();
    }

    private static bool Showing()
    {
        return panel.activeSelf || pageUp;
    }

    private static void Hold()
    {
        if (controlsHeld) return;
        controlsHeld = true;
        MainMenuButtons.SetControlsEnabled(false);
    }

    private static void Release()
    {
        if (!controlsHeld) return;
        controlsHeld = false;
        MainMenuButtons.SetControlsEnabled(true);
    }

    private static void ClosePanel()
    {
        closedFrame = Time.frameCount;
        closePending = false;
        pendingCall = 0;
        Restore();
        Select(OwnButton());
    }

    private static void Hide(Transform item)
    {
        if (!item.gameObject.activeSelf) return;
        item.gameObject.SetActive(false);
        hidden.Add(item);
    }

    private static void Restore()
    {
        foreach (Transform item in hidden)
        {
            if (item != null) item.gameObject.SetActive(true);
        }
        hidden.Clear();
        if (panel != null) panel.SetActive(false);
        if (blocker != null) blocker.SetActive(false);
        closePending = false;
        pendingCall = 0;
        newWorldArmed = false;
        if (pageUp)
        {
            pageUp = false;
            LobbyPage.Close();
        }
        if (!releasePending) Release();
    }

    private static void KeepPanelSelected()
    {
        if (EventSystem.current == null) return;
        GameObject selected = EventSystem.current.currentSelectedGameObject;
        if (selected != null && selected.activeInHierarchy && selected.transform.IsChildOf(panel.transform)) return;
        Select(FirstUsable());
    }

    private static UnityEngine.UI.Button FirstUsable()
    {
        foreach (UnityEngine.UI.Button button in buttons)
        {
            if (button != null && button.gameObject.activeSelf && button.interactable) return button;
        }
        return buttons[0];
    }

    private static void Select(Component target)
    {
        if (target == null || EventSystem.current == null) return;
        EventSystem.current.SetSelectedGameObject(target.gameObject);
    }

    private static bool CloseRequested()
    {
        return Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Joystick1Button1);
    }

    private static void UpdateStatus()
    {
        if (panel == null || status == null || !panel.activeSelf || Time.unscaledTime < nextStatusRefresh) return;
        nextStatusRefresh = Time.unscaledTime + StatusRefreshInterval;
        RefreshPanel();
    }

    internal static void JoinStateChanged()
    {
        if (panel != null && panel.activeSelf) RefreshPanel();
    }

    private static void RefreshPanel()
    {
        LobbyStatus state = Lobby.Status;
        bool join = Session.HostInWorld;
        int layout = ((int)state * 2 + (join ? 1 : 0)) * 2 + (Session.HostReload ? 1 : 0);
        if (shownLayout != layout)
        {
            shownLayout = layout;
            ShowButtons(state, join);
        }
        if (joinSlot >= 0) buttons[joinSlot].interactable = Session.JoinBlock().Length == 0 && !Session.JoinPending;
        SetText(status, Session.StatusText);
        List<ulong> members = Lobby.Members;
        for (int i = 0; i < rows.Length; i++)
        {
            SetText(rows[i], i < members.Count ? Session.MemberRow(members[i]) : "");
        }
        SetText(message, Session.JoinPending ? Session.JoinPendingText : Message());
    }

    internal static string Message()
    {
        if (Session.HostReload) return Session.HostReloadText;
        if (notice.Length > 0) return notice;
        if (LobbyPage.Notice.Length > 0) return LobbyPage.Notice;
        if (Lobby.Error.Length > 0) return Lobby.Error;
        if (Session.Notice.Length > 0) return Session.Notice;
        string block = Session.JoinBlock();
        if (block.Length > 0) return block;
        return Hint();
    }

    private static string Hint()
    {
        switch (Lobby.Status)
        {
            case LobbyStatus.Hosting:
                return HostHint;
            case LobbyStatus.Joined:
                return GuestHint;
            case LobbyStatus.Pending:
                return PendingHint;
            default:
                return OfflineHint;
        }
    }
}
