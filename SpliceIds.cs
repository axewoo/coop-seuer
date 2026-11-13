using System;
using System.Collections.Generic;
using System.Globalization;
using FullSerializer;

internal static class SpliceIds
{
    internal const string IdKey = "$id";
    internal const string RefKey = "$ref";

    internal static void Renumber(fsData node, long offset)
    {
        if (node == null) return;
        if (node.IsDictionary)
        {
            Dictionary<string, fsData> dictionary = node.AsDictionary;
            fsData id;
            if (dictionary.TryGetValue(IdKey, out id) && id != null && id.IsString)
            {
                long number;
                if (TryId(id.AsString, out number)) dictionary[IdKey] = new fsData(Shift(number, offset));
            }
            fsData reference;
            if (dictionary.TryGetValue(RefKey, out reference) && reference != null && reference.IsString)
            {
                long number;
                if (TryId(reference.AsString, out number)) dictionary[RefKey] = new fsData(Shift(number, offset));
                return;
            }
            foreach (KeyValuePair<string, fsData> pair in dictionary)
            {
                if (pair.Key == IdKey) continue;
                Renumber(pair.Value, offset);
            }
            return;
        }
        if (node.IsList)
        {
            List<fsData> list = node.AsList;
            for (int i = 0; i < list.Count; i++) Renumber(list[i], offset);
        }
    }

    private static string Shift(long number, long offset)
    {
        return (number + offset).ToString(CultureInfo.InvariantCulture);
    }

    internal static bool TryId(string text, out long number)
    {
        if (!TryDigits(text, out number)) return false;
        if (!string.Equals(text, number.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            number = 0;
            return false;
        }
        return true;
    }

    private static bool TryDigits(string text, out long number)
    {
        number = 0;
        int parsed;
        if (string.IsNullOrEmpty(text) || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)) return false;
        number = parsed;
        return true;
    }

    internal static string BadId(string key, string text, string path)
    {
        string what = text == null ? "a non-string token" : "\"" + text + "\"";
        long number;
        return "the " + key + " " + what + " at " + path + (TryDigits(text, out number) ? " is not a plain whole number" : " is not numeric");
    }

    internal static bool IsId(string text)
    {
        long number;
        return TryId(text, out number);
    }

    internal static bool CollectIds(fsData node, string source, string path, List<long> values, out string error)
    {
        error = null;
        if (node == null) return true;
        if (node.IsDictionary)
        {
            Dictionary<string, fsData> dictionary = node.AsDictionary;
            fsData id;
            if (dictionary.TryGetValue(IdKey, out id))
            {
                long number;
                if (id == null || !id.IsString || !TryId(id.AsString, out number))
                {
                    error = source + ": " + BadId(IdKey, id != null && id.IsString ? id.AsString : null, path);
                    return false;
                }
                values.Add(number);
            }
            fsData reference;
            if (dictionary.TryGetValue(RefKey, out reference))
            {
                long number;
                if (reference == null || !reference.IsString || !TryId(reference.AsString, out number))
                {
                    error = source + ": " + BadId(RefKey, reference != null && reference.IsString ? reference.AsString : null, path);
                    return false;
                }
                values.Add(number);
                return true;
            }
            foreach (KeyValuePair<string, fsData> pair in dictionary)
            {
                if (pair.Key == IdKey) continue;
                if (!CollectIds(pair.Value, source, path + "." + pair.Key, values, out error)) return false;
            }
            return true;
        }
        if (node.IsList)
        {
            List<fsData> list = node.AsList;
            for (int i = 0; i < list.Count; i++)
            {
                if (!CollectIds(list[i], source, path + "[" + i + "]", values, out error)) return false;
            }
        }
        return true;
    }

    internal static long MaxId(List<long> values)
    {
        long max = 0;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] > max) max = values[i];
        }
        return max;
    }

    internal static int Count(List<long> values, long wanted)
    {
        int count = 0;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] == wanted) count++;
        }
        return count;
    }
}
