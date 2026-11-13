using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

internal static partial class FreeRoam
{
    public const string BlobPrefix = "scene-";
    private const string StoreFolder = "coop-scenes";
    private const string SidecarSuffix = ".ckpt";
    private const float CkptWindow = 60f;
    private const int MaxSceneBytes = 16 * 1024 * 1024;
    private const int MaxSeqScenes = 128;
    private static readonly Regex LevelField = new Regex("\"LevelName\":\"[^\"]*\"");
    private static readonly Regex EntryField = new Regex("\"NewlevelEntrypoint\":\"[^\"]*\"");

    private sealed class CkptNote
    {
        public ulong Peer;
        public int Gen;
        public int Seq;
        public float At;
    }

    private sealed class StoredNote
    {
        public int Gen;
        public int Seq;
    }

    private static readonly System.Collections.Generic.Dictionary<string, CkptNote> ckptIn = new System.Collections.Generic.Dictionary<string, CkptNote>();
    private static readonly System.Collections.Generic.Dictionary<string, StoredNote> storedNote = new System.Collections.Generic.Dictionary<string, StoredNote>();
    private static readonly System.Collections.Generic.Dictionary<string, int> ckptSeq = new System.Collections.Generic.Dictionary<string, int>();

    private const float AreaMemory = 300f;
    private static readonly System.Collections.Generic.Dictionary<string, float> areas = new System.Collections.Generic.Dictionary<string, float>();

    private static void TrackAreas()
    {
        float now = Time.realtimeSinceStartup;
        foreach (ulong peer in Session.Peers)
        {
            string area = Session.AreaOf(peer);
            if (!string.IsNullOrEmpty(area)) areas[peer + "|" + area] = now;
        }
        if (areas.Count > 256) areas.Clear();
    }

    private static bool WasIn(ulong peer, string scene)
    {
        float at;
        return areas.TryGetValue(peer + "|" + scene, out at) && Time.realtimeSinceStartup - at < AreaMemory;
    }

    private static void ShipCkpt(string scene)
    {
        int seq = NextSeq(scene);
        int gen = Gen(scene);
        Session.Send(Session.HostId, Channel, "CKPT|" + scene + "|" + Num(gen) + "|" + Num(seq), true);
        if (CoopDebug.On("auth")) CoopDebug.Event("auth", "ckpt sent " + scene, "CKPT " + scene + " gen " + Num(gen) + " seq " + Num(seq) + " to the host");
    }

    private static int NextSeq(string scene)
    {
        int seq;
        ckptSeq.TryGetValue(scene, out seq);
        seq++;
        if (ckptSeq.Count > MaxSeqScenes && !ckptSeq.ContainsKey(scene)) ckptSeq.Clear();
        ckptSeq[scene] = seq;
        return seq;
    }

