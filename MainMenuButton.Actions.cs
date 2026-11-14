using System;
using System.Collections.Generic;
using System.Reflection;
using ObenseuerCore;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

internal static partial class MainMenuButton
{
    internal static void Layout(LobbyStatus state, bool join, List<string> labels, List<Func<bool>> actions)
    {
        switch (state)
        {
            case LobbyStatus.Offline:
                labels.Add("Host lobby");
                actions.Add(Session.Host);
                labels.Add(LoadWorldLabel);
                actions.Add(LoadWorldPressed);
                labels.Add(NewWorldLabel);
                actions.Add(NewWorldPressed);
                break;
            case LobbyStatus.Pending:
                labels.Add("Cancel");
                actions.Add(Session.Leave);
                break;
            case LobbyStatus.Hosting:
                labels.Add(InviteLabel);
                actions.Add(InvitePressed);
                labels.Add(LoadWorldLabel);
                actions.Add(LoadWorldPressed);
                labels.Add(NewWorldLabel);
                actions.Add(NewWorldPressed);
                labels.Add("Leave lobby");
                actions.Add(Session.Leave);
                break;
            default:
                if (Session.HostReload)
                {
                    WaitLabels(labels, actions);
                    break;
                }
                if (join)
                {
                    labels.Add(JoinLabel);
                    actions.Add(Session.JoinWorldManual);
                }
                labels.Add("Leave lobby");
                actions.Add(Session.Leave);
                break;
        }
        labels.Add("Back");
        actions.Add(BackPressed);
    }

    private static void ShowButtons(LobbyStatus state, bool join)
    {
        List<string> labels = new List<string>();
        List<Func<bool>> actions = new List<Func<bool>>();
        Layout(state, join, labels, actions);
        joinSlot = labels.IndexOf(JoinLabel);
        for (int i = 0; i < buttons.Length; i++)
        {
            if (i >= labels.Count)
            {
                buttons[i].gameObject.SetActive(false);
                continue;
            }
            SetButton(buttons[i], labels[i], actions[i]);
        }
    }

    private static void SetButton(UnityEngine.UI.Button button, string label, Func<bool> action)
    {
        foreach (TMP_Text text in button.GetComponentsInChildren<TMP_Text>(true))
        {
            text.text = label;
        }
        button.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        button.onClick.AddListener(() => Press(label, action));
        button.interactable = DisabledReason(label).Length == 0;
        button.gameObject.SetActive(true);
    }

    internal static string DisabledReason(string label)
    {
        if (label == JoinLabel) return Session.JoinBlock();
        return "";
    }

    internal static void PagePress(string label)
    {
        List<string> labels = new List<string>();
        List<Func<bool>> actions = new List<Func<bool>>();
        Layout(Lobby.Status, Session.HostInWorld, labels, actions);
        int at = labels.IndexOf(label);
        if (at < 0)
        {
            CoopLog.Log.Info("Lobby button '" + label + "' ignored: not in the current layout");
        }
        else if (DisabledReason(label).Length > 0)
        {
            CoopLog.Log.Info("Lobby button '" + label + "' ignored: " + DisabledReason(label));
        }
        else
        {
            Press(label, actions[at]);
            return;
        }
        LobbyPage.PushNow();
    }

    private static void Press(string label, Func<bool> action)
    {
        if (label == lastLabel && Time.unscaledTime - lastPressAt < PressGuard)
        {
            CoopLog.Log.Info("Lobby button '" + label + "' ignored: pressed again within 300 ms");
            LobbyPage.PushNow();
            return;
        }
        lastLabel = label;
        lastPressAt = Time.unscaledTime;
        notice = "";
        LobbyPage.ClearNotice();
        bool ok = false;
        try
        {
            ok = action();
        }
        catch (Exception e)
        {
            CoopLog.Once("MainMenuButton.press", e);
        }
        CoopLog.Log.Info("Lobby button '" + label + "' result " + ok + ", status " + Lobby.Status + ", error '" + Lobby.Error + "'");
        if (panel != null && panel.activeSelf) RefreshPanel();
        LobbyPage.PushNow();
    }

    private static bool InvitePressed()
    {
        LobbyPage.HoldOverlay();
        bool ok = false;
        try
        {
            ok = Session.Invite();
        }
        catch (Exception e)
        {
            CoopLog.Once("MainMenuButton.invite", e);
        }
        if (!ok)
        {
            LobbyPage.ReleaseOverlay();
            notice = Lobby.Error.Length > 0 ? Lobby.Error : InviteFailed;
            return false;
        }
        if (!Steam.OverlayEnabled) notice = OverlayOff;
        return true;
    }

    internal static void LobbyLoadStarted()
    {
        if (pageUp || (panel != null && panel.activeSelf)) closePending = true;
    }

    private static bool BackPressed()
    {
        closePending = true;
        return true;
    }

    private static bool LoadWorldPressed()
    {
        if (MainMenu.Instance == null)
        {
            notice = LoadWorldFailed;
            return false;
        }
        closePending = true;
        pendingCall = LoadCall;
        return true;
    }

    private static void LoadWorld()
    {
        try
        {
            MainMenu.Instance.LoadGame();
        }
        catch (Exception e)
        {
            CoopLog.Once("MainMenuButton.loadworld", e);
        }
    }

    private static void SetText(TMP_Text label, string text)
    {
        if (label.text != text) label.text = text;
    }
}
