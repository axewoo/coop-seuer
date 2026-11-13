using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using OS.Items;

internal static class ItemMeta
{
    private const int MaxText = 600;
    private const int MaxString = 64;
    private const int MaxName = 80;
    private const int MaxEntries = 8;
    private const float MaxNumber = 10000000f;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly char[] Sep = { ';', ',', '=' };
    private static readonly Dictionary<Type, FieldInfo[]> fields = new Dictionary<Type, FieldInfo[]>();
    private static readonly Dictionary<string, Type> known = new Dictionary<string, Type>
    {
        { typeof(ItemPerishabledata).FullName, typeof(ItemPerishabledata) },
        { typeof(ItemAnimalData).FullName, typeof(ItemAnimalData) },
        { typeof(ItemAmmodata).FullName, typeof(ItemAmmodata) },
        { typeof(ItemLiquidData).FullName, typeof(ItemLiquidData) },
        { typeof(ItemVariantData).FullName, typeof(ItemVariantData) },
        { typeof(ItemOwnerPermission).FullName, typeof(ItemOwnerPermission) },
        { typeof(ItemQualityData).FullName, typeof(ItemQualityData) },
        { typeof(ItemInvoiceData).FullName, typeof(ItemInvoiceData) },
        { typeof(ItemMailableData).FullName, typeof(ItemMailableData) },
        { typeof(ItemTaskItemData).FullName, typeof(ItemTaskItemData) },
        { typeof(ItemStolenData).FullName, typeof(ItemStolenData) },
        { typeof(ItemReceiptData).FullName, typeof(ItemReceiptData) },
        { typeof(ItemWritableData).FullName, typeof(ItemWritableData) },
        { typeof(ItemTextData).FullName, typeof(ItemTextData) },
        { typeof(ItemOSCoinWalletData).FullName, typeof(ItemOSCoinWalletData) },
        { typeof(ItemPackageData).FullName, typeof(ItemPackageData) },
        { typeof(ItemCertificateData).FullName, typeof(ItemCertificateData) },
        { typeof(ItemConnectivityData).FullName, typeof(ItemConnectivityData) },
        { typeof(ItemEmptyLiquidContainerData).FullName, typeof(ItemEmptyLiquidContainerData) },
        { typeof(ItemFueldata).FullName, typeof(ItemFueldata) },
        { typeof(ItemFurnitureData).FullName, typeof(ItemFurnitureData) },
        { typeof(ItemGrowdata).FullName, typeof(ItemGrowdata) },
        { typeof(ItemGroupData).FullName, typeof(ItemGroupData) },
        { typeof(ItemSkinData).FullName, typeof(ItemSkinData) },
        { typeof(ItemEmptyPackagerData).FullName, typeof(ItemEmptyPackagerData) },
        { typeof(ItemWallpaperData).FullName, typeof(ItemWallpaperData) },
        { typeof(ItemScraperData).FullName, typeof(ItemScraperData) },
        { typeof(ItemShopData).FullName, typeof(ItemShopData) },
        { typeof(ItemTrapData).FullName, typeof(ItemTrapData) }
    };
    private static readonly HashSet<string> contentFields = new HashSet<string>
    {
        typeof(ItemPackageData).FullName + ".containedItems",
        typeof(ItemReceiptData).FullName + ".itemsList"
    };
    private static readonly Dictionary<Type, object> blanks = new Dictionary<Type, object>();
    private static readonly object NoBlank = new object();
    private static readonly HashSet<string> noted = new HashSet<string>();

