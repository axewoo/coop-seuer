using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

internal enum TransferState
{
    Requested,
    Reserved,
    Granted,
    Applied,
    Closed,
    Refused,
    Expired,
    RolledBack,
}

internal struct TransferKey : IEquatable<TransferKey>
{
    public ulong GuestId;
    public int JoinEpoch;
    public int Seq;

    public TransferKey(ulong guestId, int joinEpoch, int seq)
    {
        GuestId = guestId;
        JoinEpoch = joinEpoch;
        Seq = seq;
    }

    public bool Equals(TransferKey other)
    {
        return GuestId == other.GuestId && JoinEpoch == other.JoinEpoch && Seq == other.Seq;
    }

    public override bool Equals(object obj)
    {
        return obj is TransferKey && Equals((TransferKey)obj);
    }

    public override int GetHashCode()
    {
        return (GuestId.GetHashCode() * 397) ^ ((JoinEpoch * 31) + Seq);
    }
}

internal sealed class TransferEntry
{
    public TransferKey Key;
    public string Kind;
    public string Payload;
    public TransferState State;
    public double StampSeconds;

    public TransferEntry(TransferKey key, string kind, string payload, TransferState state, double stampSeconds)
    {
        Key = key;
        Kind = kind ?? "";
        Payload = payload ?? "";
        State = state;
        StampSeconds = stampSeconds;
    }

    public bool Open
    {
        get { return !TransferLedger.Terminal(State); }
    }
}

