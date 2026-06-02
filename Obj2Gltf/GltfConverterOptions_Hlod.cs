using System.Text;

namespace SilentWave.Obj2Gltf
{
    public class GltfConverterOptions_Hlod
    {
        /// <summary>
        /// obj and mtl files' text encoding
        /// </summary>
        public Encoding ObjEncoding { get; set; }

        /// <summary>
        /// Default is false
        /// </summary>
        public bool RemoveDegenerateFaces { get; set; } = false;
        
        /// <summary>
        /// Default is false
        /// </summary>
        public bool DeleteOriginals { get; set; } = false;

        /// <summary>
        /// Apply the meshoptimizer optimize chain (vertex-cache → overdraw →
        /// vertex-fetch reorder) to each primitive's index/vertex buffers
        /// before emit. Safe to enable: pure reorder, no precision loss.
        /// Default is false.
        /// </summary>
        public bool ApplyMeshoptOptimization { get; set; } = false;

        /// <summary>
        /// Encode index/vertex buffers with EXT_meshopt_compression (and
        /// quantize positions). Smaller GLBs but requires viewer-side
        /// extension support. Default is false.
        /// </summary>
        public bool EncodeMeshoptCompression { get; set; } = false;

        /// <summary>
        /// Threshold passed to <c>meshopt_optimizeOverdraw</c> — lower values
        /// give better overdraw at the cost of vertex-cache locality. The
        /// meshoptimizer-recommended default is 1.05 (allow 5% ACMR slack).
        /// </summary>
        public float OverdrawThreshold { get; set; } = 1.05f;

        /// <summary>
        /// When true, the emitted glTF sampler uses MinFilter=LINEAR (9729) —
        /// no mips. Cesium then always samples the base atlas; matches
        /// master's KTX2 levelCount=1 sharpness. Default false
        /// (NearestMipmapLinear).
        /// </summary>
        public bool LeafNoMips { get; set; } = false;
    }
}
