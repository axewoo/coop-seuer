using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using ObenseuerCore;
using OS.Items;
using UnityEngine;

internal sealed class ContainerMsg
{
    public string Kind;
    public int ReqId;
    public string Scene;
    public string Key;
    public string LockId;
    public string Reason;
    public ulong Holder;
    public bool HasState;
    public List<string> Keys;
    public List<string> Hashes;
}

internal static class ContainerWire
{
    public const string Channel = "cont";
    public const string BlobName = "cont-state";
    public const string ModId = "obenseuer.coop";
    public const string GuidPrefix = "g-";
    public const string PiecePrefix = "f-";
    public const string BlobPrefix = "cont-state-";
    private const int MaxText = 200;
    private const int MaxReason = 120;
    private const int MaxReqId = 999999999;
    private const int MaxJson = 4194304;
    private const int MaxLock = 24;
    private const int MaxDigestEntries = 512;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly System.Random Rng = new System.Random();
    private static int lockCounter;
    private static readonly char[] Bad = { '|', '\r', '\n' };
    private static readonly FieldInfo ActivePick = typeof(PlayerLockpicking).GetField("ActiveLockpicking", BindingFlags.Static | BindingFlags.NonPublic);

    public static string ReqOpen(int reqId, string scene, string key)
    {
        return string.Format(Inv, "REQOPEN|{0}|{1}|{2}", reqId, scene, key);
    }

    public static string Open(int reqId, string key, string lockId)
    {
        return lockId == null ? string.Format(Inv, "OPEN|{0}|{1}", reqId, key) : string.Format(Inv, "OPEN|{0}|{1}|{2}", reqId, key, lockId);
    }

    public static string Applied(string lockId)
    {
        return string.Format(Inv, "APPLIED|{0}", lockId);
    }

    public static string Refused(string lockId, string reason)
    {
        return string.Format(Inv, "REFUSED|{0}|{1}", lockId, Clean(reason));
    }

    public static string StaleNotice(string lockId, string reason)
    {
        return string.Format(Inv, "STALE|{0}|{1}", lockId, Clean(reason));
    }

    public static string Deny(int reqId, string reason)
    {
        return string.Format(Inv, "DENY|{0}|{1}", reqId, Clean(reason));
    }

    public static string Lock(string scene, string key, ulong holder)
    {
        return string.Format(Inv, "LOCK|{0}|{1}|{2}", scene, key, holder);
    }

    public static string Unlock(string scene, string key)
    {
        return string.Format(Inv, "UNLOCK|{0}|{1}", scene, key);
    }

    public static string Close(string scene, string key, bool hasState, string lockId)
    {
        return lockId == null
            ? string.Format(Inv, "CLOSE|{0}|{1}|{2}", scene, key, hasState ? "1" : "0")
            : string.Format(Inv, "CLOSE|{0}|{1}|{2}|{3}", scene, key, hasState ? "1" : "0", lockId);
    }

    public static string Touch(string scene, string key, string lockId)
    {
        return lockId == null ? string.Format(Inv, "TOUCH|{0}|{1}", scene, key) : string.Format(Inv, "TOUCH|{0}|{1}|{2}", scene, key, lockId);
    }

    public static string Digest(string scene, string body)
    {
        return "DIGEST|" + scene + "|" + (body ?? "");
    }

    public static string Relock(string scene, string key)
    {
        return string.Format(Inv, "RELOCK|{0}|{1}", scene, key);
    }

    public static bool Picking(Storage storage)
    {
        if (storage == null || storage._Lock == null || !storage._Lock.Locked) return false;
        if (ActivePick == null)
        {
            CoopLog.Once("ContainerWire.Picking", "coop: the game lockpick field was not found, a container lock is not guarded during a pick");
            return false;
        }
        object pick = ActivePick.GetValue(null);
        if (pick == null) return false;
        return pick.GetType() != storage._Lock.GetType() || ReferenceEquals(pick, storage._Lock);
    }

    public static string ReqSync(int reqId, string scene, string key)
    {
        return string.Format(Inv, "REQSYNC|{0}|{1}|{2}", reqId, scene, key);
    }

    public static string BlobNameOf(string key)
    {
        return BlobNameOf(key, null);
    }