    private static void HostCkpt(ulong from, string[] f)
    {
        if (!Plain(f[1]) || !Session.Peers.Contains(from)) return;
        int gen;
        int seq;
        if (!int.TryParse(f[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out gen)) return;
        if (!int.TryParse(f[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out seq)) return;
        if (!GenMatches(f[1], gen))
        {
            CoopLog.Log.Info("coop: a checkpoint for " + f[1] + " from " + from + " was dropped, gen " + Num(gen) + " is not " + Num(Gen(f[1])));
            if (CoopDebug.On("auth")) CoopDebug.Event("auth", "ckpt gen " + f[1], f[1] + " from " + from + " gen " + Num(gen) + " is not " + Num(Gen(f[1])));
            return;
        }
        if (ckptIn.Count > MaxSeqScenes && !ckptIn.ContainsKey(f[1])) ckptIn.Clear();
        CkptNote note = new CkptNote();
        note.Peer = from;
        note.Gen = gen;
        note.Seq = seq;
        note.At = Time.unscaledTime;
        ckptIn[f[1]] = note;
        if (CoopDebug.On("auth")) CoopDebug.Event("auth", "ckpt recv " + f[1], f[1] + " from " + from + " gen " + Num(gen) + " seq " + Num(seq));
    }

    private static void OnBlob(ulong peer, string modId, string name, byte[] data)
    {
        try
        {
            if (modId != Plugin.PluginGuid || name == null || !name.StartsWith(BlobPrefix, StringComparison.Ordinal)) return;
            string scene = data == null ? null : LevelOf(data);
            if (scene == null || name != BlobName(scene)) return;
            if (!Session.IsHost || !Session.Peers.Contains(peer) || !Plain(scene) || data == null || data.Length == 0 || data.Length > MaxSceneBytes) return;
            if (!WasIn(peer, scene))
            {
                CoopLog.Log.Info("coop: a copy of " + scene + " from " + peer + " was refused, that player was not there");
                return;
            }
            ulong holder = HolderOf(scene);
            if (holder != 0UL && holder != peer && Session.AreaOf(holder) == scene)
            {
                CoopLog.Log.Info("coop: a copy of " + scene + " from " + peer + " was refused, guest " + holder + " holds that scene");
                return;
            }
            if (LevelOf(data) != scene)
            {
                CoopLog.Log.Info("coop: a copy of " + scene + " from " + peer + " was refused, it is not a save of that scene");
                return;
            }
            string store = StoreForBlob();
            if (store == null) return;
            string path = Path.Combine(store, scene + WorldShip.SceneSuffix);
            CkptNote note;
            if (!ckptIn.TryGetValue(scene, out note) || note.Peer != peer || !GenMatches(scene, note.Gen) || Time.unscaledTime - note.At > CkptWindow)
            {
                CoopLog.Log.Info("coop: a copy of " + scene + " from " + peer + " was refused, no checkpoint announcement matches it");
                if (CoopDebug.On("auth")) CoopDebug.Event("auth", "blob no ckpt " + scene, scene + " from " + peer + " has no matching announcement");
                return;
            }
            StoredNote have = StoredOf(scene, path);
            if (note.Gen < have.Gen || (note.Gen == have.Gen && note.Seq < have.Seq))
            {
                CoopLog.Log.Info("coop: stale scene blob dropped (" + scene + ", " + Num(note.Seq) + ", " + Num(have.Seq) + ")");
                if (CoopDebug.On("auth")) CoopDebug.Event("auth", "blob stale " + scene, scene + " seq " + Num(note.Seq) + " is behind " + Num(have.Seq) + " of gen " + Num(have.Gen));
                return;
            }
            Directory.CreateDirectory(store);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, data);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
            WriteSidecar(path, note.Gen, note.Seq);
            have.Gen = note.Gen;
            have.Seq = note.Seq;
            storedAt[scene] = Time.unscaledTime;
            CoopLog.Log.Info("coop: stored " + scene + " (" + data.Length + " bytes) from guest " + peer);
            if (CoopDebug.On("auth")) CoopDebug.Event("auth", "blob stored " + scene, scene + " from " + peer + " gen " + Num(note.Gen) + " seq " + Num(note.Seq));
            if (waitScene == scene) waitDone = true;
            QuitBlobScene(scene);
        }
        catch (Exception e)
        {
            CoopLog.Once("FreeRoam.OnBlob", e);
        }
    }

    private static bool GenMatches(string scene, int gen)
    {
        if (gen == Gen(scene)) return true;
        float at;
        return HeldXferGen(scene, gen, out at) && Time.unscaledTime - at <= CkptWindow;
    }

    private static readonly System.Collections.Generic.Dictionary<string, int> holdSeed = new System.Collections.Generic.Dictionary<string, int>();

    private static int HoldSeed(string scene)
    {
        int seed;
        if (holdSeed.TryGetValue(scene, out seed)) return seed;
        seed = 0;
        string name = scene + WorldShip.SceneSuffix;
        string slot = StoreDir();
        string old = StoreDirOld();
        int gen;
        int seq;
        if (slot != null)
        {
            ReadSidecar(Path.Combine(slot, name), out gen, out seq);
            if (gen > seed) seed = gen;
        }
        if (old != null && !string.Equals(old, slot, StringComparison.OrdinalIgnoreCase))
        {
            ReadSidecar(Path.Combine(old, name), out gen, out seq);
            if (gen > seed) seed = gen;
        }
        if (holdSeed.Count > MaxSeqScenes) holdSeed.Clear();
        holdSeed[scene] = seed;
        return seed;
    }

    private static string storeAtQuit;

    public static void PinStore()
    {
        storeAtQuit = StoreDir();
    }

    private static string StoreForBlob()
    {
        return storeAtQuit != null ? storeAtQuit : StoreDir();
    }

    private static StoredNote StoredOf(string scene, string path)
    {
        StoredNote note;
        if (storedNote.TryGetValue(scene, out note)) return note;
        note = new StoredNote();
        ReadSidecar(path, out note.Gen, out note.Seq);
        if (storedNote.Count > MaxSeqScenes) storedNote.Clear();
        storedNote[scene] = note;
        return note;
    }

    private static void WriteSidecar(string scenePath, int gen, int seq)
    {
        try
        {
            File.WriteAllText(scenePath + SidecarSuffix, Num(gen) + " " + Num(seq), new UTF8Encoding(false));
        }
        catch (Exception e)
        {
            CoopLog.Once("FreeRoam.WriteSidecar", e);
        }
    }

    private static void ReadSidecar(string scenePath, out int gen, out int seq)
    {
        gen = 0;
        seq = 0;
        try
        {
            string path = scenePath + SidecarSuffix;
            if (!File.Exists(path)) return;
            string[] f = File.ReadAllText(path).Trim().Split(' ');
            if (f.Length < 2) return;
            int.TryParse(f[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out gen);
            int.TryParse(f[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out seq);
        }
        catch (Exception e)
        {
            CoopLog.Once("FreeRoam.ReadSidecar", e);
            gen = 0;
            seq = 0;
        }
    }

    private static void CopySidecar(string from, string to)
    {
        try
        {
            string note = from + SidecarSuffix;
            if (File.Exists(note)) File.Copy(note, to + SidecarSuffix, true);
            else if (File.Exists(to + SidecarSuffix)) File.Delete(to + SidecarSuffix);
        }
        catch (Exception e)
        {
            CoopLog.Once("FreeRoam.CopySidecar", e);
        }
    }

    public static void LoadPrefix(string characterName, string saveName)
    {
        try
        {
            if (!Session.IsHost || string.IsNullOrEmpty(characterName) || string.IsNullOrEmpty(saveName)) return;
            string slot = Path.Combine(Path.Combine(Application.persistentDataPath + "/Saves", characterName), saveName);
            string globals = Path.Combine(slot, WorldShip.GlobalsFile);
            if (!File.Exists(globals)) return;
            string level = LevelOf(File.ReadAllBytes(globals));
            if (!Plain(level)) return;
            string stored = StoredScene(level);
            if (stored == null) return;
            string target = Path.Combine(slot, level + WorldShip.SceneSuffix);
            if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(stored)) return;
            File.Copy(stored, target, true);
            CopySidecar(stored, target);
            CoopLog.Log.Info("coop: the host loads " + level + " from the guest's stored copy");
        }
        catch (Exception e)
        {
            CoopLog.Once("FreeRoam.LoadPrefix", e);
        }
    }

    public static string NewestScene(string folder, string scene)
    {
        string own = Path.Combine(folder, scene + WorldShip.SceneSuffix);
        bool hasOwn = File.Exists(own);
        string stored = StoredScene(scene);
        if (stored == null) return hasOwn ? own : null;
        if (!hasOwn) return stored;
        return File.GetLastWriteTimeUtc(stored) > File.GetLastWriteTimeUtc(own) ? stored : own;
    }

    private static string StoredScene(string scene)
    {
        string name = scene + WorldShip.SceneSuffix;
        string slot = StoreDir();
        string best = null;
        int bestGen = -1;
        int bestSeq = -1;
        if (slot != null && Directory.Exists(slot)) Pick(Path.Combine(slot, name), ref best, ref bestGen, ref bestSeq);
        if (best == null)
        {
            string old = StoreDirOld();
            if (old != null && !string.Equals(old, slot, StringComparison.OrdinalIgnoreCase)) Pick(Path.Combine(old, name), ref best, ref bestGen, ref bestSeq);
        }
        if (best != null && bestGen > 0 && bestGen < Gen(scene) && storedAt.ContainsKey(scene)) return null;
        return best;
    }

    private static void Pick(string path, ref string best, ref int bestGen, ref int bestSeq)
    {
        if (!File.Exists(path)) return;
        int gen;
        int seq;
        ReadSidecar(path, out gen, out seq);
        if (best == null || gen > bestGen || (gen == bestGen && seq > bestSeq) || (gen == bestGen && seq == bestSeq && File.GetLastWriteTimeUtc(path) > File.GetLastWriteTimeUtc(best)))
        {
            best = path;
            bestGen = gen;
            bestSeq = seq;
        }
    }

    public static byte[] Retarget(byte[] globals, string scene, string entry)
    {
        string text = Encoding.UTF8.GetString(globals);
        int head = Math.Min(text.Length, 1024);
        string header = text.Substring(0, head);
        header = LevelField.Replace(header, m => "\"LevelName\":\"" + scene + "\"", 1);
        header = EntryField.Replace(header, m => "\"NewlevelEntrypoint\":\"" + (EntryOk(entry) && entry.Length > 0 ? entry : "NONE") + "\"", 1);
        return Encoding.UTF8.GetBytes(header + text.Substring(head));
    }

    private static string BlobName(string scene)
    {
        return BlobPrefix + CombatSync.Fnv(scene).ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string LevelOf(byte[] globals)
    {
        string text = Encoding.UTF8.GetString(globals, 0, Math.Min(globals.Length, 1024));
        Match m = LevelField.Match(text);
        if (!m.Success) return null;
        return m.Value.Substring(13, m.Value.Length - 14);
    }

    private static string StoreRoot()
    {
        string basis = SaveController.LastSaveFolderPath;
        if (string.IsNullOrEmpty(basis)) basis = Session.WorldSlot;
        if (string.IsNullOrEmpty(basis)) return null;
        string character = Path.GetDirectoryName(basis);
        return string.IsNullOrEmpty(character) ? null : Path.Combine(character, StoreFolder);
    }

    private static string StoreDir()
    {
        string root = StoreRoot();
        if (root == null) return null;
        string slot = Path.GetFileName(Session.WorldSlot);
        if (string.IsNullOrEmpty(slot)) slot = Path.GetFileName(SaveController.LastSaveFolderPath);
        return string.IsNullOrEmpty(slot) ? root : Path.Combine(root, slot);
    }

    private static string StoreDirOld()
    {
        return StoreRoot();
    }
}
