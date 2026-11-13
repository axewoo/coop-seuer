using System;
using System.Collections.Generic;
using ObenseuerCore;
using UnityEngine;

internal static class CoopPlayersPanel
{
    private const float CardWidth = 560f;
    private const float MinScale = 0.8f;
    private const float MaxScale = 1.4f;
    private const float RefreshSeconds = 1f;
    private const int CollapsedDiffs = 6;
    private const float LineHeight = 19f;
    private const float RowHeight = 78f;
    private const float RowGap = 6f;
    private const float AvatarSize = 64f;
    private const float Pad = 16f;
    private const float HeaderHeight = 64f;
    private const float FooterHeight = 56f;
    private const float BodySlide = 6f;
    private const float Slice = 16f;
    private const string HintText = "F9 or Escape closes";

    private static readonly Color Ink = new Color(0.91f, 0.894f, 0.855f, 1f);
    private static readonly Color InkDim = new Color(0.604f, 0.604f, 0.604f, 1f);
    private static readonly Color PanelFill = new Color(0.063f, 0.063f, 0.063f, 0.97f);
    private static readonly Color FrameFill = new Color(0.165f, 0.165f, 0.165f, 0.99f);
    private static readonly Color RowFill = new Color(0.188f, 0.188f, 0.188f, 0.5f);
    private static readonly Color RowHiFill = new Color(0.227f, 0.227f, 0.227f, 0.62f);
    private static readonly Color EdgeLine = new Color(0.278f, 0.184f, 0.051f, 1f);
    private static readonly Color AmberLine = new Color(0.69f, 0.478f, 0.11f, 1f);
    private static readonly Color AmberLit = new Color(0.851f, 0.604f, 0.169f, 1f);
    private static readonly Color AmberDeep = new Color(0.443f, 0.29f, 0.059f, 1f);
    private static readonly Color GreenLine = new Color(0.388f, 0.541f, 0.231f, 1f);
    private static readonly Color GreyLine = new Color(0.29f, 0.29f, 0.29f, 1f);
    private static readonly Color AmberDot = new Color(0.886f, 0.573f, 0.325f, 1f);
    private static readonly GUIContent HostTag = new GUIContent("(host)");
    private static readonly GUIContent YouTag = new GUIContent("(you)");
    private static readonly GUIContent VersionContent = new GUIContent("Co-op " + Plugin.Version);
    private static readonly GUIContent CloseTag = new GUIContent("Close (F9)");
    private static readonly List<ulong> memberIds = new List<ulong>();
    private static readonly List<RowData> rows = new List<RowData>();
    private static readonly List<PresenceRecord> records = new List<PresenceRecord>();
    private static readonly List<string> diffs = new List<string>();
    private static readonly List<string> elsewhere = new List<string>();
    private static readonly float[] sliceX = new float[3];
    private static readonly float[] sliceY = new float[3];
    private static readonly float[] sliceW = new float[3];
    private static readonly float[] sliceH = new float[3];
    private static readonly float[] uvX = new float[3];
    private static readonly float[] uvY = new float[3];
    private static readonly float[] uvW = new float[3];
    private static readonly float[] uvH = new float[3];
    private static Vector2 scroll;
    private static float nextRefresh;
    private static float styleScale = -1f;
    private static bool showAllDiffs;
    private static string hostLine = "";
    private static int diffTotal;
    private static int waitingPeers;
    private static bool allMatch;
    private static Texture2D rounded;
    private static float hostChipWidth;
    private static float youChipWidth;
    private static GUIStyle title;
    private static GUIStyle subtitle;
    private static GUIStyle version;
    private static GUIStyle section;
    private static GUIStyle name;
    private static GUIStyle initial;
    private static GUIStyle chipText;
    private static GUIStyle status;
    private static GUIStyle statusDim;
    private static GUIStyle small;
    private static GUIStyle green;
    private static GUIStyle closeText;
    private static GUIStyle hint;
    private static GUIStyle invisible;

    private sealed class RowData
    {
        public ulong Id;
        public bool You;
        public bool Host;
        public GUIContent Name = new GUIContent("");
        public GUIContent Initial = new GUIContent("?");
        public GUIContent Line = new GUIContent("");
        public GUIContent Version = new GUIContent("");
        public GUIContent Mods = new GUIContent("");
        public Color Dot = GreyLine;
        public Color ModsColor = InkDim;
    }

