using System;
using System.Globalization;

internal static class PrisonStoreWire
{
    public const string Channel = "prison";
    public const string Request = "PRQ";
    public const string Released = "PREL";
    public const string Deleted = "PDEL";
    private const string BlobPrefix = "prison-";
    private const int TagLength = 16;
    private const int MaxId = 20;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Id(ulong steamId)
    {
        return steamId.ToString(Inv);
    }

    public static bool Plain(string id)
    {
        if (id == null || id.Length < 1 || id.Length > MaxId) return false;
        for (int i = 0; i < id.Length; i++)
        {
            if (id[i] < '0' || id[i] > '9') return false;
        }
        return true;
    }

    public static string BlobName(string id)
    {
        return BlobPrefix + ContainerWire.Tag(id);
    }

    public static bool IsBlob(string name)
    {
        return name != null && name.Length == BlobPrefix.Length + TagLength && name.StartsWith(BlobPrefix, StringComparison.Ordinal);
    }

    public static bool IsBlobFor(string name, string id)
    {
        return id != null && string.Equals(name, BlobName(id), StringComparison.Ordinal);
    }

    public static string Notice(string kind, string id)
    {
        return kind + "|" + id;
    }

    public static bool TryParse(string payload, out string kind, out ulong steamId)
    {
        kind = null;
        steamId = 0UL;
        if (payload == null) return false;
        string[] f = payload.Split('|');
        if (f.Length != 2) return false;
        if (f[0] != Request && f[0] != Released && f[0] != Deleted) return false;
        if (!Plain(f[1])) return false;
        if (!ulong.TryParse(f[1], NumberStyles.None, Inv, out steamId)) return false;
        kind = f[0];
        return true;
    }
}
