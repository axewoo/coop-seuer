using System;
using System.Globalization;
using UnityEngine;

internal static class GuestSession
{
    public enum Phase { Idle, Placing, Loading, Loaded, Failed }

    public const string AutosaveSlot = "Autosave";
    public const string AutosaveSlot2 = "Autosave2";
    public const string GlobalsFile = "Globals.tnmt";

    private static ulong hostId;
    private static string worldId = "";
    private static string scene = "";
    private static string relativeName = "";
    private static string folder = "";
    private static Phase phase = Phase.Idle;
    private static float deadline;
    private static bool guard;
    private static bool following;
    private static string followScene = "";
    private static float followDeadline;
    private static float phaseAt;
    private static float followAt;

    public static bool Active
    {
        get { return phase == Phase.Placing || phase == Phase.Loading || phase == Phase.Loaded; }
    }

    public static bool Guard
    {
        get { return guard; }
    }

    public static bool InHostWorld
    {
        get { return Active || guard; }
    }

    public static Phase Current
    {
        get { return phase; }
    }

    public static bool Loaded
    {
        get { return phase == Phase.Loaded; }
    }
    public static bool Following
    {
        get { return following; }
    }

    public static bool FollowPending
    {
        get { return following && phase == Phase.Loaded; }
    }

    public static string FollowScene
    {
        get { return followScene; }
    }

    public static bool FollowExpired
    {
        get { return following && Time.realtimeSinceStartup > followDeadline; }
    }

    public static ulong HostId
    {
        get { return hostId; }
    }

    public static string WorldId
    {
        get { return worldId; }
    }

    public static string Scene
    {
        get { return scene; }
    }

    public static string RelativeName
    {
        get { return relativeName; }
    }

    public static string Folder
    {
        get { return folder; }
    }

    public static float Deadline
    {
        get { return deadline; }
    }

    internal static void Open(ulong host, string id, string sceneName, string worldFolder)
    {
        hostId = host;
        worldId = id;
        scene = sceneName;
        folder = worldFolder;
        relativeName = "../Coop/" + host + "_" + id;
        phase = Phase.Placing;
        phaseAt = Time.realtimeSinceStartup;
        deadline = 0f;
        guard = true;
        if (CoopDebug.On("sess")) CoopDebug.Event("sess", "phase", "guest phase Placing for world " + id + " from " + host + ", scene " + sceneName + ", folder " + worldFolder);
    }

    internal static void MarkLoading(float timeout)
    {
        phase = Phase.Loading;
        deadline = Time.realtimeSinceStartup + timeout;
        if (CoopDebug.On("sess")) CoopDebug.Event("sess", "phase", "guest phase Loading after " + Secs(Time.realtimeSinceStartup - phaseAt) + " in Placing, deadline " + Secs(timeout));
    }

    internal static void MarkLoaded()
    {
        float loading = Time.realtimeSinceStartup - phaseAt;
        bool followed = following;
        float followedFor = followAt <= 0f ? 0f : Time.realtimeSinceStartup - followAt;
        phase = Phase.Loaded;
        deadline = 0f;
        ClearFollowing();
        string area = GameRefs.AreaId;
        if (CoopDebug.On("sess")) CoopDebug.Event("sess", "phase", "guest phase Loaded after " + Secs(loading) + " of loading"
            + (followed ? ", " + Secs(followedFor) + " since the travel" : "") + ", area " + (area == null ? "none" : area) + ", scene " + scene);
    }

    internal static void MarkFollowing(string sceneName, float timeout)
    {
        following = true;
        followScene = sceneName == null ? "" : sceneName;
        followDeadline = Time.realtimeSinceStartup + timeout;
        followAt = Time.realtimeSinceStartup;
        if (CoopDebug.On("sess")) CoopDebug.Event("sess", "travel", "guest travel wait started for " + followScene + ", the follow ticket has " + Secs(timeout));
    }

    internal static void MarkReload(string sceneName, float timeout)
    {
        scene = sceneName;
        phase = Phase.Loading;
        deadline = Time.realtimeSinceStartup + timeout;
        if (CoopDebug.On("sess")) CoopDebug.Event("sess", "follow", "guest follow load started for " + (sceneName == null ? "no scene" : sceneName)
            + (followAt > 0f ? " after " + Secs(Time.realtimeSinceStartup - followAt) + " of the travel wait" : "") + ", deadline " + Secs(timeout));
    }

    internal static void ClearFollowing()
    {
        following = false;
        followScene = "";
        followDeadline = 0f;
        followAt = 0f;
    }

    private static string Secs(float value)
    {
        return value.ToString("0.0", CultureInfo.InvariantCulture) + " s";
    }

    internal static void Abort(string reason)
    {
        if (phase == Phase.Idle) return;
        CoopLog.Log.Error("coop: guest load aborted: " + reason);
        phase = Phase.Failed;
        deadline = 0f;
        ClearFollowing();
    }

    internal static void End()
    {
        phase = Phase.Idle;
        deadline = 0f;
        ClearFollowing();
        if (guard) return;
        Clear();
    }

    internal static bool ReleaseGuard()
    {
        if (!guard) return false;
        guard = false;
        Clear();
        return true;
    }

    private static void Clear()
    {
        hostId = 0;
        worldId = "";
        scene = "";
        relativeName = "";
        folder = "";
        deadline = 0f;
        following = false;
        followScene = "";
        followDeadline = 0f;
    }
}
