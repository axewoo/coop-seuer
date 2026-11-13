using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
#if BEPINEX6
using BepInEx.Unity.Mono;
#endif
using ObenseuerCore;
using UnityEngine;

[BepInPlugin(PluginGuid, "Obenseuer Co-op", Version)]
#if BEPINEX5
[BepInDependency("obenseuer.moddingframework", "0.9.0")]
#else
[BepInDependency("obenseuer.moddingframework", ">=0.9.0")]
#endif
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "obenseuer.coop";
    public const string Version = "0.12.2";
    public static Plugin Instance;
    private const float MovingDistance = 0.001f;
    private const float MirrorInterval = 0.1f;
    private const float MirrorLookInterval = 1f;
    private const float MirrorDistance = 2f;
    private const ulong MirrorId = 0;
    private static readonly List<ulong> shown = new List<ulong>();

    private ConfigEntry<bool> syncLooks;
    private ConfigEntry<bool> mirrorPuppet;
    private ConfigEntry<float> clockSnapMinutes;
    private ConfigEntry<float> clockSmoothSeconds;
    private ModKeybind hostKey;
    private ModKeybind leaveKey;
    private Vector3 lastPos;
    private float nextMirror;
    private float nextMirrorLook;
    private bool wasInLobby;
    private bool wasInLobbyItems;

    private void Awake()
    {
        Instance = this;
        syncLooks = Config.Bind("Coop", "SendLook", true, "Sends your look to the other players. A look received from another player is always applied to their puppet. The old SyncLooks key is no longer read.");
        mirrorPuppet = Config.Bind("General", "DebugMirrorPuppet", false, "Solo test: spawns a puppet of the local player 2 m in front. Its look follows SyncLooks.");
        clockSnapMinutes = Config.Bind("General", "ClockSnapMinutes", 5f, "Guest clock: a drift from the host clock above this many game minutes snaps at once. Smaller drift is glided.");
        clockSmoothSeconds = Config.Bind("General", "ClockSmoothSeconds", 2f, "Guest clock: time constant, in real seconds, for gliding toward the host clock.");
        hostKey = Keybinds.Register(PluginGuid, "host", "Host co-op lobby", KeyCode.F8);
        leaveKey = Keybinds.Register(PluginGuid, "leave", "Leave co-op lobby", KeyCode.F7);
        AppearanceSync.Enabled = syncLooks.Value;
        WorldSync.SnapMinutes = clockSnapMinutes.Value;
        WorldSync.SmoothSeconds = clockSmoothSeconds.Value;
        WorldHooks.Install();
        NewGameAdopt.Install();
        BuildHost.Init();
        BuildGuest.Init();
        BuildHooks.Install();
        ItemHost.Init();
        ItemGuest.Init();
        ItemHooks.Install();
        Session.Init();
        CoopChat.Install(Config);
        CoopDebug.Install(Config);
        MainMenuButton.Init();
        SessionHooks.Install();
        WorldShip.Init();
        GuestProfile.Init();
        ProfileStore.Init();
        ProfileHooks.Install();
        GuestSaveHooks.Install();
        GuestGateHooks.Install();
        if (!DeveloperConsoleHooks.Install()) CoopLog.Log.Error("the developer console guard did not install, a guest can still run console commands");
        if (!MenuLoadHooks.Install()) CoopLog.Log.Error("the menu load guard did not install, a guest can still load a save from the menu");
        DialogueProbe.Install();
        GuestWorld.Init();
        HostTravel.Install();
        FreeRoam.Install();
        Solo.Install();
        LookPin.Install();
        CoopMap.Install();
        if (!PrisonLocal.Install()) CoopLog.Log.Error("the prison hooks did not install, per player prison cells are off");
        DoorSync.Install();
        DynamiteSync.Install();
        PowerSync.Init();
        try { LiquidSync.Install(new HarmonyLib.Harmony(PluginGuid + ".liquid")); } catch (Exception e) { CoopLog.Once("LiquidSync.Install", e); }
        if (!InteractableSync.Install()) CoopLog.Log.Error("the interactable hooks did not install, interactable sync is off");
        if (!InteractableStateHooks.Install()) CoopLog.Log.Error("the phase 3a interactable hooks did not install, radio, television and shower sharing is off");
        if (!InteractableGuard.Install()) CoopLog.Log.Error("the interact guard did not install, unsupported interactables can still be used by a guest");
        CombatSync.Install();
        Harvest.Install();
        AnimalSync.Install();
        ContainerHost.Init();
        ContainerGuest.Init();
        TrainHost.Init();
        TrainGuest.Init();
        TrainHooks.Install();
        ElevatorHooks.Install();
        ContainerHooks.Install();
        TalkHost.Init();
        TalkGuest.Init();
        TalkHooks.Install();
        ShopHost.Init();
        ShopGuest.Init();
        ShopHooks.Install();
        CraftHost.Init();
        CraftGuest.Init();
        CraftHooks.Install();
        UnsharedHooks.Install();
        Shared.Init();
        SlotHost.Init();
        SlotGuest.Init();
        if (!SlotHooks.Install()) CoopLog.Log.Error("the slot machine hooks did not install, slot machine sync is off");
        SaunaHost.Init();
        SaunaGuest.Init();
        if (!SaunaHooks.Install()) CoopLog.Log.Error("the sauna hooks did not install, sauna sync is off");
        QuestHost.Init();
        QuestGuest.Init();
        QuestHooks.Install();
        VendHost.Init();
        VendGuest.Init();
        VendHooks.Install();
        if (!TenementRentHooks.Install()) CoopLog.Log.Error("the tenement rent guard did not install, a guest can be paid rent on top of the host");
        if (!TimerScale.Install(Config)) CoopLog.Log.Error("the timer scale hooks did not install, timed gameplay runs at real time");
        if (Config.Bind("Debug", "WorldShipSelfCheck", false, "Runs the join ticket pack and SHA-256 self check at start and logs the result.").Value) WorldShip.SelfCheck();
        if (Config.Bind("Debug", "GuestSpliceSelfCheck", false, "Runs the guest Globals splice self check at start and logs the result.").Value) GuestSplice.SelfCheck();
        if (Config.Bind("Debug", "DialogueProbeSelfCheck", false, "Resolves the DialogueLua probe targets at start and logs the result.").Value) CoopLog.Log.Info("coop: " + DialogueProbe.SelfCheck());
        if (Config.Bind("Debug", "TalkSelfCheck", false, "Resolves the guest talk hook targets at start and logs the result.").Value) CoopLog.Log.Info("coop: " + TalkHooks.SelfCheck());
        if (Config.Bind("Debug", "GuestGateSelfCheck", false, "Resolves every M8 guest gate hook target at start and logs the result.").Value) GuestGateHooks.SelfCheck();
        if (Config.Bind("Debug", "ContainerSelfCheck", false, "Resolves every M7 container hook target at start and logs the result.").Value) CoopLog.Log.Info("coop: " + ContainerHooks.SelfCheck());
        if (Config.Bind("Debug", "ShopSelfCheck", false, "Resolves every shop hook target at start and logs the result.").Value) CoopLog.Log.Info("coop: " + ShopHooks.SelfCheck());
        if (Config.Bind("Debug", "CraftSelfCheck", false, "Resolves every M7 station craft hook target at start and logs the result.").Value) CoopLog.Log.Info("coop: " + CraftHooks.SelfCheck());
        if (Config.Bind("Debug", "TrainSelfCheck", false, "Resolves every mine train hook target at start and logs the result.").Value) CoopLog.Log.Info("coop: " + TrainHooks.SelfCheck());
        if (Config.Bind("Debug", "ElevatorSelfCheck", false, "Resolves every elevator hook target at start and logs the result.").Value) CoopLog.Log.Info("coop: " + ElevatorHooks.SelfCheck());
        if (Config.Bind("Debug", "UnsharedSelfCheck", false, "Resolves the vending machine and liquid storage hook targets at start and logs the result.").Value) CoopLog.Log.Info("coop: " + UnsharedHooks.SelfCheck());
        if (Config.Bind("Debug", "SlotSelfCheck", false, "Resolves every slot machine hook target at start and logs the result.").Value) CoopLog.Log.Info("coop: " + SlotHooks.SelfCheck());
        if (Config.Bind("Debug", "SaunaSelfCheck", false, "Resolves every sauna hook target at start and logs the result.").Value) CoopLog.Log.Info("coop: " + SaunaHooks.SelfCheck());
        if (Config.Bind("Debug", "QuestSelfCheck", false, "Resolves the task controller and quest blob targets at start and logs the result.").Value) CoopLog.Log.Info("coop: " + QuestHooks.SelfCheck());
        if (Config.Bind("Debug", "VendSelfCheck", false, "Resolves the vending machine and its scene id lookups at start and logs the result.").Value) CoopLog.Log.Info("coop: " + VendHooks.SelfCheck());
        if (Config.Bind("Debug", "PrisonSelfCheck", false, "Resolves the prison cell hook targets and the private prison fields at start and logs the result.").Value) CoopLog.Log.Info("coop: " + PrisonLocal.SelfCheck());
        if (Config.Bind("Debug", "ProfileStoreSelfCheck", false, "Runs the profile date parse and the profile store newest candidate self checks at start and logs the result.").Value)
        {
            ProfileDate.SelfCheck();
            ProfileStore.SelfCheck();
        }
        if (!Session.Register("look", AppearanceSync.Handle)) CoopLog.Log.Error("channel look is owned by another mod, look sync is off");
        if (!Session.Register("world", OnWorld)) CoopLog.Log.Error("channel world is owned by another mod, clock, weather and session sync is off");
        if (!Session.Register(PoseSync.Channel, PoseSync.Handle)) CoopLog.Log.Error("channel " + PoseSync.Channel + " is owned by another mod, pose sync is off");
        if (!Session.Register(SitSync.Channel, SitSync.Handle)) CoopLog.Log.Error("channel " + SitSync.Channel + " is owned by another mod, seat sync is off");
        if (!Session.Register(BuildWire.Channel, OnBuild)) CoopLog.Log.Error("channel " + BuildWire.Channel + " is owned by another mod, base sync is off");
        if (!Session.Register(ContainerWire.Channel, OnContainer)) CoopLog.Log.Error("channel " + ContainerWire.Channel + " is owned by another mod, container sharing is off");
        if (!Session.Register(GuestProfile.Channel, GuestProfile.OnChannel)) CoopLog.Log.Error("channel " + GuestProfile.Channel + " is owned by another mod, guest profiles are off");
        if (!Session.Register(ItemWire.Channel, OnItem)) CoopLog.Log.Error("channel " + ItemWire.Channel + " is owned by another mod, floor item sync is off");
        if (!Session.Register(TrainWire.Channel, OnTrain)) CoopLog.Log.Error("channel " + TrainWire.Channel + " is owned by another mod, train sync is off");
        if (ElevatorHooks.Ready && !Session.Register(ElevatorWire.Channel, OnElevator)) CoopLog.Log.Error("channel " + ElevatorWire.Channel + " is owned by another mod, elevator sync is off");
        if (!Session.Register(TalkWire.Channel, OnTalk)) CoopLog.Log.Error("channel " + TalkWire.Channel + " is owned by another mod, guest talks are off");
        if (!Session.Register(SlotWire.Channel, OnSlot)) CoopLog.Log.Error("channel " + SlotWire.Channel + " is owned by another mod, slot machine sync is off");
        if (!Session.Register(SaunaWire.Channel, OnSauna)) CoopLog.Log.Error("channel " + SaunaWire.Channel + " is owned by another mod, sauna sync is off");
        if (!Session.Register(ShopWire.Channel, OnShop)) CoopLog.Log.Error("channel " + ShopWire.Channel + " is owned by another mod, shop sharing is off");
        if (!Session.Register(QuestWire.Channel, OnQuest)) CoopLog.Log.Error("channel " + QuestWire.Channel + " is owned by another mod, quest sharing is off");
        if (!Session.Register(VendWire.Channel, OnVend)) CoopLog.Log.Error("channel " + VendWire.Channel + " is owned by another mod, vending sharing is off");
        if (!Session.Register(NpcSync.Channel, NpcSync.Handle)) CoopLog.Log.Error("channel " + NpcSync.Channel + " is owned by another mod, NPC sync is off");
        if (!Session.Register(AnimalSync.Channel, AnimalSync.Handle)) CoopLog.Log.Error("channel " + AnimalSync.Channel + " is owned by another mod, animal sync is off");
        if (!Session.Register(Harvest.Channel, Harvest.Handle)) CoopLog.Log.Error("channel " + Harvest.Channel + " is owned by another mod, harvest sync is off");
        if (!Session.Register(CombatSync.Channel, CombatSync.Handle)) CoopLog.Log.Error("channel " + CombatSync.Channel + " is owned by another mod, hit sync is off");
        if (!Session.Register(CoopUi.Channel, CoopUi.Handle)) CoopLog.Log.Error("channel " + CoopUi.Channel + " is owned by another mod, the mod check is off");
        if (!Session.Register(CoopChat.Channel, CoopChat.Handle)) CoopLog.Log.Error("channel " + CoopChat.Channel + " is owned by another mod, the text chat is off");
        if (!Session.Register(PowerSync.Channel, PowerSync.Handle)) CoopLog.Log.Error("channel power is owned by another mod, live power sync is off");
        if (!Session.Register(DynamiteSync.Channel, DynamiteSync.Handle)) CoopLog.Log.Error("channel " + DynamiteSync.Channel + " is owned by another mod, dynamite sync is off");
        if (!Session.Register(LiquidSync.Channel, LiquidSync.Handle)) CoopLog.Log.Error("channel " + LiquidSync.Channel + " is owned by another mod, liquid sync is off");
        if (!Session.Register(DoorSync.Channel, DoorSync.Handle)) CoopLog.Log.Error("channel " + DoorSync.Channel + " is owned by another mod, door and light sync is off");
        if (!Session.Register(InteractableSync.Channel, InteractableSync.Handle)) CoopLog.Log.Error("channel " + InteractableSync.Channel + " is owned by another mod, interactable sync is off");
        if (!Session.Register(CraftWire.Channel, OnCraft)) CoopLog.Log.Error("channel " + CraftWire.Channel + " is owned by another mod, station crafting is off");
        if (!Session.Register(TimerScale.Channel, TimerScale.Handle)) CoopLog.Log.Error("channel " + TimerScale.Channel + " is owned by another mod, timer scaling is off");
        Events.SceneLoaded += scene =>
        {
            PowerSync.OnSceneChanged();
            DoorSync.OnSceneChanged();
            Puppet.RemoveAll();
            LookPin.OnSceneLoaded();
            BuildGuest.OnSceneLoaded();
            ContainerHost.OnSceneChanged();
            ContainerGuest.OnSceneChanged();
            TrainHost.OnSceneChanged();
            TrainGuest.OnSceneChanged();
            ElevatorHost.OnSceneChanged();
            ElevatorGuest.OnSceneChanged();
            ShopHost.OnSceneChanged();
            ShopGuest.OnSceneChanged();
            CraftHost.OnSceneChanged();
            CraftGuest.OnSceneChanged();
            ItemGuest.OnSceneLoaded();
            SlotRound.Reset();
            SlotGuest.OnSceneChanged();
            SlotHost.Reset();
            SaunaHost.OnSceneChanged();
            SaunaGuest.OnSceneChanged();
            BedVote.OnSceneChanged();
            DialogueProbe.Reset();
            TimeSpeed.OnSceneChanged();
            TalkHost.OnSceneChanged();
            TalkGuest.OnSceneChanged();
            PrisonLocal.OnSceneChanged();
            ItemHost.ForgetDrops();
        };
        PlayerLook.Changed += AppearanceSync.OnChanged;
        CoopLog.Log.Info("Obenseuer Co-op " + Version + " started on " + Core.GameBuild);
    }

    private void OnGUI()
    {
        CoopUi.Draw();
    }

    private void Update()
    {
        MainMenuButton.Tick();
        WatchOverlay();
        TickPopup();
        try
        {
            LapStart();
            if (Pressed(hostKey)) HostKey();
            Lap("keys");
            if (Pressed(leaveKey)) LeaveKey();
            Lap("keys");
            Session.Tick();
            Lap("Session.Tick");
            CoopDebug.Tick();
            Lap("CoopDebug.Tick");
            Shared.Tick();
            Lap("Shared.Tick");
            Publish();
            Lap("Publish");
            PoseSync.Tick();
            Lap("PoseSync.Tick");
            NewGameAdopt.Tick();
            Lap("NewGameAdopt.Tick");
            WorldShip.Tick();
            Lap("WorldShip.Tick");
            GuestWorld.Tick();
            Lap("GuestWorld.Tick");
            HostTravel.Tick();
            Lap("HostTravel.Tick");
            GuestProfile.Tick();
            Lap("GuestProfile.Tick");
            WorldSync.Tick();
            Lap("WorldSync.Tick");
            TickBuild();
            Lap("TickBuild");
            FreeRoam.WatchAuthority();
            Lap("FreeRoam.WatchAuthority");
            PowerSync.Tick();
            Lap("PowerSync.Tick");
            DoorSync.Tick();
            Lap("DoorSync.Tick");
            TickItems();
            Lap("TickItems");
            SlotGuest.Tick();
            Lap("SlotGuest.Tick");
            SaunaHost.Tick();
            Lap("SaunaHost.Tick");
            SaunaGuest.Tick();
            Lap("SaunaGuest.Tick");
            QuestHost.Tick();
            Lap("QuestHost.Tick");
            QuestGuest.Tick();
            Lap("QuestGuest.Tick");
            VendHost.Tick();
            Lap("VendHost.Tick");
            VendGuest.Tick();
            Lap("VendGuest.Tick");
            FreeRoam.Tick();
            Lap("FreeRoam.Tick");
            Solo.Tick();
            Lap("Solo.Tick");
            CoopUi.Tick();
            Lap("CoopUi.Tick");
            CoopChat.Tick();
            Lap("CoopChat.Tick");
            TimerScale.Tick(Time.unscaledTime);
            Lap("TimerScale.Tick");
            NpcSync.Tick();
            Lap("NpcSync.Tick");
            LiquidSync.Tick();
            Lap("LiquidSync.Tick");
            InteractableSync.Tick();
            Lap("InteractableSync.Tick");
            InteractableSync.ManifestTick();
            Lap("InteractableSync.ManifestTick");
            AnimalSync.Tick();
            Lap("AnimalSync.Tick");
            LookPin.Tick();
            Lap("LookPin.Tick");
            AppearanceSync.Tick();
            Lap("AppearanceSync.Tick");
            if (mirrorPuppet.Value) Mirror();
            Lap("Mirror");
            DrivePuppets();
            Lap("DrivePuppets");
            CoopMap.Tick();
            Lap("CoopMap.Tick");
            Puppet.Tick(Time.deltaTime);
            Lap("Puppet.Tick");
        }
        catch (Exception e)
        {
            CoopLog.Once("Plugin.Update", e);
        }
    }

    // Frame-stall diagnostics: names the co-op tick that took longer than SlowTickMs.
    private const double SlowTickMs = 250.0;
    private static readonly System.Diagnostics.Stopwatch lapWatch = new System.Diagnostics.Stopwatch();

    private static void LapStart()
    {
        lapWatch.Reset();
        lapWatch.Start();
    }

    private static void Lap(string name)
    {
        double ms = lapWatch.Elapsed.TotalMilliseconds;
        if (ms > SlowTickMs) CoopLog.Log.Warn("coop: slow tick " + name + " took " + ms.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms");
        lapWatch.Reset();
        lapWatch.Start();
    }

    private static bool Pressed(ModKeybind key)
    {
        return key != null && key.Down();
    }

    private static void WatchOverlay()
    {
        try
        {
            LobbyPage.WatchOverlay();
        }
        catch (Exception e)
        {
            CoopLog.Once("Plugin.overlay", e);
        }
    }

    private static void TickPopup()
    {
        try
        {
            CoopPopup.Tick();
        }
        catch (Exception e)
        {
            CoopLog.Once("Plugin.popup", e);
        }
    }

    private static void HostKey()
    {
        if (LobbyKeys.Busy())
        {
            CoopLog.Log.Info("host key ignored: the game is busy");
            return;
        }
        if (Session.RefuseHosting())
        {
            LobbyPage.PushNow();
            return;
        }
        Session.Host();
        LobbyPage.PushNow();
    }

    private static void LeaveKey()
    {
        if (LobbyKeys.Busy())
        {
            CoopLog.Log.Info("leave key ignored: the game is busy");
            return;
        }
        Session.Leave();
        LobbyPage.PushNow();
    }

    private void TickBuild()
    {
        bool inLobby = Session.IsInLobby;
        if (wasInLobby && !inLobby)
        {
            BuildHost.Reset();
            BuildGuest.Reset();
            ContainerHost.Reset();
            ContainerGuest.Reset();
            TrainHost.Reset();
            TrainGuest.Reset();
            ElevatorHost.Reset();
            ElevatorGuest.Reset();
            CraftHost.Reset();
            CraftGuest.Reset();
            Shared.Reset();
            TalkHost.Reset();
            TalkGuest.Reset();
            SlotRound.Reset();
            SlotHost.Reset();
            SlotGuest.Reset();
            SaunaHost.Reset();
            SaunaGuest.Reset();
            ShopHost.Reset();
            ShopGuest.Reset();
            QuestHost.Reset();
            QuestGuest.Reset();
            QuestStart.Reset();
            CoopPopup.EndLobby(Session.LeftDeliberately);
            VendHost.Reset();
            VendGuest.Reset();
            FreeRoam.Reset();
            Solo.Reset();
            PrisonLocal.Reset();
            CoopUi.Reset();
            CoopChat.Reset();
            CoopMap.Reset();
            DoorSync.Reset();
            PowerSync.Reset();
            NpcSync.Reset();
            LiquidSync.Reset();
            InteractableSync.Reset();
            ItemHost.ForgetDrops();
            CombatSync.Reset();
            AnimalSync.Reset();
            BedVote.Reset();
            LocalDrops.Reset();
            TimeSpeed.Reset();
            TimerScale.Reset();
        }
        wasInLobby = inLobby;
        BuildHost.Tick();
        BuildGuest.Tick();
        ContainerHost.Tick();
        ContainerGuest.Tick();
        PrisonLocal.Tick();
        TrainHost.Tick();
        TrainGuest.Tick();
        if (ElevatorHooks.Ready)
        {
            ElevatorHost.Tick();
            ElevatorGuest.Tick();
        }
        TalkHost.Tick();
        TalkGuest.Tick();
        ShopHost.Tick();
        ShopGuest.Tick();
        CraftHost.Tick();
        CraftGuest.Tick();
    }

    private static void OnWorld(ulong from, string text)
    {
        Session.HandleWorld(from, text);
        WorldSync.Handle(from, text);
    }

    private static void OnBuild(ulong from, string text)
    {
        BuildMsg msg;
        if (!BuildWire.TryParse(text, out msg))
        {
            CoopLog.Once("Plugin.build", "dropped an invalid build message from " + from);
            return;
        }
        if (FreeRoam.IAmAuthority) BuildHost.Handle(from, msg);
        else if (from == FreeRoam.Authority) BuildGuest.Handle(from, msg);
    }

    private static void OnContainer(ulong from, string text)
    {
        ContainerMsg msg;
        if (!ContainerWire.TryParse(text, out msg))
        {
            CoopLog.Once("Plugin.cont", "dropped an invalid container message from " + from);
            return;
        }
        if (FreeRoam.IAmAuthority) ContainerHost.Handle(from, msg);
        else if (from == FreeRoam.Authority) ContainerGuest.Handle(from, msg);
    }

    private static void OnTalk(ulong from, string text)
    {
        TalkMsg msg;
        if (!TalkWire.TryParse(text, out msg))
        {
            CoopLog.Once("Plugin.talk", "dropped an invalid talk message from " + from);
            return;
        }
        if (Session.IsHost) TalkHost.Handle(from, msg);
        else TalkGuest.Handle(from, msg);
    }

    private static void OnShop(ulong from, string text)
    {
        ShopMsg msg;
        if (!ShopWire.TryParse(text, out msg))
        {
            CoopLog.Once("Plugin.shop", "dropped an invalid shop message from " + from);
            return;
        }
        if (Session.IsHost) ShopHost.Handle(from, msg);
        else if (from == Session.HostId) ShopGuest.Handle(from, msg);
    }

    private static void OnCraft(ulong from, string text)
    {
        CraftMsg msg;
        if (!CraftWire.TryParse(text, out msg))
        {
            CoopLog.Once("Plugin.craft", "dropped an invalid craft message from " + from);
            return;
        }
        if (Session.IsHost) CraftHost.Handle(from, msg);
        else if (from == Session.HostId) CraftGuest.Handle(from, msg);
    }

    private void TickItems()
    {
        bool inLobby = Session.IsInLobby;
        if (wasInLobbyItems && !inLobby)
        {
            ItemHost.Reset();
            ItemGuest.Reset();
        }
        wasInLobbyItems = inLobby;
        ItemHost.Tick();
        ItemGuest.Tick();
    }

    private static void OnVend(ulong from, string text)
    {
        VendMsg msg;
        if (!VendWire.TryParse(text, out msg))
        {
            CoopLog.Once("Plugin.vend", "dropped an invalid vending message from " + from);
            return;
        }
        if (Session.IsHost) VendHost.Handle(from, msg);
        else if (from == Session.HostId) VendGuest.Handle(from, msg);
    }

    private static void OnQuest(ulong from, string text)
    {
        QuestMsg msg;
        if (!QuestWire.TryParse(text, out msg))
        {
            CoopLog.Once("Plugin.quest", "dropped an invalid quest message from " + from);
            return;
        }
        if (Session.IsHost) QuestHost.Handle(from, msg);
        else if (from == Session.HostId) QuestGuest.Handle(from, msg);
    }

    private static void OnItem(ulong from, string text)
    {
        ItemMsg msg;
        if (!ItemWire.TryParse(text, out msg))
        {
            CoopLog.Once("Plugin.item", "dropped an invalid item message from " + from);
            return;
        }
        if (FreeRoam.IAmAuthority) ItemHost.Handle(from, msg);
        else if (from == FreeRoam.Authority) ItemGuest.Handle(from, msg);
    }

    private static void OnTrain(ulong from, string text)
    {
        TrainMsg msg;
        if (!TrainWire.TryParse(text, out msg))
        {
            CoopLog.Once("Plugin.train", "dropped an invalid train message from " + from);
            return;
        }
        if (Session.IsHost) TrainHost.Handle(from, msg);
        else if (from == Session.HostId) TrainGuest.Handle(from, msg);
    }

    private static void OnElevator(ulong from, string text)
    {
        ElevMsg msg;
        if (!ElevatorWire.TryParse(text, out msg))
        {
            CoopLog.Once("Plugin.elevator", "dropped an invalid elevator message from " + from);
            return;
        }
        if (FreeRoam.IAmAuthority) ElevatorHost.Handle(from, msg);
        else if (from == FreeRoam.Authority) ElevatorGuest.Handle(from, msg);
    }

    private static void OnSlot(ulong from, string text)
    {
        SlotMsg msg;
        if (!SlotWire.TryParse(text, out msg))
        {
            CoopLog.Once("Plugin.slot", "dropped an invalid slot message from " + from);
            return;
        }
        if (Session.IsHost) SlotHost.Handle(from, msg);
        else if (from == Session.HostId) SlotGuest.Handle(from, msg);
    }

    private static void OnSauna(ulong from, string text)
    {
        SaunaMsg msg;
        if (!SaunaWire.TryParse(text, out msg))
        {
            CoopLog.Once("Plugin.sauna", "dropped an invalid sauna message from " + from);
            return;
        }
        if (Session.IsHost) SaunaHost.Handle(from, msg);
        else if (from == Session.HostId) SaunaGuest.Handle(from, msg);
    }

    private void Publish()
    {
        Transform body = GameRefs.LocalPlayer;
        string area = GameRefs.AreaId;
        if (body == null || area == null) return;
        Vector3 pos = body.position;
        Vector3 step = pos - lastPos;
        step.y = 0f;
        lastPos = pos;
        Session.Publish(area, pos, body.eulerAngles.y, step.magnitude > MovingDistance);
    }

    private static void DrivePuppets()
    {
        if (!Session.IsInLobby)
        {
            if (shown.Count == 0) return;
            shown.Clear();
            Puppet.ForgetAll();
            return;
        }
        string area = GameRefs.AreaId;
        List<ulong> live = new List<ulong>();
        foreach (PresenceRecord record in Session.Remotes())
        {
            live.Add(record.Peer);
            Puppet.Target(record.Peer, record.Name, record.Position, record.Yaw);
            Puppet.SetVisible(record.Peer, record.Area == area);
        }
        foreach (ulong id in shown)
        {
            if (!live.Contains(id)) Puppet.Remove(id);
        }
        shown.Clear();
        shown.AddRange(live);
    }

    private void Mirror()
    {
        float now = Time.unscaledTime;
        if (now < nextMirror) return;
        nextMirror = now + MirrorInterval;
        Transform body = GameRefs.LocalPlayer;
        if (body == null)
        {
            Puppet.Remove(MirrorId);
            return;
        }
        float yaw = body.eulerAngles.y;
        Vector3 pos = body.position + Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * MirrorDistance;
        Puppet.Target(MirrorId, "Mirror", pos, yaw + 180f);
        Puppet.SetVisible(MirrorId, true);
        if (!syncLooks.Value || now < nextMirrorLook) return;
        nextMirrorLook = now + MirrorLookInterval;
        string look = AppearanceSync.Local();
        if (look != null) Puppet.SetLook(MirrorId, look);
    }
}

internal static class CoopLog
{
    public static readonly CoreLog Log = CoreLog.For(Plugin.PluginGuid);
    private static readonly HashSet<string> seen = new HashSet<string>();

    public static void Once(string site, string message)
    {
        if (seen.Add(site)) Log.Error(site + ": " + message);
    }

    public static void Once(string site, Exception e)
    {
        Once(site, e.ToString());
    }
}
