using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using ObenseuerCore;
using UnityEngine;

internal static partial class FreeRoam
{
    private const string Channel = "world";
    private const string BigAreaPrefix = "Open Sewer ";
    private const float GotoWait = 100f;
    private const float FollowTimeout = 120f;
    private const float HoldWait = 10f;
    private const float VoteTime = 30f;
    private const float CheckpointInterval = 30f;
    private const int MaxScene = 80;
    private static string gotoScene;
    private static float gotoUntil;
    private static string voteGo;
    private static string voteGoEntry;
    private static float voteGoUntil;
    public static int VoteChoice;

    public static string VoteScene
    {
        get { return voteScene; }
    }

    private static string voteScene;
    private static string voteEntry;
    private static float voteUntil;
    private static float nextCheckpoint;
    private static string waitScene;
    private static string waitEntry;
    private static float waitUntil;
    private static bool waitDone;
    private static bool bypass;
    public static bool Ready;

    public static void Install()
    {
        NetBlob.Received += OnBlob;
        try
        {
            Harmony harmony = new Harmony(Plugin.PluginGuid + ".freeroam");
            harmony.Patch(AccessTools.Method(typeof(SaveController), "LoadGameWithMigration", new[] { typeof(string), typeof(string), typeof(bool) }), prefix: new HarmonyMethod(typeof(FreeRoam), "LoadPrefix"));
            Ready = true;
            CoopLog.Log.Info("coop: free roaming hooks installed");
        }
        catch (Exception e)
        {
            CoopLog.Once("FreeRoam.Install", e);
        }
    }

    public static void Reset()
    {
        gotoScene = null;
        voteScene = null;
        voteGo = null;
        VoteChoice = 0;
        waitScene = null;
        bypass = false;
        trips.Clear();
        areas.Clear();
        arrivals.Clear();
        storedAt.Clear();
        holders.Clear();
        heardAt.Clear();
        holdGen.Clear();
        staleHoldLogged.Clear();
        lapseLogged.Clear();
        hostHere = null;
        hostLeft = null;
        ckptIn.Clear();
        ckptSeq.Clear();
        storedNote.Clear();
        holdSeed.Clear();
        holdOrder.Clear();
        storeAtQuit = null;
        doorAt = -1f;
        shipAt = -1f;
        doorFlushed = false;
        TripReset();
        told.Clear();
        nextTell = 0f;
        HandoffReset();
    }

    private const float FlushWait = 1.5f;
    private static string doorScene;
    private static string doorEntry;
    private static float doorAt = -1f;
    private static float shipAt = -1f;
    private static bool doorFlushed;

    private static int FlushFollowers()
    {
        string here = GameRefs.AreaId;
        bool handoff = xferMineScene != null && string.Equals(xferMineScene, here, StringComparison.Ordinal);
        if (Session.IsHost || here == null || (!IAmAuthority && !handoff)) return 0;
        System.Collections.Generic.List<ulong> peers = Session.PeersInArea(here);
        for (int i = 0; i < peers.Count; i++) Session.Send(peers[i], Channel, "CFLUSH|" + here, true);
        if (peers.Count > 0) CoopLog.Log.Info("coop: asked " + peers.Count + " player(s) in " + here + " to close their containers before the handover");
        return peers.Count;
    }

    public static bool GuestDoor(string scene, string entry)
    {
        if (!GuestSession.Loaded || GuestSession.Following)
        {
            CoopNotice.Show("roam|busy", "Still travelling, try the door again in a moment", 4f);
            CoopLog.Log.Info("coop: guest travel to " + scene + " waits: " + (GuestSession.Loaded ? "a trip is still running" : "the world is still loading"));
            return false;
        }
        if (!doorFlushed && doorAt >= 0f)
        {
            CoopNotice.Show("roam|flush", "Letting the others close their containers...", 2f);
            return false;
        }
        if (!doorFlushed && doorAt < 0f && FlushFollowers() > 0)
        {
            doorScene = scene;
            doorEntry = entry;
            doorAt = Time.realtimeSinceStartup + FlushWait;
            return false;
        }
        if (!Plain(scene))
        {
            CoopLog.Log.Info("coop: guest travel to an unusable scene name ignored");
            return false;
        }
        ShipMine("leaving for " + scene);
        GuestSession.MarkFollowing(scene, FollowTimeout);
        GuestProfile.FlushNow("travelling to " + scene);
        Puppet.Suppress(Session.HostId, true);
        gotoScene = scene;
        gotoUntil = Time.realtimeSinceStartup + GotoWait;
        string text = "GOTO|" + scene + "|" + (string.IsNullOrEmpty(entry) ? "" : entry);
        if (!Session.Send(Session.HostId, Channel, text, true)) GotoFailed("the request could not be sent to the host");
        else
        {
            CoopNotice.Show("roam|going", "Travelling to " + scene + "...", 6f);
            CoopLog.Log.Info("coop: guest travel to " + scene + " (" + entry + ") asked of the host");
        }
        return false;
    }

