using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ObenseuerCore;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

internal static class CoopMap
{
    private const float LabelSize = 14f;
    private const float LabelDrop = 20f;
    private const float LabelWidth = 160f;
    private const float LabelHeight = 22f;
    private const float CanvasShown = 0.05f;
    private const float MapHalf = 0.5f;
    private const float MarkerReportSeconds = 60f;
    private const int PaletteCount = 3;
    private static readonly FieldInfo markerField = AccessTools.Field(typeof(Map), "playerMarker");
    private static readonly FieldInfo sizeField = AccessTools.Field(typeof(Map), "mapsize");
    private static readonly FieldInfo canvasField = AccessTools.Field(typeof(Map), "imageCanvas");
    private static readonly Dictionary<ulong, Marker> markers = new Dictionary<ulong, Marker>();
    private static readonly List<ulong> gone = new List<ulong>();
    private static readonly Color HostColor = new Color(1f, 0.78f, 0.15f, 1f);
    private static readonly Color[] palette = new Color[]
    {
        new Color(0.35f, 0.85f, 1f, 1f),
        new Color(0.45f, 1f, 0.5f, 1f),
        new Color(1f, 0.5f, 0.85f, 1f)
    };
    private static bool installed;
    private static int frame;
    private static int created;
    private static int destroyed;
    private static float nextMarkerReport;
    private static int lastScene = int.MinValue;
    private static string lastArea;
    private static TMP_FontAsset font;
    private static BepInEx.Configuration.ConfigEntry<bool> show;

    private sealed class Marker
    {
        public GameObject Root;
        public RectTransform Rect;
        public RectTransform LabelRect;
        public TextMeshProUGUI Label;
        public string Name;
        public int Seen;
    }

    public static void Install()
    {
        if (installed) return;
        try
        {
            show = Plugin.Instance.Config.Bind("Map", "ShowOtherPlayers", true, "Draws every other player who is in the scene the map shows as a marker on the game's map, gold for the host.");
            if (markerField == null || sizeField == null || canvasField == null)
            {
                CoopLog.Log.Error("coop map: " + SelfCheck());
                return;
            }
            installed = true;
            CoopLog.Log.Info("coop map: " + SelfCheck());
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopMap.Install", e);
        }
    }

    public static string SelfCheck()
    {
        return "map markers " + (installed ? "installed" : "not installed")
            + ": Map.playerMarker " + (markerField == null ? "missing" : "resolved")
            + ", Map.mapsize " + (sizeField == null ? "missing" : "resolved")
            + ", Map.imageCanvas " + (canvasField == null ? "missing" : "resolved")
            + ", TMP font " + (font == null ? "looked up when a label is built" : "resolved");
    }

    public static void Tick()
    {
        if (!installed) return;
        try
        {
            Report();
            if (show == null || !show.Value || !Session.IsInLobby)
            {
                DestroyAll();
                return;
            }
            Map map = Map.instance;
            if (!Shown(map))
            {
                DestroyAll();
                return;
            }
            RectTransform host = Host(map);
            if (host == null || host.parent == null)
            {
                DestroyAll();
                return;
            }
            info_map info = info_map.instance;
            RectTransform size = Size(map);
            if (info == null || info.mapImage == null || size == null)
            {
                DestroyAll();
                return;
            }
            string area = GameRefs.AreaId;
            int scene = SceneManager.GetActiveScene().handle;
            if (area == null || scene != lastScene || area != lastArea)
            {
                DestroyAll();
                lastScene = scene;
                lastArea = area;
                if (area == null) return;
            }
            frame++;
            foreach (PresenceRecord record in Session.Remotes())
            {
                if (record == null || record.Area != area) continue;
                if (!Session.Peers.Contains(record.Peer)) continue;
                Marker marker = Ensure(record, host, map.transform);
                if (marker == null) continue;
                marker.Seen = frame;
                Place(marker, record, host, size, info);
            }
            gone.Clear();
            foreach (KeyValuePair<ulong, Marker> pair in markers)
            {
                if (pair.Value.Seen != frame) gone.Add(pair.Key);
            }
            for (int i = 0; i < gone.Count; i++) Drop(gone[i]);
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopMap.Tick", e);
        }
    }

