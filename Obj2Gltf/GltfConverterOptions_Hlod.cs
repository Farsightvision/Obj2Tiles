using System.Text;

namespace SilentWave.Obj2Gltf
{
    public class GltfConverterOptions_Hlod
    {
        /// <summary>
        /// obj and mtl files' text encoding
        /// </summary>
        public Encoding ObjEncoding { get; set; }

        public bool RemoveDegenerateFaces { get; set; } = false;

        public bool DeleteOriginals { get; set; } = false;

        /// <summary>
        /// Apply the meshoptimizer optimize chain (vertex-cache, overdraw,
        /// vertex-fetch reorder) to each primitive before emit.
        /// </summary>
        public bool ApplyMeshoptOptimization { get; set; } = false;

        /// <summary>
        /// Encode buffers with EXT_meshopt_compression; requires viewer-side
        /// extension support.
        /// </summary>
        public bool EncodeMeshoptCompression { get; set; } = false;

        /// <summary>
        /// Threshold for <c>meshopt_optimizeOverdraw</c>; 1.05 allows 5% ACMR slack.
        /// </summary>
        public float OverdrawThreshold { get; set; } = 1.05f;

        /// <summary>
        /// Emit MinFilter=LINEAR (9729) so the sampler has no mips.
        /// </summary>
        public bool LeafNoMips { get; set; } = false;
    }
}
