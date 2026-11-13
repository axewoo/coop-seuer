using System;
using System.IO;

internal static class ProfileMirror
{
    public static void Choose(WorldShip.WorldTicket ticket, out byte[] profile, out bool fromMirror)
    {
        fromMirror = false;
        byte[] carried = null;
        if (ticket.Files.ContainsKey(WorldShip.ProfileFile)) carried = ticket.Files[WorldShip.ProfileFile];
        string mirrorPath = Path.Combine(GuestSession.Folder, WorldShip.ProfileFile);
        string full;
        byte[] mirror = GuestWorld.PathOk(mirrorPath, out full) ? Read(full) : null;
        if (mirror == null)
        {
            profile = carried;
            return;
        }
        string why;
        if (!GuestSplice.IsProfile(mirror, out why))
        {
            CoopLog.Log.Error("coop: the mirror profile is not usable, the ticket copy is kept: " + why);
            profile = carried;
            return;
        }
        if (carried == null || Newer(mirror, carried))
        {
            profile = mirror;
            fromMirror = true;
            return;
        }
        profile = carried;
    }

    public static bool Newer(byte[] candidate, byte[] carried)
    {
        DateTimeOffset mine;
        DateTimeOffset theirs;
        if (!ProfileDate.TryRead(candidate, out mine)) return false;
        if (!ProfileDate.TryRead(carried, out theirs)) return false;
        return mine.UtcDateTime > theirs.UtcDateTime;
    }

    private static byte[] Read(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
