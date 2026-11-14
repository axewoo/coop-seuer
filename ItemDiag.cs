using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

internal static class ItemDiag
{
    private const float Window = 5f;
    private const int MaxKey = 40;
    private const int MaxKeys = 256;
    private static readonly Dictionary<string, float> last = new Dictionary<string, float>();
    private static readonly HashSet<string> seen = new HashSet<string>();

    public static string Key(string reason)
    {
        if (reason == null) return "";
        int open = reason.IndexOf('(');
        string plain = (open >= 0 ? reason.Substring(0, open) : reason).Trim();
        StringBuilder text = new StringBuilder(MaxKey);
        for (int i = 0; i < plain.Length && text.Length < MaxKey; i++)
        {
            char c = plain[i];
            text.Append(char.IsLetterOrDigit(c) || c == ' ' || c == '.' || c == ',' || c == ':' || c == '/' ? c : '-');
        }
        return text.ToString().Trim();
    }

    public static void Info(string key, string text)
    {
        try
        {
            float now = Time.unscaledTime;
            float was;
            if (last.TryGetValue(key, out was) && now - was < Window) return;
            if (last.Count > MaxKeys) last.Clear();
            last[key] = now;
            CoopLog.Log.Info(text);
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemDiag.Info", e);
        }
    }

    public static void Once(string key, string text)
    {
        try
        {
            if (seen.Add(key)) CoopLog.Log.Info(text);
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemDiag.Once", e);
        }
    }
}
