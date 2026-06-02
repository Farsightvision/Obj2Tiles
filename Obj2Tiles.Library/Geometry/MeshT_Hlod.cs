using System.Diagnostics;
using System.Globalization;
using Obj2Tiles.Library.Algos;
using Obj2Tiles.Library.Materials;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;
using Path = System.IO.Path;
using Rectangle = Obj2Tiles.Library.Algos.Model.Rectangle;

namespace Obj2Tiles.Library.Geometry;

public class MeshT_Hlod : IMesh
{
    public const string DefaultName = "Mesh";
    private const int DefaultMaxAtlasSize = 4096;
    private const int DefaultJpegQuality = 90;

    /// <summary>
    /// Inter-cluster gutter in final-atlas pixels.
    /// Bin-pack inflates each cluster's insertion size by 2 × this value and
    /// stores the inner (visible) rect in PackedRect. The gutter ring around
    /// each cluster stays empty after FillAtlases and is then filled by
    /// <see cref="Common_Hlod.DilateAtlasBleed"/>, so bilinear sampling at cluster
    /// edges picks up THIS cluster's dilated edge pixels (correct color)
    /// instead of the NEXT cluster's pixels (wrong color → visible fringes).
    ///
    /// 16 pixels covers bilinear + mip levels 1-4 + anisotropic safety at
    /// oblique sampling angles. Must be applied in FINAL atlas pixel space
    /// (post atlas-cap scale), otherwise a large natural → small cap
    /// downscale (e.g. 22k → 2048) reduces a 16-px source-pixel gutter to
    /// ~1.5 atlas-pixels (effectively zero).
    /// </summary>
    private const int AtlasGutterPixels = 16;

    // Scale the gutter by cluster count. The 16-px default is calibrated for
    // ~50-cluster tiles where each cluster has substantial surface area and
    // 16 px is an appropriate bilinear-bleed margin. At 11k+ clusters per tile
    // each cluster's atlas patch is tiny (~50 px² typical), so a 16-px gutter
    // dominates the budget and 4 px is more than enough. Consistent with
    // stb_rect_pack default-padding=1 and texture-defrag's adaptive padding
    // heuristic.
    //
    // The 32×32 gutter footprint (2 × 16 each axis) on 11767 clusters ≈ 12M
    // px²; a 4-px gutter (8×8 footprint) on the same cluster set ≈ 0.75M px² —
    // fits comfortably even in a 2048² cap. For tiles ≤200 clusters the
    // default 16 px is preserved.
    internal static int EffectiveGutterPixels(int clusterCount)
    {
        if (clusterCount > 1000) return 4;
        if (clusterCount > 200)  return 8;
        return AtlasGutterPixels;
    }

    private sealed class ClusterInfo
    {
        public int MaterialIndex { get; set; }
        public List<int> Cluster { get; set; }
        public RectangleF UvRect { get; set; }
        public Rectangle PackedRect { get; set; }
    }

    private readonly List<FaceT> _faces;
    private readonly double _packingThreshold;
    private readonly bool _saveUv;
    private readonly bool _saveVertexColor;
    private readonly double _textureQuality;
    private readonly int _jpegQuality;
    private readonly int _maxAtlasSize;
    private readonly List<RGB> _vertexColors;
    private List<Material> _materials;
    private List<Vertex2> _textureVertices;
    private List<Vertex3> _vertices;

    // Texture repacking state
    private List<ClusterInfo> _clusterInfos;
    private Image<Rgba32> _atlasTexture;
    private Image<Rgba32> _atlasNormalMap;
    private int _naturalAtlasEdgeLength;
    private bool _useSingleResamplePath;

    public IReadOnlyList<Vertex3> Vertices => _vertices;
    public IReadOnlyList<Vertex2> TextureVertices => _textureVertices;
    public IReadOnlyList<FaceT> Faces => _faces;
    public IReadOnlyList<Material> Materials => _materials;
    public int AtlasEdgeLength { get; private set; }
    public string FilePath { get; set; }

    public MeshT_Hlod(
        IEnumerable<Vertex3> vertices,
        IEnumerable<Vertex2> textureVertices,
        IEnumerable<FaceT> faces,
        IEnumerable<Material> materials,
        bool saveVertexColor,
        bool saveUv,
        double packingThreshold,
        double textureQuality,
        int jpegQuality = DefaultJpegQuality,
        int maxAtlasSize = DefaultMaxAtlasSize)
    {
        _packingThreshold = packingThreshold;
        _textureQuality = textureQuality;
        _jpegQuality = jpegQuality;
        // Treat 0 as "use the legacy default" — the new pipeline encodes "no
        // explicit cap; let the per-depth tier decide" as 0 on LodConfig, but
        // MeshT_Hlod uses _maxAtlasSize as a hard min argument
        // so 0 here would collapse the atlas to nothing.
        _maxAtlasSize = maxAtlasSize > 0 ? maxAtlasSize : DefaultMaxAtlasSize;
        _vertices = [..vertices];
        _textureVertices = [..textureVertices];
        _faces = [..faces];
        _materials = [..materials];
        _vertexColors = new List<RGB>();
        _saveVertexColor = saveVertexColor;
        _saveUv = saveUv;
    }

    public string Name { get; set; } = DefaultName;

    /// <summary>
    /// Unsharp-mask strength applied to the atlas image just before JPEG encode.
    /// 0 = no sharpen (default). 1.0 = strong. Set by HierarchicalAtlasStage
    /// from config.AtlasUnsharpAmount.
    /// </summary>
    public double AtlasUnsharpAmount { get; set; } = 0.0;