    public static string BlobNameOf(string key, string lockId)
    {
        if (key == null) return BlobName;
        string name = BlobPrefix + Tag(key);
        return string.IsNullOrEmpty(lockId) ? name : name + "-" + LockTag(lockId);
    }

    public static bool IsBlobForKey(string name, string key)
    {
        if (name == null || key == null) return false;
        string head = BlobPrefix + Tag(key);
        if (name == head) return true;
        return name.Length == head.Length + 5 && name[head.Length] == '-' && name.StartsWith(head, StringComparison.Ordinal);
    }

    public static string LockTag(string lockId)
    {
        return Suffix(Hash(lockId));
    }

    public static string NewLockId()
    {
        lockCounter++;
        int roll = Rng.Next(65536);
        return lockCounter.ToString(Inv) + "-" + roll.ToString("x4", Inv);
    }

    public static ulong Hash(string text)
    {
        ulong hash = 14695981039346656037UL;
        if (text == null) return hash;
        for (int i = 0; i < text.Length; i++)
        {
            hash ^= text[i];
            hash *= 1099511628211UL;
        }
        return hash;
    }

    public static string HashText(string text)
    {
        return Hex16(Hash(text));
    }

    public static string StateText(Storage storage, string key)
    {
        SlotController[] slots = storage == null ? null : storage.Slots;
        StringBuilder sb = new StringBuilder(key == null ? "" : key);
        if (slots == null) return sb.ToString();
        for (int i = 0; i < slots.Length; i++)
        {
            SlotController slot = slots[i];
            ItemStack stack = slot == null ? null : slot.itemStack;
            if (stack == null || stack.itemId < 0 || stack.itemAmount <= 0) continue;
            sb.Append('|').Append(i.ToString(Inv));
            sb.Append(':').Append(stack.itemId.ToString(Inv));
            sb.Append(':').Append(stack.itemAmount.ToString(Inv));
        }
        return sb.ToString();
    }

    public static string ContentHash(Storage storage, string key)
    {
        bool locked = storage != null && storage._Lock != null && storage._Lock.Locked;
        return Hex8(Hash(StateText(storage, key) + (locked ? "|L1" : "|L0")));
    }

    private static string Hex8(ulong hash)
    {
        char[] hex = new char[8];
        for (int i = hex.Length - 1; i >= 0; i--)
        {
            hex[i] = Hex(hash & 0xF);
            hash >>= 4;
        }
        return new string(hex);
    }

    private static string Suffix(ulong hash)
    {
        char[] hex = new char[4];
        for (int i = hex.Length - 1; i >= 0; i--)
        {
            hex[i] = Hex(hash & 0xF);
            hash >>= 4;
        }
        return new string(hex);
    }

    public static bool IsBlobName(string name)
    {
        return name != null && (name == BlobName || name.StartsWith(BlobPrefix, StringComparison.Ordinal));
    }

    public static string Tag(string key)
    {
        return HashText(key);
    }

    private static string Hex16(ulong hash)
    {
        char[] hex = new char[16];
        for (int i = hex.Length - 1; i >= 0; i--)
        {
            hex[i] = Hex(hash & 0xF);
            hash >>= 4;
        }
        return new string(hex);
    }

    private static char Hex(ulong value)
    {
        return value < 10 ? (char)(48 + value) : (char)(97 + (value - 10));
    }

    public static string GuidKey(string guid)
    {
        return string.IsNullOrEmpty(guid) ? null : GuidPrefix + guid;
    }

    public static string PieceKey(string syncId)
    {
        return string.IsNullOrEmpty(syncId) ? null : PiecePrefix + syncId;
    }

    public static bool IsPieceKey(string key)
    {
        return key != null && key.StartsWith(PiecePrefix, StringComparison.Ordinal);
    }

    public static string GuidOf(string key)
    {
        if (key == null || IsPieceKey(key)) return null;
        return key.StartsWith(GuidPrefix, StringComparison.Ordinal) ? key.Substring(GuidPrefix.Length) : null;
    }

    public static string SyncIdOf(string key)
    {
        if (!IsPieceKey(key)) return null;
        string body = key.Substring(PiecePrefix.Length);
        int hash = body.IndexOf('#');
        return hash < 0 ? body : body.Substring(0, hash);
    }

