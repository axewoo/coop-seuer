using System;
using System.Collections.Generic;
using System.Text;
using FullSerializer;

internal static class GuestSplice
{
    public static readonly string[] RemoveGuids =
    {
        "PlayerIdentity", "PlayerStats", "Inventory", "PlayerHandItems", "Money", "Crime",
        "NoteController", "HeadSlotController", "FaceSlotController", "WatchSlotController",
        "CharacterSlotController", "DrunkEffect", "MushroomEffect", "OrangeMushroomEffect", "Tutorial"
    };

    private const string DataKey = "Data";
    private const string GuidKey = "GUID";
    private const string HostSource = "the host Globals file";
    private const string OutputSource = "the spliced Globals file";
    private const int MaxReportedIds = 8;
    private const int MaxProfileBytes = 8 * 1024 * 1024;
    private const int MaxProfileEntries = 64;

    public static HashSet<string> RemoveSet()
    {
        return new HashSet<string>(RemoveGuids, StringComparer.Ordinal);
    }

    public static bool Splice(byte[] hostGlobals, ISet<string> removeGuids, out byte[] result, out string error)
    {
        int hostCount;
        int removedCount;
        int profileCount;
        return Splice(hostGlobals, removeGuids, null, out result, out error, out hostCount, out removedCount, out profileCount);
    }

    public static bool Splice(byte[] hostGlobals, ISet<string> removeGuids, byte[] profile, out byte[] result, out string error)
    {
        int hostCount;
        int removedCount;
        int profileCount;
        return Splice(hostGlobals, removeGuids, profile, out result, out error, out hostCount, out removedCount, out profileCount);
    }

