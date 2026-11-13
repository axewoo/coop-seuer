using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;

internal static partial class Session
{
    private const string ModId = "obenseuer.coop";
    private const string GameTag = "obenseuer-coop";
    private const string WorldChannel = "world";
    private const string TravelPrefix = "TRAVEL|";
    private const int MaxSceneLength = 80;
    private const float HostInterval = 2f;
    private const float HostStale = 6f;
    private const int MaxTextLength = 64;
    private const float AutoJoinRetry = 2f;
    private const float AutoJoinTimeout = 30f;
    private const float JoinAnswerTimeout = 20f;
    public const int MaxMembers = 4;
    public const string JoinPendingText = "Joining world";
    private static bool hostRequested;
    private static bool hostLeft;
    private static bool hostQuit;
    private static ulong knownHost;
    private static bool hostInWorld;
    private static bool hostSavesOff;
    private static string hostScene = "";
    private static string hostBuild = "";
    private static float hostSeenAt = -1000f;
    private static string sentHost = "";
    private static float nextHostSend;
    private static string notice = "";
    private static bool menuPending;
    private static bool releasePending;
    private static bool worldReceived;
    private static bool joinPending;
    private static float joinAskedAt;
    private static bool invited;
    private static float invitedAt;
    private static float lastAutoJoinAt;
    private static string worldId = "";
    private static string worldIdOwner = "";
    private static readonly Queue<ulong> joinQueue = new Queue<ulong>();

    public static void Init()
    {
        Lobby.JoinRequested += id => { invited = true; invitedAt = Time.unscaledTime; Join(id); };
        Lobby.MemberLeft += OnMemberLeft;
        Lobby.MemberLeft += PowerSync.OnMemberLeft;
        Lobby.OwnerChanged += OnOwnerChanged;
        WorldShip.Idle += StartNextJoin;
        WorldShip.Received += OnWorldReceived;
        DebugInit();
    }

    public static void Tick()
    {
        MenuTick();
        ReleaseOnMenu();
        AutoJoinTick();
        HostReloadTick();
        JoinPendingTick();
        if (IsGuest) knownHost = Lobby.Owner;
        HostGoneTick();
        ConnectionTick();
        if (Lobby.Status == LobbyStatus.Hosting) SendHost();
    }

    public static bool IsInLobby
    {
        get { return Lobby.Status == LobbyStatus.Hosting || Lobby.Status == LobbyStatus.Joined; }
    }

    public static bool IsGuest
    {
        get { return Lobby.Status == LobbyStatus.Joined; }
    }

    public static bool IsHost
    {
        get { return Lobby.IsHost; }
    }

    public static bool HostInWorld
    {
        get { return IsGuest && hostInWorld && Time.unscaledTime - hostSeenAt < HostStale; }
    }

    public static bool LocalInWorld
    {
        get { return GameRefs.LocalPlayer != null && GameRefs.AreaId != null; }
    }

    public static string Notice
    {
        get { return notice; }
    }

    public static bool WorldReceived
    {
        get { return worldReceived; }
    }

    public static bool JoinPending
    {
        get { return joinPending; }
    }

    internal static void SetNotice(string text)
    {
        notice = text;
    }

    public static ulong Me
    {
        get { return Net.Me; }
    }

    public static List<ulong> Peers
    {
        get { return Net.Peers; }
    }

    internal static ulong HostId
    {
        get { return Lobby.Owner; }
    }

    public static string StatusText
    {
        get
        {
            switch (Lobby.Status)
            {
                case LobbyStatus.Pending:
                    return hostRequested ? "Creating lobby..." : "Joining lobby...";
                case LobbyStatus.Hosting:
                    return "Your lobby (" + Lobby.Members.Count + "/" + MaxMembers + ")";
                case LobbyStatus.Joined:
                    return "In " + DisplayName(Lobby.Owner) + "'s lobby (" + Lobby.Members.Count + "/" + MaxMembers + ")";
                default:
                    return "Not in a lobby";
            }
        }
    }