    private static Texture2D Rounded
    {
        get
        {
            if (rounded == null) rounded = BuildRounded(64, 10);
            return rounded;
        }
    }

    public static void Reset()
    {
        scroll = Vector2.zero;
        nextRefresh = 0f;
        showAllDiffs = false;
        hostLine = "";
        diffTotal = 0;
        waitingPeers = 0;
        allMatch = false;
        records.Clear();
        rows.Clear();
        elsewhere.Clear();
        memberIds.Clear();
        diffs.Clear();
    }

    public static void Tick()
    {
        try
        {
            if (!Session.IsInLobby) return;
            Styles(Mathf.Clamp(Screen.height / 1080f, MinScale, MaxScale));
            Refresh(false);
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopPlayersPanel.Tick", e);
        }
    }

    public static void Draw()
    {
        try
        {
            float scale = Mathf.Clamp(Screen.height / 1080f, MinScale, MaxScale);
            Styles(scale);
            Refresh(false);
            float width = CardWidth * scale;
            float height = Mathf.Min((HeaderHeight + FooterHeight + 2f * Pad) * scale + BodyHeight() * scale, Screen.height - 24f * scale);
            Rect card = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);
            int depth = GUI.depth;
            GUI.depth = 0;
            try
            {
                Fill(new Rect(card.x - 1f, card.y - 1f, card.width + 2f, card.height + 2f), FrameFill);
                DrawBox(card, PanelFill);
                Fill(new Rect(card.x + 10f * scale, card.y + 1f, card.width - 20f * scale, 3f * scale), AmberLine);
                DrawHeader(new Rect(card.x, card.y, card.width, HeaderHeight * scale), scale);
                DrawBody(new Rect(card.x, card.y + HeaderHeight * scale, card.width, card.height - (HeaderHeight + FooterHeight) * scale), scale);
                DrawFooter(new Rect(card.x, card.yMax - FooterHeight * scale, card.width, FooterHeight * scale), scale);
            }
            finally
            {
                GUI.depth = depth;
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopPlayersPanel.Draw", e);
        }
    }