    public static bool Splice(byte[] hostGlobals, ISet<string> removeGuids, byte[] profile, out byte[] result, out string error, out int hostCount, out int removedCount, out int profileCount)
    {
        hostCount = 0;
        removedCount = 0;
        profileCount = 0;
        result = null;
        error = null;
        if (hostGlobals == null || hostGlobals.Length == 0)
        {
            error = "the host Globals file is empty";
            return false;
        }
        if (removeGuids == null) removeGuids = RemoveSet();
        string text = Decode(hostGlobals);
        fsData root;
        try
        {
            root = fsJsonParser.Parse(text);
        }
        catch (Exception e)
        {
            error = "the host Globals file does not parse: " + e.Message;
            return false;
        }
        if (root == null || !root.IsDictionary)
        {
            error = "the host Globals file is not an object";
            return false;
        }
        fsData data;
        if (!root.AsDictionary.TryGetValue(DataKey, out data) || data == null || !data.IsList)
        {
            error = "the host Globals file has no Data list";
            return false;
        }
        List<fsData> entries = data.AsList;
        hostCount = entries.Count;
        Dictionary<string, int> owner = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < entries.Count; i++) IndexIds(entries[i], i, owner);
        HashSet<int> removed = new HashSet<int>();
        for (int i = 0; i < entries.Count; i++)
        {
            string guid = EntryGuid(entries[i]);
            if (guid != null && removeGuids.Contains(guid)) removed.Add(i);
        }
        removedCount = removed.Count;
        List<string> dangling = new List<string>();
        for (int i = 0; i < entries.Count; i++)
        {
            if (removed.Contains(i)) continue;
            CollectDangling(entries[i], owner, removed, dangling);
        }
        if (dangling.Count > 0)
        {
            error = "a kept entry references a removed entry: " + Join(dangling, MaxReportedIds) + " (" + dangling.Count + " references)";
            return false;
        }
        List<fsData> kept = new List<fsData>(entries.Count - removed.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            if (!removed.Contains(i)) kept.Add(entries[i]);
        }
        if (profile != null)
        {
            profileCount = ProfileEntryCount(profile);
            List<long> hostIds = new List<long>();
            if (!SpliceIds.CollectIds(root, HostSource, "$", hostIds, out error)) return false;
            if (!Insert(kept, SpliceIds.MaxId(hostIds), profile, out error)) return false;
        }
        Dictionary<string, fsData> output = root.AsDictionary;
        output[DataKey] = new fsData(kept);
        string json = fsJsonPrinter.CompressedJson(root);
        try
        {
            fsData check = fsJsonParser.Parse(json);
            if (check == null || !check.IsDictionary || !check.AsDictionary.ContainsKey(DataKey) || !check.AsDictionary[DataKey].IsList)
            {
                error = "the spliced Globals file does not read back";
                return false;
            }
            if (check.AsDictionary[DataKey].AsList.Count != kept.Count)
            {
                error = "the spliced Globals file lost entries: " + check.AsDictionary[DataKey].AsList.Count + " of " + kept.Count;
                return false;
            }
            List<long> checkIds = new List<long>();
            if (!SpliceIds.CollectIds(check, OutputSource, "$", checkIds, out error)) return false;
        }
        catch (Exception e)
        {
            error = "the spliced Globals file does not parse: " + e.Message;
            return false;
        }
        result = Encode(json);
        return true;
    }

    private static bool Insert(List<fsData> kept, long hostCeiling, byte[] profile, out string error)
    {
        error = null;
        if (profile.Length == 0 || profile.Length > MaxProfileBytes)
        {
            error = "the profile is " + profile.Length + " bytes";
            return false;
        }
        fsData root;
        try
        {
            root = fsJsonParser.Parse(Decode(profile));
        }
        catch (Exception e)
        {
            error = "the profile does not parse: " + e.Message;
            return false;
        }
        fsData data;
        if (root == null || !root.IsDictionary || !root.AsDictionary.TryGetValue(DataKey, out data) || data == null || !data.IsList)
        {
            error = "the profile is not a save document";
            return false;
        }
        List<fsData> entries = data.AsList;
        if (entries.Count == 0 || entries.Count > MaxProfileEntries)
        {
            error = "the profile holds " + entries.Count + " entries";
            return false;
        }
        ISet<string> players = RemoveSet();
        HashSet<string> guids = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
        List<string> refs = new List<string>();
        List<string> refPaths = new List<string>();
        for (int i = 0; i < entries.Count; i++)
        {
            string guid = EntryGuid(entries[i]);
            if (guid == null || !players.Contains(guid))
            {
                error = "the profile entry " + i + " is not a player entry (" + (guid == null ? "no guid" : guid) + ")";
                return false;
            }
            if (!guids.Add(guid))
            {
                error = "the profile holds " + guid + " twice";
                return false;
            }
            if (!Walk(entries[i], "the profile", "Data[" + i + "]", ids, refs, refPaths, out error)) return false;
        }
        for (int i = 0; i < refs.Count; i++)
        {
            if (!ids.Contains(refs[i]))
            {
                error = "the profile ref " + refs[i] + " at " + refPaths[i] + " points outside the profile";
                return false;
            }
        }
        long offset = 0;
        if (ids.Count > 0)
        {
            long low = long.MaxValue;
            long high = 0;
            foreach (string text in ids)
            {
                long id;
                if (!SpliceIds.TryId(text, out id))
                {
                    error = "the profile: " + SpliceIds.BadId(SpliceIds.IdKey, text, "Data");
                    return false;
                }
                if (id < low) low = id;
                if (id > high) high = id;
            }
            offset = hostCeiling + 1 - low;
            if (high + offset > int.MaxValue)
            {
                error = "the profile ids do not fit an int once renumbered above " + hostCeiling;
                return false;
            }
        }
        for (int i = 0; i < kept.Count; i++)
        {
            string guid = EntryGuid(kept[i]);
            if (guid != null && guids.Contains(guid))
            {
                error = "the host already keeps an entry for " + guid;
                return false;
            }
        }
        if (offset != 0)
        {
            for (int i = 0; i < entries.Count; i++) SpliceIds.Renumber(entries[i], offset);
        }
        for (int i = 0; i < entries.Count; i++) kept.Add(entries[i]);
        return true;
    }

    private static bool Walk(fsData node, string source, string path, HashSet<string> ids, List<string> refs, List<string> refPaths, out string error)
    {
        error = null;
        if (node == null) return true;
        if (node.IsDictionary)
        {
            Dictionary<string, fsData> dictionary = node.AsDictionary;
            fsData id;
            if (dictionary.TryGetValue(SpliceIds.IdKey, out id))
            {
                if (id == null || !id.IsString || !SpliceIds.IsId(id.AsString))
                {
                    error = source + ": " + SpliceIds.BadId(SpliceIds.IdKey, id != null && id.IsString ? id.AsString : null, path);
                    return false;
                }
                if (!ids.Add(id.AsString))
                {
                    error = "a duplicate " + SpliceIds.IdKey + " " + id.AsString + " at " + path;
                    return false;
                }
            }
            fsData reference;
            if (dictionary.TryGetValue(SpliceIds.RefKey, out reference))
            {
                if (reference == null || !reference.IsString || !SpliceIds.IsId(reference.AsString))
                {
                    error = source + ": " + SpliceIds.BadId(SpliceIds.RefKey, reference != null && reference.IsString ? reference.AsString : null, path);
                    return false;
                }
                refs.Add(reference.AsString);
                refPaths.Add(path);
                return true;
            }
            foreach (KeyValuePair<string, fsData> pair in dictionary)
            {
                if (pair.Key == SpliceIds.IdKey) continue;
                if (!Walk(pair.Value, source, path + "." + pair.Key, ids, refs, refPaths, out error)) return false;
            }
            return true;
        }
        if (node.IsList)
        {
            List<fsData> list = node.AsList;
            for (int i = 0; i < list.Count; i++)
            {
                if (!Walk(list[i], source, path + "[" + i + "]", ids, refs, refPaths, out error)) return false;
            }
        }
        return true;
    }

    public static bool IdValues(byte[] document, out List<long> values, out string error)
    {
        values = null;
        fsData root;
        if (!TryParse(document, out root, out error)) return false;
        List<long> found = new List<long>();
        if (!SpliceIds.CollectIds(root, OutputSource, "$", found, out error)) return false;
        values = found;
        return true;
    }

    public static bool IdPairs(byte[] document, out string error)
    {
        fsData root;
        if (!TryParse(document, out root, out error)) return false;
        fsData data;
        if (root == null || !root.IsDictionary || !root.AsDictionary.TryGetValue(DataKey, out data) || data == null || !data.IsList)
        {
            error = "the document is not a save document";
            return false;
        }
        HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
        List<string> refs = new List<string>();
        List<string> refPaths = new List<string>();
        List<fsData> entries = data.AsList;
        for (int i = 0; i < entries.Count; i++)
        {
            if (!Walk(entries[i], OutputSource, "Data[" + i + "]", ids, refs, refPaths, out error)) return false;
        }
        for (int i = 0; i < refs.Count; i++)
        {
            if (!ids.Contains(refs[i]))
            {
                error = OutputSource + " ref " + refs[i] + " at " + refPaths[i] + " has no matching " + SpliceIds.IdKey;
                return false;
            }
        }
        return true;
    }

    private static bool TryParse(byte[] document, out fsData root, out string error)
    {
        root = null;
        error = null;
        if (document == null || document.Length == 0)
        {
            error = "the document is empty";
            return false;
        }
        try
        {
            root = fsJsonParser.Parse(Decode(document));
            return true;
        }
        catch (Exception e)
        {
            error = "the document does not parse: " + e.Message;
            return false;
        }
    }

    public static int ProfileEntryCount(byte[] profile)
    {
        if (profile == null || profile.Length == 0) return -1;
        try
        {
            fsData root = fsJsonParser.Parse(Decode(profile));
            if (root == null || !root.IsDictionary) return -1;
            fsData data;
            if (!root.AsDictionary.TryGetValue(DataKey, out data) || data == null || !data.IsList) return -1;
            return data.AsList.Count;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    public static bool Guids(byte[] document, out List<string> guids, out string error)
    {
        guids = null;
        fsData root;
        if (!TryParse(document, out root, out error)) return false;
        fsData data;
        if (root == null || !root.IsDictionary || !root.AsDictionary.TryGetValue(DataKey, out data) || data == null || !data.IsList)
        {
            error = "the document is not a save document";
            return false;
        }
        List<fsData> entries = data.AsList;
        List<string> found = new List<string>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            string guid = EntryGuid(entries[i]);
            if (guid == null)
            {
                error = "the document entry " + i + " has no guid";
                return false;
            }
            found.Add(guid);
        }
        guids = found;
        return true;
    }

    public static bool IsProfile(byte[] profile, out string error)
    {
        error = null;
        if (profile == null || profile.Length == 0 || profile.Length > MaxProfileBytes)
        {
            error = "the profile is " + (profile == null ? 0 : profile.Length) + " bytes";
            return false;
        }
        fsData root;
        try
        {
            root = fsJsonParser.Parse(Decode(profile));
        }
        catch (Exception e)
        {
            error = "the profile does not parse: " + e.Message;
            return false;
        }
        fsData data;
        if (root == null || !root.IsDictionary || !root.AsDictionary.TryGetValue(DataKey, out data) || data == null || !data.IsList)
        {
            error = "the profile is not a save document";
            return false;
        }
        List<fsData> entries = data.AsList;
        if (entries.Count == 0 || entries.Count > MaxProfileEntries)
        {
            error = "the profile holds " + entries.Count + " entries";
            return false;
        }
        ISet<string> players = RemoveSet();
        HashSet<string> guids = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
        List<string> refs = new List<string>();
        List<string> refPaths = new List<string>();
        for (int i = 0; i < entries.Count; i++)
        {
            string guid = EntryGuid(entries[i]);
            if (guid == null || !players.Contains(guid))
            {
                error = "the profile entry " + i + " is not a player entry (" + (guid == null ? "no guid" : guid) + ")";
                return false;
            }
            if (!guids.Add(guid))
            {
                error = "the profile holds " + guid + " twice";
                return false;
            }
            if (!Walk(entries[i], "the profile", "Data[" + i + "]", ids, refs, refPaths, out error)) return false;
        }
        for (int i = 0; i < refs.Count; i++)
        {
            if (!ids.Contains(refs[i]))
            {
                error = "the profile ref " + refs[i] + " at " + refPaths[i] + " points outside the profile";
                return false;
            }
        }
        return true;
    }

    public static bool SelfCheck()
    {
        string fixture = "{\"SaveName\":\"Autosave\",\"Data\":[" +
            "{\"GUID\":\"Inventory\",\"Data\":{\"slot\":{\"$id\":\"1\"},\"count\":2}}," +
            "{\"GUID\":\"TaskItemsManager\",\"Data\":{\"keys\":[{\"$ref\":\"1\"}],\"own\":{\"$id\":\"2\"}}}," +
            "{\"GUID\":\"TimeOfDayAzure\",\"Data\":{\"hour\":9}}]}";
        byte[] input = Encode(fixture);
        ISet<string> removeInventory = new HashSet<string>(new string[] { "Inventory" }, StringComparer.Ordinal);
        ISet<string> removeTask = new HashSet<string>(new string[] { "TaskItemsManager" }, StringComparer.Ordinal);
        ISet<string> removeNone = new HashSet<string>(StringComparer.Ordinal);
        ISet<string> removeBoth = new HashSet<string>(new string[] { "Inventory", "TaskItemsManager" }, StringComparer.Ordinal);
        byte[] result;
        string error;
        List<long> values;
        bool ok = !Splice(input, removeInventory, out result, out error) && error != null;
        ok = ok && Splice(input, removeTask, out result, out error) && error == null && EntryCount(result) == 2;
        ok = ok && Splice(input, removeNone, out result, out error) && EntryCount(result) == 3;
        ok = ok && Splice(input, removeBoth, out result, out error) && EntryCount(result) == 1;
        ok = ok && !Splice(new byte[] { 1, 2, 3 }, removeNone, out result, out error) && error != null;
        ok = ok && !Splice(Encode("{\"Data\":5}"), removeNone, out result, out error) && error != null;
        byte[] profile = Encode(ProfileFixture());
        byte[] host = Encode(HostProfileFixture());
        ISet<string> removePlayers = new HashSet<string>(new string[] { "Inventory", "PlayerHandItems" }, StringComparer.Ordinal);
        ISet<string> removeMoney = new HashSet<string>(new string[] { "Money" }, StringComparer.Ordinal);
        ok = ok && Splice(host, removePlayers, profile, out result, out error) && error == null && ProfileEntryCount(profile) == 2;
        ok = ok && EntryCount(result) == 4 && IdValues(result, out values, out error) && error == null && values.Count == 2 && SpliceIds.Count(values, 12) == 2 && SpliceIds.MaxId(values) == 12;        ok = ok && IdPairs(result, out error) && error == null;
        ok = ok && Splice(host, removePlayers, profile, out result, out error) && EntryCount(result) == 4;
        ok = ok && Splice(Encode(HighHostFixture()), removePlayers, profile, out result, out error) && error == null && EntryCount(result) == 4 && IdValues(result, out values, out error) && values.Count == 4 && SpliceIds.Count(values, 9000) == 2 && SpliceIds.MaxId(values) == 9001;
        ok = ok && IdPairs(result, out error) && error == null;
        ok = ok && !Splice(host, removePlayers, Encode(BadIdFixture()), out result, out error) && error != null && error.Contains("is not numeric");
        ok = ok && !Splice(host, removePlayers, Encode(BadRefFixture()), out result, out error) && error != null && error.Contains("is not numeric");
        ok = ok && !Splice(Encode(BadHostFixture()), removePlayers, profile, out result, out error) && error != null && error.Contains("is not numeric");
        ok = ok && !Splice(host, removePlayers, Encode(DuplicateIdFixture()), out result, out error) && error != null && error.Contains("duplicate $id");
        ok = ok && !Splice(host, removePlayers, Encode(LeadingZeroIdFixture()), out result, out error) && error != null && error.Contains("is not a plain whole number");
        ok = ok && !IsProfile(Encode(LeadingZeroIdFixture()), out error) && error != null && error.Contains("is not a plain whole number");
        ok = ok && !Splice(host, removeMoney, profile, out result, out error) && error != null && error.Contains("already keeps");
        ok = ok && !Splice(host, removePlayers, Encode(EscapeFixture()), out result, out error) && error != null && error.Contains("outside the profile");
        ok = ok && !Splice(host, removePlayers, Encode(WorldFixture()), out result, out error) && error != null && error.Contains("not a player entry");
        ok = ok && !Splice(host, removePlayers, Encode("{\"Data\":5}"), out result, out error) && error != null;
        ok = ok && !Splice(host, removePlayers, new byte[] { 1 }, out result, out error) && error != null;
        ok = ok && IsProfile(profile, out error) && error == null;
        ok = ok && !IsProfile(Encode(EscapeFixture()), out error) && error != null && error.Contains("outside the profile");
        ok = ok && !IsProfile(Encode(WorldFixture()), out error) && error != null && error.Contains("not a player entry");
        ok = ok && !IsProfile(new byte[] { 1 }, out error) && error != null;
        List<string> guids;
        ok = ok && Guids(profile, out guids, out error) && error == null && guids.Count == 2 && guids[0] == "Inventory" && guids[1] == "PlayerHandItems";
        ok = ok && Guids(host, out guids, out error) && error == null && guids.Count == 4;
        ok = ok && !Guids(Encode("{\"Data\":5}"), out guids, out error) && error != null;
        ok = ok && !Guids(Encode("{\"Data\":[{\"Data\":1}]}"), out guids, out error) && error != null;
        ok = ok && !Guids(new byte[] { 1 }, out guids, out error) && error != null;
        ok = ok && !Guids(null, out guids, out error) && error != null;
        int hostCount;
        int removedCount;
        int profileCount;
        ok = ok && Splice(host, removePlayers, profile, out result, out error, out hostCount, out removedCount, out profileCount) && hostCount == 4 && removedCount == 2 && profileCount == 2 && EntryCount(result) == 4;
        CoopLog.Log.Info("coop: guest splice self check " + (ok ? "passed" : "FAILED"));
        return ok;
    }

    private static string ProfileFixture()
    {
        return "{\"SaveName\":\"Profile\",\"Data\":[" +
            "{\"GUID\":\"Inventory\",\"Data\":{\"slots\":[{\"$id\":\"7001\"}],\"count\":3}}," +
            "{\"GUID\":\"PlayerHandItems\",\"Data\":{\"hand\":{\"$ref\":\"7001\"}}}]}";
    }

    private static string HostProfileFixture()
    {
        return "{\"SaveName\":\"Autosave\",\"Data\":[" +
            "{\"GUID\":\"Inventory\",\"Data\":{\"slots\":[{\"$id\":\"11\"}]}}," +
            "{\"GUID\":\"PlayerHandItems\",\"Data\":{\"hand\":{\"$ref\":\"11\"}}}," +
            "{\"GUID\":\"Money\",\"Data\":{\"amount\":42}}," +
            "{\"GUID\":\"TimeOfDayAzure\",\"Data\":{\"hour\":9}}]}";
    }

    private static string HighHostFixture()
    {
        return "{\"SaveName\":\"Autosave\",\"Data\":[" +
            "{\"GUID\":\"TaskItemsManager\",\"Data\":{\"keys\":[{\"$id\":\"9000\"}]}}," +
            "{\"GUID\":\"TimeOfDayAzure\",\"Data\":{\"hour\":9,\"own\":{\"$ref\":\"9000\"}}}]}";
    }

    private static string BadIdFixture()
    {
        return "{\"SaveName\":\"Profile\",\"Data\":[" +
            "{\"GUID\":\"Inventory\",\"Data\":{\"slots\":[{\"$id\":\"p7001_\"}]}}," +
            "{\"GUID\":\"PlayerHandItems\",\"Data\":{\"hand\":{\"$ref\":\"7001\"}}}]}";
    }

    private static string BadRefFixture()
    {
        return "{\"SaveName\":\"Profile\",\"Data\":[" +
            "{\"GUID\":\"Inventory\",\"Data\":{\"slots\":[{\"$id\":\"7001\"}]}}," +
            "{\"GUID\":\"PlayerHandItems\",\"Data\":{\"hand\":{\"$ref\":\"p7001_\"}}}]}";
    }

    private static string BadHostFixture()
    {
        return "{\"SaveName\":\"Autosave\",\"Data\":[" +
            "{\"GUID\":\"TaskItemsManager\",\"Data\":{\"keys\":[{\"$id\":\"p11_\"}]}}," +
            "{\"GUID\":\"TimeOfDayAzure\",\"Data\":{\"hour\":9}}]}";
    }

    private static string DuplicateIdFixture()
    {
        return "{\"SaveName\":\"Profile\",\"Data\":[" +
            "{\"GUID\":\"Inventory\",\"Data\":{\"slots\":[{\"$id\":\"7001\"}]}}," +
            "{\"GUID\":\"PlayerHandItems\",\"Data\":{\"hand\":[{\"$id\":\"7001\"}]}}]}";
    }

    private static string LeadingZeroIdFixture()
    {
        return "{\"SaveName\":\"Profile\",\"Data\":[" +
            "{\"GUID\":\"Inventory\",\"Data\":{\"slots\":[{\"$id\":\"07001\"}]}}," +
            "{\"GUID\":\"PlayerHandItems\",\"Data\":{\"hand\":{\"$ref\":\"7001\"}}}]}";
    }

    private static string EscapeFixture()
    {
        return "{\"SaveName\":\"Profile\",\"Data\":[" +
            "{\"GUID\":\"Inventory\",\"Data\":{\"slots\":[{\"$ref\":\"3085\"}]}}]}";
    }

    private static string WorldFixture()
    {
        return "{\"SaveName\":\"Profile\",\"Data\":[" +
            "{\"GUID\":\"TimeOfDayAzure\",\"Data\":{\"hour\":9}}]}";
    }

    public static int EntryCount(byte[] globals)
    {
        string text = Decode(globals);
        fsData root = fsJsonParser.Parse(text);
        if (root == null || !root.IsDictionary) return -1;
        fsData data;
        if (!root.AsDictionary.TryGetValue(DataKey, out data) || data == null || !data.IsList) return -1;
        return data.AsList.Count;
    }

    private static string EntryGuid(fsData entry)
    {
        if (entry == null || !entry.IsDictionary) return null;
        fsData guid;
        if (!entry.AsDictionary.TryGetValue(GuidKey, out guid) || guid == null || !guid.IsString) return null;
        return guid.AsString;
    }

    private static void IndexIds(fsData node, int entry, Dictionary<string, int> owner)
    {
        if (node == null) return;
        if (node.IsDictionary)
        {
            Dictionary<string, fsData> dictionary = node.AsDictionary;
            fsData id;
            if (dictionary.TryGetValue(SpliceIds.IdKey, out id) && id != null && id.IsString && !string.IsNullOrEmpty(id.AsString) && !owner.ContainsKey(id.AsString))
            {
                owner.Add(id.AsString, entry);
            }
            foreach (KeyValuePair<string, fsData> pair in dictionary)
            {
                if (pair.Key == SpliceIds.RefKey) continue;
                IndexIds(pair.Value, entry, owner);
            }
            return;
        }
        if (node.IsList)
        {
            List<fsData> list = node.AsList;
            for (int i = 0; i < list.Count; i++) IndexIds(list[i], entry, owner);
        }
    }

    private static void CollectDangling(fsData node, Dictionary<string, int> owner, HashSet<int> removed, List<string> dangling)
    {
        if (node == null) return;
        if (node.IsDictionary)
        {
            Dictionary<string, fsData> dictionary = node.AsDictionary;
            string reference = ReferenceId(dictionary);
            if (reference != null)
            {
                int target;
                if (owner.TryGetValue(reference, out target) && removed.Contains(target)) dangling.Add(reference);
                return;
            }
            foreach (KeyValuePair<string, fsData> pair in dictionary) CollectDangling(pair.Value, owner, removed, dangling);
            return;
        }
        if (node.IsList)
        {
            List<fsData> list = node.AsList;
            for (int i = 0; i < list.Count; i++) CollectDangling(list[i], owner, removed, dangling);
        }
    }

    private static string ReferenceId(Dictionary<string, fsData> dictionary)
    {
        if (dictionary.Count != 1) return null;
        fsData value;
        if (!dictionary.TryGetValue(SpliceIds.RefKey, out value) || value == null || !value.IsString) return null;
        return value.AsString;
    }

    private static string Join(List<string> ids, int limit)
    {
        StringBuilder text = new StringBuilder();
        for (int i = 0; i < ids.Count && i < limit; i++)
        {
            if (text.Length > 0) text.Append(", ");
            text.Append(ids[i]);
        }
        return text.ToString();
    }

    private static string Decode(byte[] bytes)
    {
        int at = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) at = 3;
        return Encoding.UTF8.GetString(bytes, at, bytes.Length - at);
    }

    private static byte[] Encode(string text)
    {
        byte[] body = Encoding.UTF8.GetBytes(text);
        byte[] preamble = Encoding.UTF8.GetPreamble();
        byte[] all = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, all, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, all, preamble.Length, body.Length);
        return all;
    }
}
