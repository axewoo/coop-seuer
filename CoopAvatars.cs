using System;
using System.Collections.Generic;
using ObenseuerCore;
using Steamworks;
using UnityEngine;

internal static class CoopAvatars
{
    private const float RetrySeconds = 3f;
    private const int MaxTries = 8;
    private const int CirclePixels = 64;
    private const int Channels = 4;
    private static readonly Dictionary<ulong, Texture2D> textures = new Dictionary<ulong, Texture2D>();
    private static readonly Dictionary<ulong, float> retryAt = new Dictionary<ulong, float>();
    private static readonly Dictionary<ulong, int> attempts = new Dictionary<ulong, int>();
    private static readonly Dictionary<ulong, Color> tints = new Dictionary<ulong, Color>();
    private static readonly List<ulong> dropped = new List<ulong>();
    private static Texture2D circle;
    private static bool missingLogged;

    public static Texture2D Circle
    {
        get
        {
            if (circle == null) circle = BuildCircle(CirclePixels);
            return circle;
        }
    }

    public static Texture2D Get(ulong id)
    {
        Texture2D texture;
        return textures.TryGetValue(id, out texture) ? texture : null;
    }

    public static Color Tint(ulong id)
    {
        Color color;
        if (tints.TryGetValue(id, out color)) return color;
        int hue = (int)(unchecked(id * 2654435761UL) % 360UL);
        color = Color.HSVToRGB(hue / 360f, 0.5f, 0.45f);
        tints[id] = color;
        return color;
    }

    public static void Sync(List<ulong> members)
    {
        try
        {
            float now = Time.unscaledTime;
            if (Circle == null) return;
            for (int i = 0; i < members.Count; i++) Fetch(members[i], now);
            dropped.Clear();
            foreach (KeyValuePair<ulong, Texture2D> pair in textures)
            {
                if (!members.Contains(pair.Key)) dropped.Add(pair.Key);
            }
            for (int i = 0; i < dropped.Count; i++) Drop(dropped[i]);
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopAvatars.Sync", e);
        }
    }

    public static void Clear()
    {
        try
        {
            foreach (KeyValuePair<ulong, Texture2D> pair in textures)
            {
                if (pair.Value != null) UnityEngine.Object.Destroy(pair.Value);
            }
            textures.Clear();
            retryAt.Clear();
            attempts.Clear();
            tints.Clear();
            dropped.Clear();
            missingLogged = false;
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopAvatars.Clear", e);
        }
    }

    private static void Fetch(ulong id, float now)
    {
        if (textures.ContainsKey(id)) return;
        int tried;
        attempts.TryGetValue(id, out tried);
        if (tried >= MaxTries) return;
        float at;
        if (retryAt.TryGetValue(id, out at) && now < at) return;
        if (!Steam.IsReady) return;
        attempts[id] = tried + 1;
        retryAt[id] = now + RetrySeconds;
        try
        {
            Texture2D texture = Read(id);
            if (texture == null)
            {
                SteamFriends.RequestUserInformation(new CSteamID(id), false);
                if (!missingLogged && tried + 1 >= MaxTries)
                {
                    missingLogged = true;
                    CoopLog.Log.Info("coop avatars: Steam has no avatar image for a peer, the placeholder initial is drawn");
                }
                return;
            }
            textures[id] = texture;
            CoopLog.Log.Info("coop avatars: loaded the Steam avatar of " + id + " at " + texture.width + "x" + texture.height);
        }
        catch (Exception e)
        {
            CoopLog.Once("CoopAvatars.Fetch", e);
        }
    }

    private static Texture2D Read(ulong id)
    {
        int handle = SteamFriends.GetMediumFriendAvatar(new CSteamID(id));
        if (handle <= 0) return null;
        uint width;
        uint height;
        if (!SteamUtils.GetImageSize(handle, out width, out height) || width == 0 || height == 0) return null;
        int length = (int)(width * height * Channels);
        byte[] pixels = new byte[length];
        if (!SteamUtils.GetImageRGBA(handle, pixels, length)) return null;
        FlipRows(pixels, (int)width, (int)height);
        MaskCircle(pixels, (int)width, (int)height);
        Texture2D texture = new Texture2D((int)width, (int)height, TextureFormat.RGBA32, false);
        try
        {
            texture.name = "coop-avatar-" + id;
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.LoadRawTextureData(pixels);
            texture.Apply();
        }
        catch (Exception)
        {
            UnityEngine.Object.Destroy(texture);
            throw;
        }
        return texture;
    }

    private static void Drop(ulong id)
    {
        Texture2D texture;
        if (textures.TryGetValue(id, out texture) && texture != null) UnityEngine.Object.Destroy(texture);
        textures.Remove(id);
        retryAt.Remove(id);
        attempts.Remove(id);
        tints.Remove(id);
    }

    private static void FlipRows(byte[] pixels, int width, int height)
    {
        int stride = width * Channels;
        byte[] row = new byte[stride];
        for (int y = 0; y < height / 2; y++)
        {
            int top = y * stride;
            int bottom = (height - 1 - y) * stride;
            Buffer.BlockCopy(pixels, top, row, 0, stride);
            Buffer.BlockCopy(pixels, bottom, pixels, top, stride);
            Buffer.BlockCopy(row, 0, pixels, bottom, stride);
        }
    }

    private static void MaskCircle(byte[] pixels, int width, int height)
    {
        float cx = width * 0.5f;
        float cy = height * 0.5f;
        float radius = Mathf.Min(cx, cy) - 1f;
        for (int y = 0; y < height; y++)
        {
            float dy = y + 0.5f - cy;
            for (int x = 0; x < width; x++)
            {
                float dx = x + 0.5f - cx;
                float cover = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                int alpha = (y * width + x) * Channels + 3;
                pixels[alpha] = (byte)(pixels[alpha] * cover);
            }
        }
    }

    private static Texture2D BuildCircle(int size)
    {
        Color32[] pixels = new Color32[size * size];
        float cx = size * 0.5f;
        float radius = size * 0.5f - 1f;
        for (int y = 0; y < size; y++)
        {
            float dy = y + 0.5f - cx;
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - cx;
                float cover = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(cover * 255f));
            }
        }
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.name = "coop-avatar-circle";
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.SetPixels32(pixels);
        texture.Apply();
        return texture;
    }
}
