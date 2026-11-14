using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using ObenseuerCore;
using UnityEngine;

internal static class CoopUi
{
    public const string Channel = "mods";
    private const float ModResend = 30f;
    private static readonly Dictionary<string, string> mine = new Dictionary<string, string>();
    private static readonly Dictionary<ulong, Dictionary<string, string>> theirs = new Dictionary<ulong, Dictionary<string, string>>();
    private static readonly Dictionary<ulong, Dictionary<string, string>> incoming = new Dictionary<ulong, Dictionary<string, string>>();
    private static readonly Dictionary<ulong, float> sentAt = new Dictionary<ulong, float>();
    private static readonly HashSet<ulong> warned = new HashSet<ulong>();
    private static bool showPlayers;
    private static GUIStyle box;
    private static GUIStyle label;
    private static GUIStyle button;
    public static bool DeathOpen;

    private static void ReadMine()
    {
        if (mine.Count > 0) return;
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string location;
            try { location = asm.Location; }
            catch (Exception) { continue; }
            if (string.IsNullOrEmpty(location) || location.IndexOf("plugins", StringComparison.OrdinalIgnoreCase) < 0) continue;
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            catch (Exception) { continue; }
            foreach (Type t in types)
            {
                if (t == null) continue;
                object[] attrs;
                try { attrs = t.GetCustomAttributes(typeof(BepInPlugin), false); }
                catch (Exception) { continue; }
                for (int i = 0; i < attrs.Length; i++)
                {
                    BepInPlugin p = attrs[i] as BepInPlugin;
                    if (p == null || string.IsNullOrEmpty(p.GUID)) continue;
                    PropertyInfo vp = p.GetType().GetProperty("Version");
                    object v = vp == null ? null : vp.GetValue(p, null);
                    mine[Clean(p.GUID)] = Clean(v == null ? "?" : v.ToString());
                }
            }
        }
    }

    private static string Clean(string s)
    {
        return string.IsNullOrEmpty(s) ? "" : s.Replace('|', '_').Replace('\n', ' ');
    }

    public static void Tick()
    {
        try
        {
            if (!Session.IsInLobby) return;
            ReadMine();
            float now = Time.unscaledTime;
            foreach (ulong peer in Session.Peers)
            {
                float at;
                if (sentAt.TryGetValue(peer, out at) && now - at < ModResend) continue;
                sentAt[peer] = now;
                foreach (KeyValuePair<string, string> m in mine) Session.Send(peer, Channel, "MOD|" + m.Key + "|" + m.Value, true);
                Session.Send(peer, Channel, "MODEND|" + mine.Count, true);
            }
            CoopPlayersPanel.Tick();
            if (Input.GetKeyDown(KeyCode.F9) && Paused) showPlayers = !showPlayers;
            else if (showPlayers && Input.GetKeyDown(KeyCode.Escape)) showPlayers = false;
            if (!Paused) showPlayers = false;
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopUi.Tick", e);
        }
    }

    public static void Handle(ulong from, string text)
    {
        if (!Session.Peers.Contains(from) || text == null || text.Length > 300) return;
        string[] f = text.Split('|');
        Dictionary<string, string> list;
        if (!incoming.TryGetValue(from, out list)) incoming[from] = list = new Dictionary<string, string>();
        if (f[0] == "MOD" && f.Length == 3 && list.Count < 200) list[f[1]] = f[2];
        else if (f[0] == "MODEND")
        {
            theirs[from] = list;
            incoming.Remove(from);
            List<string> diff = Differences(from);
            if (diff.Count > 0 && warned.Add(from))
            {
                CoopLog.Log.Info("coop: mod differences with " + Session.DisplayName(from) + ": " + string.Join("; ", diff.ToArray()));
                CoopNotice.Show("mods|" + from, "Mods differ from " + Session.DisplayName(from) + " (" + diff.Count + "). Press F9 to see.", 8f);
            }
        }
    }

    internal static List<string> Differences(ulong peer)
    {
        List<string> result = new List<string>();
        Dictionary<string, string> list;
        if (!theirs.TryGetValue(peer, out list)) return result;
        foreach (KeyValuePair<string, string> m in mine)
        {
            string v;
            if (!list.TryGetValue(m.Key, out v)) result.Add(m.Key + " " + m.Value + " only here");
            else if (v != m.Value) result.Add(m.Key + " here " + m.Value + ", there " + v);
        }
        foreach (KeyValuePair<string, string> m in list)
        {
            if (!mine.ContainsKey(m.Key)) result.Add(m.Key + " " + m.Value + " only on " + Session.DisplayName(peer));
        }
        return result;
    }

    internal static string ModVersion(ulong peer)
    {
        Dictionary<string, string> list;
        if (!theirs.TryGetValue(peer, out list)) return "";
        string version;
        return list.TryGetValue(Plugin.PluginGuid, out version) ? version : "";
    }

    internal static string ModList()
    {
        ReadMine();
        string text = "";
        foreach (KeyValuePair<string, string> m in mine)
        {
            text += (text.Length == 0 ? "" : "; ") + m.Key + " " + m.Value;
        }
        return text.Length == 0 ? "none" : text;
    }

    internal static bool ModListKnown(ulong peer)
    {
        return theirs.ContainsKey(peer);
    }

    internal static void ClosePlayers()
    {
        showPlayers = false;
    }

    private static bool Paused
    {
        get { return PauseMenu.instance != null && GameController.instance != null && GameController.instance.GameIsPaused; }
    }

    public static void Reset()
    {
        showPlayers = false;
        DeathOpen = false;
        CoopAvatars.Clear();
        CoopPlayersPanel.Reset();
        theirs.Clear();
        incoming.Clear();
        sentAt.Clear();
        warned.Clear();
    }

    private static void Styles()
    {
        if (box != null) return;
        box = new GUIStyle(GUI.skin.box);
        box.normal.background = Texture2D.whiteTexture;
        label = new GUIStyle(GUI.skin.label);
        label.fontSize = 15;
        label.wordWrap = true;
        label.normal.textColor = new Color(0.93f, 0.88f, 0.76f);
        button = new GUIStyle(GUI.skin.button);
        button.fontSize = 16;
        button.fixedHeight = 36f;
    }

    public static void Draw()
    {
        try
        {
            CoopAlert.Draw();
            CoopAlert.DrawWaiting();
            CoopChat.Draw();
            if (!Session.IsInLobby) return;
            Styles();
            if (DeathOpen)
            {
                DrawDeath();
                return;
            }
            if (!Paused) return;
            if (FreeRoam.VoteScene != null) DrawVote();
            if (!showPlayers)
            {
                if (GUI.Button(new Rect(Screen.width - 240, 20, 220, 36), "Co-op players (F9)", button)) showPlayers = true;
                return;
            }
            CoopPlayersPanel.Draw();
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopUi.Draw", e);
        }
    }

    private static void Panel(Rect r)
    {
        Color old = GUI.color;
        GUI.color = new Color(0.08f, 0.07f, 0.06f, 0.92f);
        GUI.Box(r, GUIContent.none, box);
        GUI.color = old;
    }

    private static void DrawVote()
    {
        Rect r = new Rect(20, 20, 420, 130);
        Panel(r);
        GUILayout.BeginArea(new Rect(r.x + 14, r.y + 10, r.width - 28, r.height - 20));
        GUILayout.Label("The host invites you to " + FreeRoam.VoteScene, label);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Go", button)) FreeRoam.VoteChoice = 1;
        if (GUILayout.Button("Stay", button)) FreeRoam.VoteChoice = 2;
        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }

    private static void DrawDeath()
    {
        float w = 460f, h = 230f;
        Rect r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
        Panel(r);
        GUILayout.BeginArea(new Rect(r.x + 18, r.y + 16, w - 36, h - 32));
        GUILayout.Label("<size=22><b>You died</b></size>", label);
        GUILayout.Label("The others keep playing. Choose how to come back.", label);
        GUILayout.Space(8f);
        if (GUILayout.Button("Wake up at home", button)) Solo.ChooseHome();
        if (GUILayout.Button(Session.IsHost ? "Load a save (the guests come along)" : "Rejoin the host's world", button)) Solo.ChooseReload();
        GUILayout.Label("Waking at home automatically in a minute.", label);
        GUILayout.EndArea();
    }
}
