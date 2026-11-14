using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ObenseuerCore;
using UnityEngine;

internal static partial class GuestWorld
{
    private const float LoadTimeout = 60f;
    private const float SpawnOffset = 1.5f;
    private const float GroundProbeUp = 2f;
    private const float GroundProbeDown = 6f;
    private const int MaxHostName = 32;
    private const string CoopFolderName = "Coop";
    private const string SavesSegment = "Saves";
    private const string FailureNotice = "Could not load the host's world";
    private const string HostLeftNotice = "Host left: return to the main menu";
    private const string FollowingNotice = "Host is travelling";
    private const string FollowFailedNotice = "Could not follow the host";
    private const float TravelTimeout = 90f;
    private const float SpawnDelay = 0.3f;

    private static readonly string[] ReservedScenes = new string[] { "Globals", "Info", "Globals.dialogue" };
    private static readonly string[] TutorialScenes = new string[] { "Open Sewer Intro", "Interior Start" };

    private static readonly MethodInfo GameDeserialize = typeof(SaveController).GetMethod(
        "Deserialize",
        BindingFlags.Public | BindingFlags.Static,
        null,
        new Type[] { typeof(string), typeof(Type), typeof(string), typeof(Exception).MakeByRefType() },
        null);
    private static readonly Type HeaderType = typeof(SaveController).GetNestedType("SaveDataHeader", BindingFlags.NonPublic | BindingFlags.Public);

    private static bool installed;
    private static bool fadeSeen;
    private static bool spawnDue;
    private static bool dryRunPassed;
    private static bool hostLeftNoticed;
    private static bool loadRoutineActive;
    private static bool loadAborted;
    private static bool followLoad;
    private static float spawnAt;
    private static string hostName = "";
    private static float hostX;
    private static float hostY;
    private static float hostZ;
    private static float hostYaw;

    public static void Init()
    {
        if (installed) return;
        installed = true;
        SaveController.LoadingDone += OnLoadingDone;
        dryRunPassed = DryRun();
    }

    public static bool Begin(WorldShip.WorldTicket ticket)
    {
        loadRoutineActive = false;
        loadAborted = false;
        followLoad = false;
        string error;
        if (!Validate(ticket, false, out error))
        {
            CoopLog.Log.Error("coop: ticket rejected: " + error);
            Session.SetNotice(FailureNotice);
            return false;
        }
        ulong host = Session.HostId;
        string world = Path.Combine(CoopRoot(), host + "_" + ticket.Manifest.worldId);
        string full;
        if (!PathOk(world, out full))
        {
            CoopLog.Log.Error("coop: the coop folder is not under the coop root: " + world);
            Session.SetNotice(FailureNotice);
            return false;
        }
        hostName = HostLabel(ticket.Manifest.hostName);
        hostX = ticket.Manifest.hostX;
        hostY = ticket.Manifest.hostY;
        hostZ = ticket.Manifest.hostZ;
        hostYaw = ticket.Manifest.hostYaw;
        GuestSession.Open(host, ticket.Manifest.worldId, ticket.Manifest.hostScene, full);
        CoopLog.Log.Info("coop: guest session opened for world " + GuestSession.WorldId + " from " + host + " at " + full);
        if (!Place(ticket, out error))
        {
            WipeSlots();
            GuestSession.Abort(error);
            GuestSession.End();
            Session.SetNotice(FailureNotice);
            return false;
        }
        fadeSeen = false;
        spawnDue = false;
        spawnAt = 0f;
        hostLeftNoticed = false;
        GuestSession.MarkLoading(LoadTimeout);
        if (Plugin.Instance == null)
        {
            GuestSession.Abort("the plugin is not running");
            GuestSession.End();
            Session.SetNotice(FailureNotice);
            return false;
        }
        loadRoutineActive = true;
        Plugin.Instance.StartCoroutine(LoadRoutine(null));
        return true;
    }