    public static bool TryEncode(object[] meta, out string text, out string refused)
    {
        text = "";
        refused = null;
        if (meta == null || meta.Length == 0) return true;
        try
        {
            List<string> parts = new List<string>();
            for (int i = 0; i < meta.Length; i++)
            {
                object entry = meta[i];
                if (entry == null) continue;
                if (entry is ItemOwnerPermission)
                {
                    Note("OS.Items.ItemOwnerPermission");
                    continue;
                }
                Type type = entry.GetType();
                if (!Known(type))
                {
                    refused = Cut(type.FullName);
                    return false;
                }
                string part = EncodeEntry(type, entry);
                if (part == null)
                {
                    Note(type.FullName);
                    continue;
                }
                parts.Add(part);
                if (parts.Count > MaxEntries)
                {
                    refused = "meta over " + MaxEntries + " entries";
                    return false;
                }
            }
            text = string.Join(";", parts.ToArray());
            if (text.Length > MaxText)
            {
                refused = "meta over " + MaxText + " characters";
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemMeta.Encode", e);
            refused = "meta could not be read";
            return false;
        }
    }

    public static bool LosesData(object[] meta)
    {
        if (meta == null) return false;
        for (int i = 0; i < meta.Length; i++)
        {
            if (meta[i] == null) continue;
            Type type = meta[i].GetType();
            FieldInfo[] members = FieldsOf(type);
            for (int j = 0; j < members.Length; j++)
            {
                ICollection collection = members[j].GetValue(meta[i]) as ICollection;
                if (collection == null || collection.Count == 0) continue;
                if (contentFields.Contains(type.FullName + "." + members[j].Name)) return true;
            }
        }
        return false;
    }

    public static bool TryDecode(string text, out object[] meta, out string refused)
    {
        meta = null;
        refused = null;
        if (text == null || text.Length == 0)
        {
            meta = new object[0];
            return true;
        }
        try
        {
            if (text.Length > MaxText || HasBad(text))
            {
                refused = "meta text";
                return false;
            }
            string[] entries = text.Split(';');
            if (entries.Length > MaxEntries)
            {
                refused = "meta over " + MaxEntries + " entries";
                return false;
            }
            List<object> list = new List<object>();
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Length == 0) continue;
                object entry;
                if (!Entry(entries[i], out entry, out refused)) return false;
                if (entry != null) list.Add(entry);
            }
            meta = list.ToArray();
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemMeta.Decode", e);
            refused = "meta could not be applied";
            return false;
        }
    }

    public static object[] Merge(Item item, int stackSize, object[] decoded)
    {
        object[] defaults = null;
        if (item != null)
        {
            try
            {
                defaults = item.CloneMeta(false, stackSize);
            }
            catch (Exception e)
            {
                CoopLog.Once("ItemMeta.Merge", e);
            }
        }
        if (decoded == null || decoded.Length == 0) return defaults == null ? new object[0] : defaults;
        if (defaults == null || defaults.Length == 0) return decoded;
        List<object> merged = new List<object>(defaults);
        bool[] used = new bool[merged.Count];
        for (int i = 0; i < decoded.Length; i++)
        {
            object entry = decoded[i];
            if (entry == null) continue;
            Type type = entry.GetType();
            bool replaced = false;
            for (int j = 0; j < used.Length; j++)
            {
                object current = merged[j];
                if (used[j] || current == null || current.GetType() != type) continue;
                KeepUnsent(current, entry);
                merged[j] = entry;
                used[j] = true;
                replaced = true;
                break;
            }
            if (!replaced) merged.Add(entry);
        }
        return merged.ToArray();
    }

    private static void KeepUnsent(object from, object to)
    {
        FieldInfo[] fields = to.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < fields.Length; i++)
        {
            FieldInfo field = fields[i];
            if (field.FieldType.IsValueType || field.FieldType == typeof(string) || field.IsInitOnly) continue;
            try
            {
                if (field.GetValue(to) == null) field.SetValue(to, field.GetValue(from));
            }
            catch (Exception e)
            {
                CoopLog.Once("ItemMeta.KeepUnsent", e);
            }
        }
    }

    private static bool Entry(string text, out object entry, out string refused)
    {
        entry = null;
        refused = null;
        string[] parts = text.Split(',');
        Type type = TypeOf(parts[0]);
        if (type == null || !Known(type))
        {
            refused = Cut(parts[0]);
            return false;
        }
        if (type == typeof(ItemOwnerPermission))
        {
            refused = Cut(type.FullName);
            return false;
        }
        object created = Activator.CreateInstance(type);
        int filled = 0;
        for (int i = 1; i < parts.Length; i++)
        {
            int at = parts[i].IndexOf('=');
            if (at <= 0) continue;
            string name = parts[i].Substring(0, at);
            string value = parts[i].Substring(at + 1);
            FieldInfo field = FieldOf(type, name);
            if (field == null)
            {
                Note(type.FullName + "." + name);
                continue;
            }
            bool applied = Set(field, created, value);
            if (field.FieldType == typeof(ItemReference))
            {
                ItemReference reference = (ItemReference)field.GetValue(created);
                if (reference == null || reference.ID <= 0)
                {
                    refused = Cut(type.FullName + "." + name);
                    return false;
                }
            }
            if (applied) filled++;
        }
        if (filled == 0) return true;
        entry = created;
        return true;
    }

    private static string EncodeEntry(Type type, object entry)
    {
        FieldInfo[] members = FieldsOf(type);
        List<string> parts = new List<string>();
        parts.Add(type.FullName);
        for (int i = 0; i < members.Length; i++)
        {
            FieldInfo field = members[i];
            string text;
            if (!Value(field.FieldType, field.GetValue(entry), out text))
            {
                Note(type.FullName + "." + field.Name);
                continue;
            }
            if (text == null) continue;
            parts.Add(field.Name + "=" + text);
        }
        if (parts.Count == 1) return null;
        return string.Join(",", parts.ToArray());
    }

    private static bool Set(FieldInfo field, object entry, string value)
    {
        try
        {
            Type type = field.FieldType;
            if (type == typeof(bool))
            {
                if (value == "1")
                {
                    field.SetValue(entry, true);
                    return true;
                }
                if (value == "0")
                {
                    field.SetValue(entry, false);
                    return true;
                }
                return false;
            }
            if (type == typeof(string))
            {
                if (value.Length > MaxString || value.IndexOfAny(Sep) >= 0 || HasBad(value)) return false;
                field.SetValue(entry, value);
                return true;
            }
            if (type == typeof(ItemReference))
            {
                int id;
                if (!int.TryParse(value, NumberStyles.Integer, Inv, out id) || id <= 0) return false;
                field.SetValue(entry, new ItemReference(id));
                return true;
            }
            if (type.IsEnum)
            {
                long number;
                if (!long.TryParse(value, NumberStyles.Integer, Inv, out number)) return false;
                field.SetValue(entry, Enum.ToObject(type, number));
                return true;
            }
            if (type == typeof(float))
            {
                float number;
                if (!float.TryParse(value, NumberStyles.Float, Inv, out number) || float.IsNaN(number) || Math.Abs(number) > MaxNumber) return false;
                field.SetValue(entry, number);
                return true;
            }
            if (type == typeof(double))
            {
                double number;
                if (!double.TryParse(value, NumberStyles.Float, Inv, out number) || double.IsNaN(number) || Math.Abs(number) > MaxNumber) return false;
                field.SetValue(entry, number);
                return true;
            }
            if (type == typeof(int))
            {
                int number;
                if (!int.TryParse(value, NumberStyles.Integer, Inv, out number)) return false;
                field.SetValue(entry, number);
                return true;
            }
            if (type == typeof(long))
            {
                long number;
                if (!long.TryParse(value, NumberStyles.Integer, Inv, out number)) return false;
                field.SetValue(entry, number);
                return true;
            }
            if (type == typeof(short))
            {
                short number;
                if (!short.TryParse(value, NumberStyles.Integer, Inv, out number)) return false;
                field.SetValue(entry, number);
                return true;
            }
            if (type == typeof(byte))
            {
                byte number;
                if (!byte.TryParse(value, NumberStyles.Integer, Inv, out number)) return false;
                field.SetValue(entry, number);
                return true;
            }
            if (type == typeof(uint))
            {
                uint number;
                if (!uint.TryParse(value, NumberStyles.Integer, Inv, out number)) return false;
                field.SetValue(entry, number);
                return true;
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemMeta.Set", e);
        }
        return false;
    }

    private static bool Value(Type type, object value, out string text)
    {
        text = null;
        if (value == null) return true;
        if (type == typeof(bool))
        {
            text = (bool)value ? "1" : "0";
            return true;
        }
        if (type == typeof(string))
        {
            string plain = (string)value;
            if (plain.Length == 0) return true;
            if (plain.Length > MaxString || plain.IndexOfAny(Sep) >= 0 || HasBad(plain)) return false;
            text = plain;
            return true;
        }
        if (type == typeof(ItemReference))
        {
            ItemReference reference = (ItemReference)value;
            if (reference == null || reference.ID < 1) return true;
            text = reference.ID.ToString(Inv);
            return true;
        }
        if (type.IsEnum)
        {
            text = Convert.ToInt64(value, Inv).ToString(Inv);
            return true;
        }
        if (type == typeof(float))
        {
            float number = (float)value;
            if (float.IsNaN(number) || float.IsInfinity(number) || Math.Abs(number) > MaxNumber) return false;
            text = number.ToString("R", Inv);
            return true;
        }
        if (type == typeof(double))
        {
            double number = (double)value;
            if (double.IsNaN(number) || double.IsInfinity(number) || Math.Abs(number) > MaxNumber) return false;
            text = number.ToString("R", Inv);
            return true;
        }
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) || type == typeof(uint))
        {
            text = Convert.ToString(value, Inv);
            return true;
        }
        ICollection collection = value as ICollection;
        if (collection != null) return collection.Count == 0;
        return Default(type, value);
    }

    private static bool Default(Type type, object value)
    {
        object blank;
        if (!blanks.TryGetValue(type, out blank))
        {
            try
            {
                blank = Activator.CreateInstance(type);
            }
            catch (Exception)
            {
                blank = null;
            }
            if (blank == null) blank = NoBlank;
            blanks[type] = blank;
        }
        if (ReferenceEquals(blank, NoBlank)) return false;
        return blank.Equals(value);
    }

    private static bool Known(Type type)
    {
        if (type == null || type.FullName == null) return false;
        if (type.IsAbstract || type.IsGenericType) return false;
        if (typeof(UnityEngine.Object).IsAssignableFrom(type)) return false;
        Type found;
        return known.TryGetValue(type.FullName, out found) && found == type;
    }

    private static Type TypeOf(string name)
    {
        if (name == null || name.Length > MaxName) return null;
        Type type;
        known.TryGetValue(name, out type);
        return type;
    }

    private static FieldInfo[] FieldsOf(Type type)
    {
        FieldInfo[] members;
        if (fields.TryGetValue(type, out members)) return members;
        members = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
        fields[type] = members;
        return members;
    }

    private static FieldInfo FieldOf(Type type, string name)
    {
        FieldInfo[] members = FieldsOf(type);
        for (int i = 0; i < members.Length; i++)
        {
            if (members[i].Name == name) return members[i];
        }
        return null;
    }

    private static void Note(string key)
    {
        if (!noted.Add(key)) return;
        CoopLog.Log.Info("coop: item meta key not sent: " + Cut(key));
    }

    private static string Cut(string text)
    {
        if (text == null) return "";
        return text.Length > MaxName ? text.Substring(0, MaxName) : text;
    }

    private static bool HasBad(string text)
    {
        return text.IndexOf('|') >= 0 || text.IndexOf('\r') >= 0 || text.IndexOf('\n') >= 0;
    }
}
