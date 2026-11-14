using System;
using UnityEngine;

internal static class CoopAlert
{
    internal enum Kind
    {
        None = 0,
        ConnectionLost = 1,
        HostLeft = 2,
        HostClosed = 3,
        HostQuit = 4
    }

    private const string Title = "Co-op";
    private const float BoxWidth = 470f;
    private const float BoxHeight = 200f;
    private const float ButtonWidth = 150f;
    private const float ButtonHeight = 36f;
    private const float WaitWidth = 460f;
    private const float WaitHeight = 50f;
    private const float WaitButtonWidth = 132f;
    private const int AlertDepth = -100;
    private static Kind showing;
    private static GUIStyle box;
    private static GUIStyle title;
    private static GUIStyle text;
    private static GUIStyle button;

    public static bool Active
    {
        get { return showing != Kind.None; }
    }

    public static void Show(Kind kind)
    {
        try
        {
            if (kind == Kind.None) return;
            if ((int)kind < (int)showing) return;
            showing = kind;
            Session.SetNotice(Message(kind));
            CoopLog.Log.Info("coop alert: " + Message(kind));
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopAlert.Show", e);
        }
    }

    public static void Clear()
    {
        if (showing == Kind.None) return;
        showing = Kind.None;
        CoopLog.Log.Info("coop alert cleared for a new session");
    }

    public static void Draw()
    {
        try
        {
            if (showing == Kind.None) return;
            Styles();
            Rect r = new Rect((Screen.width - BoxWidth) / 2f, (Screen.height - BoxHeight) / 2f, BoxWidth, BoxHeight);
            GUI.depth = AlertDepth;
            Color old = GUI.color;
            GUI.color = new Color(0.08f, 0.07f, 0.06f, 0.94f);
            GUI.Box(r, GUIContent.none, box);
            GUI.color = old;
            GUI.Label(new Rect(r.x + 20, r.y + 16, r.width - 40, 32), Title, title);
            GUI.Label(new Rect(r.x + 20, r.y + 58, r.width - 40, 80), Message(showing), text);
            if (GUI.Button(new Rect(r.x + (r.width - ButtonWidth) / 2f, r.y + r.height - ButtonHeight - 18, ButtonWidth, ButtonHeight), "OK", button)) Dismiss();
            if (showing != Kind.None && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Escape))) Dismiss();
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopAlert.Draw", e);
        }
    }

    public static void DrawWaiting()
    {
        try
        {
            if (!Session.HostReload) return;
            Styles();
            Rect r = new Rect((Screen.width - WaitWidth) / 2f, 18f, WaitWidth, WaitHeight);
            GUI.depth = AlertDepth;
            Color old = GUI.color;
            GUI.color = new Color(0.08f, 0.07f, 0.06f, 0.9f);
            GUI.Box(r, GUIContent.none, box);
            GUI.color = old;
            GUI.Label(new Rect(r.x + 16, r.y + 13, r.width - WaitButtonWidth - 36, 26), Session.HostReloadText + Spinner(), text);
            if (GUI.Button(new Rect(r.x + r.width - WaitButtonWidth - 14, r.y + 7, WaitButtonWidth, ButtonHeight), "Cancel", button)) Session.CancelHostReload();
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopAlert.DrawWaiting", e);
        }
    }

    private static string Spinner()
    {
        int dots = 1 + (int)(Time.unscaledTime * 2f) % 3;
        return new string('.', dots);
    }

    private static void Dismiss()
    {
        showing = Kind.None;
        CoopLog.Log.Info("coop alert dismissed");
    }

    private static string Message(Kind kind)
    {
        switch (kind)
        {
            case Kind.HostQuit:
                return "The host quit the game.";
            case Kind.HostClosed:
                return "The host closed the lobby.";
            case Kind.HostLeft:
                return "The host left the lobby.";
            case Kind.ConnectionLost:
                return "The host left the lobby or the connection was lost.";
            default:
                return "";
        }
    }

    private static void Styles()
    {
        if (box != null) return;
        box = new GUIStyle(GUI.skin.box);
        box.normal.background = Texture2D.whiteTexture;
        title = new GUIStyle(GUI.skin.label);
        title.fontSize = 20;
        title.fontStyle = FontStyle.Bold;
        title.normal.textColor = new Color(0.93f, 0.88f, 0.76f);
        text = new GUIStyle(GUI.skin.label);
        text.fontSize = 15;
        text.wordWrap = true;
        text.normal.textColor = new Color(0.93f, 0.88f, 0.76f);
        button = new GUIStyle(GUI.skin.button);
        button.fontSize = 16;
    }
}
