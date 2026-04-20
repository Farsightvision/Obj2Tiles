using System.Collections.Concurrent;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Obj2Tiles.Library;

public static class TexturesCache
{
    private static readonly ConcurrentDictionary<string, Lazy<Image<Rgba32>>> Textures = new();
    private static readonly ConcurrentDictionary<string, Lazy<ImageInfo>> TextureInfos = new();

    public static Image<Rgba32> GetTexture(string textureName)
    {
        // Use Lazy<T> to ensure exactly one thread loads each texture,
        // even under parallel access from multiple meshes/LODs.
        var lazy = Textures.GetOrAdd(textureName,
            key => new Lazy<Image<Rgba32>>(() => Image.Load<Rgba32>(key)));
        return lazy.Value;
    }

    public static void EvictTexture(string? textureName)
    {
        if (string.IsNullOrEmpty(textureName)) return;
        if (Textures.TryRemove(textureName, out var lazy) && lazy.IsValueCreated)
            lazy.Value.Dispose();
    }

    public static void Clear()
    {
        foreach (var kvp in Textures)
        {
            if (kvp.Value.IsValueCreated)
                kvp.Value.Value.Dispose();
        }
        Textures.Clear();
        TextureInfos.Clear();
    }

    public static ImageInfo GetTextureInfo(string textureName)
    {
        var lazy = TextureInfos.GetOrAdd(textureName,
            key => new Lazy<ImageInfo>(() => Image.Identify(key)));
        return lazy.Value;
    }
}