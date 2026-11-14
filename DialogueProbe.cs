using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PixelCrushers.DialogueSystem;

internal static class DialogueProbe
{
    private const string NoTalk = "-";
    private const int MaxName = 120;
    private const int MaxValue = 120;
    private const int MaxSeen = 512;
    private static bool installed;
    private static readonly Dictionary<string, string> last = new Dictionary<string, string>();
    private static string talk = NoTalk;
    private static string loggedTalk = NoTalk;
    private static int writes;

    public static bool Ready { get; private set; }

    public static int Writes
    {
        get { return writes; }
    }

    public static bool Install()
    {
        if (installed) return Ready;
        installed = true;
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".probe");
        try
        {
            MethodBase variable = AccessTools.Method(typeof(DialogueLua), "SetVariable", new[] { typeof(string), typeof(object) });
            if (variable == null) throw new MissingMethodException("DialogueLua", "SetVariable");
            MethodBase field = AccessTools.Method(typeof(DialogueLua), "SetTableField", new[] { typeof(string), typeof(string), typeof(string), typeof(object) });
            if (field == null) throw new MissingMethodException("DialogueLua", "SetTableField");
            harmony.Patch(variable, postfix: new HarmonyMethod(typeof(DialogueProbe), "Variable"));
            harmony.Patch(field, postfix: new HarmonyMethod(typeof(DialogueProbe), "Field"));
        }
        catch (Exception e)
        {
            CoopLog.Once("DialogueProbe.Install", e);
            try
            {
                harmony.UnpatchSelf();
            }
            catch (Exception)
            {
            }
            return false;
        }
        Ready = true;
        CoopLog.Log.Info("coop: dialogue probe installed on DialogueLua.SetVariable and DialogueLua.SetTableField");
        return true;
    }

    public static string SelfCheck()
    {
        if (AccessTools.Method(typeof(DialogueLua), "SetVariable", new[] { typeof(string), typeof(object) }) == null) return "DialogueLua.SetVariable was not found";
        if (AccessTools.Method(typeof(DialogueLua), "SetTableField", new[] { typeof(string), typeof(string), typeof(string), typeof(object) }) == null) return "DialogueLua.SetTableField was not found";
        return "dialogue probe targets ok";
    }

    public static void Note(string name, object value)
    {
        if (!Active()) return;
        try
        {
            string text = Describe(value);
            string key = Conversation() + "|" + name;
            string seen;
            if (last.TryGetValue(key, out seen) && seen == text) return;
            if (last.Count > MaxSeen) last.Clear();
            last[key] = text;
            writes++;
            if (Conversation() != loggedTalk)
            {
                loggedTalk = Conversation();
                CoopLog.Log.Info("coop: dialogue " + name + " = " + text + " (" + loggedTalk + ")");
            }
            else
            {
                CoopLog.Log.Debug("talk", "dialogue " + name + " = " + text + " (" + loggedTalk + ")");
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("DialogueProbe.Note", e);
        }
    }

    public static void Reset()
    {
        last.Clear();
        talk = NoTalk;
        loggedTalk = NoTalk;
    }

    private static bool Active()
    {
        try
        {
            return DialogueManager.isConversationActive;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string Conversation()
    {
        try
        {
            if (!DialogueManager.isConversationActive) return NoTalk;
            string name = DialogueManager.lastConversationStarted;
            if (string.IsNullOrEmpty(name)) return NoTalk;
            string clean = Clean(name, MaxName);
            talk = clean.Length > 0 ? clean : NoTalk;
            return talk;
        }
        catch (Exception)
        {
            return talk;
        }
    }

    private static void Variable(string variable, object value)
    {
        Note("var " + Clean(variable, MaxName), value);
        TalkHost.NoteVariable(variable, value);
        TalkGuest.NoteVariable(variable, value);
    }

    private static void Field(string table, string element, string field, object value)
    {
        Note("field " + Clean(table, 16) + "." + Clean(element, MaxName) + "." + Clean(field, MaxName), value);
        TalkGuest.NoteField(table, element, field, value);
    }

    private static string Describe(object value)
    {
        if (value == null) return "nil";
        string text = value as string;
        if (text == null) return Clean(value.ToString(), MaxValue);
        return Clean(text, MaxValue);
    }

    private static string Clean(string text, int max)
    {
        if (text == null) return "";
        string plain = text.Replace("\r", "").Replace("\n", "").Replace("|", "/");
        return plain.Length > max ? plain.Substring(0, max) : plain;
    }
}
