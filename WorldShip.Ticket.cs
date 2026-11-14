using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using ObenseuerCore;
using UnityEngine;

internal static partial class WorldShip
{
    public static byte[] Pack(TicketManifest manifest, string[] names, byte[][] data)
    {
        manifest.format = Format;
        manifest.files = new TicketEntry[names.Length];
        int total = 0;
        for (int i = 0; i < names.Length; i++)
        {
            TicketEntry entry = new TicketEntry();
            entry.name = names[i];
            entry.size = data[i].Length;
            entry.sha256 = Hash(data[i], 0, data[i].Length);
            manifest.files[i] = entry;
            total += data[i].Length;
        }
        byte[] json = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(manifest));
        byte[] blob = new byte[4 + json.Length + total];
        PutInt(blob, 0, json.Length);
        Buffer.BlockCopy(json, 0, blob, 4, json.Length);
        int at = 4 + json.Length;
        for (int i = 0; i < data.Length; i++)
        {
            Buffer.BlockCopy(data[i], 0, blob, at, data[i].Length);
            at += data[i].Length;
        }
        return blob;
    }

    public static bool Unpack(byte[] blob, out WorldTicket ticket, out string error)
    {
        ticket = null;
        error = null;
        if (blob == null || blob.Length < 4)
        {
            error = "the ticket is too short";
            return false;
        }
        int jsonLength = GetInt(blob, 0);
        if (jsonLength < 2 || jsonLength > MaxManifestBytes || jsonLength > blob.Length - 4)
        {
            error = "bad manifest length " + jsonLength;
            return false;
        }
        TicketManifest manifest;
        try
        {
            manifest = JsonConvert.DeserializeObject<TicketManifest>(Encoding.UTF8.GetString(blob, 4, jsonLength));
        }
        catch (Exception e)
        {
            error = "the manifest is not JSON: " + e.Message;
            return false;
        }
        if (manifest == null || manifest.format != Format || manifest.files == null)
        {
            error = "bad manifest";
            return false;
        }
        if (!IsWorldId(manifest.worldId))
        {
            error = "bad world id '" + manifest.worldId + "'";
            return false;
        }
        if (manifest.files.Length < 1 || manifest.files.Length > MaxFiles)
        {
            error = "bad file count " + manifest.files.Length;
            return false;
        }
        Dictionary<string, byte[]> files = new Dictionary<string, byte[]>();
        int at = 4 + jsonLength;
        foreach (TicketEntry entry in manifest.files)
        {
            if (entry == null || !SafeName(entry.name))
            {
                error = "bad file name";
                return false;
            }
            if (files.ContainsKey(entry.name))
            {
                error = "duplicate file " + entry.name;
                return false;
            }
            if (entry.size < 0 || entry.size > blob.Length - at)
            {
                error = "the size of " + entry.name + " runs past the ticket";
                return false;
            }
            string actual = Hash(blob, at, entry.size);
            if (!string.Equals(actual, entry.sha256, StringComparison.OrdinalIgnoreCase))
            {
                error = "sha256 mismatch for " + entry.name + ": expected " + entry.sha256 + ", got " + actual;
                return false;
            }
            byte[] bytes = new byte[entry.size];
            Buffer.BlockCopy(blob, at, bytes, 0, entry.size);
            files.Add(entry.name, bytes);
            at += entry.size;
        }
        if (at != blob.Length)
        {
            error = "extra bytes after the last file";
            return false;
        }
        ticket = new WorldTicket(manifest, files);
        return true;
    }

    public static bool SelfCheck()
    {
        byte[] first = Encoding.UTF8.GetBytes("globals");
        byte[] blob = Pack(Fixture(), new string[] { GlobalsFile, "Scene.tnmt" }, new byte[][] { first, new byte[] { 1, 2, 3 } });
        byte[] flipped = (byte[])blob.Clone();
        flipped[flipped.Length - 1] ^= 0xFF;
        byte[] cut = new byte[blob.Length - 1];
        Buffer.BlockCopy(blob, 0, cut, 0, cut.Length);
        byte[] extra = new byte[blob.Length + 1];
        Buffer.BlockCopy(blob, 0, extra, 0, blob.Length);
        byte[] escape = Pack(Fixture(), new string[] { "../x" }, new byte[][] { first });
        byte[] noWorld = Pack(new TicketManifest(), new string[] { GlobalsFile }, new byte[][] { first });
        WorldTicket ticket;
        string error;
        bool ok = Unpack(blob, out ticket, out error) && ticket.Manifest.files.Length == 2 && Encoding.UTF8.GetString(ticket.Files[GlobalsFile]) == "globals";
        ok = ok && !Unpack(flipped, out ticket, out error) && !Unpack(cut, out ticket, out error) && !Unpack(extra, out ticket, out error) && !Unpack(escape, out ticket, out error);
        ok = ok && !Unpack(noWorld, out ticket, out error) && error != null;
        CoopLog.Log.Info("coop: join ticket self check " + (ok ? "passed" : "FAILED"));
        return ok;
    }

    private static TicketManifest Fixture()
    {
        TicketManifest manifest = new TicketManifest();
        manifest.worldId = "0123456789abcdef";
        manifest.hostScene = "Open Sewer Tenement";
        manifest.gameBuild = Core.GameBuild;
        manifest.modVersion = Plugin.Version;
        return manifest;
    }

    private static bool SafeName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name[0] == '.') return false;
        return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static string Hash(byte[] data, int offset, int count)
    {
        using (SHA256 sha = SHA256.Create())
        {
            return BitConverter.ToString(sha.ComputeHash(data, offset, count)).Replace("-", "").ToLowerInvariant();
        }
    }

    private static void PutInt(byte[] target, int at, int value)
    {
        for (int i = 0; i < 4; i++) target[at + i] = (byte)(value >> (8 * i));
    }

    private static int GetInt(byte[] source, int at)
    {
        return source[at] | (source[at + 1] << 8) | (source[at + 2] << 16) | (source[at + 3] << 24);
    }

    private static string Describe(TicketManifest manifest)
    {
        StringBuilder text = new StringBuilder();
        text.Append(" world=").Append(manifest.worldId).Append(" mode=").Append(manifest.mode).Append(" scene=").Append(manifest.hostScene);
        text.Append(" host=").Append(manifest.hostName).Append(" build=").Append(manifest.gameBuild);
        foreach (TicketEntry entry in manifest.files)
        {
            text.Append(" [").Append(entry.name).Append(' ').Append(entry.size).Append(' ').Append(entry.sha256).Append(']');
        }
        return text.ToString();
    }
}