    private static void GotoFailed(string reason)
    {
        GiveUpGoto();
        CoopNotice.Show("roam|fail", "Could not travel: " + reason, 6f);
        CoopLog.Log.Info("coop: guest travel failed: " + reason);
    }

    internal static void GiveUpGoto()
    {
        if (gotoScene == null) return;
        GuestSession.ClearFollowing();
        Puppet.Suppress(Session.HostId, false);
        gotoScene = null;
    }

    private static void ShipMine(string why)
    {
        try
        {
            string here = GameRefs.AreaId;
            if (!Session.IsGuest || here == null || !GuestSession.Loaded || GuestSession.FollowPending) return;
            bool handoff = xferMineScene != null && string.Equals(xferMineScene, here, StringComparison.Ordinal);
            if (!handoff && Session.AreaOf(Session.HostId) == here) return;
            if (!IAmAuthority && !handoff)
            {
                if (CoopDebug.On("auth")) CoopDebug.Event("auth", "ship skipped " + here, here + " is not held by this machine (" + why + ")");
                return;
            }
            DateTime before = DateTime.UtcNow.AddSeconds(-1);
            SaveController.SaveGame(GuestSession.AutosaveSlot, "", "NONE");
            string path = Application.persistentDataPath + "/Saves/" + GuestSession.RelativeName + "/" + GuestSession.AutosaveSlot + "/" + here + WorldShip.SceneSuffix;
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < before)
            {
                CoopLog.Log.Info("coop: no fresh copy of " + here + " to ship (" + why + ")");
                return;
            }
            byte[] data = File.ReadAllBytes(path);
            ShipCkpt(here);
            int id = NetBlob.Send(Session.HostId, Plugin.PluginGuid, BlobName(here), data);
            CoopLog.Log.Info("coop: shipped " + here + " (" + data.Length + " bytes) to the host, " + why + (id < 0 ? ", the send was refused" : ""));
        }
        catch (Exception e)
        {
            CoopLog.Once("FreeRoam.ShipMine", e);
        }
    }

    private static void GuestTick(float now)
    {
        if (gotoScene != null && !GuestSession.FollowPending) gotoScene = null;
        if (gotoScene != null && now > gotoUntil) GotoFailed("the host did not answer");
        if (voteScene != null)
        {
            if (now > voteUntil)
            {
                VoteChoice = 0;
                CoopNotice.Show("roam|vote", "You stayed.", 3f);
                voteScene = null;
            }
            else if (Input.GetKeyDown(KeyCode.F7) || VoteChoice == 1)
            {
                VoteChoice = 0;
                voteGo = voteScene;
                voteGoEntry = voteEntry;
                voteGoUntil = now + 20f;
                voteScene = null;
                CoopLog.Log.Info("coop: travel vote: going to " + voteGo);
            }
            else if (Input.GetKeyDown(KeyCode.F8) || VoteChoice == 2)
            {
                VoteChoice = 0;
                voteScene = null;
                CoopLog.Log.Info("coop: travel vote: staying");
                CoopNotice.Show("roam|vote", "You stayed.", 3f);
            }
        }
        if (voteGo != null)
        {
            if (gotoScene == voteGo)
            {
                voteGo = null;
            }
            else if (now > voteGoUntil)
            {
                CoopLog.Log.Info("coop: travel vote: could not start the trip to " + voteGo);
                CoopNotice.Show("roam|vote", "Could not follow the host, use the door yourself.", 5f);
                voteGo = null;
            }
            else if (GuestSession.Loaded && !GuestSession.Following && doorAt < 0f)
            {
                GuestDoor(voteGo, voteGoEntry);
                if (gotoScene == voteGo) voteGo = null;
            }
        }
        if (doorAt >= 0f && now >= doorAt)
        {
            doorAt = -1f;
            doorFlushed = true;
            try { GuestDoor(doorScene, doorEntry); }
            finally { doorFlushed = false; }
        }
        if (shipAt >= 0f && now >= shipAt)
        {
            shipAt = -1f;
            string handoffScene = xferMineScene;
            if (handoffScene == null) ShipMine("the host or a newcomer is coming in");
            else
            {
                int handoffGen = xferMineGen;
                if (string.Equals(handoffScene, GameRefs.AreaId, StringComparison.Ordinal)) ShipMine("the planned handoff of " + handoffScene);
                AnswerXfer(handoffScene, handoffGen);
            }
        }
        if (now >= nextCheckpoint)
        {
            nextCheckpoint = now + CheckpointInterval;
            ShipMine("checkpoint");
        }
    }

    private sealed class Trip
    {
        public ulong Guest;
        public string Scene;
        public string Entry;
        public float At;
        public bool Asked;
        public float AskedAt;
    }

    private static readonly System.Collections.Generic.Dictionary<string, float> storedAt = new System.Collections.Generic.Dictionary<string, float>();

    private static readonly System.Collections.Generic.List<Trip> trips = new System.Collections.Generic.List<Trip>();

    private static void HostGoto(ulong from, string scene, string entry)
    {
        TripSaveReset();
        trips.RemoveAll(t => t.Guest == from);
        if (trips.Count >= 8)
        {
            Session.Send(from, Channel, "GOERR|the host is busy with other travels", true);
            return;
        }
        trips.Add(new Trip { Guest = from, Scene = scene, Entry = entry, At = Time.unscaledTime });
        CoopLog.Log.Info("coop: guest " + from + " asks to travel to " + scene);
    }

    private static void ShipTrips()
    {
        TripWatch();
        if (trips.Count == 0 || WorldShip.Busy) return;
        Trip t = trips[0];
        if ((SaveData.Busy || GameRefs.AreaId == null) && Time.unscaledTime - t.At < 60f) return;
        if (!ProfileStore.StoredAfter(t.Guest, t.At) && Time.unscaledTime - t.At < 10f) return;
        ulong holder = HolderOf(t.Scene);
        if (holder != 0UL && holder != t.Guest && !string.Equals(t.Scene, GameRefs.AreaId, StringComparison.Ordinal))
        {
            float stored;
            storedAt.TryGetValue(t.Scene, out stored);
            if (!t.Asked)
            {
                t.Asked = true;
                t.AskedAt = Time.unscaledTime;
                Session.Send(holder, Channel, "SCENEREQ|" + t.Scene, true);
                CoopLog.Log.Info("coop: " + t.Scene + " is held by guest " + holder + ", asking for its live copy for " + t.Guest);
                return;
            }
            if (stored < t.AskedAt && Time.unscaledTime - t.AskedAt < HoldWait) return;
        }
        if (TripSaveWait(t)) return;
        trips.RemoveAt(0);
        if (!Session.Peers.Contains(t.Guest)) return;
        string here = GameRefs.AreaId;
        bool ok = string.Equals(t.Scene, here, StringComparison.Ordinal) ? WorldShip.SendFollow(t.Guest) : WorldShip.SendGoto(t.Guest, t.Scene, t.Entry);
        if (!ok && WorldShip.Deferred) { trips.Insert(0, t); return; }
        if (!ok) Session.Send(t.Guest, Channel, "GOERR|" + Cut(WorldShip.Refusal), true);
        else TripStarted(t.Guest, t.Scene);
        CoopLog.Log.Info("coop: guest " + t.Guest + " travels to " + t.Scene + (ok ? ", ticket started" : ", refused: " + WorldShip.Refusal));
    }

    public static bool HostChangeLevel(string scene, string entry, bool solo, bool door)
    {
        if (bypass)
        {
            bypass = false;
            return true;
        }
        string here = GameRefs.AreaId;
        if (waitScene != null && !door)
        {
            CoopLog.Log.Info("coop: the held travel to " + waitScene + " is dropped for a forced travel to " + scene);
            waitScene = null;
        }
        if (waitScene != null)
        {
            GuestSaveHooks.ReleaseDoorLock();
            CoopNotice.Show("roam|hold", "Still getting " + waitScene + " from the guest there...", 4f);
            return false;
        }
        if (!solo && here != null && scene.StartsWith(BigAreaPrefix, StringComparison.Ordinal)) Vote(scene, entry, Session.PeersInArea(here));
        if (door && Session.PeersInArea(scene).Count > 0)
        {
            ulong holder = HolderOf(scene) != 0UL ? HolderOf(scene) : Session.PeersInArea(scene)[0];
            waitScene = scene;
            waitEntry = entry;
            waitDone = false;
            waitUntil = Time.realtimeSinceStartup + HoldWait;
            Session.Send(holder, Channel, "SCENEREQ|" + scene, true);
            GuestSaveHooks.ReleaseDoorLock();
            CoopNotice.Show("roam|hold", "Getting " + scene + " from the guest there...", 5f);
            CoopLog.Log.Info("coop: host travel to " + scene + " waits for the live copy from guest " + holder);
            return false;
        }
        return true;
    }

    public static void Gather()
    {
        string here = GameRefs.AreaId;
        if (!Session.IsHost || here == null) return;
        System.Collections.Generic.List<ulong> away = new System.Collections.Generic.List<ulong>();
        foreach (ulong peer in Session.Peers)
        {
            if (Session.AreaOf(peer) != here) away.Add(peer);
        }
        Vote(here, "", away);
        CoopNotice.Show("roam|gather", away.Count == 0 ? "Everyone is already here" : "Asked " + away.Count + " player(s) to come to you", 4f);
    }

    private static void Vote(string scene, string entry, System.Collections.Generic.List<ulong> peers)
    {
        for (int i = 0; i < peers.Count; i++) Session.Send(peers[i], Channel, "VOTE|" + scene + "|" + (entry ?? ""), true);
        if (peers.Count > 0) CoopLog.Log.Info("coop: travel vote to " + scene + " sent to " + peers.Count + " player(s)");
    }

    private static void HostTick()
    {
        TrackAreas();
        TrackArrivals();
        TellHolders();
        ShipTrips();
        if (waitScene == null) return;
        if (!waitDone && Time.realtimeSinceStartup < waitUntil) return;
        string scene = waitScene;
        string entry = waitEntry;
        waitScene = null;
        CoopLog.Log.Info("coop: host travel to " + scene + (waitDone ? " with the guest's live copy" : ", the guest did not answer, using the stored copy"));
        bypass = true;
        SaveController.ChangeLevel(scene, string.IsNullOrEmpty(entry) ? "NONE" : entry);
    }

    public static bool HandleWorld(ulong from, string text)
    {
        string[] f = text.Split('|');
        if (f[0] == "GOTO" && f.Length == 3)
        {
            if (Session.IsHost && Session.Peers.Contains(from) && Plain(f[1]) && EntryOk(f[2])) HostGoto(from, f[1], f[2]);
            return true;
        }
        if (f[0] == "CFLUSH" && f.Length == 2)
        {
            if (!Session.IsHost && from == Authority && f[1] == GameRefs.AreaId)
            {
                ContainerGuest.CloseRunning();
                LiquidSync.CloseRunning();
            }
            return true;
        }
        if (f[0] == "CKPT" && f.Length == 4)
        {
            if (Session.IsHost) HostCkpt(from, f);
            return true;
        }
        if (f[0] == "XFERACK" && f.Length == 3)
        {
            if (Session.IsHost && Plain(f[1])) HostXferAck(from, f);
            return true;
        }
        if (Session.IsHost || from != Session.HostId) return f[0] == "GOERR" || f[0] == "SCENEREQ" || f[0] == "VOTE" || f[0] == "HOLD" || f[0] == "HOLDXFER" || f[0] == "XFERACK";
        if (f[0] == "GOERR" && f.Length >= 2)
        {
            if (Solo.HomePending) CoopLog.Log.Info("death: GOERR received");
            if (gotoScene != null) GotoFailed(f[1]);
            return true;
        }
        if (f[0] == "HOLD" && (f.Length == 3 || f.Length == 4))
        {
            HandleHold(f);
            return true;
        }
        if (f[0] == "HOLDXFER" && f.Length == 4)
        {
            HandleHoldXfer(f);
            return true;
        }
        if (f[0] == "SCENEREQ" && f.Length == 2)
        {
            if (f[1] == GameRefs.AreaId)
            {
                if (FlushFollowers() > 0) shipAt = Time.realtimeSinceStartup + FlushWait;
                else ShipMine("the host or a newcomer is coming in");
            }
            return true;
        }
        if (f[0] == "VOTE" && f.Length == 3 && Plain(f[1]) && EntryOk(f[2]))
        {
            if (f[1] == GameRefs.AreaId) return true;
            voteScene = f[1];
            voteEntry = f[2];
            voteUntil = Time.realtimeSinceStartup + VoteTime;
            CoopNotice.Show("roam|vote", "The host invites you to " + f[1] + ". F7 go, F8 stay (or Esc for buttons, " + (int)VoteTime + " s)", VoteTime);
            CoopLog.Log.Info("coop: travel vote received for " + f[1]);
            return true;
        }
        return false;
    }

    public static float BecameAuthorityAt = -1000f;
    private static string watchScene;
    private static ulong watchAuthority;

    public static void WatchAuthority()
    {
        string here = GameRefs.AreaId;
        ulong now = Session.IsInLobby && here != null ? Authority : 0UL;
        if (here == watchScene && now != watchAuthority && watchAuthority != 0UL && now != 0UL)
        {
            CoopLog.Log.Info("coop: the authority of " + here + " moved from " + watchAuthority + " to " + now + " (gen " + Num(Gen(here)) + ")");
            if (CoopDebug.On("auth")) CoopDebug.Event("auth", "authority " + here, here + " moved from " + watchAuthority + " to " + now + ", gen " + Num(Gen(here)));
            if (now == Session.Me)
            {
                BecameAuthorityAt = Time.unscaledTime;
                ItemGuest.Adopt();
                InteractableSync.AuthorityTaken();
            }
            else ItemGuest.Reset();
            BuildGuest.Reset();
            ContainerGuest.CloseRunning();
            ContainerGuest.Reset();
            LiquidSync.AuthorityChanged();
        }
        if (here != watchScene || now != 0UL) watchAuthority = now;
        watchScene = here;
    }

    public static void Tick()
    {
        try
        {
            if (Session.IsHost)
            {
                try { TickXfers(); }
                catch (Exception e) { CoopLog.Once("FreeRoam.TickXfers", e); }
            }
            TickQuit(Time.realtimeSinceStartup);
            WatchAuthority();
            float now = Time.realtimeSinceStartup;
            if (Session.IsHost)
            {
                HostTick();
                if (Input.GetKeyDown(KeyCode.F6)) Gather();
            }
            else if (Session.IsGuest && GuestSession.Active) GuestTick(now);
        }
        catch (Exception e)
        {
            CoopLog.Once("FreeRoam.Tick", e);
        }
    }

    private static readonly Regex EntryChars = new Regex("^[A-Za-z0-9 _\\-]{0,80}$");

    private static bool EntryOk(string entry)
    {
        return entry != null && EntryChars.IsMatch(entry);
    }

    public static bool TicketMatches(WorldShip.WorldTicket ticket)
    {
        if (gotoScene == null || ticket == null || ticket.Manifest == null) return gotoScene == null;
        return string.Equals(ticket.Manifest.hostScene, gotoScene, StringComparison.Ordinal);
    }

    private static bool Plain(string scene)
    {
        return !string.IsNullOrEmpty(scene) && scene.Length <= MaxScene && scene.IndexOfAny(new[] { '/', '\\', '|', '"', ':', '.' }) < 0;
    }

    private static string Cut(string s)
    {
        if (string.IsNullOrEmpty(s)) return "busy";
        s = s.Replace('|', ' ').Replace('\n', ' ');
        return s.Length > 100 ? s.Substring(0, 100) : s;
    }
}