    public static int PieceIndexOf(string key)
    {
        if (key == null) return -1;
        int hash = key.IndexOf('#');
        if (hash < 0) return 0;
        int value;
        return int.TryParse(key.Substring(hash + 1), NumberStyles.None, Inv, out value) && value >= 0 ? value : -1;
    }

    public static string KeyOf(Storage storage)
    {
        if (storage == null) return null;
        if (PrisonLocal.IsCellStorage(storage)) return null;
        GameObject piece = PieceOf(storage);
        string syncId = piece == null ? null : IdOf(piece);
        if (syncId == null) return GuidKey(storage.GUID);
        return PieceKey(syncId) + "#" + PieceIndexOf(piece, storage);
    }

    public static string SurrogateGuid(string key)
    {
        return "coop-" + key.Replace('#', '-') + "-storage";
    }

    public static string Capture(Storage storage, string key)
    {
        return StationState.Wrap(storage, key, CaptureStorage(storage, key));
    }

    public static bool Apply(Storage storage, string key, string json)
    {
        string[] rest;
        string plain = StationState.StoragePart(json, out rest);
        string before = rest == null ? null : CaptureStorage(storage, key);
        bool ok = ApplyStorage(storage, key, plain);
        if (!ok || rest == null) return ok;
        if (StationState.ApplyRest(storage, key, rest))
        {
            PowerSync.StationChanged(StationState.StationOf(storage));
            return true;
        }
        if (before != null) ApplyStorage(storage, key, before);
        return false;
    }

    private static string CaptureStorage(Storage storage, string key)
    {
        if (storage == null || !IsPieceKey(key)) return SaveData.Capture(storage);
        string real = SaveController.GetObjectGUID(storage);
        SaveController.SetObjectGUID(storage, SurrogateGuid(key));
        try
        {
            return SaveData.Capture(storage);
        }
        finally
        {
            SaveController.SetObjectGUID(storage, real);
        }
    }

    private static bool ApplyStorage(Storage storage, string key, string json)
    {
        if (storage == null || !IsPieceKey(key)) return SaveData.Apply(storage, json);
        string real = SaveController.GetObjectGUID(storage);
        SaveController.SetObjectGUID(storage, SurrogateGuid(key));
        try
        {
            return SaveData.Apply(storage, json);
        }
        finally
        {
            SaveController.SetObjectGUID(storage, real);
        }
    }

    public static byte[] Bytes(string json)
    {
        return json == null ? null : Encoding.UTF8.GetBytes(json);
    }

    public static string Text(byte[] data)
    {
        if (data == null || data.Length == 0 || data.Length > MaxJson) return null;
        return Encoding.UTF8.GetString(data);
    }

    public static bool TryParse(string payload, out ContainerMsg msg)
    {
        msg = null;
        try
        {
            if (payload == null) return false;
            string[] f = payload.Split('|');
            ContainerMsg m = new ContainerMsg();
            m.Kind = f[0];
            if (!Fields(f, m)) return false;
            msg = m;
            return true;
        }
        catch (Exception)
        {
            msg = null;
            return false;
        }
    }

    public static GameObject PieceOf(Component storage)
    {
        Transform step = storage.transform;
        while (step != null)
        {
            if (IdOf(step.gameObject) != null) return step.gameObject;
            step = step.parent;
        }
        return null;
    }

    public static string IdOf(GameObject go)
    {
        return Session.IsHost ? ContainerHost.SyncIdOf(go) : BuildGuest.IdOf(go);
    }

