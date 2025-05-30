using Obj2Tiles.Library.Geometry;

namespace Obj2Tiles.Stages;

public static partial class StagesFacade
{
    public static async Task Compress(Dictionary<LodConfig, IMesh[]> meshes, int threadsCount)
    {
        var semaphore = new SemaphoreSlim(threadsCount);
        var tasks = new List<Task>();

        foreach (var lodMeshPair in meshes)
        {
            for (var i = 0; i < lodMeshPair.Value.Length; i++)
            {
                var mesh = lodMeshPair.Value[i];

                if (mesh is MeshT meshT && meshT.Materials.Count > 0)
                    tasks.Add(Compress(meshT, lodMeshPair.Key, semaphore));
            }
        }

        await Task.WhenAll(tasks);
    }

    private static async Task Compress(MeshT meshT, LodConfig lodConfig, SemaphoreSlim semaphore)
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
                await BasisuConverter.ConvertPngToKtx2Async(lodConfig.KtxQuality, lodConfig.KtxCompressionLevel, pathTexture, pathKtxTexture);
            }

            if (!string.IsNullOrEmpty(material.NormalMap))
            {
                var ktxNormalMap = Path.ChangeExtension(material.NormalMap, ".ktx2");
                var pathNormalMap = Path.Combine(targetFolder, material.NormalMap);
                var pathKtxNormalMap = Path.Combine(targetFolder, ktxNormalMap);
                material.NormalMap = ktxNormalMap;
                meshT.WriteMaterial();
                await BasisuConverter.ConvertPngToKtx2Async(lodConfig.KtxQuality, lodConfig.KtxCompressionLevel, pathNormalMap, pathKtxNormalMap);
            }
        
            meshT.WriteMaterial();
        }
        finally
        {
            semaphore.Release();
        }
    }
}

//./Obj2Tiles --input ./factory/odm_textured_model_geo.obj --output ./obj_tiles/ --lods '[{"Quality":1.0,"SaveVertexColor":false,"SaveUv":true,"KtxQuality":170,"KtxCompressionLevel":0},{"Quality":0.5,"SaveVertexColor":false,"SaveUv":true,"KtxQuality":128,"KtxCompressionLevel":0},{"Quality":0.2,"SaveVertexColor":true,"SaveUv":false,"KtxQuality":128,"KtxCompressionLevel":0}]'