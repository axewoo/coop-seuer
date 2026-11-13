using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using ObenseuerCore;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static class LookPin
{
    private const string StoreName = "obenseuer.coop.look.json";
    private const float RetryInterval = 0.25f;
    private const string StartMethod = "Start";
    private const float StoreGap = 2f;
    private const float HeightEpsilon = 0.001f;
    private const int HeightRepairLimit = 3;
    public static bool Installed;
    private static Look pin;
    private static bool installTried;
    private static bool storeRead;
    private static bool applying;
    private static int appliedScene = int.MinValue;
    private static int heightRepairs;
    private static float nextApply;
    private static float nextStoreAt;
    private static string pendingText;

    public static string Text()
    {
        return pin == null ? null : pin.Text();
    }

    public static void Install()
    {
        if (installTried) return;
        installTried = true;
        try
        {
            MethodInfo start = AccessTools.Method(typeof(Appearance), StartMethod);
            if (start == null)
            {
                CoopLog.Log.Error("look pin: Appearance.Start was not found, the pinned look is written back from the tick only");
            }
            else
            {
                Harmony harmony = new Harmony(Plugin.PluginGuid + ".lookpin");
                harmony.Patch(start, postfix: new HarmonyMethod(typeof(LookPin), "StartPostfix"));
            }
            PlayerLook.Changed += OnChanged;
            Installed = true;
            CoopLog.Log.Info("look pin: the local look is pinned for the whole session and stored in " + StorePath());
        }
        catch (Exception e)
        {
            CoopLog.Once("LookPin.Install", e);
        }
    }

    public static void OnSceneLoaded()
    {
        appliedScene = int.MinValue;
        nextApply = 0f;
        heightRepairs = 0;
    }

    public static void StartPostfix(Appearance __instance)
    {
        try
        {
            if (!Session.IsInLobby || __instance == null || !IsLocalBody(__instance)) return;
            if (!PlayerLook.IsAvailable) return;
            ReadStore();
            if (pin == null)
            {
                Capture(__instance);
                return;
            }
            Apply(__instance);
        }
        catch (Exception e)
        {
            CoopLog.Once("LookPin.StartPostfix", e);
        }
    }

    public static void Tick()
    {
        try
        {
            if (!Session.IsInLobby) return;
            FlushStore();
            if (!PlayerLook.IsAvailable) return;
            Appearance ap = GameRefs.LocalAppearance;
            if (!GameRefs.AppearanceReady(ap)) return;
            ReadStore();
            if (pin == null)
            {
                Capture(ap);
                return;
            }
            Apply(ap);
        }
        catch (Exception e)
        {
            CoopLog.Once("LookPin.Tick", e);
        }
    }

    private static void Apply(Appearance ap)
    {
        if (pin == null || ap == null) return;
        if (!PlayerLook.IsAvailable) return;
        int scene = SceneManager.GetActiveScene().handle;
        bool repair = scene == appliedScene;
        if (repair && (heightRepairs >= HeightRepairLimit || !HeightDrifted(ap))) return;
        float now = Time.unscaledTime;
        if (now < nextApply) return;
        nextApply = now + RetryInterval;
        if (SaveData.Busy) return;
        if (repair) heightRepairs++;
        try
        {
            applying = true;
            if (PlayerLook.Apply(pin.ToCore(ap), ap))
            {
                appliedScene = scene;
                CoopLog.Log.Info("look pin: the pinned look was written to the local body in " + SceneName());
                return;
            }
            CoopLog.Once("LookPin.refused", "PlayerLook.Apply did not write the pinned look, it is retried every " + RetryInterval + " s");
            if (CoopDebug.On("look")) CoopDebug.Event("look", "refused", "the pinned look of this body was refused in " + SceneName() + ", it is retried every " + RetryInterval + " s");
        }
        catch (Exception e)
        {
            CoopLog.Once("LookPin.Apply", e);
        }
        finally
        {
            applying = false;
        }
    }

    private static bool HeightDrifted(Appearance ap)
    {
        return Math.Abs(ap.height - pin.Height) > HeightEpsilon;
    }

    private static void Capture(Appearance ap)
    {
        try
        {
            if (!GameRefs.AppearanceReady(ap)) return;
            if (!PlayerLook.IsAvailable) return;
            if (Key() == 0UL) return;
            PlayerLook.Look core = PlayerLook.Capture(ap);
            if (core == null) return;
            Look captured = Look.FromCore(core, ap);
            if (!Usable(captured))
            {
                CoopLog.Once("LookPin.capture.unusable", "look pin: the look of the local body names no wearable of this build, it is not pinned");
                return;
            }
            pin = captured;
            appliedScene = SceneManager.GetActiveScene().handle;
            Store(pin.Text());
            CoopLog.Log.Info("look pin: the local look was captured in " + SceneName() + ", it is kept for every later scene and stored for later sessions");
        }
        catch (Exception e)
        {
            CoopLog.Once("LookPin.Capture", e);
        }
    }

    private static void OnChanged(PlayerLook.Look look)
    {
        try
        {
            if (look == null || applying) return;
            Appearance body = GameRefs.LocalAppearance;
            if (!GameRefs.AppearanceReady(body)) return;
            Look changed = Look.FromCore(look, body);
            string text = changed.Text();
            if (pin != null && pin.Text() == text) return;
            pin = changed;
            appliedScene = SceneManager.GetActiveScene().handle;
            Store(text);
            CoopLog.Log.Info("look pin: the local look changed on purpose, the pin follows it and later scenes use the new one");
        }
        catch (Exception e)
        {
            CoopLog.Once("LookPin.OnChanged", e);
        }
    }

    private static bool IsLocalBody(Appearance ap)
    {
        PlayerLocator locator = PlayerLocator.instance;
        if (locator == null) return false;
        Transform t = ap.transform;
        if (t != locator.transform && !t.IsChildOf(locator.transform)) return false;
        return ap.GetComponentInParent<FirstPersonHands>() == null;
    }

    private static void ReadStore()
    {
        if (storeRead || pin != null) return;
        try
        {
            ulong id = Key();
            if (id == 0UL) return;
            storeRead = true;
            string path = StorePath();
            if (path == null || !File.Exists(path)) return;
            Dictionary<string, string> entries = Read(path);
            string text;
            if (!entries.TryGetValue(id.ToString(), out text)) return;
            Look stored;
            if (!Look.Parse(text, out stored))
            {
                CoopLog.Log.Error("look pin: the stored look in " + path + " is not a look, it is ignored");
                return;
            }
            if (!Usable(stored))
            {
                CoopLog.Once("LookPin.stored.unusable", "look pin: the stored look of " + id + " names no wearable of this build, it is ignored");
                return;
            }
            pin = stored;
            CoopLog.Log.Info("look pin: the stored look of " + id + " was read from " + path);
        }
        catch (Exception e)
        {
            CoopLog.Once("LookPin.ReadStore", e);
        }
    }

    private static void Store(string text)
    {
        try
        {
            ulong id = Key();
            if (id == 0UL)
            {
                CoopLog.Once("LookPin.store.noid", "look pin: the look cannot be stored before the Steam id of this player is known");
                return;
            }
            if (string.IsNullOrEmpty(text)) return;
            if (Time.unscaledTime < nextStoreAt)
            {
                pendingText = text;
                return;
            }
            pendingText = null;
            Write(text, id);
        }
        catch (Exception e)
        {
            CoopLog.Once("LookPin.Store", e);
        }
    }

    private static void FlushStore()
    {
        if (pendingText == null) return;
        try
        {
            ulong id = Key();
            if (id == 0UL) return;
            if (Time.unscaledTime < nextStoreAt) return;
            string text = pendingText;
            pendingText = null;
            Write(text, id);
        }
        catch (Exception e)
        {
            CoopLog.Once("LookPin.FlushStore", e);
        }
    }

    private static void Write(string text, ulong id)
    {
        string path = StorePath();
        if (path == null) return;
        Dictionary<string, string> entries = Read(path);
        entries[id.ToString()] = Escape(text);
        StringBuilder builder = new StringBuilder();
        builder.Append("{\n");
        int written = 0;
        foreach (KeyValuePair<string, string> entry in entries)
        {
            written++;
            builder.Append("  \"").Append(Escape(entry.Key)).Append("\": \"").Append(entry.Value).Append("\"");
            builder.Append(written < entries.Count ? ",\n" : "\n");
        }
        builder.Append("}\n");
        string temp = path + ".tmp";
        File.WriteAllText(temp, builder.ToString(), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
        nextStoreAt = Time.unscaledTime + StoreGap;
        CoopLog.Log.Info("look pin: the look of " + id + " was stored in " + path);
    }

    private static Dictionary<string, string> Read(string path)
    {
        Dictionary<string, string> entries = new Dictionary<string, string>();
        if (!File.Exists(path)) return entries;
        string[] lines = File.ReadAllLines(path);
        foreach (string line in lines)
        {
            string key;
            string value;
            if (!Pair(line, out key, out value)) continue;
            entries[key] = value;
        }
        return entries;
    }

    private static bool Pair(string line, out string key, out string value)
    {
        key = null;
        value = null;
        if (line == null) return false;
        int first = line.IndexOf('"');
        int second = first < 0 ? -1 : line.IndexOf('"', first + 1);
        if (second < 0) return false;
        int open = line.IndexOf('"', second + 1);
        int close = line.LastIndexOf('"');
        if (open < 0 || close <= open) return false;
        key = Unescape(line.Substring(first + 1, second - first - 1).Trim());
        value = Unescape(line.Substring(open + 1, close - open - 1).Trim());
        return key.Length > 0 && value.Length > 0;
    }

    private static string Escape(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        StringBuilder builder = new StringBuilder(text.Length + 8);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\') builder.Append("\\\\");
            else if (c == '"') builder.Append("\\\"");
            else if (c == '\n') builder.Append("\\n");
            else if (c == '\r') builder.Append("\\r");
            else builder.Append(c);
        }
        return builder.ToString();
    }

    private static string Unescape(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('\\') < 0) return text;
        StringBuilder builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\\' || i + 1 >= text.Length)
            {
                builder.Append(c);
                continue;
            }
            i++;
            char next = text[i];
            if (next == 'n') builder.Append('\n');
            else if (next == 'r') builder.Append('\r');
            else builder.Append(next);
        }
        return builder.ToString();
    }

    private static bool Usable(Look look)
    {
        if (look == null) return false;
        return look.Head >= 0 || look.Hat >= 0 || look.Hair >= 0 || look.Glasses >= 0 || look.Beard >= 0
            || look.ShirtT >= 0 || look.ShirtB >= 0 || look.PantsT >= 0 || look.PantsB >= 0 || look.Socks >= 0 || look.Shoes >= 0;
    }

    private static string StorePath()
    {
        try
        {
            string folder = Paths.ConfigPath;
            return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, StoreName);
        }
        catch (Exception e)
        {
            CoopLog.Once("LookPin.StorePath", e);
            return null;
        }
    }

    private static ulong Key()
    {
        return Session.Me;
    }

    private static string SceneName()
    {
        Scene scene = SceneManager.GetActiveScene();
        return string.IsNullOrEmpty(scene.name) ? "the current scene" : scene.name;
    }
}