    public static void Reset()
    {
        DestroyAll();
        lastScene = int.MinValue;
        lastArea = null;
    }

    private static bool Shown(Map map)
    {
        if (map == null || !map.gameObject.activeInHierarchy) return false;
        if (canvasField == null) return true;
        CanvasGroup group = canvasField.GetValue(map) as CanvasGroup;
        return group == null || group.alpha > CanvasShown;
    }

    private static RectTransform Host(Map map)
    {
        if (map == null || markerField == null) return null;
        GameObject host = markerField.GetValue(map) as GameObject;
        if (host == null) return null;
        RectTransform rect = host.transform as RectTransform;
        if (rect == null)
        {
            CoopLog.Once("CoopMap.prefab", "the game's player marker is not a UI object, map markers are off");
            return null;
        }
        return rect;
    }

    private static RectTransform Size(Map map)
    {
        if (map == null || sizeField == null) return null;
        RectTransform size = sizeField.GetValue(map) as RectTransform;
        return size != null ? size : map.GetComponent<RectTransform>();
    }

    private static Marker Ensure(PresenceRecord record, RectTransform host, Transform mapRoot)
    {
        Marker marker;
        if (markers.TryGetValue(record.Peer, out marker))
        {
            if (marker.Root != null)
            {
                Rename(marker, record);
                return marker;
            }
            markers.Remove(record.Peer);
        }
        marker = Create(record, host, mapRoot);
        if (marker != null)
        {
            created++;
            markers[record.Peer] = marker;
        }
        return marker;
    }

    private static Marker Create(PresenceRecord record, RectTransform host, Transform mapRoot)
    {
        Transform parent = host.parent;
        if (parent == null) return null;
        Color color = ColorOf(record.Peer);
        GameObject root = new GameObject("CoopMapMarker", typeof(RectTransform));
        try
        {
            RectTransform rect = root.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            root.layer = parent.gameObject.layer;
            CopyRect(rect, host);
            Image source = host.GetComponent<Image>();
            if (source != null)
            {
                Image icon = root.AddComponent<Image>();
                icon.sprite = source.sprite;
                icon.type = source.type;
                icon.preserveAspect = source.preserveAspect;
                icon.raycastTarget = false;
                icon.color = color;
            }
            else
            {
                RawImage rawSource = host.GetComponent<RawImage>();
                if (rawSource == null)
                {
                    CoopLog.Once("CoopMap.prefab", "the game's player marker has no image, map markers are off");
                    UnityEngine.Object.Destroy(root);
                    return null;
                }
                RawImage raw = root.AddComponent<RawImage>();
                raw.texture = rawSource.texture;
                raw.uvRect = rawSource.uvRect;
                raw.raycastTarget = false;
                raw.color = color;
            }
            Marker marker = new Marker();
            marker.Root = root;
            marker.Rect = rect;
            marker.Name = record.Name;
            marker.Label = BuildLabel(parent, mapRoot, record.Name, color);
            marker.LabelRect = marker.Label == null ? null : marker.Label.rectTransform;
            return marker;
        }
        catch (Exception e)
        {
            UnityEngine.Object.Destroy(root);
            CoopLog.Once("CoopMap.Create", e);
            return null;
        }
    }

    private static void CopyRect(RectTransform to, RectTransform from)
    {
        to.anchorMin = from.anchorMin;
        to.anchorMax = from.anchorMax;
        to.pivot = from.pivot;
        to.sizeDelta = from.sizeDelta;
        to.localPosition = Vector3.zero;
        to.localScale = from.localScale;
        to.localEulerAngles = Vector3.zero;
    }

