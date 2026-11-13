using System;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

internal static partial class GuestWorld
{
    public static string CoopRoot()
    {
        string path = Path.GetFullPath(Path.Combine(Application.persistentDataPath, CoopFolderName));
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool PathOk(string path, out string full)
    {
        full = null;
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return false;
        }
        string root = CoopRoot() + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        string[] segments = full.Split(new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        for (int i = 0; i < segments.Length; i++)
        {
            if (string.Equals(segments[i], SavesSegment, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static void WipeSlots()
    {
        string folder = GuestSession.Folder;
        if (string.IsNullOrEmpty(folder)) return;
        Wipe(Path.Combine(folder, GuestSession.AutosaveSlot));
        Wipe(Path.Combine(folder, GuestSession.AutosaveSlot2));
    }

    private static bool Wipe(string path)
    {
        string full;
        if (!PathOk(path, out full)) return false;
        try
        {
            if (Directory.Exists(full)) Directory.Delete(full, true);
            else if (File.Exists(full)) File.Delete(full);
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Log.Error("coop: could not clear " + full + ": " + e.Message);
            return false;
        }
    }

    private static bool Write(string path, byte[] bytes, out string error)
    {
        error = null;
        if (bytes == null)
        {
            error = "no bytes for " + path;
            return false;
        }
        string temp = path + ".coop-tmp";
        try
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
            return true;
        }
        catch (Exception e)
        {
            error = "could not write " + path + ": " + e.Message;
            return false;
        }
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

    private static byte[] Strip(byte[] bytes)
    {
        if (bytes != null && bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            byte[] body = new byte[bytes.Length - 3];
            Buffer.BlockCopy(bytes, 3, body, 0, body.Length);
            return body;
        }
        return bytes;
    }

    private static string Hash(byte[] bytes)
    {
        using (SHA256 sha = SHA256.Create())
        {
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }

    private static bool Has(WorldShip.WorldTicket ticket, string name)
    {
        return ticket.Files != null && ticket.Files.ContainsKey(name);
    }

    private static string HostLabel(string name)
    {
        string text = name == null ? "" : name.Trim();
        if (text.Length == 0) text = Session.DisplayName(Session.HostId);
        if (text.Length > MaxHostName) text = text.Substring(0, MaxHostName);
        return text;
    }

    private static bool IsWorldId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) continue;
            return false;
        }
        return true;
    }

    private static bool IsPlainName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 80) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        return name.IndexOf("..", StringComparison.Ordinal) < 0;
    }

    private static bool IsReservedScene(string scene)
    {
        for (int i = 0; i < ReservedScenes.Length; i++)
        {
            if (string.Equals(scene, ReservedScenes[i], StringComparison.OrdinalIgnoreCase)) return true;
        }
        string file = scene + WorldShip.SceneSuffix;
        if (string.Equals(file, WorldShip.GlobalsFile, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(file, WorldShip.DialogueFile, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(file, WorldShip.InfoFile, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(file, WorldShip.WorldIdFile, StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(file, WorldShip.ProfileFile, StringComparison.OrdinalIgnoreCase);
    }

    private static bool DryRun()
    {
        string root = CoopRoot();
        string probe = Path.Combine(root, "0_0123456789abcdef");
        string full;
        bool ok = root.Length > 0 && PathOk(probe, out full) && full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        ok = ok && !PathOk(Path.Combine(Application.persistentDataPath, "Saves", "probe"), out full);
        CoopLog.Log.Info("coop: coop folder dry run " + (ok ? "passed" : "FAILED") + ": " + probe + " (guest save hooks " + (GuestSaveHooks.Ready ? "ready" : "NOT ready") + ", guest gate hooks " + (GuestGateHooks.Ready ? "ready" : "NOT ready") + ")");
        return ok;
    }
}
