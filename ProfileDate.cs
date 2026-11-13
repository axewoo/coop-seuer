using System;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;

internal static class ProfileDate
{
    private const string DateKey = "Date";

    public static bool TryRead(string path, out DateTimeOffset when)
    {
        when = default(DateTimeOffset);
        try
        {
            if (!File.Exists(path)) return false;
            using (StreamReader reader = new StreamReader(path, Encoding.UTF8, true))
            {
                return Scan(reader, out when);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TryRead(byte[] data, out DateTimeOffset when)
    {
        when = default(DateTimeOffset);
        if (data == null || data.Length == 0) return false;
        try
        {
            int at = 0;
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) at = 3;
            using (StringReader reader = new StringReader(Encoding.UTF8.GetString(data, at, data.Length - at)))
            {
                return Scan(reader, out when);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool Parse(string text, out DateTimeOffset when)
    {
        when = default(DateTimeOffset);
        if (string.IsNullOrEmpty(text)) return false;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out when);
    }

    private static bool Scan(TextReader reader, out DateTimeOffset when)
    {
        when = default(DateTimeOffset);
        using (JsonTextReader json = new JsonTextReader(reader))
        {
            json.DateParseHandling = DateParseHandling.None;
            if (!json.Read() || json.TokenType != JsonToken.StartObject) return false;
            while (json.Read())
            {
                if (json.TokenType == JsonToken.EndObject && json.Depth == 0) break;
                if (json.TokenType != JsonToken.PropertyName || json.Depth != 1) continue;
                string name = json.Value as string;
                if (!json.Read()) return false;
                if (name != DateKey) continue;
                if (json.TokenType != JsonToken.String) return false;
                return Parse(json.Value as string, out when);
            }
        }
        return false;
    }

    public static bool SelfCheck()
    {
        DateTimeOffset when;
        DateTime roundTrip = new DateTime(2026, 10, 16, 22, 6, 23, DateTimeKind.Utc).AddTicks(1033859);
        bool ok = Parse("2026-10-16T18:06:23.1033859-04:00", out when) && when.UtcDateTime == roundTrip;
        ok = ok && Parse("2026-10-16T22:06:23Z", out when) && when.UtcDateTime == new DateTime(2026, 10, 16, 22, 6, 23, DateTimeKind.Utc);
        ok = ok && !Parse("", out when) && !Parse("not a date", out when);
        ok = ok && TryRead(Document("{\"SaveName\":\"Profile\",\"Date\":\"2026-10-16T18:06:23.1033859-04:00\",\"Data\":[]}"), out when) && when.UtcDateTime == roundTrip;
        ok = ok && TryRead(Document("{\"Data\":[{\"Date\":\"2000-01-01T00:00:00Z\"}],\"Date\":\"2026-10-16T18:06:23-04:00\"}"), out when) && when.Year == 2026;
        ok = ok && TryRead(Bom(Document("{\"Date\":\"2026-10-16T22:06:23Z\"}")), out when) && when.Year == 2026;
        ok = ok && !TryRead(new byte[] { 1, 2, 3 }, out when) && !TryRead((byte[])null, out when) && !TryRead((string)null, out when);
        CoopLog.Log.Info("coop: profile date self check " + (ok ? "passed" : "FAILED"));
        return ok;
    }

    private static byte[] Document(string text)
    {
        return new UTF8Encoding(false).GetBytes(text);
    }

    private static byte[] Bom(byte[] body)
    {
        byte[] preamble = Encoding.UTF8.GetPreamble();
        byte[] all = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, all, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, all, preamble.Length, body.Length);
        return all;
    }
}