    public int Split(IVertexUtils utils, double q, out IMesh left,
        out IMesh right)
    {
        var leftVertices = new Dictionary<Vertex3, int>(_vertices.Count);
        var rightVertices = new Dictionary<Vertex3, int>(_vertices.Count);

        var leftFaces = new List<FaceT>(_faces.Count);
        var rightFaces = new List<FaceT>(_faces.Count);

        var leftTextureVertices = new Dictionary<Vertex2, int>(_textureVertices.Count);
        var rightTextureVertices = new Dictionary<Vertex2, int>(_textureVertices.Count);

        var count = 0;

        for (var index = 0; index < _faces.Count; index++)
        {
            var face = _faces[index];

            var vA = _vertices[face.IndexA];
            var vB = _vertices[face.IndexB];
            var vC = _vertices[face.IndexC];

            var vtA = _textureVertices[face.TextureIndexA];
            var vtB = _textureVertices[face.TextureIndexB];
            var vtC = _textureVertices[face.TextureIndexC];

            var aSide = utils.GetDimension(vA) < q;
            var bSide = utils.GetDimension(vB) < q;
            var cSide = utils.GetDimension(vC) < q;

            if (aSide)
            {
                if (bSide)
                {
                    if (cSide)
                    {
                        // All on the left

                        var indexALeft = leftVertices.AddIndex(vA);
                        var indexBLeft = leftVertices.AddIndex(vB);
                        var indexCLeft = leftVertices.AddIndex(vC);

                        var indexATextureLeft = leftTextureVertices!.AddIndex(vtA);
                        var indexBTextureLeft = leftTextureVertices!.AddIndex(vtB);
                        var indexCTextureLeft = leftTextureVertices!.AddIndex(vtC);

                        leftFaces.Add(new FaceT(indexALeft, indexBLeft, indexCLeft,
                            indexATextureLeft, indexBTextureLeft, indexCTextureLeft,
                            face.MaterialIndex));
                    }
                    else
                    {
                        IntersectRight2DWithTexture(utils, q, face.IndexC, face.IndexA, face.IndexB,
                            leftVertices,
                            rightVertices,
                            face.TextureIndexC, face.TextureIndexA, face.TextureIndexB,
                            leftTextureVertices, rightTextureVertices, face.MaterialIndex, leftFaces, rightFaces
                        );
                        count++;
                    }
                }
                else
                {
                    if (cSide)
                    {
                        IntersectRight2DWithTexture(utils, q, face.IndexB, face.IndexC, face.IndexA,
                            leftVertices,
                            rightVertices,
                            face.TextureIndexB, face.TextureIndexC, face.TextureIndexA,
                            leftTextureVertices, rightTextureVertices, face.MaterialIndex, leftFaces,
                            rightFaces);
                        count++;
                    }
                    else
                    {
                        IntersectLeft2DWithTexture(utils, q, face.IndexA, face.IndexB, face.IndexC,
                            leftVertices,
                            rightVertices,
                            face.TextureIndexA, face.TextureIndexB, face.TextureIndexC,
                            leftTextureVertices, rightTextureVertices, face.MaterialIndex, leftFaces,
                            rightFaces);
                        count++;
                    }
                }
            }
            else
            {
                if (bSide)
                {
                    if (cSide)
                    {
                        IntersectRight2DWithTexture(utils, q, face.IndexA, face.IndexB, face.IndexC,
                            leftVertices,
                            rightVertices,
                            face.TextureIndexA, face.TextureIndexB, face.TextureIndexC,
                            leftTextureVertices, rightTextureVertices, face.MaterialIndex, leftFaces,
                            rightFaces);
                        count++;
                    }
                    else
                    {
                        IntersectLeft2DWithTexture(utils, q, face.IndexB, face.IndexC, face.IndexA,
                            leftVertices,
                            rightVertices,
                            face.TextureIndexB, face.TextureIndexC, face.TextureIndexA,
                            leftTextureVertices, rightTextureVertices, face.MaterialIndex, leftFaces,
                            rightFaces);
                        count++;
                    }
                }
                else
                {
                    if (cSide)
                    {
                        IntersectLeft2DWithTexture(utils, q, face.IndexC, face.IndexA, face.IndexB,
                            leftVertices,
                            rightVertices,
                            face.TextureIndexC, face.TextureIndexA, face.TextureIndexB,
                            leftTextureVertices, rightTextureVertices, face.MaterialIndex, leftFaces,
                            rightFaces);
                        count++;
                    }
                    else
                    {
                        // All on the right

                        var indexARight = rightVertices.AddIndex(vA);
                        var indexBRight = rightVertices.AddIndex(vB);
                        var indexCRight = rightVertices.AddIndex(vC);

                        var indexATextureRight = rightTextureVertices!.AddIndex(vtA);
                        var indexBTextureRight = rightTextureVertices!.AddIndex(vtB);
                        var indexCTextureRight = rightTextureVertices!.AddIndex(vtC);

                        rightFaces.Add(new FaceT(indexARight, indexBRight, indexCRight,
                            indexATextureRight, indexBTextureRight, indexCTextureRight,
                            face.MaterialIndex));
                    }
                }
            }
        }

        var orderedLeftVertices = leftVertices.OrderBy(x => x.Value).Select(x => x.Key);
        var orderedRightVertices = rightVertices.OrderBy(x => x.Value).Select(x => x.Key);

        var orderedLeftTextureVertices = leftTextureVertices.OrderBy(x => x.Value).Select(x => x.Key);
        var orderedRightTextureVertices = rightTextureVertices.OrderBy(x => x.Value).Select(x => x.Key);

        left = new MeshT_Hlod(orderedLeftVertices, orderedLeftTextureVertices, leftFaces, _materials, _saveVertexColor,
            _saveUv, _packingThreshold, _textureQuality, _jpegQuality, _maxAtlasSize)
        {
            Name = $"{Name}-{utils.Axis}L"
        };
        right = new MeshT_Hlod(orderedRightVertices, orderedRightTextureVertices, rightFaces, _materials,
            _saveVertexColor, _saveUv, _packingThreshold, _textureQuality, _jpegQuality, _maxAtlasSize)
        {
            Name = $"{Name}-{utils.Axis}R"
        };

        return count;
    }

    private void IntersectLeft2DWithTexture(IVertexUtils utils, double q, int indexVL,
        int indexVR1, int indexVR2,
        IDictionary<Vertex3, int> leftVertices, IDictionary<Vertex3, int> rightVertices,
        int indexTextureVL, int indexTextureVR1, int indexTextureVR2,
        IDictionary<Vertex2, int> leftTextureVertices, IDictionary<Vertex2, int> rightTextureVertices,
        int materialIndex, ICollection<FaceT> leftFaces, ICollection<FaceT> rightFaces)
    {
        var vL = _vertices[indexVL];
        var vR1 = _vertices[indexVR1];
        var vR2 = _vertices[indexVR2];

        var tVL = _textureVertices[indexTextureVL];
        var tVR1 = _textureVertices[indexTextureVR1];
        var tVR2 = _textureVertices[indexTextureVR2];

        var indexVLLeft = leftVertices.AddIndex(vL);
        var indexTextureVLLeft = leftTextureVertices.AddIndex(tVL);

        if (Math.Abs(utils.GetDimension(vR1) - q) < Common.Epsilon &&
            Math.Abs(utils.GetDimension(vR2) - q) < Common.Epsilon)
        {
            // Right Vertices are on the line

            var indexVR1Left = leftVertices.AddIndex(vR1);
            var indexVR2Left = leftVertices.AddIndex(vR2);

            var indexTextureVR1Left = leftTextureVertices.AddIndex(tVR1);
            var indexTextureVR2Left = leftTextureVertices.AddIndex(tVR2);

            leftFaces.Add(new FaceT(indexVLLeft, indexVR1Left, indexVR2Left,
                indexTextureVLLeft, indexTextureVR1Left, indexTextureVR2Left, materialIndex));

            return;
        }

        var indexVR1Right = rightVertices.AddIndex(vR1);
        var indexVR2Right = rightVertices.AddIndex(vR2);

        // a on the left, b and c on the right

        // Prima intersezione
        var t1 = utils.CutEdge(vL, vR1, q);
        var indexT1Left = leftVertices.AddIndex(t1);
        var indexT1Right = rightVertices.AddIndex(t1);

        // Seconda intersezione
        var t2 = utils.CutEdge(vL, vR2, q);
        var indexT2Left = leftVertices.AddIndex(t2);
        var indexT2Right = rightVertices.AddIndex(t2);

        // Split texture
        var indexTextureVR1Right = rightTextureVertices.AddIndex(tVR1);
        var indexTextureVR2Right = rightTextureVertices.AddIndex(tVR2);

        var perc1 = Common.GetIntersectionPerc(vL, vR1, t1);

        // Prima intersezione texture
        var t1t = tVL.CutEdgePerc(tVR1, perc1);
        var indexTextureT1Left = leftTextureVertices.AddIndex(t1t);
        var indexTextureT1Right = rightTextureVertices.AddIndex(t1t);

        var perc2 = Common.GetIntersectionPerc(vL, vR2, t2);

        // Seconda intersezione texture
        var t2t = tVL.CutEdgePerc(tVR2, perc2);
        var indexTextureT2Left = leftTextureVertices.AddIndex(t2t);
        var indexTextureT2Right = rightTextureVertices.AddIndex(t2t);

        var lface = new FaceT(indexVLLeft, indexT1Left, indexT2Left,
            indexTextureVLLeft, indexTextureT1Left, indexTextureT2Left, materialIndex);
        leftFaces.Add(lface);

        var rface1 = new FaceT(indexT1Right, indexVR1Right, indexVR2Right,
            indexTextureT1Right, indexTextureVR1Right, indexTextureVR2Right, materialIndex);
        rightFaces.Add(rface1);

        var rface2 = new FaceT(indexT1Right, indexVR2Right, indexT2Right,
            indexTextureT1Right, indexTextureVR2Right, indexTextureT2Right, materialIndex);
        rightFaces.Add(rface2);
    }

