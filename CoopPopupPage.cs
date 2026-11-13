using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using ObenseuerCore;
using UnityEngine;

public class PopupRequest
{
    public string type;
    public int seq;
}

public class PopupNotice
{
    public string type = "notice";
    public string title;
    public string text;
    public int seq;
}

internal static class CoopPopupPage
{
    private const string FolderName = "lobby";
    private const string PageFile = "notice.html";
    private const int EscapeKey = 27;
    private const int NoSeq = 0;
    private const int CardRems = 32;
    private const int CardHeightRems = 17;
    private const float TopFraction = 0.06f;
    private const float IgnoreDismiss = 0.3f;

    private static WebView view;
    private static bool ready;
    private static bool dismissed;
    private static int postedSeq;
    private static float postedAt;
    private static float openedAt;

    public static bool HasView
    {
        get { return view != null; }
    }

    public static bool IsClosed
    {
        get { return view != null && view.IsClosed; }
    }

    public static bool Ready
    {
        get { return ready; }
    }

    public static float Age
    {
        get { return Time.unscaledTime - openedAt; }
    }

    public static bool TakeDismissed()
    {
        if (!dismissed) return false;
        dismissed = false;
        return true;
    }

    public static string LastError
    {
        get { return WebUi.LastError; }
    }

    public static bool Open()
    {
        if (Screen.fullScreenMode == FullScreenMode.ExclusiveFullScreen) return false;
        string folder = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "web", FolderName);
        WebView opened = WebUi.Open(Plugin.PluginGuid, folder, PageFile, false);
        if (opened == null)
        {
            CoopLog.Log.Info("coop: the quest notice page did not open: " + WebUi.LastError);
            return false;
        }
        view = opened;
        view.Hide();
        view.Message += OnMessage;
        view.KeyPressed += OnKey;
        view.EscapeHides = false;
        ready = false;
        dismissed = false;
        postedAt = 0f;
        openedAt = Time.unscaledTime;
        return true;
    }

    public static bool Post(string title, string text)
    {
        if (view == null) return false;
        int x;
        int y;
        int width;
        int height;
        Bounds(out x, out y, out width, out height);
        if (!view.SetBounds(x, y, width, height)) return false;
        if (!view.IsVisible && !view.Show()) return false;
        postedSeq++;
        dismissed = false;
        postedAt = Time.unscaledTime;
        PopupNotice notice = new PopupNotice();
        notice.title = title;
        notice.text = text;
        notice.seq = postedSeq;
        return view.Post(JsonConvert.SerializeObject(notice));
    }

    public static void Hide()
    {
        if (view == null || !view.IsVisible) return;
        view.Hide();
    }

    public static void Close()
    {
        ready = false;
        dismissed = false;
        postedAt = 0f;
        if (view == null) return;
        WebView closing = view;
        view = null;
        closing.Message -= OnMessage;
        closing.KeyPressed -= OnKey;
        closing.Close();
    }

    private static void Bounds(out int x, out int y, out int width, out int height)
    {
        int rem = 10;
        height = CardHeightRems * rem;
        for (int i = 0; i < 3; i++)
        {
            rem = 10 + Mathf.RoundToInt(height * 0.007f);
            height = CardHeightRems * rem;
        }
        int screenWidth = Screen.width > 0 ? Screen.width : 1920;
        int screenHeight = Screen.height > 0 ? Screen.height : 1080;
        width = CardRems * rem;
        if (width > screenWidth - rem * 2) width = screenWidth - rem * 2;
        if (height > screenHeight - rem * 2) height = screenHeight - rem * 2;
        if (width < rem) width = rem;
        if (height < rem) height = rem;
        x = (screenWidth - width) / 2;
        y = Mathf.RoundToInt(screenHeight * TopFraction);
    }

    private static void OnMessage(string json)
    {
        try
        {
            PopupRequest request = JsonConvert.DeserializeObject<PopupRequest>(json);
            if (request == null || request.type == null) return;
            if (request.type == "ready")
            {
                ready = true;
                CoopLog.Log.Info("coop: the quest notice page is ready");
                return;
            }
            if (request.type == "ok") Dismiss(request.seq);
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopPopupPage.message", e);
        }
    }

    private static void OnKey(int key)
    {
        if (key != EscapeKey) return;
        Dismiss(NoSeq);
    }

    private static void Dismiss(int seq)
    {
        if (view == null) return;
        if (seq != NoSeq && seq != postedSeq) return;
        if (Time.unscaledTime < postedAt + IgnoreDismiss) return;
        dismissed = true;
    }
}