internal sealed class TransferLedger
{
    public const string Head = "TL1";
    public const int MaxEntries = 4096;
    private const int MaxKind = 24;
    private const int MaxPayload = 240;
    private const int MaxEpoch = 1000000000;
    private const int MaxSeq = 1000000000;
    private const int Stride = 5;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] Tokens = { "REQ", "RES", "GRA", "APP", "CLO", "REF", "EXP", "RBK" };
    private static readonly TransferState[] States = { TransferState.Requested, TransferState.Reserved, TransferState.Granted, TransferState.Applied, TransferState.Closed, TransferState.Refused, TransferState.Expired, TransferState.RolledBack };
    private readonly Dictionary<TransferKey, TransferEntry> entries = new Dictionary<TransferKey, TransferEntry>();
    private readonly Dictionary<ulong, TransferKey> applied = new Dictionary<ulong, TransferKey>();

    public double Now;

    public static bool Terminal(TransferState state)
    {
        return state == TransferState.Closed || state == TransferState.Refused || state == TransferState.Expired || state == TransferState.RolledBack;
    }

    public static bool Legal(TransferState from, TransferState to)
    {
        switch (from)
        {
            case TransferState.Requested:
                return to == TransferState.Reserved || to == TransferState.Refused || to == TransferState.RolledBack || to == TransferState.Expired;
            case TransferState.Reserved:
                return to == TransferState.Granted || to == TransferState.Refused || to == TransferState.RolledBack || to == TransferState.Expired;
            case TransferState.Granted:
                return to == TransferState.Applied || to == TransferState.RolledBack || to == TransferState.Expired;
            case TransferState.Applied:
                return to == TransferState.Closed || to == TransferState.RolledBack;
            default:
                return false;
        }
    }

    public static string KeyText(TransferKey key)
    {
        return string.Format(Inv, "{0}.{1}.{2}", key.GuestId, key.JoinEpoch, key.Seq);
    }

    public TransferEntry Open(TransferKey key, string kind, string payload)
    {
        TransferEntry entry;
        if (entries.TryGetValue(key, out entry)) return entry;
        if (!Fits(key)) return null;
        string kindText = Escape(kind);
        string payloadText = Escape(payload);
        if (kindText.Length > MaxKind || payloadText.Length > MaxPayload) return null;
        if (entries.Count >= MaxEntries && !Prune()) return null;
        entry = new TransferEntry(key, kind, payload, TransferState.Requested, Now);
        entries[key] = entry;
        return entry;
    }

    public bool Advance(TransferKey key, TransferState state)
    {
        TransferEntry entry;
        if (!entries.TryGetValue(key, out entry)) return false;
        if (!Legal(entry.State, state)) return false;
        entry.State = state;
        entry.StampSeconds = Now;
        if (state == TransferState.Applied) Mark(key);
        return true;
    }

    public TransferEntry Find(TransferKey key)
    {
        TransferEntry entry;
        return entries.TryGetValue(key, out entry) ? entry : null;
    }

    public void Clear()
    {
        entries.Clear();
        applied.Clear();
    }

    public int Forget(ulong guestId)
    {
        List<TransferKey> drop = new List<TransferKey>();
        foreach (TransferKey key in entries.Keys)
        {
            if (key.GuestId == guestId) drop.Add(key);
        }
        for (int i = 0; i < drop.Count; i++) entries.Remove(drop[i]);
        applied.Remove(guestId);
        return drop.Count;
    }

    public int Expire(double nowSeconds, double maxAgeSeconds)
    {
        Now = nowSeconds;
        if (double.IsNaN(maxAgeSeconds) || maxAgeSeconds < 0) return 0;
        int count = 0;
        foreach (KeyValuePair<TransferKey, TransferEntry> pair in entries)
        {
            TransferEntry entry = pair.Value;
            if (!entry.Open || nowSeconds - entry.StampSeconds < maxAgeSeconds) continue;
            entry.State = TransferState.Expired;
            entry.StampSeconds = nowSeconds;
            Mark(pair.Key);
            count++;
        }
        return count;
    }

    public int AppliedSeq(ulong guestId)
    {
        TransferKey key;
        return applied.TryGetValue(guestId, out key) ? key.Seq : -1;
    }

    public string Serialize()
    {
        List<TransferEntry> list = new List<TransferEntry>();
        foreach (KeyValuePair<TransferKey, TransferEntry> pair in entries)
        {
            if (pair.Value.Open) list.Add(pair.Value);
        }
        list.Sort(Order);
        StringBuilder text = new StringBuilder(Head);
        for (int i = 0; i < list.Count; i++)
        {
            TransferEntry entry = list[i];
            text.Append(':').Append(KeyText(entry.Key)).Append(':').Append(Tokens[(int)entry.State]).Append(':').Append(Escape(entry.Kind))
                .Append(':').Append(entry.StampSeconds.ToString("R", Inv)).Append(':').Append(Escape(entry.Payload));
        }
        return text.ToString();
    }

    public bool TryParse(string line)
    {
        List<TransferEntry> list;
        if (!Decode(line, out list)) return false;
        Dictionary<TransferKey, TransferEntry> next = new Dictionary<TransferKey, TransferEntry>();
        for (int i = 0; i < list.Count; i++) next[list[i].Key] = list[i];
        if (next.Count != list.Count) return false;
        entries.Clear();
        foreach (KeyValuePair<TransferKey, TransferEntry> pair in next) entries[pair.Key] = pair.Value;
        return true;
    }

    private static bool Decode(string line, out List<TransferEntry> list)
    {
        list = null;
        if (string.IsNullOrEmpty(line)) return false;
        string[] f = line.Split(':');
        int count = f.Length - 1;
        if (f[0] != Head || count % Stride != 0) return false;
        int open = count / Stride;
        if (open > MaxEntries) return false;
        List<TransferEntry> found = new List<TransferEntry>();
        for (int i = 0; i < open; i++)
        {
            TransferEntry entry;
            if (!Field(f, 1 + (i * Stride), out entry)) return false;
            found.Add(entry);
        }
        list = found;
        return true;
    }

    private static bool Field(string[] f, int at, out TransferEntry entry)
    {
        entry = null;
        string[] k = f[at].Split('.');
        if (k.Length != 3) return false;
        ulong guestId;
        int epoch;
        int seq;
        TransferState state;
        double stamp;
        if (!ulong.TryParse(k[0], NumberStyles.Integer, Inv, out guestId)) return false;
        if (!int.TryParse(k[1], NumberStyles.Integer, Inv, out epoch)) return false;
        if (!int.TryParse(k[2], NumberStyles.Integer, Inv, out seq)) return false;
        if (!StateOf(f[at + 1], out state)) return false;
        if (!double.TryParse(f[at + 3], NumberStyles.Float, Inv, out stamp)) return false;
        if (double.IsNaN(stamp) || double.IsInfinity(stamp)) return false;
        TransferKey key = new TransferKey(guestId, epoch, seq);
        if (!Fits(key) || f[at + 2].Length > MaxKind || f[at + 4].Length > MaxPayload) return false;
        entry = new TransferEntry(key, Unescape(f[at + 2]), Unescape(f[at + 4]), state, stamp);
        return true;
    }

    private static bool StateOf(string token, out TransferState state)
    {
        state = TransferState.Requested;
        for (int i = 0; i < Tokens.Length; i++)
        {
            if (Tokens[i] != token) continue;
            state = States[i];
            return true;
        }
        return false;
    }

    private static bool Fits(TransferKey key)
    {
        return key.JoinEpoch >= 0 && key.JoinEpoch <= MaxEpoch && key.Seq >= 0 && key.Seq <= MaxSeq;
    }

    private static string Escape(string text)
    {
        return string.IsNullOrEmpty(text) ? "" : Uri.EscapeDataString(text);
    }

    private static string Unescape(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('%') < 0) return text ?? "";
        try
        {
            return Uri.UnescapeDataString(text);
        }
        catch (Exception)
        {
            return text;
        }
    }

    private static int Order(TransferEntry a, TransferEntry b)
    {
        if (a.Key.GuestId != b.Key.GuestId) return a.Key.GuestId < b.Key.GuestId ? -1 : 1;
        if (a.Key.JoinEpoch != b.Key.JoinEpoch) return a.Key.JoinEpoch < b.Key.JoinEpoch ? -1 : 1;
        return a.Key.Seq.CompareTo(b.Key.Seq);
    }

    private void Mark(TransferKey key)
    {
        TransferKey old;
        if (applied.TryGetValue(key.GuestId, out old) && (old.JoinEpoch > key.JoinEpoch || (old.JoinEpoch == key.JoinEpoch && old.Seq >= key.Seq))) return;
        applied[key.GuestId] = key;
    }

    private bool Prune()
    {
        List<TransferEntry> done = new List<TransferEntry>();
        foreach (KeyValuePair<TransferKey, TransferEntry> pair in entries)
        {
            if (!pair.Value.Open) done.Add(pair.Value);
        }
        if (done.Count == 0) return false;
        done.Sort(delegate (TransferEntry a, TransferEntry b) { return a.StampSeconds.CompareTo(b.StampSeconds); });
        int drop = done.Count < 64 ? done.Count : 64;
        for (int i = 0; i < drop; i++) entries.Remove(done[i].Key);
        return true;
    }
}
