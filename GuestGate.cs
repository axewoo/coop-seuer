using System;
using System.Collections.Generic;
using UnityEngine;

internal static class GuestGate
{
    public const string KindTravel = "travel";
    public const string KindArrest = "arrest";
    public const string KindShop = "shop";
    public const string KindDialogue = "dialogue";
    public const string KindQuest = "quest";
    public const string KindPhone = "phone";
    public const string KindMail = "mail";
    public const string KindBank = "bank";
    public const string KindPassout = "passout";
    public const string KindBuild = "build";
    public const string KindItem = "item";
    public const string KindContainer = "container";
    public const string KindCraftStation = "craft-station";
    public const string KindUnshared = "unshared";

    private const float NoticeRepeat = 5f;
    private const string NoticeTitle = "Co-op";
    private const string FollowText = "Waiting to follow the host";

    private static readonly HashSet<string> AllowedKinds = new HashSet<string>
    {
        KindBuild, KindItem, KindContainer, "door", "sleep", "wait", "craft"
    };

    private static readonly Dictionary<string, float> nextNoticeAt = new Dictionary<string, float>();

    private const float GrantSeconds = 600f;
    private static readonly Dictionary<string, float> grants = new Dictionary<string, float>();

    public static bool Allowed(string kind)
    {
        if (!GuestSession.InHostWorld) return true;
        if (Frozen(kind)) return false;
        return kind != null && AllowedKinds.Contains(kind);
    }

    public static bool Frozen(string kind)
    {
        if (!GuestSession.Following) return false;
        return kind == KindBuild || kind == KindItem || kind == KindContainer || kind == KindQuest;
    }

    public static bool Block(string kind)
    {
        return Block(kind, Text(kind));
    }

    public static bool Block(string kind, string text)
    {
        if (Allowed(kind)) return false;
        Notice(kind, text);
        return true;
    }

    public static void Notice(string kind, string text)
    {
        float now = Time.unscaledTime;
        float at;
        if (nextNoticeAt.TryGetValue(kind, out at) && now < at) return;
        nextNoticeAt[kind] = now + NoticeRepeat;
        CoopLog.Log.Info("coop: blocked guest " + kind);
        Notifications panel = Notifications.instance;
        if (panel == null)
        {
            Session.SetNotice(text);
            return;
        }
        try
        {
            panel.CreateNotification(NoticeTitle, text);
        }
        catch (Exception e)
        {
            Fallback(text, e);
        }
    }

    private static void Fallback(string text, Exception e)
    {
        CoopLog.Once("GuestGate.Notice", e);
        Session.SetNotice(text);
    }

    public static void Grant(string shopId)
    {
        if (shopId == null) return;
        grants[shopId] = Time.unscaledTime;
    }

    public static bool Granted(string shopId)
    {
        if (shopId == null) return false;
        float at;
        if (!grants.TryGetValue(shopId, out at)) return false;
        if (Time.unscaledTime - at > GrantSeconds)
        {
            grants.Remove(shopId);
            return false;
        }
        return true;
    }

    public static void Forget(string shopId)
    {
        if (shopId != null) grants.Remove(shopId);
    }

    public static void ForgetGrants()
    {
        grants.Clear();
    }

    public static string Text(string kind)
    {
        if (Frozen(kind)) return FollowText;
        if (kind == KindTravel) return "Travel is not in co-op yet; stay with the host";
        if (kind == KindArrest) return "Arrest is not in co-op yet";
        if (kind == KindShop) return "Shops are not in co-op yet";
        if (kind == KindPassout) return "Forced passout is not in co-op yet";
        if (kind == KindQuest) return "That quest step could not be sent to the host";
        return "Only the host can talk and do quests";
    }
}