    private static TextMeshProUGUI BuildLabel(Transform parent, Transform mapRoot, string name, Color color)
    {
        GameObject go = new GameObject("CoopMapLabel", typeof(RectTransform), typeof(TextMeshProUGUI));
        try
        {
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            go.layer = parent.gameObject.layer;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(LabelWidth, LabelHeight);
            rect.localPosition = Vector3.zero;
            rect.localScale = Vector3.one;
            rect.localEulerAngles = Vector3.zero;
            TextMeshProUGUI label = go.GetComponent<TextMeshProUGUI>();
            TMP_FontAsset asset = Font(mapRoot);
            if (asset != null) label.font = asset;
            label.fontSize = LabelSize;
            label.alignment = TextAlignmentOptions.Center;
            label.color = color;
            label.richText = false;
            label.raycastTarget = false;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Overflow;
            label.text = name == null ? "" : name;
            return label;
        }
        catch (Exception e)
        {
            UnityEngine.Object.Destroy(go);
            CoopLog.Once("CoopMap.label", e);
            return null;
        }
    }

    private static TMP_FontAsset Font(Transform mapRoot)
    {
        if (font != null) return font;
        if (mapRoot != null)
        {
            TMP_Text text = mapRoot.GetComponentInChildren<TMP_Text>(true);
            if (text != null && text.font != null) font = text.font;
        }
        if (font == null) font = TMP_Settings.defaultFontAsset;
        return font;
    }

    private static void Place(Marker marker, PresenceRecord record, RectTransform host, RectTransform size, info_map info)
    {
        Vector3 relative = info.GetRelativePosition(record.Position);
        bool inside = relative.x >= -MapHalf && relative.x <= MapHalf && relative.z >= -MapHalf && relative.z <= MapHalf;
        if (marker.Root.activeSelf != inside) marker.Root.SetActive(inside);
        if (marker.LabelRect != null && marker.LabelRect.gameObject.activeSelf != inside) marker.LabelRect.gameObject.SetActive(inside);
        if (!inside) return;
        float scale = Mathf.Abs(host.localScale.y);
        if (scale < 0.0001f) scale = 1f;
        Vector3 at = new Vector3(relative.x * size.rect.width, relative.z * size.rect.height, 0f);
        marker.Rect.localPosition = at;
        marker.Rect.localEulerAngles = new Vector3(0f, 0f, record.Yaw * -1f);
        marker.Rect.localScale = host.localScale;
        if (marker.LabelRect != null)
        {
            marker.LabelRect.localPosition = at + new Vector3(0f, -LabelDrop * scale, 0f);
            marker.LabelRect.localScale = host.localScale;
            marker.LabelRect.localEulerAngles = Vector3.zero;
        }
    }

    private static void Rename(Marker marker, PresenceRecord record)
    {
        if (marker.Name == record.Name) return;
        marker.Name = record.Name;
        if (marker.Label != null) marker.Label.text = record.Name == null ? "" : record.Name;
    }

    private static Color ColorOf(ulong id)
    {
        if (id == Session.HostId) return HostColor;
        return palette[(int)(id % (ulong)PaletteCount)];
    }

    private static void Drop(ulong id)
    {
        Marker marker;
        if (!markers.TryGetValue(id, out marker)) return;
        markers.Remove(id);
        DestroyMarker(marker);
    }

    private static void DestroyMarker(Marker marker)
    {
        destroyed++;
        if (marker.Root != null) UnityEngine.Object.Destroy(marker.Root);
        if (marker.LabelRect != null) UnityEngine.Object.Destroy(marker.LabelRect.gameObject);
    }

    private static void DestroyAll()
    {
        if (markers.Count == 0) return;
        foreach (KeyValuePair<ulong, Marker> pair in markers) DestroyMarker(pair.Value);
        markers.Clear();
    }

    private static void Report()
    {
        float now = Time.unscaledTime;
        if (nextMarkerReport > 0f && now < nextMarkerReport) return;
        nextMarkerReport = now + MarkerReportSeconds;
        if (created == 0 && destroyed == 0) return;
        CoopDebug.Event("map", "markers", created + " map markers created and " + destroyed + " destroyed in the last " + MarkerReportSeconds + " s, " + markers.Count + " up now");
        created = 0;
        destroyed = 0;
    }
}
