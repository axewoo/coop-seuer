using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static class AppearanceSync
{
    private const string Channel = "look";
    private const float CheckInterval = 2f;
    private const float ResendInterval = 12f;
    public static bool Enabled;
    private static float nextCheck;
    private static int lastScene = int.MinValue;
    private static readonly Dictionary<ulong, Sent> sent = new Dictionary<ulong, Sent>();

    private struct Sent
    {
        public string Text;
        public float At;
    }

    public static void Tick()
    {
        try
        {
            if (!Enabled) return;
            float now = Time.unscaledTime;
            if (SceneChanged()) nextCheck = 0f;
            if (now < nextCheck) return;
            nextCheck = now + CheckInterval;
            List<ulong> others = Session.Peers;
            foreach (ulong id in new List<ulong>(sent.Keys))
            {
                if (!others.Contains(id)) sent.Remove(id);
            }
            if (others.Count == 0) return;
            string text = Local();
            if (text == null) return;
            foreach (ulong id in others)
            {
                Sent last;
                if (sent.TryGetValue(id, out last) && last.Text == text && now - last.At < ResendInterval) continue;
                if (Session.Send(id, Channel, text, true))
                {
                    sent[id] = new Sent { Text = text, At = now };
                }
                else CoopLog.Log.Debug("appearance.send", "APP to " + id + " was not sent");
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("AppearanceSync.Tick", e);
        }
    }

    private static bool SceneChanged()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (scene.handle == lastScene) return false;
        lastScene = scene.handle;
        sent.Clear();
        CoopLog.Log.Info("look sync: " + (string.IsNullOrEmpty(scene.name) ? "a new scene" : scene.name) + " loaded, the look is sent to every member again");
        return true;
    }

    public static void OnChanged(PlayerLook.Look look)
    {
        nextCheck = 0f;
    }

    public static void Handle(ulong sender, string text)
    {
        try
        {
            if (!Session.Peers.Contains(sender))
            {
                CoopLog.Log.Debug("appearance.sender", "ignored APP from non-member " + sender);
                return;
            }
            bool first = !Puppet.HasLook(sender);
            Puppet.SetLook(sender, text);
            if (first && Puppet.HasLook(sender)) sent.Remove(sender);
        }
        catch (Exception e)
        {
            CoopLog.Once("AppearanceSync.Handle", e);
        }
    }

    public static string Local()
    {
        string pinned = LookPin.Text();
        if (pinned != null) return pinned;
        PlayerLook.Look look = Capture();
        if (look == null)
        {
            CoopLog.Once("AppearanceSync.unavailable", "the local look is not readable, no APP is sent");
            return null;
        }
        return Look.FromCore(look, GameRefs.LocalAppearance).Text();
    }

    private static PlayerLook.Look Capture()
    {
        if (!PlayerLook.IsAvailable) return null;
        Appearance appearance = GameRefs.LocalAppearance;
        if (!GameRefs.AppearanceReady(appearance)) return null;
        return PlayerLook.Capture(appearance);
    }
}