    public static string DisplayName(ulong id)
    {
        string name = Lobby.MemberName(id);
        return name.Length > 0 ? name : "Player " + (id % 10000).ToString("0000");
    }

    public static string MemberRow(ulong id)
    {
        string text = DisplayName(id);
        if (id == Lobby.Owner) text += " (host)";
        if (id == Steam.LocalId) text += " (you)";
        return text;
    }

    public static bool Host()
    {
        if (RefuseHosting()) return false;
        ClearWorldId();
        hostRequested = true;
        notice = "";
        CoopAlert.Clear();
        return Lobby.Host(GameTag, Plugin.Version, MaxMembers, false);
    }

    internal static bool RefuseHosting()
    {
        if (!GuestSession.InHostWorld) return false;
        notice = "You are still in the host's world.";
        if (Notifications.instance != null) CoopNotice.Show("host|inworld", notice, 5f);
        CoopLog.Log.Info("coop: hosting refused while the guest is in the host's world");
        return true;
    }

    public static bool Join(ulong lobby)
    {
        hostRequested = false;
        notice = "";
        menuPending = false;
        CoopAlert.Clear();
        bool ok = Lobby.Join(lobby, GameTag, Plugin.Version);
        if (ok) ResetHost();
        return ok;
    }

    public static bool Leave()
    {
        invited = false;
        leftDeliberately = true;
        if (Lobby.Status == LobbyStatus.Hosting) SendAll(WorldChannel, "BYE|closed", true);
        ResetHost();
        menuPending = false;
        return Lobby.Leave();
    }

    public static bool Invite()
    {
        return Lobby.Invite();
    }

    public static string JoinBlock()
    {
        if (!IsGuest) return "";
        if (!HostInWorld) return "Wait for the host to load a world.";
        if (GuestWorld.TutorialScene(hostScene)) return "Wait for the host to finish the tutorial.";
        if (worldReceived || GuestSession.Active || GuestSession.Guard) return "You are already in the host's world.";
        string hooks = GuestWorld.HookBlock();
        if (hooks.Length > 0) return hooks;
        if (hostSavesOff) return "Host's difficulty has saving turned off.";
        if (hostBuild != Core.GameBuild) return "The host runs game build " + hostBuild + ", you run " + Core.GameBuild + ".";
        return "";
    }

    internal static bool LocalSavesDisabled()
    {
        DifficultyController controller = DifficultyController.instance;
        if (controller == null || controller.currentDifficultyInfo == null) return false;
        return controller.currentDifficultyInfo.userSavesDisabled;
    }

    public static bool JoinWorld()
    {
        string block = JoinBlock();
        if (block.Length > 0)
        {
            CoopLog.Log.Info("join world refused: " + block);
            return false;
        }
        bool sent = Send(HostId, WorldChannel, "WREQ", true);
        CoopLog.Log.Info("join world request sent to " + DisplayName(HostId) + ": " + sent);
        if (sent)
        {
            lastJoinAt = Time.unscaledTime;
            StartJoinPending();
        }
        return sent;
    }

    public static bool JoinWorldManual()
    {
        invited = false;
        return JoinWorld();
    }

    public static void WorldRequested(ulong guest)
    {
        CoopLog.Log.Info("join world requested by " + DisplayName(guest) + " (" + guest + ")");
        if (!WorldShip.Busy)
        {
            StartJoin(guest);
            return;
        }
        if (!joinQueue.Contains(guest)) joinQueue.Enqueue(guest);
        Ack(guest, true, "queued");
    }

    public static void SayGoodbye()
    {
        if (Lobby.Status != LobbyStatus.Hosting) return;
        SendAll(WorldChannel, "BYE", true);
    }

    public static void SayGoodbye(string reason)
    {
        if (Lobby.Status != LobbyStatus.Hosting) return;
        SendAll(WorldChannel, "BYE|" + reason, true);
    }

