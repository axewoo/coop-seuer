using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

internal static class LobbyAvatars
{
    private const string PngPrefix = "data:image/png;base64,";
    private static readonly Dictionary<ulong, string> encoded = new Dictionary<ulong, string>();

    public static string Get(ulong id)
    {
        string data;
        if (encoded.TryGetValue(id, out data)) return data;
        try
        {
            data = Encode(id);
        }
        catch (Exception e)
        {
            CoopLog.Once("LobbyAvatars.Get", e);
            return null;
        }
        if (data != null) encoded[id] = data;
        return data;
    }

    public static void Clear()
    {
        encoded.Clear();
    }

    private static string Encode(ulong id)
    {
        int handle = SteamFriends.GetMediumFriendAvatar(new CSteamID(id));
        if (handle <= 0) return null;
        uint width;
        uint height;
        if (!SteamUtils.GetImageSize(handle, out width, out height) || width == 0 || height == 0) return null;
        int length = (int)(width * height * 4);
        byte[] pixels = new byte[length];
        if (!SteamUtils.GetImageRGBA(handle, pixels, length)) return null;
        FlipRows(pixels, (int)width, (int)height);
        Texture2D texture = new Texture2D((int)width, (int)height, TextureFormat.RGBA32, false);
        try
        {
            texture.LoadRawTextureData(pixels);
            texture.Apply();
            return PngPrefix + Convert.ToBase64String(ImageConversion.EncodeToPNG(texture));
        }
        finally
        {
            UnityEngine.Object.Destroy(texture);
        }
    }

    private static void FlipRows(byte[] pixels, int width, int height)
    {
        int stride = width * 4;
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
}