    private static void Refresh(bool force)
    {
        if (!force && Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + RefreshSeconds;
        memberIds.Clear();
        memberIds.AddRange(Lobby.Members);
        memberIds.Sort(CompareMembers);
        CoopAvatars.Sync(memberIds);
        records.Clear();
        records.AddRange(Session.Remotes());
        for (int i = 0; i < memberIds.Count; i++)
        {
            if (i >= rows.Count) rows.Add(new RowData());
            Fill(rows[i], memberIds[i]);
        }
        for (int i = rows.Count - 1; i >= memberIds.Count; i--) rows.RemoveAt(i);
        BuildElsewhere();
        BuildDiffs();
        hostLine = (Session.IsHost ? "Hosting" : "Joined " + Session.DisplayName(Session.HostId) + "'s lobby") + "  -  " + memberIds.Count + " of " + Session.MaxMembers + " players";
    }

    private static int CompareMembers(ulong a, ulong b)
    {
        ulong host = Session.HostId;
        if (a == host && b != host) return -1;
        if (b == host && a != host) return 1;
        int byName = string.Compare(Session.DisplayName(a), Session.DisplayName(b), StringComparison.OrdinalIgnoreCase);
        if (byName != 0) return byName;
        return a.CompareTo(b);
    }

    private static void Fill(RowData row, ulong id)
    {
        bool self = id == Session.Me;
        row.Id = id;
        row.You = self;
        row.Host = id == Session.HostId;
        string display = Session.DisplayName(id);
        row.Name.text = display;
        row.Initial.text = Initial(display);
        string area = self ? GameRefs.AreaId : Session.AreaOf(id);
        bool inWorld = self ? Session.LocalInWorld : area != null;
        bool peer = self || Session.Peers.Contains(id);
        if (inWorld)
        {
            row.Dot = GreenLine;
            row.Line.text = Join("connected", Where(id, self, area, inWorld));
        }
        else if (self && Session.JoinPending)
        {
            row.Dot = AmberDot;
            row.Line.text = "joining";
        }
        else if (row.Host && Session.HostReload)
        {
            row.Dot = AmberDot;
            row.Line.text = "loading world";
        }
        else if (peer)
        {
            row.Dot = AmberDot;
            row.Line.text = "in menu";
        }
        else
        {
            row.Dot = GreyLine;
            row.Line.text = "away";
        }
        string known = self ? Plugin.Version : CoopUi.ModVersion(id);
        row.Version.text = known.Length > 0 ? "Co-op " + known : "";
    }

    private static void BuildElsewhere()
    {
        elsewhere.Clear();
        string here = GameRefs.AreaId;
        for (int i = 0; i < rows.Count; i++)
        {
            RowData row = rows[i];
            if (row.You) continue;
            string area = Session.AreaOf(row.Id);
            if (string.IsNullOrEmpty(area) || area == here) continue;
            elsewhere.Add(row.Name.text + "  -  " + Prettify(area));
        }
    }

    private static void BuildDiffs()
    {
        diffs.Clear();
        diffTotal = 0;
        waitingPeers = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            RowData row = rows[i];
            if (row.You)
            {
                row.Mods.text = "";
                continue;
            }
            if (!CoopUi.ModListKnown(row.Id))
            {
                waitingPeers++;
                row.Mods.text = "mod list pending";
                row.ModsColor = InkDim;
                continue;
            }
            List<string> list = CoopUi.Differences(row.Id);
            if (list.Count == 0)
            {
                row.Mods.text = "mods match";
                row.ModsColor = GreenLine;
                continue;
            }
            row.Mods.text = list.Count == 1 ? "1 mod difference" : list.Count + " mod differences";
            row.ModsColor = AmberLit;
            diffTotal += list.Count;
            for (int j = 0; j < list.Count; j++)
            {
                if (!showAllDiffs && diffs.Count >= CollapsedDiffs) continue;
                diffs.Add(Shorten(list[j], row.Name.text));
            }
        }
        allMatch = diffTotal == 0 && waitingPeers == 0;
    }