    public static void HandleWorld(ulong from, string text)
    {
        try
        {
            if (text.StartsWith("HST|", StringComparison.Ordinal))
            {
                if (IsGuest && from == HostId) HandleHost(text);
            }
            else if (text == "BYE" || text.StartsWith("BYE|", StringComparison.Ordinal))
            {
                if (IsGuest && from == HostId) HandleBye(text);
            }
            else if (text == "WREQ")
            {
                if (IsHost && Peers.Contains(from)) WorldRequested(from);
            }
            else if (text.StartsWith("WACK|", StringComparison.Ordinal))
            {
                if (IsGuest && from == HostId) HandleAck(text);
            }
            else if (text.StartsWith(TravelPrefix, StringComparison.Ordinal))
            {
                if (IsGuest && from == HostId) HandleTravel(text);
            }
            else if (FreeRoam.HandleWorld(from, text) || Solo.HandleWorld(from, text))
            {
            }
            else if (text.StartsWith("LOAD|", StringComparison.Ordinal))
            {
                if (IsHost && Peers.Contains(from)) HandleLoaded(from, text);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("Session.HandleWorld", e);
        }
    }

    public static bool Register(string channel, Action<ulong, string> handler)
    {
        return Net.Register(ModId, channel, handler);
    }

    public static bool Send(ulong peer, string channel, string payload, bool reliable)
    {
        return Net.Send(peer, channel, payload, reliable);
    }

    public static void SendAll(string channel, string payload, bool reliable)
    {
        foreach (ulong id in Net.Peers)
        {
            Net.Send(id, channel, payload, reliable);
        }
    }

    public static bool Publish(string area, Vector3 position, float yaw, bool moving)
    {
        return Presence.Publish(area, position, yaw, moving);
    }

    public static List<PresenceRecord> Remotes()
    {
        return Presence.Remotes;
    }

    public static string AreaOf(ulong peer)
    {
        foreach (PresenceRecord record in Presence.Remotes)
        {
            if (record.Peer == peer) return record.Area;
        }
        return null;
    }

    public static List<ulong> PeersInArea(string area)
    {
        List<ulong> result = new List<ulong>();
        foreach (PresenceRecord record in Presence.Remotes)
        {
            if (record.Area == area) result.Add(record.Peer);
        }
        return result;
    }

    private static void OnMemberLeft(ulong id)
    {
        if (IsGuest && id == knownHost) MarkHostLeft();
    }

    private static void OnOwnerChanged(ulong previous, ulong owner)
    {
        if (IsGuest && previous != 0 && owner != 0) MarkHostLeft();
    }

    internal static string WorldIdFor(string owner)
    {
        if (owner.Length == 0 || owner != worldIdOwner) return "";
        return worldId;
    }

    internal static string WorldSlot
    {
        get { return worldIdOwner; }
    }

    internal static void NoteSaveSlot(string slot)
    {
        if (slot.Length == 0 || slot == worldIdOwner) return;
        worldIdOwner = slot;
        worldId = "";
        CoopLog.Log.Info("coop: the host loaded the save slot " + slot + ", the world id starts fresh");
    }

    internal static void AdoptWorldId(string id, string owner)
    {
        worldId = id;
        worldIdOwner = owner;
    }

    private static void ClearWorldId()
    {
        worldId = "";
        worldIdOwner = "";
    }

    internal static void RejoinAllowed()
    {
        worldReceived = false;
        ClearJoinPending();
    }

    private static void HandleTravel(string text)
    {
        string scene = text.Substring(TravelPrefix.Length);
        if (scene.Length == 0 || scene.Length > MaxSceneLength)
        {
            CoopLog.Log.Info("coop: TRAVEL ignored, the scene name is " + scene.Length + " characters");
            return;
        }
        GuestWorld.OnTravel(scene);
    }

    private static void HandleLoaded(ulong from, string text)
    {
        string[] parts = text.Split('|');
        string scene = parts.Length > 2 ? parts[2] : "";
        CoopLog.Log.Info("coop: guest " + DisplayName(from) + " reports the world loaded (" + text + ")");
        if (scene.Length == 0 || scene.Length > MaxSceneLength)
        {
            CoopLog.Log.Info("coop: the LOAD from " + DisplayName(from) + " names no usable scene, no travel is sent");
            return;
        }
        HostTravel.GuestLoaded(from, scene);
    }

    private static void ReleaseInput()
    {
        try
        {
            Time.timeScale = 1f;
            GameController controller = GameController.instance;
            if (controller != null)
            {
                controller.GameIsPaused = false;
                controller.ChangeCursorStatus(CursorLockMode.None, true);
            }
            else
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("Session.ReleaseInput", e);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    private static void ReleaseOnMenu()
    {
        if (!releasePending || MainMenu.Instance == null) return;
        releasePending = false;
        ReleaseInput();
        CoopLog.Log.Info("coop: back on the main menu, cursor released: " + notice);
    }

    internal static void ReturnToMenu(string text, bool force)
    {
        notice = text;
        releasePending = true;
        ReleaseInput();
        if (!force)
        {
            if (GameRefs.LocalPlayer != null && PauseMenu.instance != null)
            {
                CoopLog.Log.Info("returning to the main menu: " + text);
                PauseMenu.instance.QuitMainMenu();
            }
            return;
        }
        menuPending = true;
        if (GuestWorld.LoadRoutineActive) GuestWorld.CancelLoad();
        CoopLog.Log.Info("coop: the return to the main menu is deferred, waiting for the menu path: " + text);
    }

    private static void MenuTick()
    {
        if (!menuPending) return;
        if (GameRefs.LocalPlayer != null && PauseMenu.instance != null)
        {
            menuPending = false;
            CoopLog.Log.Info("returning to the main menu: " + notice);
            PauseMenu.instance.QuitMainMenu();
            return;
        }
        if (GuestWorld.LoadRoutineActive) return;
        if (SaveController.Loading) return;
        if (MainMenu.Instance != null)
        {
            menuPending = false;
            CoopLog.Log.Info("coop: the deferred return to the main menu is not needed, the main menu is up");
            return;
        }
        LoadingScreen screen = LoadingScreen.instance;
        if (screen == null) return;
        menuPending = false;
        PlayerIdentity identity = UnityEngine.Object.FindFirstObjectByType<PlayerIdentity>();
        if (identity != null) UnityEngine.Object.Destroy(identity.gameObject);
        if (Achievements.instance != null) Achievements.instance.ResetValues();
        SaveController.LastSaveFolderPath = "";
        CoopLog.Log.Info("coop: main menu fallback load, PlayerIdentity and achievements reset, LastSaveFolderPath cleared");
        screen.LoadLevel("Main Menu", false, false);
    }

    private static void AutoJoinTick()
    {
        if (hostReload) return;
        if (!invited || !IsGuest) return;
        if (worldReceived || GuestSession.InHostWorld)
        {
            invited = false;
            return;
        }
        if (MainMenu.Instance == null)
        {
            invited = false;
            AutoJoinNotice("You are in a world. Return to the main menu to join the host.");
            return;
        }
        if (!HostInWorld)
        {
            if (Time.unscaledTime - invitedAt < AutoJoinTimeout) return;
            invited = false;
            notice = "The host is not in a world yet. Open the co-op menu to join.";
            CoopLog.Log.Info("coop: the auto join gave up after " + AutoJoinTimeout + " s, the host is not in a world");
            return;
        }
        if (Time.unscaledTime - lastAutoJoinAt < AutoJoinRetry) return;
        lastAutoJoinAt = Time.unscaledTime;
        string block = JoinBlock();
        if (block.Length > 0)
        {
            invited = false;
            notice = block;
            CoopLog.Log.Info("coop: the auto join is blocked: " + block);
            return;
        }
        if (JoinWorld()) invited = false;
    }

    private static void AutoJoinNotice(string text)
    {
        notice = text;
        if (Notifications.instance != null) CoopNotice.Show("autojoin|inworld", text, 5f);
        CoopLog.Log.Info("coop: the auto join is not possible: " + text);
    }
}
