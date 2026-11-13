using System;
using System.Collections.Generic;
using UnityEngine;

internal static class CoopNotice
{
    private const string Title = "Co-op";
    private const float MinGap = 3f;
    private static readonly string[] Keyed = { "popup", "queststart" };
    private static readonly Dictionary<string, float> nextKind = new Dictionary<string, float>();
    private static string shownKey = "";
    private static float repeatAt;

    public static bool Show(string key, string text, float interval)
    {
        try
        {
            Notifications notifications = Notifications.instance;
            if (notifications == null) return false;
            float now = Time.unscaledTime;
            string kind = Kind(key);
            float gate;
            if (nextKind.TryGetValue(kind, out gate) && now < gate) return true;
            if (key == shownKey && now < repeatAt) return true;
            shownKey = key;
            repeatAt = now + interval;
            nextKind[kind] = now + MinGap;
            notifications.CreateNotification(Title, text);
            CoopLog.Log.Debug("notice", text);
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopNotice.Show", e);
            return false;
        }
    }

    public static void Reset()
    {
        nextKind.Clear();
        shownKey = "";
        repeatAt = 0f;
    }

    private static string Kind(string key)
    {
        if (key == null) return "";
        int cut = key.IndexOf('|');
        if (cut < 0) return key;
        string head = key.Substring(0, cut);
        for (int i = 0; i < Keyed.Length; i++)
        {
            if (head == Keyed[i]) return key;
        }
        return head;
    }
}
