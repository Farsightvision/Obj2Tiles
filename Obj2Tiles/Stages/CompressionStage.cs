using Obj2Tiles.Library.Geometry;

namespace Obj2Tiles.Stages;

public static partial class StagesFacade
{
    public static async Task Compress(List<IMesh> meshes, byte ktxQuality, byte ktxCompressionLevel, byte threadsCount)
    {
        var semaphore = new SemaphoreSlim(threadsCount);
        var tasks = new List<Task>();
        for (var i = 0; i < meshes.Count; i++)
        {
            var mesh = meshes[i];

            if (mesh is MeshT meshT && meshT.Materials.Count > 0)
                tasks.Add(Compress(meshT, ktxQuality, ktxCompressionLevel, semaphore));
        }

        await Task.WhenAll(tasks);
    }

    private static async Task Compress(MeshT meshT, byte ktxQuality, byte ktxCompressionLevel, SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();

        try
        {
            var material = meshT.Materials[0];
        
            if (string.IsNullOrEmpty(material.Texture) && string.IsNullOrEmpty(material.NormalMap))
                return;
        
            var targetFolder = Path.GetDirectoryName(meshT.FilePath);

            if (!string.IsNullOrEmpty(material.Texture))
            {
                var ktxTexture = Path.ChangeExtension(material.Texture, ".ktx2");
                var pathTexture = Path.Combine(targetFolder, material.Texture);
                var pathKtxTexture = Path.Combine(targetFolder, ktxTexture);
                material.Texture = ktxTexture;
                meshT.WriteMaterial();
                await BasisuConverter.ConvertPngToKtx2Async(ktxQuality, ktxCompressionLevel, pathTexture, pathKtxTexture);
            }

            if (!string.IsNullOrEmpty(material.NormalMap))
            {
                var ktxNormalMap = Path.ChangeExtension(material.NormalMap, ".ktx2");
                var pathNormalMap = Path.Combine(targetFolder, material.NormalMap);
                var pathKtxNormalMap = Path.Combine(targetFolder, ktxNormalMap);
                material.NormalMap = ktxNormalMap;
                meshT.WriteMaterial();
                await BasisuConverter.ConvertPngToKtx2Async(ktxQuality, ktxCompressionLevel, pathNormalMap, pathKtxNormalMap);
            }
        
            meshT.WriteMaterial();
        }
        finally
        {
            semaphore.Release();
        }
    }
}