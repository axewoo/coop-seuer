using System;
using System.Collections.Generic;
using System.Globalization;
using ObenseuerCore;

internal static class CoopChatHistory
{
    public const string Prefix = "CHATH|";
    private const int Max = 50;
    private const int Fields = 6;
    private static readonly char[] Separator = new char[] { '|' };
    private static readonly List<ulong> senders = new List<ulong>();
    private static readonly List<string> bodies = new List<string>();
    private static readonly HashSet<ulong> sent = new HashSet<ulong>();
    private static int run;
    private static int lastRun = -1;
    private static int lastIndex = -1;
    private static bool shown;

    public static void Clear()
    {
        senders.Clear();
        bodies.Clear();
        sent.Clear();
        run = 0;
        lastRun = -1;
        lastIndex = -1;
        shown = false;
    }

    public static void Leave(ulong peer)
    {
        sent.Remove(peer);
    }

    public static void Record(ulong sender, string text)
    {
        senders.Add(sender);
        bodies.Add(text);
        if (bodies.Count <= Max) return;
        int drop = bodies.Count - Max;
        bodies.RemoveRange(0, drop);
        senders.RemoveRange(0, drop);
    }

    public static void Send(ulong peer)
    {
        if (!Session.IsHost || peer == Session.Me || bodies.Count == 0) return;
        if (!Session.Peers.Contains(peer) || !sent.Add(peer)) return;
        run++;
        string seq = run.ToString(CultureInfo.InvariantCulture);
        string total = bodies.Count.ToString(CultureInfo.InvariantCulture);
        for (int i = 0; i < bodies.Count; i++)
        {
            string packet = Prefix + seq + "|" + i.ToString(CultureInfo.InvariantCulture) + "|" + total + "|" + senders[i].ToString(CultureInfo.InvariantCulture) + "|" + bodies[i];
            Session.Send(peer, CoopChat.Channel, packet, true);
        }
    }

    public static void Handle(ulong from, string text)
    {
        try
        {
            if (from != Session.HostId) return;
            string[] parts = text.Split(Separator, Fields);
            if (parts.Length != Fields) return;
            int seq;
            int index;
            int total;
            ulong sender;
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out seq)) return;
            if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out index)) return;
            if (!int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out total)) return;
            if (!ulong.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out sender)) return;
            string body = parts[5];
            if (seq < 0 || total < 1 || total > Max || index < 0 || index >= total) return;
            if (body.Length == 0 || body.Length > CoopChat.MaxChars) return;
            if (seq < lastRun) return;
            if (seq > lastRun)
            {
                if (shown) return;
                lastRun = seq;
                lastIndex = -1;
            }
            if (index != lastIndex + 1) return;
            lastIndex = index;
            bool known = sender == Session.HostId || sender == Session.Me || Session.Peers.Contains(sender);
            if (!shown) CoopChat.Note("Chat history, " + total + (total == 1 ? " earlier line" : " earlier lines"));
            shown = true;
            CoopChat.Append(sender, body, known).Grey = true;
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopChatHistory.Handle", e);
        }
    }
}