    public static void OnTravel(string sceneName)
    {
        if (!Session.IsGuest || !GuestSession.Active)
        {
            CoopLog.Log.Info("coop: TRAVEL ignored, the guest is not in the host's world");
            return;
        }
        if (GuestSession.Current != GuestSession.Phase.Loaded)
        {
            CoopLog.Log.Info("coop: TRAVEL ignored, the guest is not settled in the world");
            return;
        }
        if (!IsPlainName(sceneName) || IsReservedScene(sceneName))
        {
            CoopLog.Log.Error("coop: TRAVEL ignored, bad scene name '" + sceneName + "'");
            return;
        }
        GuestSession.MarkFollowing(sceneName, TravelTimeout);
        GuestProfile.FlushNow("the host is travelling");
        Puppet.Suppress(Session.HostId, true);
        ShowNotice("host-travel", FollowingNotice);
        CoopLog.Log.Info("coop: the host is travelling to " + sceneName + ", waiting for the follow ticket");
    }

    public static void BeginFollow(WorldShip.WorldTicket ticket)
    {
        loadRoutineActive = false;
        loadAborted = false;
        followLoad = true;
        string error;
        if (!Validate(ticket, true, out error))
        {
            FailFollow(error);
            return;
        }
        if (!string.Equals(ticket.Manifest.worldId, GuestSession.WorldId, StringComparison.Ordinal))
        {
            FailFollow("the follow ticket world id " + ticket.Manifest.worldId + " is not the session world id " + GuestSession.WorldId);
            return;
        }
        hostName = HostLabel(ticket.Manifest.hostName);
        hostX = ticket.Manifest.hostX;
        hostY = ticket.Manifest.hostY;
        hostZ = ticket.Manifest.hostZ;
        hostYaw = ticket.Manifest.hostYaw;
        fadeSeen = false;
        spawnDue = false;
        spawnAt = 0f;
        hostLeftNoticed = false;
        GuestSession.MarkReload(ticket.Manifest.hostScene, LoadTimeout);
        CoopLog.Log.Info("coop: following the host into " + ticket.Manifest.hostScene);
        if (Plugin.Instance == null)
        {
            FailFollow("the plugin is not running");
            return;
        }
        loadRoutineActive = true;
        Plugin.Instance.StartCoroutine(LoadRoutine(ticket));
    }

    private static void FailFollow(string reason)
    {
        loadAborted = true;
        followLoad = false;
        GuestSession.Abort(reason);
        EndFollow(reason);
    }

    private static void EndFollow(string reason)
    {
        GuestSession.End();
        Puppet.Suppress(Session.HostId, false);
        CoopLog.Log.Error("coop: the guest could not follow the host: " + reason);
        Session.RejoinAllowed();
        Session.ReturnToMenu(FollowFailedNotice, true);
    }

    private static void ShowNotice(string key, string text)
    {
        if (Notifications.instance == null)
        {
            Session.SetNotice(text);
            return;
        }
        CoopNotice.Show(key, text, 5f);
    }