    private void IntersectRight2DWithTexture(IVertexUtils utils, double q, int indexVR,
        int indexVL1, int indexVL2,
        IDictionary<Vertex3, int> leftVertices, IDictionary<Vertex3, int> rightVertices,
        int indexTextureVR, int indexTextureVL1, int indexTextureVL2,
        IDictionary<Vertex2, int> leftTextureVertices, IDictionary<Vertex2, int> rightTextureVertices,
        int materialIndex, ICollection<FaceT> leftFaces, ICollection<FaceT> rightFaces)
    {
        var vR = _vertices[indexVR];
        var vL1 = _vertices[indexVL1];
        var vL2 = _vertices[indexVL2];

        var tVR = _textureVertices[indexTextureVR];
        var tVL1 = _textureVertices[indexTextureVL1];
        var tVL2 = _textureVertices[indexTextureVL2];

        var indexVRRight = rightVertices.AddIndex(vR);
        var indexTextureVRRight = rightTextureVertices.AddIndex(tVR);

        if (Math.Abs(utils.GetDimension(vL1) - q) < Common.Epsilon &&
            Math.Abs(utils.GetDimension(vL2) - q) < Common.Epsilon)
        {
            // Left Vertices are on the line

            var indexVL1Right = rightVertices.AddIndex(vL1);
            var indexVL2Right = rightVertices.AddIndex(vL2);

            var indexTextureVL1Right = rightTextureVertices.AddIndex(tVL1);
            var indexTextureVL2Right = rightTextureVertices.AddIndex(tVL2);

            rightFaces.Add(new FaceT(indexVRRight, indexVL1Right, indexVL2Right,
                indexTextureVRRight, indexTextureVL1Right, indexTextureVL2Right, materialIndex));

            return;
        }

        var indexVL1Left = leftVertices.AddIndex(vL1);
        var indexVL2Left = leftVertices.AddIndex(vL2);

        // a on the right, b and c on the left

        // Prima intersezione
        var t1 = utils.CutEdge(vR, vL1, q);
        var indexT1Left = leftVertices.AddIndex(t1);
        var indexT1Right = rightVertices.AddIndex(t1);

        // Seconda intersezione
        var t2 = utils.CutEdge(vR, vL2, q);
        var indexT2Left = leftVertices.AddIndex(t2);
        var indexT2Right = rightVertices.AddIndex(t2);

        // Split texture
        var indexTextureVL1Left = leftTextureVertices.AddIndex(tVL1);
        var indexTextureVL2Left = leftTextureVertices.AddIndex(tVL2);

        var perc1 = Common.GetIntersectionPerc(vR, vL1, t1);

        // Prima intersezione texture
        var t1t = tVR.CutEdgePerc(tVL1, perc1);
        var indexTextureT1Left = leftTextureVertices.AddIndex(t1t);
        var indexTextureT1Right = rightTextureVertices.AddIndex(t1t);

        var perc2 = Common.GetIntersectionPerc(vR, vL2, t2);

        // Seconda intersezione texture
        var t2t = tVR.CutEdgePerc(tVL2, perc2);
        var indexTextureT2Left = leftTextureVertices.AddIndex(t2t);
        var indexTextureT2Right = rightTextureVertices.AddIndex(t2t);

        var rface = new FaceT(indexVRRight, indexT1Right, indexT2Right,
            indexTextureVRRight, indexTextureT1Right, indexTextureT2Right, materialIndex);
        rightFaces.Add(rface);

        var lface1 = new FaceT(indexT2Left, indexVL1Left, indexVL2Left,
            indexTextureT2Left, indexTextureVL1Left, indexTextureVL2Left, materialIndex);
        leftFaces.Add(lface1);

        var lface2 = new FaceT(indexT2Left, indexT1Left, indexVL1Left,
            indexTextureT2Left, indexTextureT1Left, indexTextureVL1Left, materialIndex);
        leftFaces.Add(lface2);
    }

