using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

internal static class CoopPopup
{
    private sealed class Card
    {
        public string Key;
        public string Title;
        public string Text;
        public bool Posted;
        public float Expires;
        public float NoticeUntil;
    }

    private const float Lifetime = 8f;
    private const float ReadyTimeout = 10f;
    private const float RetryOpenAfter = 60f;
    private const float NoticeWait = 20f;
    private const int MaxQueued = 3;
    private const int MaxTickFails = 3;
    private static readonly List<Card> queue = new List<Card>();
    private static Card current;
    private static bool pageDead;
    private static int tickFails;
    private static float giveUpUntil;

    public static void Show(string title, string text)
    {
        Show("", title, text);
    }

    public static void Show(string key, string title, string text)
    {
        try
        {
            if (string.IsNullOrEmpty(text)) return;
            if (queue.Count >= MaxQueued) queue.RemoveAt(0);
            Card card = new Card();
            card.Key = key;
            card.Title = string.IsNullOrEmpty(title) ? "Quest" : title;
            card.Text = text;
            queue.Add(card);
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopPopup.Show", e);
        }
    }

    public static void Tick()
    {
        try
        {
            if (pageDead || WaitingForNotice())
            {
                FlushByNotice();
                tickFails = 0;
                return;
            }
            TickPage();
            tickFails = 0;
        }
        catch (TypeLoadException e)
        {
            PageFailed(e);
        }
        catch (FileNotFoundException e)
        {
            PageFailed(e);
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopPopup.Tick", e);
            TickFailed();
        }
    }

    public static void Reset()
    {
        EndLobby(true);
    }

    public static void EndLobby(bool deliberate)
    {
        try
        {
            if (deliberate) queue.Clear();
            current = null;
            giveUpUntil = 0f;
            pageDead = false;
            tickFails = 0;
            ResetPage();
        }
        catch (TypeLoadException e)
        {
            PageFailed(e);
        }
        catch (FileNotFoundException e)
        {
            PageFailed(e);
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopPopup.EndLobby", e);
        }
    }

    private static void TickPage()
    {
        if (Screen.fullScreenMode == FullScreenMode.ExclusiveFullScreen)
        {
            if (CoopPopupPage.HasView) CoopPopupPage.Close();
            FlushByNotice();
            return;
        }
        if (LobbyPage.IsOpen || LobbyPage.OverlayHeld)
        {
            Defer();
            return;
        }
        if (current == null && queue.Count == 0)
        {
            CoopPopupPage.Hide();
            return;
        }
        if (CoopPopupPage.IsClosed)
        {
            GiveUp("closed", "the notice page closed itself");
            CoopPopupPage.Close();
            FlushByNotice();
            return;
        }
        if (Time.unscaledTime < giveUpUntil)
        {
            FlushByNotice();
            return;
        }
        if (current == null)
        {
            current = queue[0];
            queue.RemoveAt(0);
        }
        if (!CoopPopupPage.HasView && !CoopPopupPage.Open())
        {
            GiveUp("open", "the notice page did not open: " + CoopPopupPage.LastError);
            FlushByNotice();
            return;
        }
        if (!CoopPopupPage.Ready)
        {
            if (CoopPopupPage.Age > ReadyTimeout)
            {
                GiveUp("ready", "the notice page did not report ready within " + ReadyTimeout + " s");
                CoopPopupPage.Close();
                FlushByNotice();
            }
            return;
        }
        if (!current.Posted)
        {
            if (!CoopPopupPage.Post(current.Title, current.Text))
            {
                GiveUp("post", "the notice page refused the message: " + CoopPopupPage.LastError);
                CoopPopupPage.Close();
                FlushByNotice();
                return;
            }
            current.Posted = true;
            current.Expires = Time.unscaledTime + Lifetime;
            return;
        }
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            CoopPopupPage.TakeDismissed();
            current = null;
            CoopPopupPage.Hide();
            return;
        }
        if (CoopPopupPage.TakeDismissed())
        {
            current = null;
            CoopPopupPage.Hide();
            return;
        }
        if (Time.unscaledTime < current.Expires) return;
        current = null;
        if (queue.Count == 0) CoopPopupPage.Hide();
    }

    private static void ResetPage()
    {
        CoopPopupPage.Close();
    }

    private static void PageFailed(Exception e)
    {
        PageFailed(e.ToString());
    }

    private static void PageFailed(string reason)
    {
        pageDead = true;
        CoopLog.Once("CoopPopup.page", "the quest popup page failed: " + reason);
        FlushByNotice();
    }

    private static void Defer()
    {
        if (current != null)
        {
            current.Posted = false;
            Requeue(current);
        }
        current = null;
        CoopPopupPage.Hide();
    }

    private static bool WaitingForNotice()
    {
        return queue.Count > 0 && queue[0].NoticeUntil != 0f;
    }

    private static void Requeue(Card card)
    {
        if (card == null) return;
        queue.Insert(0, card);
        while (queue.Count > MaxQueued) queue.RemoveAt(queue.Count - 1);
    }

    private static void FlushByNotice()
    {
        float now = Time.unscaledTime;
        if (current != null)
        {
            Requeue(current);
            current = null;
        }
        while (queue.Count > 0)
        {
            Card card = queue[0];
            if (card.NoticeUntil != 0f && now >= card.NoticeUntil)
            {
                queue.RemoveAt(0);
                CoopLog.Once("CoopPopup.noticeDrop", "a quest popup was dropped: the game notice channel was not ready within " + NoticeWait + " s");
                continue;
            }
            if (!Fallback(card)) return;
            queue.RemoveAt(0);
        }
    }

    private static bool Fallback(Card card)
    {
        if (card == null) return true;
        string key = string.IsNullOrEmpty(card.Key) ? card.Title : card.Key;
        if (CoopNotice.Show("popup|" + key, card.Text, 5f)) return true;
        if (card.NoticeUntil == 0f) card.NoticeUntil = Time.unscaledTime + NoticeWait;
        return false;
    }

    private static void GiveUp(string reason, string detail)
    {
        giveUpUntil = Time.unscaledTime + RetryOpenAfter;
        CoopLog.Once("CoopPopup.giveUp." + reason, "the quest popup uses the notice channel: " + detail);
    }

    private static void TickFailed()
    {
        tickFails++;
        if (tickFails < MaxTickFails) return;
        PageFailed("the notice page threw " + MaxTickFails + " ticks in a row");
    }
}
