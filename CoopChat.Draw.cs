using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using ObenseuerCore;
using UnityEngine;
using UnityEngine.EventSystems;

internal static partial class CoopChat
{
    private static bool Key(ConfigEntry<KeyCode> entry)
    {
        return entry != null && entry.Value != KeyCode.None && Input.GetKeyDown(entry.Value);
    }

    private static bool MenuVisible()
    {
        return GameUIController.instance != null && GameUIController.instance.IsMenuVisible;
    }

    private static bool Blocked()
    {
        return BadState() || VanillaTyping() || LobbyKeys.Busy();
    }

    private static bool BadState()
    {
        if (GameController.instance != null && GameController.instance.GameIsPaused) return true;
        if (MenuVisible()) return true;
        if (DialogueController.instance != null && DialogueController.instance.DialogueActive) return true;
        if (ShopController.instance != null && ShopController.instance.menuIsOpen) return true;
        if (DeveloperConsole.Instance != null && DeveloperConsole.Instance.consoleOpened) return true;
        if (SaveController.Loading || SaveData.Busy) return true;
        if (GuestSession.Current == GuestSession.Phase.Placing || GuestSession.Current == GuestSession.Phase.Loading) return true;
        if (Session.JoinPending) return true;
        if (CoopUi.DeathOpen) return true;
        return false;
    }

    private static bool VanillaTyping()
    {
        if (Selected()) return true;
        UnityEngine.UI.InputField[] fields = UnityEngine.Object.FindObjectsByType<UnityEngine.UI.InputField>(FindObjectsSortMode.None);
        for (int i = 0; i < fields.Length; i++)
        {
            if (fields[i] != null && fields[i].isFocused) return true;
        }
        TMPro.TMP_InputField[] tmp = UnityEngine.Object.FindObjectsByType<TMPro.TMP_InputField>(FindObjectsSortMode.None);
        for (int i = 0; i < tmp.Length; i++)
        {
            if (tmp[i] != null && tmp[i].isFocused) return true;
        }
        return false;
    }

    private static bool Selected()
    {
        EventSystem system = EventSystem.current;
        if (system == null) return false;
        GameObject selected = system.currentSelectedGameObject;
        if (selected == null) return false;
        return selected.GetComponent<UnityEngine.UI.InputField>() != null || selected.GetComponent<TMPro.TMP_InputField>() != null;
    }
    private static void Styles()
    {
        if (box != null) return;
        box = new GUIStyle(GUI.skin.box);
        box.normal.background = Texture2D.whiteTexture;
        label = new GUIStyle(GUI.skin.label);
        label.fontSize = 15;
        label.richText = true;
        label.wordWrap = false;
        label.clipping = TextClipping.Clip;
        label.normal.textColor = new Color(0.93f, 0.88f, 0.76f);
        plain = new GUIStyle(GUI.skin.label);
        plain.fontSize = 15;
        plain.richText = false;
        plain.wordWrap = false;
        plain.clipping = TextClipping.Clip;
        plain.normal.textColor = new Color(0.93f, 0.88f, 0.76f);
        grey = new GUIStyle(plain);
        grey.normal.textColor = new Color(0.71f, 0.71f, 0.71f);
        field = new GUIStyle(GUI.skin.textField);
        field.fontSize = 15;
    }

    public static void Draw()
    {
        try
        {
            if (!Session.IsInLobby) return;
            if (!open && lines.Count == 0) return;
            Styles();
            DrawLog();
            if (open) DrawInput();
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopChat.Draw", e);
        }
    }

    private static float Alpha(Line line, float now)
    {
        float left = HoldSeconds + FadeSeconds - (now - line.At);
        if (left <= 0f) return 0f;
        return left >= FadeSeconds ? 1f : left / FadeSeconds;
    }

    private static void DrawLog()
    {
        float now = Time.unscaledTime;
        int max = open ? OpenLines : ClosedLines;
        int first = Mathf.Max(0, lines.Count - max);
        int count = 0;
        for (int i = first; i < lines.Count; i++)
        {
            if (open || Alpha(lines[i], now) > 0.02f) count++;
        }
        if (count < 0) count = 0;
        float width = Mathf.Clamp(Screen.width * 0.3f, 300f, 640f);
        float x = 12f;
        float y = Screen.height - FieldHeight - 20f;
        if (count > 0)
        {
            float height = count * LineHeight + (open ? FieldHeight + 12f : 8f);
            y = Screen.height - height - 14f;
            if (open)
            {
                Color oldBox = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.45f);
                GUI.Box(new Rect(x - 4f, y - 4f, width + 8f, height + 8f), GUIContent.none, box);
                GUI.color = oldBox;
            }
            float ly = y;
            for (int i = first; i < lines.Count; i++)
            {
                float alpha = open ? 1f : Alpha(lines[i], now);
                if (alpha <= 0.02f) continue;
                Color old = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, alpha);
                float chip = 0f;
                if (lines[i].Chip.Length > 0)
                {
                    chip = label.CalcSize(new GUIContent(lines[i].Chip)).x;
                    GUI.Label(new Rect(x, ly, width, LineHeight), lines[i].Chip, label);
                }
                GUI.Label(new Rect(x + chip, ly, Mathf.Max(0f, width - chip), LineHeight), lines[i].Body, lines[i].Grey ? grey : plain);
                GUI.color = old;
                ly += LineHeight;
            }
            y = y + count * LineHeight + 6f;
        }
        inputRect = new Rect(x, y, width, FieldHeight);
    }

    private static void DrawInput()
    {
        Event e = Event.current;
        bool send = false;
        bool cancel = false;
        bool fresh = Time.frameCount == openFrame;
        if (e != null && e.type == EventType.KeyDown)
        {
            if (fresh)
            {
                e.Use();
            }
            else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                send = true;
                e.Use();
            }
            else if (e.keyCode == KeyCode.Escape)
            {
                cancel = true;
                e.Use();
            }
        }
        GUI.SetNextControlName(FieldName);
        input = GUI.TextField(inputRect, input, MaxChars + 1, field);
        if (fresh) input = initial;
        if (GUI.GetNameOfFocusedControl() != FieldName) GUI.FocusControl(FieldName);
        if (cancel)
        {
            Close("escape");
            return;
        }
        if (send) Submit();
    }
}