    /// <summary>
    /// Phase 1: Prepares texture repacking by creating cluster information and calculating optimal atlas size.
    /// This method also performs bin packing and fills the PackedRect for each cluster.
    /// </summary>
    public void PrepareRepackTextures(bool removeUnused = true)
    {
        Debug.WriteLine("Preparing texture repack for " + Name);

        if (removeUnused)
            RemoveUnusedVertices();

        // Initialize vertex colors if needed
        _vertexColors.Clear();

        if (_saveVertexColor)
        {
            for (var i = 0; i < _vertices.Count; i++)
            {
                _vertexColors.Add(new RGB(0, 0, 0));
            }
        }

        if (!_saveUv)
        {
            return;
        }

        var facesByMaterial = GetFacesByMaterial();
        var clustersByMaterial = new Dictionary<int, IReadOnlyList<List<int>>>();
        var sw = new Stopwatch();

        // Build clusters for each material
        for (var m = 0; m < facesByMaterial.Count; m++)
        {
            var material = _materials[m];
            var facesIndexes = facesByMaterial[m];
            Debug.WriteLine($"Working on material {m} -> {material.Name}");

            if (facesIndexes.Count == 0)
            {
                Debug.WriteLine("No faces with this material");
                continue;
            }

            sw.Restart();

            Debug.WriteLine("Creating edges mapper");
            var edgesMapper = GetEdgesMapper(facesIndexes);
            Debug.WriteLine("Done in " + sw.ElapsedMilliseconds + "ms");
            sw.Restart();

            Debug.WriteLine("Creating faces mapper");
            var facesMapper = GetFacesMapper(edgesMapper);
            Debug.WriteLine("Done in " + sw.ElapsedMilliseconds + "ms");
            sw.Restart();

            Debug.WriteLine("Assembling faces clusters");
            var clusters = GetFacesClusters(facesIndexes, facesMapper);
            Debug.WriteLine("Done in " + sw.ElapsedMilliseconds + "ms");
            sw.Restart();

            Debug.WriteLine("Sorting clusters");
            clusters.Sort((a, b) => b.Count.CompareTo(a.Count));
            Debug.WriteLine("Done in " + sw.ElapsedMilliseconds + "ms");

            Debug.WriteLine($"Material {material.Name} has {clusters.Count} clusters");

            clustersByMaterial.Add(m, clusters);
        }

        // Build cluster infos and calculate atlas size
        Debug.WriteLine("Building cluster infos");
        _clusterInfos = new List<ClusterInfo>();
        double totalTextureArea = 0;

        foreach (var kvp in clustersByMaterial)
        {
            var materialIndex = kvp.Key;
            var material = _materials[materialIndex];

            if (material.Texture == null && material.NormalMap == null)
                continue;

            foreach (var cluster in kvp.Value)
            {
                var rect = GetClusterRect(cluster);
                _clusterInfos.Add(new ClusterInfo
                {
                    MaterialIndex = materialIndex,
                    Cluster = cluster,
                    UvRect = rect,
                    PackedRect = new Rectangle()
                });

                var texturePath = string.IsNullOrEmpty(material.Texture) ? material.NormalMap : material.Texture;
                var info = TexturesCache.GetCappedDims(texturePath);
                totalTextureArea += rect.Width * info.Width * rect.Height * info.Height;
            }
        }

        if (_clusterInfos.Count == 0)
        {
            Debug.WriteLine("No clusters to pack");
            return;
        }

        // Sort clusters by size for better packing
        _clusterInfos.Sort((a, b) => b.Cluster.Count.CompareTo(a.Cluster.Count));

        // Calculate optimal atlas size
        var edgeLength = (int)Math.Sqrt(totalTextureArea);
        var powerOfTwo = Common.NextPowerOfTwo(edgeLength);
        var fraction = totalTextureArea / powerOfTwo / powerOfTwo;

        if (_textureQuality >= 1 && fraction > _packingThreshold)
            edgeLength = powerOfTwo;
        else
            edgeLength = (int)(edgeLength * 1.01);

        edgeLength = Math.Max(edgeLength, 32);
        _naturalAtlasEdgeLength = edgeLength;
        _useSingleResamplePath = false;

        // Single-resample path (shipped default): when the natural edge is up to 4× the cap (and under the
        // 12288² absolute ceiling ≈576MB RGBA32, a memory bound), pack at natural size and let ONE Lanczos3
        // downsample to cap produce the atlas — sharper than cumulative per-cluster resamples (per-cluster
        // regressed −1.85/−1.88% sharp on small2/hd). Tiles whose natural edge EXCEEDS 4× cap fall through to
        // the per-cluster cap-bound path below (the memory-bounded fallback for huge tiles).
        if (_maxAtlasSize > 0 &&
            edgeLength > _maxAtlasSize &&
            edgeLength <= Math.Min(_maxAtlasSize * 4, 12288) &&
            TryPackClusterInfosAtNaturalSize(edgeLength, Math.Min(_maxAtlasSize * 4, 12288), out var naturalPackedEdge))
        {
            AtlasEdgeLength = naturalPackedEdge;
            _naturalAtlasEdgeLength = naturalPackedEdge;
            _useSingleResamplePath = true;
            return;
        }

        // Pre-apply the atlas cap so the bin-pack and gutter live in FINAL
        // atlas pixel space. Pre-cap, gutter shrinks with the post-pack scale
        // (a 22k → 2048 scale reduces 8 px → 0.75 px, killing the bilinear-safe
        // band). Post-cap, gutter stays the full AtlasGutterPixels in the
        // saved file.
        double scale = 1.0;
        if (_maxAtlasSize > 0 && edgeLength > _maxAtlasSize)
        {
            scale = (double)_maxAtlasSize / edgeLength;
            edgeLength = _maxAtlasSize;
        }

        // Perform bin packing to find optimal layout and fill PackedRect.
        // Cluster dims are scaled to final-atlas-pixel space; the bin-pack
        // inflates by 2 × AtlasGutterPixels and stores the inner rect.
        var iterations = 1;
        // Safety guard: edgeLength is bounded so a runaway pack loop fails
        // fast instead of spinning. Natural packs on typical fixtures stay
        // well under 1M px (max observed ~22K for 3396 clusters; post-cap
        // rescales to _maxAtlasSize anyway), so growth past 1M is a runaway.
        const int packEdgeLimit = 1_000_000;
        // Replace iterative 0.98 scale-shrink with a single computed-jump on
        // the first hard-cap failure. The previous loop's O(retries × N²)
        // behaviour was pathological for fixtures whose natural pack edge >>
        // MaxAtlasSize (e.g. 84 materials × ~5000² PNGs, natural ~45824,
        // cap 4096 → scale 0.045 → ~150 retries × O(50k²) bin-pack rebuilds).
        // Fix: compute the required scale from the failed attempt's
        // cluster-area sum, jump once. Subsequent retries fall back to 0.98
        // for gutter overhead correction.
        //
        // Also check the gutter-only floor before entering the retry loop.
        // Each cluster gets 2 × AtlasGutterPixels in BOTH dimensions (16 px →
        // +32 W, +32 H). For tiles with many UV islands the gutter alone can
        // exceed cap area (e.g. 100k clusters × 32² = 102M vs 2048² = 4.2M),
        // making bin-pack convergence geometrically impossible. Detect this
        // BEFORE entering the retry loop and bail with a clear diagnostic.
        // Also cap the post-jump 0.98 fine-shrink so we fail fast instead of
        // spinning.
        if (_maxAtlasSize > 0 && edgeLength >= _maxAtlasSize)
        {
            int gPxThis = EffectiveGutterPixels(_clusterInfos.Count);
            double gutterOnlyArea = (double)_clusterInfos.Count * (2 * gPxThis) * (2 * gPxThis);
            double capArea = (double)_maxAtlasSize * _maxAtlasSize;
            // Threshold: true infeasibility is gutter > cap (small uniform
            // rects pack >90% efficient in practice). Borderline cases (e.g.
            // 83% gutter:cap) converge fine; only flag the geometrically
            // impossible cases where the gutter alone exceeds cap area.
            if (gutterOnlyArea > capArea)
            {
                throw new InvalidOperationException(
                    $"Atlas pack infeasible (gutter floor): {_clusterInfos.Count} clusters × " +
                    $"({2 * gPxThis})² gutter = {gutterOnlyArea / 1e6:F1}M px² " +
                    $"exceeds cap area ({_maxAtlasSize}² = {capArea / 1e6:F1}M px²). " +
                    "Bin-pack convergence is geometrically impossible. Options: " +
                    "(a) raise --max-atlas-size for this depth, (b) reduce AtlasGutterPixels, " +
                    "(c) aggregate UV islands before packing, (d) split this tile (deeper LOD).");
            }
        }
        bool computedJumpUsed = false;
        int postJumpRetries = 0;
        // Marginal-feasibility tiles (high gutter:cap ratio, e.g. 88%) can
        // need 50-80 fine-shrink iterations to converge. 200 is high enough
        // to let those tiles finish but low enough to catch true infeasibles.
        const int postJumpRetryLimit = 200;
        while (!TryPackClusterInfos(_clusterInfos, edgeLength, scale))
        {
            if (edgeLength > packEdgeLimit)
                throw new InvalidOperationException(
                    $"Atlas packing did not converge: edgeLength={edgeLength} " +
                    $"exceeded {packEdgeLimit} after {iterations} retries " +
                    $"({_clusterInfos.Count} clusters). " +
                    "Likely a runaway gutter/insert formula — check before retrying.");
            // The new gutter (8 px / side) can push small atlases over the
            // edge. If we already hit the cap, never grow past it — instead
            // re-scale clusters down so they fit (preserves the cap contract
            // for very-large fixtures while the gutter consumes some area).
            if (_maxAtlasSize > 0 && edgeLength >= _maxAtlasSize)
            {
                if (!computedJumpUsed)
                {
                    // Single computed-scale jump: compute the area that the
                    // current scale's clusters would need (including gutter
                    // inflation), derive the implied natural-pack edge at
                    // that area, and choose scale to fit _maxAtlasSize with
                    // bin-pack packing-efficiency headroom.
                    double clusterAreaSum = 0;
                    for (int i = 0; i < _clusterInfos.Count; i++)
                    {
                        var info = _clusterInfos[i];
                        var matSrc = _materials[info.MaterialIndex];
                        var texPath = string.IsNullOrEmpty(matSrc.Texture) ? matSrc.NormalMap : matSrc.Texture;
                        if (string.IsNullOrEmpty(texPath)) continue;
                        var srcInfo = TexturesCache.GetCappedDims(texPath);
                        // Match TryPackClusterInfos exactly: inflate by 2 × gutter,
                        // ceil to integer px (same source-pixel-space scaling).
                        int gpx = EffectiveGutterPixels(_clusterInfos.Count);
                        int clusterW = (int)Math.Max(Math.Ceiling(info.UvRect.Width * srcInfo.Width * scale), 1) + 2 * gpx;
                        int clusterH = (int)Math.Max(Math.Ceiling(info.UvRect.Height * srcInfo.Height * scale), 1) + 2 * gpx;
                        clusterAreaSum += (double)clusterW * clusterH;
                    }
                    // Bin-pack packing efficiency ≈ 0.7 in practice; use 0.55 for
                    // headroom so the first jump lands at or just below the cap
                    // and the (rare) fine retry uses the 0.98 fallback below.
                    double impliedNaturalEdge = Math.Sqrt(clusterAreaSum / 0.55);
                    if (impliedNaturalEdge > _maxAtlasSize)
                    {
                        double targetScale = scale * ((double)_maxAtlasSize / impliedNaturalEdge);
                        scale = targetScale;
                    }
                    else
                    {
                        // Implied edge already ≤ cap — must be gutter overhead.
                        // Fall through to 0.98 fine-shrink.
                        scale *= 0.98;
                    }
                    computedJumpUsed = true;
                }
                else
                {
                    postJumpRetries++;
                    if (postJumpRetries > postJumpRetryLimit)
                        throw new InvalidOperationException(
                            $"Atlas packing did not converge after B.12 jump + " +
                            $"{postJumpRetryLimit} fine-shrink retries " +
                            $"({_clusterInfos.Count} clusters, scale={scale:F4}, cap={_maxAtlasSize}). " +
                            "B.12 jump landed close but bin-pack still cannot fit. " +
                            "Likely bin-pack fragmentation worse than the 0.55 efficiency model " +
                            "or gutter-area-floor edge case. Options as in gutter-floor diagnostic.");
                    scale *= 0.98;
                }
            }
            else
            {
                var newEdgeLength = Math.Max(edgeLength + 10, (int)(edgeLength * 1.02));
                if (_maxAtlasSize > 0 && newEdgeLength > _maxAtlasSize)
                {
                    // Crossed the cap mid-grow; clamp + re-scale instead.
                    scale *= (double)_maxAtlasSize / newEdgeLength;
                    edgeLength = _maxAtlasSize;
                }
                else
                {
                    edgeLength = newEdgeLength;
                }
            }
            iterations++;
        }

        AtlasEdgeLength = edgeLength;
    }