    private static int PieceIndexOf(GameObject piece, Storage storage)
    {
        Storage[] all = PieceStorages(piece);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == storage) return i;
        }
        return 0;
    }

    public static Storage[] PieceStorages(GameObject piece)
    {
        Storage[] all = piece.GetComponentsInChildren<Storage>(true);
        List<Storage> own = new List<Storage>();
        for (int i = 0; i < all.Length; i++)
        {
            if (PieceOf(all[i]) == piece) own.Add(all[i]);
        }
        return own.ToArray();
    }

    private static bool Fields(string[] f, ContainerMsg m)
    {
        switch (m.Kind)
        {
            case "REQOPEN":
                return f.Length == 4 && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[2], out m.Scene) && Text(f[3], out m.Key);
            case "OPEN":
                if (f.Length == 3) return Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[2], out m.Key);
                return f.Length == 4 && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[2], out m.Key) && Lid(f[3], out m.LockId);
            case "DENY":
                return f.Length == 3 && Int(f[1], 1, MaxReqId, out m.ReqId) && Note(f[2], out m.Reason);
            case "LOCK":
                return f.Length == 4 && Text(f[1], out m.Scene) && Text(f[2], out m.Key) && ULong(f[3], out m.Holder);
            case "CLOSE":
                if (f.Length == 4) return Text(f[1], out m.Scene) && Text(f[2], out m.Key) && Flag(f[3], out m.HasState);
                return f.Length == 5 && Text(f[1], out m.Scene) && Text(f[2], out m.Key) && Flag(f[3], out m.HasState) && Lid(f[4], out m.LockId);
            case "UNLOCK":
                return f.Length == 3 && Text(f[1], out m.Scene) && Text(f[2], out m.Key);
            case "TOUCH":
                if (f.Length == 3) return Text(f[1], out m.Scene) && Text(f[2], out m.Key);
                return f.Length == 4 && Text(f[1], out m.Scene) && Text(f[2], out m.Key) && Lid(f[3], out m.LockId);
            case "DIGEST":
                return f.Length == 3 && Text(f[1], out m.Scene) && DigestEntries(f[2], m);
            case "RELOCK":
                return f.Length == 3 && Text(f[1], out m.Scene) && Text(f[2], out m.Key);
            case "REQSYNC":
                return f.Length == 4 && Int(f[1], 1, MaxReqId, out m.ReqId) && Text(f[2], out m.Scene) && Text(f[3], out m.Key);
            case "APPLIED":
                return f.Length == 2 && Lid(f[1], out m.LockId);
            case "REFUSED":
            case "STALE":
                return f.Length == 3 && Lid(f[1], out m.LockId) && Note(f[2], out m.Reason);
            default:
                return false;
        }
    }

    private static bool DigestEntries(string body, ContainerMsg m)
    {
        m.Keys = new List<string>();
        m.Hashes = new List<string>();
        if (string.IsNullOrEmpty(body)) return true;
        string[] entries = body.Split(',');
        if (entries.Length > MaxDigestEntries) return false;
        for (int i = 0; i < entries.Length; i++)
        {
            string entry = entries[i];
            int colon = entry.IndexOf(':');
            if (colon <= 0 || colon != entry.LastIndexOf(':')) return false;
            string key;
            if (!Text(entry.Substring(0, colon), out key)) return false;
            string hash = entry.Substring(colon + 1);
            if (!DigestHash(hash)) return false;
            m.Keys.Add(key);
            m.Hashes.Add(hash);
        }
        return true;
    }

    private static bool DigestHash(string text)
    {
        if (text == null || text.Length != 8) return false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')) continue;
            return false;
        }
        return true;
    }

    private static string Clean(string text)
    {
        string plain = (text ?? "").Replace("|", "").Replace("\r", "").Replace("\n", "");
        return plain.Length > MaxReason ? plain.Substring(0, MaxReason) : plain;
    }

    private static bool Lid(string text, out string value)
    {
        value = text;
        if (text == null || text.Length < 1 || text.Length > MaxLock) return false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || c == '-') continue;
            return false;
        }
        return true;
    }

    private static bool Flag(string text, out bool value)
    {
        value = text == "1";
        return text == "0" || text == "1";
    }

    private static bool Int(string text, int min, int max, out int value)
    {
        return int.TryParse(text, NumberStyles.Integer, Inv, out value) && value >= min && value <= max;
    }

    private static bool ULong(string text, out ulong value)
    {
        return ulong.TryParse(text, NumberStyles.None, Inv, out value);
    }

    private static bool Text(string text, out string value)
    {
        value = text;
        return text != null && text.Length >= 1 && text.Length <= MaxText && text.IndexOfAny(Bad) < 0;
    }

    private static bool Note(string text, out string value)
    {
        value = text;
        return text != null && text.Length <= MaxReason;
    }
}