    private static string Shorten(string diff, string peer)
    {
        int space = diff.IndexOf(' ');
        if (space <= 0) return diff;
        string id = diff.Substring(0, space);
        int dot = id.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < id.Length) id = id.Substring(dot + 1);
        string rest = diff.Substring(space + 1);
        if (rest.EndsWith(" only here", StringComparison.Ordinal)) return id + " " + rest.Substring(0, rest.Length - 10) + " only on you";
        int there = rest.IndexOf(", there ", StringComparison.Ordinal);
        if (there > 0 && rest.StartsWith("here ", StringComparison.Ordinal)) return id + " " + rest.Substring(5, there - 5) + " on you, " + rest.Substring(there + 8) + " on " + peer;
        return id + " " + rest;
    }

    private static string Join(string first, string second)
    {
        return second.Length == 0 ? first : first + "  -  " + second;
    }

    private static string Where(ulong id, bool self, string area, bool inWorld)
    {
        string place = Prettify(area);
        string distance = Distance(id, self, area, inWorld);
        if (distance.Length == 0) return place;
        return place.Length == 0 ? distance : place + "  -  " + distance;
    }

    private static string Distance(ulong id, bool self, string area, bool inWorld)
    {
        if (self || !inWorld || area != GameRefs.AreaId) return "";
        Transform local = GameRefs.LocalPlayer;
        if (local == null) return "";
        for (int i = 0; i < records.Count; i++)
        {
            if (records[i].Peer != id) continue;
            float distance = Vector3.Distance(local.position, records[i].Position);
            if (float.IsNaN(distance) || float.IsInfinity(distance)) return "";
            return Mathf.RoundToInt(distance) + " m";
        }
        return "";
    }

    private static string Prettify(string area)
    {
        if (string.IsNullOrEmpty(area)) return "";
        if (area.StartsWith("Interior ", StringComparison.Ordinal) || area.StartsWith("Exterior ", StringComparison.Ordinal)) area = area.Substring(9);
        if (area.Length > 26) area = area.Substring(0, 25) + "...";
        return area;
    }

    private static string Initial(string display)
    {
        for (int i = 0; i < display.Length; i++)
        {
            if (char.IsLetterOrDigit(display[i])) return char.ToUpperInvariant(display[i]).ToString();
        }
        return "?";
    }

    private static float BodyHeight()
    {
        float height = BodySlide + rows.Count * (RowHeight + RowGap);
        if (elsewhere.Count > 0) height += LineHeight * (elsewhere.Count + 1) + 10f;
        height += LineHeight + 12f;
        if (allMatch) height += LineHeight;
        else
        {
            height += diffs.Count * LineHeight;
            if (waitingPeers > 0) height += LineHeight;
        }
        if (diffTotal > diffs.Count || (showAllDiffs && diffTotal > CollapsedDiffs)) height += 30f;
        return height + BodySlide;
    }

    private static void DrawHeader(Rect r, float scale)
    {
        float pad = Pad * scale;
        GUI.Label(new Rect(r.x + pad, r.y + 11f * scale, r.width - 2f * pad, 30f * scale), "CO-OP PLAYERS", title);
        GUI.Label(new Rect(r.x + pad, r.y + 42f * scale, r.width - 2f * pad, 20f * scale), hostLine, subtitle);
        float versionWidth = version.CalcSize(VersionContent).x;
        GUI.Label(new Rect(r.xMax - pad - versionWidth, r.y + 16f * scale, versionWidth, 24f * scale), VersionContent, version);
    }

    private static void DrawBody(Rect area, float scale)
    {
        if (area.height < 16f) return;
        GUILayout.BeginArea(new Rect(area.x + Pad * scale, area.y, area.width - 2f * Pad * scale, area.height));
        try
        {
            scroll = GUILayout.BeginScrollView(scroll, false, true);
            try
            {
                for (int i = 0; i < rows.Count; i++) DrawRow(rows[i], scale);
                DrawElsewhere(scale);
                DrawMods(scale);
            }
            finally
            {
                GUILayout.EndScrollView();
            }
        }
        finally
        {
            GUILayout.EndArea();
        }
    }

    private static void DrawRow(RowData row, float scale)
    {
        float height = RowHeight * scale;
        Rect r = GUILayoutUtility.GetRect(1f, height, GUILayout.ExpandWidth(true));
        DrawBox(r, r.Contains(Event.current.mousePosition) ? RowHiFill : RowFill);
        float avatar = AvatarSize * scale;
        Rect box = new Rect(r.x + 8f * scale, r.y + (height - avatar) * 0.5f, avatar, avatar);
        DrawAvatar(row, box, scale);
        float tx = box.xMax + 12f * scale;
        float tw = r.xMax - tx - 10f * scale;
        float chipWidth = row.Host ? hostChipWidth : (row.You ? youChipWidth : 0f);
        float nameWidth = Mathf.Min(name.CalcSize(row.Name).x + 2f, Mathf.Max(0f, tw - chipWidth));
        GUI.Label(new Rect(tx, r.y + 7f * scale, nameWidth, 26f * scale), row.Name, name);
        if (chipWidth > 0f)
        {
            Rect chip = new Rect(tx + nameWidth + 8f * scale, r.y + 11f * scale, chipWidth, 19f * scale);
            DrawBox(chip, row.Host ? AmberDeep : FrameFill);
            GUI.Label(chip, row.Host ? HostTag : YouTag, chipText);
        }
        float dot = 11f * scale;
        float y2 = r.y + 34f * scale;
        Rect dotRect = new Rect(tx, y2 + (20f * scale - dot) * 0.5f, dot, dot);
        Color old = GUI.color;
        GUI.color = row.Dot;
        GUI.DrawTexture(dotRect, CoopAvatars.Circle);
        GUI.color = old;
        GUI.Label(new Rect(tx + dot + 8f * scale, y2, tw - dot - 8f * scale, 20f * scale), row.Line, status);
        float y3 = r.y + 56f * scale;
        float versionWidth = row.Version.text.Length == 0 ? 0f : statusDim.CalcSize(row.Version).x;
        if (versionWidth > 0f) GUI.Label(new Rect(tx, y3, versionWidth, 18f * scale), row.Version, statusDim);
        if (row.Mods.text.Length > 0)
        {
            GUI.color = row.ModsColor;
            GUI.Label(new Rect(tx + versionWidth + 10f * scale, y3, tw - versionWidth, 18f * scale), row.Mods, statusDim);
            GUI.color = old;
        }
        GUILayout.Space(RowGap * scale);
    }

    private static void DrawAvatar(RowData row, Rect box, float scale)
    {
        Color old = GUI.color;
        GUI.color = row.Host ? AmberLit : EdgeLine;
        GUI.DrawTexture(new Rect(box.x - 2f * scale, box.y - 2f * scale, box.width + 4f * scale, box.height + 4f * scale), CoopAvatars.Circle);
        Texture2D avatar = CoopAvatars.Get(row.Id);
        if (avatar != null)
        {
            GUI.color = Color.white;
            GUI.DrawTexture(box, avatar);
        }
        else
        {
            GUI.color = CoopAvatars.Tint(row.Id);
            GUI.DrawTexture(box, CoopAvatars.Circle);
            GUI.color = Ink;
            GUI.Label(box, row.Initial, initial);
        }
        GUI.color = old;
    }

    private static void DrawElsewhere(float scale)
    {
        if (elsewhere.Count == 0) return;
        GUILayout.Space(10f * scale);
        GUILayout.Label("In other areas", section);
        for (int i = 0; i < elsewhere.Count; i++) GUILayout.Label(elsewhere[i], statusDim);
    }

    private static void DrawMods(float scale)
    {
        GUILayout.Space(10f * scale);
        GUILayout.Label("Mod differences", section);
        if (allMatch)
        {
            GUILayout.Label("All mods match", green);
            return;
        }
        for (int i = 0; i < diffs.Count; i++) GUILayout.Label(diffs[i], statusDim);
        if (waitingPeers > 0)
        {
            GUILayout.Label(waitingPeers == 1 ? "One player has not sent its mod list yet" : waitingPeers + " players have not sent their mod lists yet", statusDim);
        }
        if (diffTotal > diffs.Count || (showAllDiffs && diffTotal > CollapsedDiffs))
        {
            GUILayout.BeginHorizontal();
            if (diffTotal > diffs.Count) GUILayout.Label("and " + (diffTotal - diffs.Count) + " more", statusDim);
            if (GUILayout.Button(showAllDiffs ? "Show less" : "Show all", small))
            {
                showAllDiffs = !showAllDiffs;
                nextRefresh = 0f;
            }
            GUILayout.EndHorizontal();
        }
    }

    private static void DrawFooter(Rect r, float scale)
    {
        float pad = Pad * scale;
        float buttonWidth = 140f * scale;
        float buttonHeight = 34f * scale;
        Fill(new Rect(r.x + pad, r.y, r.width - 2f * pad, 1f), EdgeLine);
        Rect button = new Rect(r.xMax - pad - buttonWidth, r.y + (r.height - buttonHeight) * 0.5f - 2f * scale, buttonWidth, buttonHeight);
        bool hover = button.Contains(Event.current.mousePosition);
        DrawBox(button, hover ? AmberDeep : RowFill);
        if (hover) Fill(new Rect(button.x + 6f * scale, button.y + 1f, button.width - 12f * scale, 2f * scale), AmberLit);
        if (GUI.Button(button, GUIContent.none, invisible)) CoopUi.ClosePlayers();
        GUI.Label(button, CloseTag, closeText);
        GUI.Label(new Rect(r.x + pad, r.y + (r.height - 20f * scale) * 0.5f, r.width - buttonWidth - 3f * pad, 20f * scale), HintText, hint);
    }

    private static void DrawBox(Rect r, Color color)
    {
        Texture2D texture = Rounded;
        float u = Slice / texture.width;
        float v = Slice / texture.height;
        float w = Mathf.Min(Slice, r.width * 0.5f);
        float h = Mathf.Min(Slice, r.height * 0.5f);
        sliceX[0] = r.x;
        sliceX[1] = r.x + w;
        sliceX[2] = r.xMax - w;
        sliceY[0] = r.y;
        sliceY[1] = r.y + h;
        sliceY[2] = r.yMax - h;
        sliceW[0] = w;
        sliceW[1] = r.width - 2f * w;
        sliceW[2] = w;
        sliceH[0] = h;
        sliceH[1] = r.height - 2f * h;
        sliceH[2] = h;
        uvX[0] = 0f;
        uvX[1] = u;
        uvX[2] = 1f - u;
        uvY[0] = 0f;
        uvY[1] = v;
        uvY[2] = 1f - v;
        uvW[0] = u;
        uvW[1] = 1f - 2f * u;
        uvW[2] = u;
        uvH[0] = v;
        uvH[1] = 1f - 2f * v;
        uvH[2] = v;
        Color old = GUI.color;
        GUI.color = color;
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                if (sliceW[col] <= 0f || sliceH[row] <= 0f) continue;
                GUI.DrawTextureWithTexCoords(new Rect(sliceX[col], sliceY[row], sliceW[col], sliceH[row]), texture, new Rect(uvX[col], uvY[row], uvW[col], uvH[row]));
            }
        }
        GUI.color = old;
    }

    private static void Fill(Rect r, Color color)
    {
        Color old = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
    }

    private static void Styles(float scale)
    {
        if (name != null && Mathf.Approximately(styleScale, scale)) return;
        styleScale = scale;
        title = Skin(22f * scale, FontStyle.Bold, Ink, TextAnchor.UpperLeft);
        subtitle = Skin(14f * scale, FontStyle.Normal, InkDim, TextAnchor.UpperLeft);
        version = Skin(14f * scale, FontStyle.Bold, AmberLit, TextAnchor.UpperRight);
        section = Skin(15f * scale, FontStyle.Bold, AmberLine, TextAnchor.UpperLeft);
        name = Skin(18f * scale, FontStyle.Bold, Ink, TextAnchor.UpperLeft);
        name.clipping = TextClipping.Clip;
        initial = Skin(26f * scale, FontStyle.Bold, Ink, TextAnchor.MiddleCenter);
        chipText = Skin(13f * scale, FontStyle.Normal, Ink, TextAnchor.MiddleCenter);
        status = Skin(14f * scale, FontStyle.Normal, Ink, TextAnchor.UpperLeft);
        status.clipping = TextClipping.Clip;
        statusDim = Skin(14f * scale, FontStyle.Normal, InkDim, TextAnchor.UpperLeft);
        statusDim.clipping = TextClipping.Clip;
        green = Skin(14f * scale, FontStyle.Bold, GreenLine, TextAnchor.UpperLeft);
        hint = Skin(13f * scale, FontStyle.Normal, InkDim, TextAnchor.MiddleLeft);
        closeText = Skin(16f * scale, FontStyle.Bold, Ink, TextAnchor.MiddleCenter);
        small = new GUIStyle(GUI.skin.button);
        small.fontSize = Sized(13f * scale);
        small.fixedHeight = 22f * scale;
        invisible = new GUIStyle(GUI.skin.button);
        invisible.normal.background = null;
        invisible.hover.background = null;
        invisible.active.background = null;
        invisible.focused.background = null;
        invisible.onNormal.background = null;
        invisible.onHover.background = null;
        invisible.onActive.background = null;
        invisible.onFocused.background = null;
        hostChipWidth = chipText.CalcSize(HostTag).x + 14f * scale;
        youChipWidth = chipText.CalcSize(YouTag).x + 14f * scale;
    }

    private static GUIStyle Skin(float size, FontStyle style, Color color, TextAnchor anchor)
    {
        GUIStyle result = new GUIStyle(GUI.skin.label);
        result.fontSize = Sized(size);
        result.fontStyle = style;
        result.alignment = anchor;
        result.normal.textColor = color;
        return result;
    }

    private static int Sized(float size)
    {
        return Mathf.Max(8, Mathf.RoundToInt(size));
    }

    private static Texture2D BuildRounded(int size, int radius)
    {
        Color32[] pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float px = x + 0.5f;
                float py = y + 0.5f;
                float cx = px < radius ? radius : (px > size - radius ? size - radius : px);
                float cy = py < radius ? radius : (py > size - radius ? size - radius : py);
                float dx = px - cx;
                float dy = py - cy;
                float cover = Mathf.Clamp01(radius + 0.5f - Mathf.Sqrt(dx * dx + dy * dy));
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(cover * 255f));
            }
        }
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.name = "coop-panel-round";
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.SetPixels32(pixels);
        texture.Apply();
        return texture;
    }
}