    /// <summary>
    /// Phase 2: Fills the atlas textures for clusters belonging to the specified material.
    /// This method can be called multiple times with different materials to fill the atlas incrementally.
    /// Textures are loaded from TexturesCache.
    /// Must be called after PrepareRepackTextures().
    /// </summary>
    /// <param name="material">The material whose textures should be used to fill matching clusters</param>
    public void FillAtlases(Material material)
    {
        if (!_saveUv && !_saveVertexColor)
            return;

        var materialIndex = _materials.IndexOf(material);

        if (materialIndex == -1)
        {
            Debug.WriteLine($"Material {material.Name} not found in mesh materials");
            return;
        }

        // EARLY EXIT before TexturesCache.GetTexture so we don't pay the
        // PNG-decode cost (the dominant per-tile cost on large fixtures,
        // ~50-80 MB compressed per texture) when this tile doesn't actually
        // reference this material. A naive code path that loads `tex` first
        // and only checks emptiness later wastes one PNG decode per unused
        // (material, tile) pair — for a 69-material fixture with ~15 used
        // per tile × 43 tiles that's >2000 redundant decodes.
        //
        // For the _saveUv path (hierarchical pipeline) the cluster set already
        // partitions by material; if no cluster uses this material, neither
        // does any face, and the texture is dead weight.
        // For the _saveVertexColor path (legacy pipeline) check faces directly.
        bool hasWork = false;
        if (_saveUv && _clusterInfos != null)
        {
            foreach (var info in _clusterInfos)
                if (info.MaterialIndex == materialIndex) { hasWork = true; break; }
        }
        if (!hasWork && _saveVertexColor)
        {
            for (int i = 0; i < _faces.Count; i++)
                if (_faces[i].MaterialIndex == materialIndex) { hasWork = true; break; }
        }
        if (!hasWork) return;

        long decodeTicks = 0, resampleTicks = 0, stepStartTicks; // perf telemetry — decode/resample CPU ticks; printed in the [fillsplit] line below
        Image<Rgba32> tex = null;

        if (!string.IsNullOrEmpty(material.Texture))
        {
            stepStartTicks = Stopwatch.GetTimestamp();
            tex = TexturesCache.GetTexture(material.Texture);
            decodeTicks += Stopwatch.GetTimestamp() - stepStartTicks;
        }

        // Special case: When !_saveUv, extract vertex colors directly without atlas/clustering
        if (_saveVertexColor && tex != null)
        {
            Debug.WriteLine($"Extracting vertex colors for material {material.Name} [{Name}]");

            var texWidth = tex.Width;
            var texHeight = tex.Height;

            // Extract colors for all faces using this material
            for (var i = 0; i < _faces.Count; i++)
            {
                var face = _faces[i];
                if (face.MaterialIndex != materialIndex)
                    continue;

                var vtA = _textureVertices[face.TextureIndexA];
                var vtB = _textureVertices[face.TextureIndexB];
                var vtC = _textureVertices[face.TextureIndexC];

                var colorA = tex[(int)(vtA.X * texWidth), (int)((1 - vtA.Y) * texHeight)];
                var colorB = tex[(int)(vtB.X * texWidth), (int)((1 - vtB.Y) * texHeight)];
                var colorC = tex[(int)(vtC.X * texWidth), (int)((1 - vtC.Y) * texHeight)];

                _vertexColors[face.IndexA] = Common.ConvertToRGB(colorA);
                _vertexColors[face.IndexB] = Common.ConvertToRGB(colorB);
                _vertexColors[face.IndexC] = Common.ConvertToRGB(colorC);
            }

            Debug.WriteLine($"Extracted vertex colors for material {material.Name}");
        }

        if (!_saveUv)
            return;

        if (_clusterInfos == null || _clusterInfos.Count == 0)
        {
            Debug.WriteLine("No cluster infos available. Call PrepareRepackTextures() first.");
            return;
        }

        Debug.WriteLine($"Filling atlases for material {material.Name} [{Name}]");

        // Get only clusters for this material
        var materialClusters = _clusterInfos.Where(c => c.MaterialIndex == materialIndex).ToList();

        if (materialClusters.Count == 0)
        {
            Debug.WriteLine($"No clusters found for material {material.Name}");
            return;
        }

        Image<Rgba32> norm = null;

        try
        {
            if (!string.IsNullOrEmpty(material.NormalMap))
            {
                stepStartTicks = Stopwatch.GetTimestamp();
                norm = TexturesCache.GetTexture(material.NormalMap);
                decodeTicks += Stopwatch.GetTimestamp() - stepStartTicks;
            }

            if (tex == null && norm == null)
            {
                Debug.WriteLine($"No textures available for material {material.Name}");
                return;
            }

            var texWidth = tex?.Width ?? norm.Width;
            var texHeight = tex?.Height ?? norm.Height;

            // Copy texture regions to atlas for this material's clusters
            stepStartTicks = Stopwatch.GetTimestamp();
            foreach (var info in materialClusters)
            {
                var clusterX = (int)Math.Floor(info.UvRect.Left * (texWidth - 1));
                var clusterY = (int)Math.Floor(info.UvRect.Top * (texHeight - 1));
                var clusterW = (int)Math.Max(Math.Ceiling(info.UvRect.Width * texWidth), 1);
                var clusterH = (int)Math.Max(Math.Ceiling(info.UvRect.Height * texHeight), 1);

                var height = tex?.Height ?? norm.Height;
                var adjustedSourceY = height - (clusterY + clusterH);
                if (adjustedSourceY < 0)
                    adjustedSourceY = (int)Math.Ceiling((double)(clusterY + clusterH) / height) * height -
                                      (clusterY + clusterH);

                // Dest height/Y uses PackedRect (which may have been scaled
                // down in PrepareRepackTextures if natural exceeded cap).
                int destW = info.PackedRect.Width;
                int destH = info.PackedRect.Height;
                var adjustedDestY = Math.Max(AtlasEdgeLength - (info.PackedRect.Y + destH), 0);

                if (tex != null)
                {
                    if (_atlasTexture == null)
                        _atlasTexture = new Image<Rgba32>(AtlasEdgeLength, AtlasEdgeLength);

                    if (_useSingleResamplePath)
                    {
                        Common.CopyImage(tex, _atlasTexture,
                            clusterX, adjustedSourceY, clusterW, clusterH,
                            info.PackedRect.X, adjustedDestY);
                    }
                    else
                    {
                        Common_Hlod.CopyImageScaled(tex, _atlasTexture,
                            clusterX, adjustedSourceY, clusterW, clusterH,
                            info.PackedRect.X, adjustedDestY, destW, destH);
                    }
                }

                if (norm != null)
                {
                    if (_atlasNormalMap == null)
                        _atlasNormalMap = new Image<Rgba32>(AtlasEdgeLength, AtlasEdgeLength);

                    if (_useSingleResamplePath)
                    {
                        Common.CopyImage(norm, _atlasNormalMap,
                            clusterX, adjustedSourceY, clusterW, clusterH,
                            info.PackedRect.X, adjustedDestY);
                    }
                    else
                    {
                        Common_Hlod.CopyImageScaled(norm, _atlasNormalMap,
                            clusterX, adjustedSourceY, clusterW, clusterH,
                            info.PackedRect.X, adjustedDestY, destW, destH);
                    }
                }
            }
            resampleTicks += Stopwatch.GetTimestamp() - stepStartTicks;
            Console.WriteLine($" [fillsplit] {FilePath} mat={materialIndex} decodeMs={decodeTicks * 1000.0 / Stopwatch.Frequency:F1} resampleMs={resampleTicks * 1000.0 / Stopwatch.Frequency:F1} clusters={materialClusters.Count}");

            Debug.WriteLine($"Filled {materialClusters.Count} clusters for material {material.Name}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error filling atlas for material {material.Name}: {e}");
            throw;
        }
    }

