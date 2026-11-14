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
    private static bool Place(WorldShip.WorldTicket ticket, out string error)
    {
        error = null;
        string world = GuestSession.Folder;
        string slot = Path.Combine(world, GuestSession.AutosaveSlot);
        string slot2 = Path.Combine(world, GuestSession.AutosaveSlot2);
        string full;
        if (!PathOk(world, out full) || !PathOk(slot, out full) || !PathOk(slot2, out full))
        {
            error = "the coop folder is not under the coop root";
            return false;
        }
        if (!Wipe(slot) || !Wipe(slot2))
        {
            error = "could not clear " + slot;
            return false;
        }
        byte[] profile;
        bool fromMirror;
        ProfileMirror.Choose(ticket, out profile, out fromMirror);
        byte[] globals;
        int hostCount;
        int removedCount;
        int profileCount;
        if (!GuestSplice.Splice(ticket.Files[WorldShip.GlobalsFile], GuestSplice.RemoveSet(), profile, out globals, out error, out hostCount, out removedCount, out profileCount)) return false;
        int expected = hostCount - removedCount + profileCount;
        if (profile == null) CoopLog.Log.Info("coop: the ticket carries no profile, the guest starts a fresh character");
        else if (fromMirror) CoopLog.Log.Info("coop: the guest's mirror of " + profile.Length + " bytes (" + profileCount + " entries) is newer than the ticket copy, so it is spliced and sent back");
        else CoopLog.Log.Info("coop: the ticket carries a " + profile.Length + " byte profile of " + profileCount + " entries");
        try
        {
            Directory.CreateDirectory(slot);
        }
        catch (Exception e)
        {
            error = "could not create " + slot + ": " + e.Message;
            return false;
        }
        string written = Path.Combine(slot, WorldShip.GlobalsFile);
        if (!Write(written, globals, out error)) return false;
        string hash = Hash(globals);
        string sceneFile = GuestSession.Scene + WorldShip.SceneSuffix;
        byte[] levelFile;
        if (ticket.Files.TryGetValue(sceneFile, out levelFile) && !Write(Path.Combine(slot, sceneFile), levelFile, out error)) return false;
        if (!Write(Path.Combine(slot, WorldShip.DialogueFile), ticket.Files[WorldShip.DialogueFile], out error)) return false;
        byte[] optional;
        if (ticket.Files.TryGetValue(WorldShip.InfoFile, out optional) && !Write(Path.Combine(slot, WorldShip.InfoFile), optional, out error)) return false;
        if (ticket.Files.TryGetValue(WorldShip.WorldIdFile, out optional))
        {
            string idPath = Path.Combine(world, WorldShip.WorldIdFile);
            if (PathOk(idPath, out full) && !Write(idPath, optional, out error)) return false;
            if (!Write(Path.Combine(slot, WorldShip.CoopFolder, WorldShip.WorldIdFile), optional, out error)) return false;
        }
        byte[] readBack = Read(written);
        if (readBack == null || Hash(readBack) != hash)
        {
            error = "the spliced Globals file did not hold its bytes on disk";
            return false;
        }
        CoopLog.Log.Info("coop: placed " + GuestSplice.EntryCount(globals) + " entries into " + slot + " (sha256 " + hash.Substring(0, 16) + ")");
        if (!VerifySpliced(globals, expected, out error)) return false;
        if (fromMirror) GuestProfile.Stage(profile, "the mirror is newer than the ticket");
        return true;
    }

    private static bool VerifySpliced(byte[] globals, int expected, out string error)
    {
        error = null;
        if (GameDeserialize == null)
        {
            error = "the game has no SaveController.Deserialize(string, Type, string, out Exception)";
            return false;
        }
        Type header = HeaderType;
        if (header == null)
        {
            error = "the game has no SaveController.SaveDataHeader type";
            return false;
        }
        string text = Encoding.UTF8.GetString(Strip(globals));
        object[] args = new object[] { WorldShip.GlobalsFile, header, text, null };
        object parsed = null;
        try
        {
            parsed = GameDeserialize.Invoke(null, args);
        }
        catch (Exception e)
        {
            error = "the game parser threw on the spliced Globals file: " + e.Message;
            return false;
        }
        Exception exception = args[3] as Exception;
        if (parsed == null || exception != null)
        {
            error = "the game parser refused the spliced Globals file" + (exception != null ? ": " + exception.Message : "");
            return false;
        }
        FieldInfo dataField = header.GetField("Data", BindingFlags.Public | BindingFlags.Instance);
        IList list = dataField != null ? dataField.GetValue(parsed) as IList : null;
        if (list == null)
        {
            error = "the game header has no Data list";
            return false;
        }
        if (list.Count != expected)
        {
            error = "the game parser read " + list.Count + " entries of " + expected;
            return false;
        }
        if (GuestSplice.EntryCount(globals) != expected)
        {
            error = "the spliced Globals file reads " + GuestSplice.EntryCount(globals) + " entries of " + expected;
            return false;
        }
        return true;
    }

    private static void Spawn()
    {
        Transform body = GameRefs.LocalPlayer;
        PlayerLocator locator = PlayerLocator.instance;
        if (body == null || locator == null) return;
        if (hostX == 0f && hostY == 0f && hostZ == 0f)
        {
            CoopLog.Log.Info("coop: the host sent no position, the guest stays at the scene entrypoint");
            return;
        }
        Vector3 offset = Quaternion.Euler(0f, hostYaw, 0f) * Vector3.right * SpawnOffset;
        Vector3 target = new Vector3(hostX, hostY, hostZ) + offset;
        RaycastHit hit;
        if (Physics.Raycast(target + Vector3.up * GroundProbeUp, Vector3.down, out hit, GroundProbeUp + GroundProbeDown))
        {
            target = hit.point + Vector3.up * 0.1f;
        }
        GameObject marker = new GameObject("CoopGuestSpawn");
        try
        {
            marker.transform.position = target;
            marker.transform.eulerAngles = new Vector3(0f, hostYaw, 0f);
            marker.transform.localScale = body.localScale;
            locator.Teleport(marker.transform);
            CoopLog.Log.Info("coop: guest spawned at " + target.ToString() + " near the host");
        }
        catch (Exception e)
        {
            CoopLog.Once("GuestWorld.spawn", e);
        }
        finally
        {
            UnityEngine.Object.Destroy(marker);
        }
    }

    public static bool LoadRoutineActive
    {
        get { return loadRoutineActive; }
    }
}