    public static void Tick()
    {
        try
        {
            WatchLoad();
            if (GuestSession.FollowPending && GuestSession.FollowExpired)
            {
                FailFollow("the follow ticket did not arrive in time");
                return;
            }
            if (ReleaseIfHome()) return;
            if (!GuestSession.Active)
            {
                if (GuestSession.Current != GuestSession.Phase.Idle) End("the guest session is closed");
                return;
            }
            if (!Session.IsInLobby || !Session.IsGuest)
            {
                Session.DebugLobbyGone("GuestWorld.Tick");
                if (GuestSession.Guard)
                {
                    NoticeHostLeft();
                    return;
                }
                End("the guest left the lobby");
                return;
            }
            if (spawnDue && Time.realtimeSinceStartup >= spawnAt)
            {
                spawnDue = false;
                Spawn();
            }
            if (GuestSession.Current == GuestSession.Phase.Loaded && GameRefs.LocalPlayer == null)
            {
                End("the guest left the world");
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("GuestWorld.Tick", e);
        }
    }

    private static void WatchLoad()
    {
        if (GuestSession.Current != GuestSession.Phase.Loading) return;
        LoadingScreen screen = LoadingScreen.instance;
        if (screen != null && screen.FadeActive) fadeSeen = true;
        else if (fadeSeen)
        {
            Abort("the load did not finish");
            return;
        }
        if (Time.realtimeSinceStartup > GuestSession.Deadline) Abort("the load did not finish in time");
    }

    private static bool ReleaseIfHome()
    {
        if (!GuestSession.Guard) return false;
        if (loadRoutineActive) return false;
        GuestSession.Phase phase = GuestSession.Current;
        if (phase == GuestSession.Phase.Placing || phase == GuestSession.Phase.Loading) return false;
        if (SaveController.Loading) return false;
        if (MainMenu.Instance == null) return false;
        bool cleared = DropLastSaveFolder();
        Puppet.Suppress(Session.HostId, false);
        GuestSession.ReleaseGuard();
        CoopLog.Log.Info("coop: guest write guard released, LastSaveFolderPath " + (cleared ? "cleared" : "left alone"));
        End("the guest is back on the main menu");
        GuestSaveHooks.RefreshCharacterName();
        return true;
    }

    private static bool DropLastSaveFolder()
    {
        string path = SaveController.LastSaveFolderPath;
        if (string.IsNullOrEmpty(path)) return false;
        string full;
        if (!PathOk(path, out full)) return false;
        SaveController.LastSaveFolderPath = "";
        return true;
    }

    private static void NoticeHostLeft()
    {
        if (hostLeftNoticed) return;
        hostLeftNoticed = true;
        GuestProfile.OnLeave();
        if (!CoopAlert.Active) Session.SetNotice(HostLeftNotice);
        CoopLog.Log.Info("coop: the lobby is gone but the guest is still in the host's world, the write guard stays");
    }

    private static IEnumerator LoadRoutine(WorldShip.WorldTicket ticket)
    {
        try
        {
            LoadingScreen screen = LoadingScreen.instance;
            if (screen != null && !screen.FadeActive)
            {
                screen.Fade(true, true, 0f, true, GuestSession.Scene);
                yield return null;
                if (loadAborted) yield break;
                while (screen != null && screen.Fading && !loadAborted) yield return new WaitForSecondsRealtime(0.001f);
                yield return null;
                if (loadAborted) yield break;
            }
            if (loadAborted) yield break;
            if (ticket != null)
            {
                if (!SaveData.Busy) GuestProfile.MirrorNow();
                string placeError;
                if (!Place(ticket, out placeError))
                {
                    FailFollow(placeError);
                    yield break;
                }
            }
            CoopLog.Log.Info("coop: guest load starting from " + GuestSession.RelativeName + "/" + GuestSession.AutosaveSlot);
            yield return SaveController.LoadGameWithMigration(GuestSession.RelativeName, GuestSession.AutosaveSlot, true);
        }
        finally
        {
            loadRoutineActive = false;
        }
    }

    private static void OnLoadingDone()
    {
        if (GuestSession.Current != GuestSession.Phase.Loading) return;
        bool follow = followLoad;
        followLoad = false;
        GuestSession.MarkLoaded();
        QuestGuest.Resync();
        spawnDue = true;
        spawnAt = Time.realtimeSinceStartup + SpawnDelay;
        Puppet.Suppress(Session.HostId, false);
        Session.SetNotice(follow ? "Followed " + hostName + " to " + GuestSession.Scene : "Joined " + hostName + "'s world");
        CoopLog.Log.Info("coop: guest load done, " + (follow ? "followed " + hostName + " into " : "joined " + hostName + " in ") + GuestSession.Scene);
        string loaded = GameRefs.AreaId;
        if (!string.Equals(loaded, GuestSession.Scene, StringComparison.Ordinal)) loaded = "";
        Session.Send(Session.HostId, "world", "LOAD|1|" + loaded, true);
    }

    internal static string HookBlock()
    {
        string missing = "";
        if (!BuildHooks.Ready) missing = With(missing, "build");
        if (!ItemHooks.Ready) missing = With(missing, "item");
        if (!ContainerHooks.Ready) missing = With(missing, "container");
        if (!CraftHooks.Ready) missing = With(missing, "craft");
        if (!UnsharedHooks.Ready) missing = With(missing, "unshared");
        if (!ShopHooks.Ready) missing = With(missing, "shop");
        if (!HostTravel.Ready) missing = With(missing, "host travel");
        if (!ProfileHooks.Ready) missing = With(missing, "profile");
        if (!QuestHooks.Ready) missing = With(missing, "quest");
        if (!VendHooks.Ready) missing = With(missing, "vending");
        return missing.Length == 0 ? "" : "The " + missing + " hooks are not installed.";
    }

    internal static bool TutorialScene(string scene)
    {
        return Array.IndexOf(TutorialScenes, scene) >= 0;
    }

    private static string With(string list, string name)
    {
        return list.Length == 0 ? name : list + ", " + name;
    }

    private static bool Validate(WorldShip.WorldTicket ticket, bool follow, out string error)
    {
        error = null;
        string hooks = HookBlock();
        if (hooks.Length > 0)
        {
            error = hooks;
            return false;
        }
        if (!GuestSaveHooks.Ready)
        {
            error = "the guest save hooks are not installed";
            return false;
        }
        if (!GuestGateHooks.Ready)
        {
            error = "the guest gate hooks are not installed";
            return false;
        }
        if (!dryRunPassed)
        {
            error = "the coop folder dry run failed at startup";
            return false;
        }
        if (ticket == null || ticket.Manifest == null)
        {
            error = "the ticket is empty";
            return false;
        }
        WorldShip.TicketManifest manifest = ticket.Manifest;
        if (manifest.format != WorldShip.Format)
        {
            error = "the ticket format is " + manifest.format;
            return false;
        }
        if (!IsWorldId(manifest.worldId))
        {
            error = "the ticket has no world id";
            return false;
        }
        if (manifest.mode != WorldShip.ResumeMode && manifest.mode != WorldShip.FreshMode)
        {
            error = "the ticket mode " + (manifest.mode == null ? "is missing" : manifest.mode) + ", not fresh or resume";
            return false;
        }
        if (manifest.mode == WorldShip.ResumeMode && !Has(ticket, WorldShip.ProfileFile))
        {
            error = "the ticket says resume but carries no profile";
            return false;
        }
        if (manifest.mode == WorldShip.FreshMode && Has(ticket, WorldShip.ProfileFile))
        {
            error = "the ticket says fresh but carries a profile";
            return false;
        }
        if (!IsPlainName(manifest.hostScene))
        {
            error = "the ticket scene name is not a plain name";
            return false;
        }
        if (IsReservedScene(manifest.hostScene))
        {
            error = "the ticket scene name " + manifest.hostScene + " would overwrite a coop save file";
            return false;
        }
        if (manifest.gameBuild != Core.GameBuild)
        {
            error = "the host runs game build " + manifest.gameBuild + ", this machine runs " + Core.GameBuild;
            return false;
        }
        if (!Session.IsGuest)
        {
            error = "this machine is not a guest in the host's lobby";
            return false;
        }
        if (follow)
        {
            if (!GuestSession.Following)
            {
                error = "the ticket does not follow a TRAVEL from the host";
                return false;
            }
            if (!GuestSession.Loaded)
            {
                error = "the guest is not in the host's world yet";
                return false;
            }
            if (MainMenu.Instance != null || GameRefs.LocalPlayer == null)
            {
                error = "the follow load can only start in the host's world";
                return false;
            }
        }
        else if (MainMenu.Instance == null || GameRefs.LocalPlayer != null)
        {
            error = "the load can only start from the main menu";
            return false;
        }
        string sceneFile = manifest.hostScene + WorldShip.SceneSuffix;
        if (!Has(ticket, WorldShip.GlobalsFile) || !Has(ticket, WorldShip.DialogueFile))
        {
            error = "the ticket is missing Globals.tnmt or Globals.dialogue";
            return false;
        }
        if (!follow && !Has(ticket, sceneFile))
        {
            error = "the ticket is missing " + sceneFile;
            return false;
        }
        return true;
    }

    public static void CancelLoad()
    {
        loadAborted = true;
        followLoad = false;
    }

    private static void Abort(string reason)
    {
        if (followLoad)
        {
            FailFollow(reason);
            return;
        }
        loadAborted = true;
        followLoad = false;
        GuestSession.Abort(reason);
        Session.RejoinAllowed();
        Session.SetNotice(FailureNotice);
        GuestSession.End();
    }

    private static void End(string reason)
    {
        CoopLog.Log.Info("coop: guest session ended: " + reason);
        spawnDue = false;
        Session.RejoinAllowed();
        Puppet.Suppress(Session.HostId, false);
        GuestSession.End();
    }
}