    /// <summary>
    /// Phase 3: Finalizes texture repacking by updating UV coordinates, saving atlases to disk, and updating materials.
    /// Must be called after PrepareRepackTextures() and FillAtlases().
    /// </summary>
    public void SaveAtlasesAndUpdateMaterial()
    {
        var folderPath = Path.GetDirectoryName(FilePath) ?? string.Empty;

        try
        {
            if (!_saveUv)
            {
                var material = _materials[0];
                var newMaterial = new Material($"{Name}-material", null, null,
                    material.AmbientColor, material.DiffuseColor, material.SpecularColor,
                    material.SpecularExponent, material.Dissolve, material.IlluminationModel);

                _materials.Clear();
                _materials.Add(newMaterial);

                for (var i = 0; i < _faces.Count; i++)
                    _faces[i].MaterialIndex = 0;
            }

            if (_clusterInfos == null || _clusterInfos.Count == 0)
            {
                Debug.WriteLine("No cluster infos available. Call PrepareRepackTextures() first.");
                return;
            }

            Debug.WriteLine($"Saving atlases and updating material for {Name}");

            // Update UV coordinates for all faces based on packed positions
            var newTextureVertices = new Dictionary<Vertex2, int>(_textureVertices.Count);

            foreach (var info in _clusterInfos)
            {
                // Map source-UV offsets to atlas-UV offsets via the
                // CLUSTER's atlas-UV extent (PackedRect / AtlasEdgeLength),
                // not via the source texture size. The naive form
                // (`scaleX = texWidth / AtlasEdgeLength`) is only correct
                // when PackedRect.Width ≈ UvRect.Width × texWidth (the
                // natural-pack 1:1 copy case). When the natural atlas
                // exceeds the per-depth cap, PackedRects are scaled down
                // and that assumption breaks — UVs would spill outside the
                // cluster's (now-smaller) atlas region, sampling
                // neighboring clusters or empty space. The form below uses
                // the actual atlas-UV dimensions of the packed rect and
                // reduces to the simple formula in the unscaled case.
                double atlasUvWidth = info.PackedRect.Width / (double)AtlasEdgeLength;
                double atlasUvHeight = info.PackedRect.Height / (double)AtlasEdgeLength;
                double invUvW = info.UvRect.Width > 0 ? 1.0 / info.UvRect.Width : 0;
                double invUvH = info.UvRect.Height > 0 ? 1.0 / info.UvRect.Height : 0;

                foreach (var faceIndex in info.Cluster)
                {
                    var face = _faces[faceIndex];

                    var vtA = _textureVertices[face.TextureIndexA];
                    var vtB = _textureVertices[face.TextureIndexB];
                    var vtC = _textureVertices[face.TextureIndexC];

                    // Offset within cluster in source UV space, normalized
                    // to [0, atlas-UV-extent] of this cluster's packed rect.
                    var dxA = Math.Max(0, vtA.X - info.UvRect.X) * invUvW * atlasUvWidth;
                    var dyA = Math.Max(0, vtA.Y - info.UvRect.Y) * invUvH * atlasUvHeight;
                    var dxB = Math.Max(0, vtB.X - info.UvRect.X) * invUvW * atlasUvWidth;
                    var dyB = Math.Max(0, vtB.Y - info.UvRect.Y) * invUvH * atlasUvHeight;
                    var dxC = Math.Max(0, vtC.X - info.UvRect.X) * invUvW * atlasUvWidth;
                    var dyC = Math.Max(0, vtC.Y - info.UvRect.Y) * invUvH * atlasUvHeight;

                    // Calculate new UV coordinates in atlas space
                    var relX = info.PackedRect.X / (double)AtlasEdgeLength;
                    var relY = info.PackedRect.Y / (double)AtlasEdgeLength;

                    var newVtA = new Vertex2(Math.Clamp(relX + dxA, 0, 1), Math.Clamp(relY + dyA, 0, 1));
                    var newVtB = new Vertex2(Math.Clamp(relX + dxB, 0, 1), Math.Clamp(relY + dyB, 0, 1));
                    var newVtC = new Vertex2(Math.Clamp(relX + dxC, 0, 1), Math.Clamp(relY + dyC, 0, 1));

                    face.TextureIndexA = newTextureVertices.AddIndex(newVtA);
                    face.TextureIndexB = newTextureVertices.AddIndex(newVtB);
                    face.TextureIndexC = newTextureVertices.AddIndex(newVtC);
                }
            }

            // Update texture vertices list
            _textureVertices = newTextureVertices.OrderBy(item => item.Value).Select(item => item.Key).ToList();

            // Save atlases to disk. Ensure folderPath exists — the per-tile
            // temp dir is created by the caller in
            // HierarchicalTilingStage.PrepareTileForGlb before PackAndWrite,
            // but an intermittent race during bakes has been observed
            // (DirectoryNotFoundException at the JpegEncoder Save below), so
            // we cannot assume the dir persists. CreateDirectory is a no-op
            // if it already exists.
            Directory.CreateDirectory(folderPath);
            var textureFileName = $"{Name}-texture-diffuse-atlas.jpg";
            var normalFileName = $"{Name}-texture-normal-atlas.png"; // normal maps stay PNG (lossless)

            var pathTexture = Path.Combine(folderPath, textureFileName);
            var pathNormal = Path.Combine(folderPath, normalFileName);

            var hasAtlasTexture = _atlasTexture != null;
            var hasAtlasNormalMap = _atlasNormalMap != null;

            if (hasAtlasTexture)
            {
                // Bleed cluster boundary pixels into surrounding empty atlas space.
                // Why: bilinear filtering at cluster edges samples both the cluster
                // pixel and its neighbor in the atlas. If the neighbor is empty
                // (black), the rendered triangle edge shows a dark fringe —
                // visible as a "crack" along every tile boundary across the whole
                // model. Dilating cluster pixels outward by N pixels into the
                // empty space makes the bilinear sampler pick up valid cluster
                // colors at the boundary.
                //
                // 4 px is a sane lower bound (1-2 mip levels at a 4096px atlas
                // edge). Lower (e.g. Nexus 1 px) misses mipmap sampling; higher
                // costs more compute but has no other artifact. 16 px covers
                // bilinear + several mip levels + anisotropic safety.
                Common_Hlod.DilateAtlasBleed(_atlasTexture, bleed: 16);
                var compressedTextureWidth = (int)(_atlasTexture.Width * _textureQuality);
                // Clamp the final target to _maxAtlasSize. The bin-pack in
                // PrepareRepackTextures has ALREADY sized this atlas to fit
                // within the cap; this clamp guards against an off-by-one
                // from ImageSharp's resize or a non-power-of-2 bin-pack size.
                // (An older path used PreviousPowerOfTwo, which would round
                // 3500 → 2048 even when _maxAtlasSize was 4096 and erase the
                // benefit of raising the per-tile cap — avoided here.)
                var targetSize = Math.Min(compressedTextureWidth, _maxAtlasSize);
                var mode = _useSingleResamplePath ? "single-resample" : "per-cluster";
                Console.WriteLine(
                    $" [HLOD atlas] {FilePath} natural={_naturalAtlasEdgeLength} cap={_maxAtlasSize} final={targetSize} mode={mode} clusters={_clusterInfos?.Count ?? 0}");

                if (_atlasTexture.Width != targetSize)
                {
                    var quality = (float)targetSize / _atlasTexture.Width;
                    Debug.WriteLine(
                        $"Downscale {_atlasTexture.Width} => {targetSize} {quality:F2}% (target Quality {_textureQuality:F2}) [{Name}]");
                    // Lanczos3 whole-atlas downsample — the shipped HLOD resampler. (Sharper kernels
                    // lanczos8/Compand were operator-visual-gate-REJECTED on real photogrammetry: no visible
                    // benefit; removed. See TRACK-1-ATTEMPT-LEDGER-SUMMARY.md.)
                    _atlasTexture.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Size = new Size(targetSize, targetSize),
                        Mode = ResizeMode.Max,
                        Sampler = KnownResamplers.Lanczos3,
                    }));
                }

