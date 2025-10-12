using System.Collections.Concurrent;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Obj2Tiles.Library;

public static class TexturesCache
{
    private static readonly ConcurrentDictionary<string, Image<Rgba32>> Textures = new();
    private static readonly ConcurrentDictionary<string, ImageInfo> TextureInfos = new();
    
    public static Image<Rgba32> GetTexture(string textureName)
    {
        if (Textures.TryGetValue(textureName, out var txout))
            return txout;

        var texture = Image.Load<Rgba32>(textureName);
        Textures.TryAdd(textureName, texture);

        return texture;
    }
    
    public static void Clear()
    {
        foreach(var texture in Textures)
        {
            texture.Value.Dispose();
        }
        Textures.Clear();
    }

    public static ImageInfo GetTextureInfo(string textureName)
    {
        if (TextureInfos.TryGetValue(textureName, out var info))
            return info;

        info = Image.Identify(textureName);
        TextureInfos.TryAdd(textureName, info);

        return info;
    }
}