                // Optional unsharp-mask sharpening of the BASE atlas before
                // JPEG encode. Lets Cesium's auto-generated mip chain inherit
                // some sharpness, partially compensating for the mip-LOD-induced
                // softness at distance. Strength 0..1 maps to ImageSharp
                // GaussianSharpen sigma 0..3.0 (0.5 → σ≈1.5 mild, 1.0 → σ≈3.0
                // strong).
                if (AtlasUnsharpAmount > 0.0)
                {
                    float sigma = (float)(AtlasUnsharpAmount * 3.0);
                    _atlasTexture.Mutate(x => x.GaussianSharpen(sigma));
                }
                // JPEG quality from config (--jpeg-quality, default 90). 4:4:4 chroma (HLOD_JPEG_444) was
                // operator-visual-gate-REJECTED (invisible at real zooms, +15-18% bytes); removed → ImageSharp's
                // default 4:2:0 at q90.
                int _jq = int.TryParse(System.Environment.GetEnvironmentVariable("HLOD_JPEG_QUALITY"), out var _qv) && _qv is > 0 and <= 100 ? _qv : _jpegQuality;
                _atlasTexture.Save(pathTexture, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = _jq });
                Debug.WriteLine($"Saved texture atlas to {pathTexture}");
            }

            if (_atlasNormalMap != null)
            {
                var normalTargetSize = Math.Min((int)(_atlasNormalMap.Width * _textureQuality), _maxAtlasSize);
                if (!hasAtlasTexture)
                {
                    var mode = _useSingleResamplePath ? "single-resample" : "per-cluster";
                    Console.WriteLine(
                        $" [HLOD atlas] {FilePath} natural={_naturalAtlasEdgeLength} cap={_maxAtlasSize} final={normalTargetSize} mode={mode}");
                }

                if (_atlasNormalMap.Width != normalTargetSize)
                {
                    _atlasNormalMap.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Size = new Size(normalTargetSize, normalTargetSize),
                        Mode = ResizeMode.Max,
                        Sampler = KnownResamplers.Lanczos3
                    }));
                }

                _atlasNormalMap.Save(pathNormal);
                Debug.WriteLine($"Saved normal map atlas to {pathNormal}");
            }

            // Create merged material
            var firstMaterial = _materials[_clusterInfos[0].MaterialIndex];
            var mergedMaterial = new Material($"{Name}-material", null, null,
                firstMaterial.AmbientColor, firstMaterial.DiffuseColor, firstMaterial.SpecularColor,
                firstMaterial.SpecularExponent, firstMaterial.Dissolve, firstMaterial.IlluminationModel);

            if (hasAtlasTexture)
                mergedMaterial.Texture = textureFileName;

            if (hasAtlasNormalMap)
                mergedMaterial.NormalMap = normalFileName;

            // Replace all materials with merged material
            _materials.Clear();
            _materials.Add(mergedMaterial);

            // Update all faces to use the merged material
            for (var i = 0; i < _faces.Count; i++)
                _faces[i].MaterialIndex = 0;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
        finally
        {
            // ALWAYS dispose atlases, even on early return or exception
            _atlasTexture?.Dispose();
            _atlasNormalMap?.Dispose();
            _atlasTexture = null;
            _atlasNormalMap = null;
        }
    }

    /// <summary>
    /// Helper method for bin packing clusters without loading textures.
    /// Fills the PackedRect property of each ClusterInfo.
    ///
    /// <paramref name="scale"/> = final-atlas-pixel / source-texture-pixel
    /// ratio. 1.0 when natural pack fits the cap; smaller when the natural
    /// pack would exceed <see cref="_maxAtlasSize"/> and clusters must be
    /// scaled down to fit. Cluster dims are computed in final-pixel space so
    /// the gutter (<see cref="AtlasGutterPixels"/>) is a fixed atlas-pixel
    /// width regardless of source resolution — see the constant docs.
    ///
    /// The bin-pack inflates each cluster's insertion size by 2 × gutter and
    /// stores the inner rect (visible cluster pixels) in PackedRect. The
    /// gutter ring around each inner rect is empty after FillAtlases and is
    /// then filled by <see cref="Common_Hlod.DilateAtlasBleed"/> from the
    /// cluster's own edge pixels.
    /// </summary>
    // When cluster count exceeds this threshold, switch from
    // MaxRectanglesBinPack (RectangleBestAreaFit is O(N²) per Insert due to
    // PruneFreeList) to SkylineBinPack (O(N × W_avg), deterministic, no
    // free-rect-list growth). MaxRects packs slightly tighter on heterogeneous
    // N and is preferred at high gutter-fill ratios; the skyline path keeps
    // very-high-cluster tiles (10k+ UV islands) from stalling.
    // G9-FASTPACK WIN: route ANY tile with >256 clusters to the fast Skyline packer (was 5000).
    // MaxRects is O(F²)-per-insert; on tiles with hundreds-to-thousands of UV islands that
    // dominated the critical path (small2 1.47× / hd 1.45×). Skyline is the SAME packer already
    // used for >5000-cluster tiles (render-validated in every champion). Verified artifact-level:
    // atlas dims (=per-cluster resolution) preserved on all 156 matched hd+vlrg tiles (0 scale-down).
    // Env override HLOD_FASTPACK_THRESHOLD for tuning.
    private static readonly int HighClusterFastPathThreshold =
        int.TryParse(System.Environment.GetEnvironmentVariable("HLOD_FASTPACK_THRESHOLD"), out var _t) && _t > 0
            ? _t : 256;

    private bool TryPackClusterInfosAtNaturalSize(int initialEdgeLength, int maxEdgeLength, out int edgeLength)
    {
        edgeLength = initialEdgeLength;
        while (!TryPackClusterInfos(_clusterInfos, edgeLength, 1.0))
        {
            var newEdgeLength = Math.Max(edgeLength + 10, (int)(edgeLength * 1.02));
            if (newEdgeLength > maxEdgeLength)
                return false;

            edgeLength = newEdgeLength;
        }

        return true;
    }

    private bool TryPackClusterInfos(List<ClusterInfo> clusterInfos, int edgeLength, double scale)
    {
        int g = EffectiveGutterPixels(clusterInfos.Count);
        bool useFastPath = clusterInfos.Count > HighClusterFastPathThreshold;

        if (useFastPath)
        {
            var skyline = new SkylineBinPack(edgeLength, edgeLength);
            for (var i = 0; i < clusterInfos.Count; i++)
            {
                var info = clusterInfos[i];
                var material = _materials[info.MaterialIndex];
                var texturePath = string.IsNullOrEmpty(material.Texture) ? material.NormalMap : material.Texture;
                var textureInfo = TexturesCache.GetCappedDims(texturePath);
                var clusterW = (int)Math.Max(Math.Ceiling(info.UvRect.Width * textureInfo.Width * scale), 1);
                var clusterH = (int)Math.Max(Math.Ceiling(info.UvRect.Height * textureInfo.Height * scale), 1);
                var placed = skyline.Insert(clusterW + 2 * g, clusterH + 2 * g);
                if (placed.Width == 0) return false;
                info.PackedRect = new Rectangle(placed.X + g, placed.Y + g, clusterW, clusterH);
            }
            return true;
        }

        var binPack = new MaxRectanglesBinPack(edgeLength, edgeLength, false);
        for (var i = 0; i < clusterInfos.Count; i++)
        {
            var info = clusterInfos[i];
            var material = _materials[info.MaterialIndex];
            var texturePath = string.IsNullOrEmpty(material.Texture) ? material.NormalMap : material.Texture;
            var textureInfo = TexturesCache.GetCappedDims(texturePath);
            // Scale source cluster size into final-atlas-pixel space.
            var clusterW = (int)Math.Max(Math.Ceiling(info.UvRect.Width * textureInfo.Width * scale), 1);
            var clusterH = (int)Math.Max(Math.Ceiling(info.UvRect.Height * textureInfo.Height * scale), 1);
            // Inflate insertion size by 2 × gutter to reserve the bilinear-safe
            // border. Inner rect = (rect.X + g, rect.Y + g, clusterW, clusterH).
            var packedRect = binPack.Insert(clusterW + 2 * g, clusterH + 2 * g, FreeRectangleChoiceHeuristic.RectangleBestAreaFit);

            if (packedRect.Width == 0)
                return false;

            info.PackedRect = new Rectangle(
                packedRect.X + g,
                packedRect.Y + g,
                clusterW,
                clusterH);
        }

        return true;
    }


    /// <summary>
    ///     Calculates the bounding box of a set of points.
    /// </summary>
    /// <param name="cluster"></param>
    /// <returns></returns>
    private RectangleF GetClusterRect(IReadOnlyList<int> cluster)
    {
        double maxX = double.MinValue, maxY = double.MinValue;
        double minX = double.MaxValue, minY = double.MaxValue;

        for (var n = 0; n < cluster.Count; n++)
        {
            var face = _faces[cluster[n]];

            var vtA = _textureVertices[face.TextureIndexA];
            var vtB = _textureVertices[face.TextureIndexB];
            var vtC = _textureVertices[face.TextureIndexC];

            maxX = Math.Max(Math.Max(Math.Max(maxX, vtC.X), vtB.X), vtA.X);
            maxY = Math.Max(Math.Max(Math.Max(maxY, vtC.Y), vtB.Y), vtA.Y);

            minX = Math.Min(Math.Min(Math.Min(minX, vtC.X), vtB.X), vtA.X);
            minY = Math.Min(Math.Min(Math.Min(minY, vtC.Y), vtB.Y), vtA.Y);
        }

        return new RectangleF((float)minX, (float)minY, (float)(maxX - minX), (float)(maxY - minY));
    }

    // Cluster build using a single Visited HashSet + a self-growing cluster
    // list that acts as the BFS queue. Each face is enqueued at most once
    // (visited.Add is O(1)). The walk uses index iteration on `cluster` so
    // any neighbors discovered mid-walk get processed before the loop ends.
    // No Remove() calls — net O(F + edges) per material, vs the naive
    // O(F²) of removing visited faces from a remaining-faces list.
    private static List<List<int>> GetFacesClusters(IEnumerable<int> facesIndexes,
        IReadOnlyDictionary<int, List<int>> facesMapper)
    {
        var clusters = new List<List<int>>();
        var visited = new HashSet<int>();

        foreach (var seed in facesIndexes)
        {
            if (!visited.Add(seed)) continue;
            var cluster = new List<int> { seed };
            // Walk-while-growing: i indexes into cluster; new neighbors are
            // appended past cluster.Count and seen by subsequent iterations.
            for (int i = 0; i < cluster.Count; i++)
            {
                int faceIndex = cluster[i];
                if (!facesMapper.TryGetValue(faceIndex, out var connected)) continue;
                for (int k = 0; k < connected.Count; k++)
                {
                    int n = connected[k];
                    if (visited.Add(n)) cluster.Add(n);
                }
            }
            clusters.Add(cluster);
        }

        return clusters;
    }

    private static Dictionary<int, List<int>> GetFacesMapper(Dictionary<Edge, List<int>> edgesMapper)
    {
        var facesMapper = new Dictionary<int, List<int>>();

        foreach (var edge in edgesMapper)
            for (var i = 0; i < edge.Value.Count; i++)
            {
                var faceIndex = edge.Value[i];
                if (!facesMapper.ContainsKey(faceIndex))
                    facesMapper.Add(faceIndex, []);

                for (var index = 0; index < edge.Value.Count; index++)
                {
                    var f = edge.Value[index];
                    if (f != faceIndex)
                        facesMapper[faceIndex].Add(f);
                }
            }

        return facesMapper;
    }

    private Dictionary<Edge, List<int>> GetEdgesMapper(IReadOnlyList<int> facesIndexes)
    {
        var edgesMapper = new Dictionary<Edge, List<int>>();
        edgesMapper.EnsureCapacity(facesIndexes.Count * 3);

        for (var idx = 0; idx < facesIndexes.Count; idx++)
        {
            var faceIndex = facesIndexes[idx];
            var f = _faces[faceIndex];

            var e1 = new Edge(f.TextureIndexA, f.TextureIndexB);
            var e2 = new Edge(f.TextureIndexB, f.TextureIndexC);
            var e3 = new Edge(f.TextureIndexA, f.TextureIndexC);

            if (!edgesMapper.ContainsKey(e1))
                edgesMapper.Add(e1, []);

            if (!edgesMapper.ContainsKey(e2))
                edgesMapper.Add(e2, []);

            if (!edgesMapper.ContainsKey(e3))
                edgesMapper.Add(e3, []);

            edgesMapper[e1].Add(faceIndex);
            edgesMapper[e2].Add(faceIndex);
            edgesMapper[e3].Add(faceIndex);
        }

        return edgesMapper;
    }

    private List<List<int>> GetFacesByMaterial()
    {
        var res = _materials.Select(_ => new List<int>()).ToList();

        for (var i = 0; i < _faces.Count; i++)
        {
            var f = _faces[i];

            res[f.MaterialIndex].Add(i);
        }

        return res;
    }

    #region Utils

    public Box3 Bounds
    {
        get
        {
            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var minZ = double.MaxValue;

            var maxX = double.MinValue;
            var maxY = double.MinValue;
            var maxZ = double.MinValue;

            for (var index = 0; index < _vertices.Count; index++)
            {
                var v = _vertices[index];
                minX = minX < v.X ? minX : v.X;
                minY = minY < v.Y ? minY : v.Y;
                minZ = minZ < v.Z ? minZ : v.Z;

                maxX = v.X > maxX ? v.X : maxX;
                maxY = v.Y > maxY ? v.Y : maxY;
                maxZ = v.Z > maxZ ? v.Z : maxZ;
            }

            return new Box3(minX, minY, minZ, maxX, maxY, maxZ);
        }
    }

    public Vertex3 GetAverageOrientation()
    {
        double x = 0;
        double y = 0;
        double z = 0;

        for (var index = 0; index < _faces.Count; index++)
        {
            var f = _faces[index];
            var v1 = _vertices[f.IndexA];
            var v2 = _vertices[f.IndexB];
            var v3 = _vertices[f.IndexC];

            var orientation = Common.Orientation(v1, v2, v3);

            x += orientation.X;
            y += orientation.Y;
            z += orientation.Z;
        }

        x /= _faces.Count;
        y /= _faces.Count;
        z /= _faces.Count;

        // Calculate x, y and z angles
        var xAngle = Math.Atan2(y, z);
        var yAngle = Math.Atan2(x, z);
        var zAngle = Math.Atan2(y, x);

        return new Vertex3(xAngle, yAngle, zAngle);
    }

    public Vertex3 GetVertexBaricenter()
    {
        var x = 0.0;
        var y = 0.0;
        var z = 0.0;

        for (var index = 0; index < _vertices.Count; index++)
        {
            var v = _vertices[index];
            x += v.X;
            y += v.Y;
            z += v.Z;
        }

        x /= _vertices.Count;
        y /= _vertices.Count;
        z /= _vertices.Count;

        return new Vertex3(x, y, z);
    }

    public void WriteObj(string path, bool removeUnused = true)
    {
        // Phase 1: Prepare
        PrepareRepackTextures(removeUnused);

        // Phase 2: Fill atlases
        for (var i = 0; i < _materials.Count; i++)
        {
            var material = _materials[i];
            FillAtlases(material);
        }

        FilePath = path;

        // Phase 3: Save atlases and update materials
        SaveAtlasesAndUpdateMaterial();

        // Phase 4: Save atlases and update materials
        WriteGeometry();
    }

    private void RemoveUnusedVertices()
    {
        var newVertexes = new Dictionary<Vertex3, int>(_vertices.Count);

        for (var f = 0; f < _faces.Count; f++)
        {
            var face = _faces[f];

            var vA = _vertices[face.IndexA];
            var vB = _vertices[face.IndexB];
            var vC = _vertices[face.IndexC];

            if (!newVertexes.TryGetValue(vA, out var newVA))
                newVA = newVertexes.AddIndex(vA);

            face.IndexA = newVA;

            if (!newVertexes.TryGetValue(vB, out var newVB))
                newVB = newVertexes.AddIndex(vB);

            face.IndexB = newVB;

            if (!newVertexes.TryGetValue(vC, out var newVC))
                newVC = newVertexes.AddIndex(vC);

            face.IndexC = newVC;
        }

        _vertices = newVertexes.Keys.ToList();
    }

    public void WriteMaterial()
    {
        var path = Path.ChangeExtension(FilePath, "mtl");

        using (var writer = new FormattingStreamWriter(path, CultureInfo.InvariantCulture))
        {
            for (var index = 0; index < _materials.Count; index++)
            {
                var material = _materials[index];
                writer.WriteLine(material.ToMtl());
            }
        }
    }

    /// <summary>
    /// Writes the mesh geometry to an OBJ file, handling both textured and non-textured cases.
    /// </summary>
    public void WriteGeometry()
    {
        var hasTextures = _materials.Count > 0 && _textureVertices.Count > 0;

        using var writer = new FormattingStreamWriter(FilePath, CultureInfo.InvariantCulture);

        writer.Write("o ");
        writer.WriteLine(string.IsNullOrWhiteSpace(Name) ? DefaultName : Name);

        // Write material library reference if we have textures
        if (hasTextures)
        {
            var materialsPath = Path.ChangeExtension(FilePath, "mtl");
            writer.WriteLine("mtllib {0}", Path.GetFileName(materialsPath));
        }

        // Write vertices
        for (var i = 0; i < _vertices.Count; i++)
        {
            var vertex = _vertices[i];

            writer.Write("v ");
            writer.Write(vertex.X);
            writer.Write(" ");
            writer.Write(vertex.Y);
            writer.Write(" ");
            writer.Write(vertex.Z);

            // Write vertex colors if available
            if (_saveVertexColor && _vertexColors.Count > 0)
            {
                var vertexColor = _vertexColors[i];
                writer.Write(" ");
                writer.Write(vertexColor.R);
                writer.Write(" ");
                writer.Write(vertexColor.G);
                writer.Write(" ");
                writer.Write(vertexColor.B);
            }

            writer.WriteLine();
        }

        // Write texture vertices if we have textures and should save UVs
        if (hasTextures && _saveUv)
        {
            foreach (var textureVertex in _textureVertices)
            {
                writer.Write("vt ");
                writer.Write(textureVertex.X);
                writer.Write(" ");
                writer.WriteLine(textureVertex.Y);
            }
        }

        // Write faces
        if (hasTextures)
        {
            // Group faces by material
            var materialFaces = _faces
                .GroupBy(f => f.MaterialIndex)
                .OrderBy(g => g.Key);

            // Write faces grouped by material
            foreach (var group in materialFaces)
            {
                writer.WriteLine($"usemtl {_materials[group.Key].Name}");

                foreach (var face in group)
                    writer.WriteLine(face.ToObj(_saveUv));
            }
        }
        else
        {
            // Write faces without material/texture references
            for (var index = 0; index < _faces.Count; index++)
            {
                var face = _faces[index];
                writer.WriteLine(face.ToObj(false));
            }
        }

        WriteMaterial();
    }

    public int FacesCount => _faces.Count;
    public int VertexCount => _vertices.Count;

    #endregion
}
