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
    /// Inter-cluster gutter, in final-atlas pixels, reserved around each packed
    /// cluster and later filled by <see cref="Common_Hlod.DilateAtlasBleed"/> so
    /// bilinear sampling at a cluster edge picks up that cluster's edge pixels
    /// rather than the neighbour's. Must be sized in final atlas pixel space
    /// (post atlas-cap scale), or a large natural→small-cap downscale shrinks it
    /// to near zero.
    /// </summary>
    private const int AtlasGutterPixels = 16;

    // Shrink the gutter as cluster count grows: at high cluster counts each atlas
    // patch is tiny and a 16-px gutter would dominate the area budget.
    public static int EffectiveGutterPixels(int clusterCount)
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
    private int _maxAtlasSize;
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
    public int TextureBearingClusterCount => _clusterInfos?.Count ?? 0;
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
        // 0 means "no explicit cap" on LodConfig; here it would collapse the
        // atlas to nothing, so fall back to the default.
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

    /// <summary>Unsharp-mask strength (0 = off, 1 = strong) applied before JPEG encode.</summary>
    public double AtlasUnsharpAmount { get; set; } = 0.0;

    public int AtlasCapCeiling { get; set; } = 0;

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

    private double ClusterWorldArea(IReadOnlyList<int> cluster)
    {
        double a = 0;
        for (int i = 0; i < cluster.Count; i++)
        {
            var f = _faces[cluster[i]];
            var va = _vertices[f.IndexA];
            var vb = _vertices[f.IndexB];
            var vc = _vertices[f.IndexC];
            double abx = vb.X - va.X, aby = vb.Y - va.Y, abz = vb.Z - va.Z;
            double acx = vc.X - va.X, acy = vc.Y - va.Y, acz = vc.Z - va.Z;
            double cx = aby * acz - abz * acy;
            double cy = abz * acx - abx * acz;
            double cz = abx * acy - aby * acx;
            a += 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
        }
        return a;
    }

    private void DropSmallestIslandsToFit(Dictionary<int, IReadOnlyList<List<int>>> clustersByMaterial)
    {
        int ceiling = AtlasCapCeiling > _maxAtlasSize ? AtlasCapCeiling : _maxAtlasSize;
        if (ceiling <= 0) return;

        int count = 0;
        foreach (var kvp in clustersByMaterial)
        {
            var mat = _materials[kvp.Key];
            if (mat.Texture == null && mat.NormalMap == null) continue;
            count += kvp.Value.Count;
        }
        if (count == 0 || ClusterDensity.NeededAtlasEdge(count) <= ceiling) return;

        int maxC = ClusterDensity.MaxClustersForCeiling(ceiling);
        int target = Math.Max(1, (int)(maxC * 0.85));
        if (count <= target) return;

        var keyed = new List<(int mat, List<int> cluster, double area)>(count);
        foreach (var kvp in clustersByMaterial)
        {
            var mat = _materials[kvp.Key];
            if (mat.Texture == null && mat.NormalMap == null) continue;
            foreach (var cluster in kvp.Value)
                keyed.Add((kvp.Key, cluster, ClusterWorldArea(cluster)));
        }
        keyed.Sort((a, b) => b.area.CompareTo(a.area));

        var droppedFaces = new HashSet<int>();
        for (int i = target; i < keyed.Count; i++)
            foreach (var fi in keyed[i].cluster) droppedFaces.Add(fi);

        var map = new int[_faces.Count];
        var newFaces = new List<FaceT>(_faces.Count - droppedFaces.Count);
        for (int i = 0; i < _faces.Count; i++)
        {
            if (droppedFaces.Contains(i)) { map[i] = -1; continue; }
            map[i] = newFaces.Count;
            newFaces.Add(_faces[i]);
        }
        _faces.Clear();
        _faces.AddRange(newFaces);

        var keptByMat = new Dictionary<int, List<List<int>>>();
        for (int i = 0; i < target && i < keyed.Count; i++)
        {
            var (mat, cluster, _) = keyed[i];
            var remapped = new List<int>(cluster.Count);
            for (int k = 0; k < cluster.Count; k++) remapped.Add(map[cluster[k]]);
            if (!keptByMat.TryGetValue(mat, out var lst)) { lst = new List<List<int>>(); keptByMat[mat] = lst; }
            lst.Add(remapped);
        }

        clustersByMaterial.Clear();
        foreach (var kv in keptByMat) clustersByMaterial[kv.Key] = kv.Value;

        RemoveUnusedVertices();

        Console.WriteLine(
            $" [HLOD island-drop] {Name} {count} -> {target} clusters (dropped {count - target} smallest by world area, cap {ceiling})");
    }

    private void RaiseCapForClusterFloor(int clusterCount)
    {
        if (_maxAtlasSize <= 0 || clusterCount <= 0)
            return;

        int g = EffectiveGutterPixels(clusterCount);
        double floorEdge = 1 + 2 * g;
        double minArea = clusterCount * floorEdge * floorEdge / 0.5;
        int minEdge = Common.NextPowerOfTwo((int)Math.Ceiling(Math.Sqrt(minArea)));

        if (minEdge <= _maxAtlasSize)
            return;

        int ceiling = AtlasCapCeiling > _maxAtlasSize ? AtlasCapCeiling : _maxAtlasSize;
        if (minEdge > ceiling)
            throw new InvalidOperationException(
                $"Atlas pack infeasible at ceiling: {clusterCount} clusters need >= {minEdge}px edge " +
                $"(gutter {g}px floor, 0.5 pack efficiency) but ceiling is {ceiling}px. " +
                "Split this tile (deeper LOD) or raise --max-atlas-size.");

        Console.WriteLine(
            $" [HLOD cap-raise] {Name} clusters={clusterCount} gutter={g} cap {_maxAtlasSize} -> {minEdge} (ceiling {ceiling})");
        _maxAtlasSize = minEdge;
    }

    /// <summary>
    /// Builds cluster info, sizes the atlas, and bin-packs each cluster's PackedRect.
    /// </summary>
    public void PrepareRepackTextures(bool removeUnused = true)
    {
        Debug.WriteLine("Preparing texture repack for " + Name);

        if (removeUnused)
            RemoveUnusedVertices();

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

        DropSmallestIslandsToFit(clustersByMaterial);

        int textureBearingClusters = 0;
        foreach (var kvp in clustersByMaterial)
        {
            var mat = _materials[kvp.Key];
            if (mat.Texture == null && mat.NormalMap == null)
                continue;
            textureBearingClusters += kvp.Value.Count;
        }
        RaiseCapForClusterFloor(textureBearingClusters);

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
                var info = TexturesCache.GetCappedDims(texturePath, _maxAtlasSize);
                totalTextureArea += rect.Width * info.Width * rect.Height * info.Height;
            }
        }

        if (_clusterInfos.Count == 0)
        {
            Debug.WriteLine("No clusters to pack");
            return;
        }

        _clusterInfos.Sort((a, b) => b.Cluster.Count.CompareTo(a.Cluster.Count));

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

        // Single-resample path: when the natural edge is at most 4× the cap (and
        // under the 12288² ceiling that bounds RGBA32 memory), pack at natural
        // size and let one Lanczos3 downsample produce the atlas — sharper than
        // cumulative per-cluster resamples. Larger tiles fall through to the
        // per-cluster cap-bound path below.
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

        // Pre-apply the atlas cap so the bin-pack and gutter live in final atlas
        // pixel space; otherwise the gutter shrinks with the post-pack scale and
        // loses its bilinear-safe band.
        double scale = 1.0;
        if (_maxAtlasSize > 0 && edgeLength > _maxAtlasSize)
        {
            scale = (double)_maxAtlasSize / edgeLength;
            edgeLength = _maxAtlasSize;
        }

        var iterations = 1;
        // Bound edgeLength so a runaway pack loop fails fast instead of spinning.
        const int packEdgeLimit = 1_000_000;
        bool computedJumpUsed = false;
        int postJumpRetries = 0;
        const int postJumpRetryLimit = 200;
        while (!TryPackClusterInfos(_clusterInfos, edgeLength, scale))
        {
            if (edgeLength > packEdgeLimit)
                throw new InvalidOperationException(
                    $"Atlas packing did not converge: edgeLength={edgeLength} " +
                    $"exceeded {packEdgeLimit} after {iterations} retries " +
                    $"({_clusterInfos.Count} clusters). " +
                    "Likely a runaway gutter/insert formula — check before retrying.");
            // At the cap, never grow past it — re-scale clusters down so they fit.
            if (_maxAtlasSize > 0 && edgeLength >= _maxAtlasSize)
            {
                if (!computedJumpUsed)
                {
                    // Compute the area the current clusters need (with gutter),
                    // derive the implied natural-pack edge, and pick a scale that
                    // fits the cap with packing-efficiency headroom.
                    double clusterAreaSum = 0;
                    for (int i = 0; i < _clusterInfos.Count; i++)
                    {
                        var info = _clusterInfos[i];
                        var matSrc = _materials[info.MaterialIndex];
                        var texPath = string.IsNullOrEmpty(matSrc.Texture) ? matSrc.NormalMap : matSrc.Texture;
                        if (string.IsNullOrEmpty(texPath)) continue;
                        var srcInfo = TexturesCache.GetCappedDims(texPath);
                        // Must match TryPackClusterInfos exactly (inflate by 2 × gutter, ceil to px).
                        int gpx = EffectiveGutterPixels(_clusterInfos.Count);
                        int clusterW = (int)Math.Max(Math.Ceiling(info.UvRect.Width * srcInfo.Width * scale), 1) + 2 * gpx;
                        int clusterH = (int)Math.Max(Math.Ceiling(info.UvRect.Height * srcInfo.Height * scale), 1) + 2 * gpx;
                        clusterAreaSum += (double)clusterW * clusterH;
                    }
                    // 0.55 packing-efficiency factor leaves headroom so the first
                    // jump lands at or just below the cap.
                    double impliedNaturalEdge = Math.Sqrt(clusterAreaSum / 0.55);
                    if (impliedNaturalEdge > _maxAtlasSize)
                    {
                        double targetScale = scale * ((double)_maxAtlasSize / impliedNaturalEdge);
                        scale = targetScale;
                    }
                    else
                    {
                        // Implied edge already fits — only gutter overhead remains.
                        scale *= 0.98;
                    }
                    computedJumpUsed = true;
                }
                else
                {
                    postJumpRetries++;
                    if (postJumpRetries > postJumpRetryLimit)
                    {
                        int ceiling = AtlasCapCeiling > _maxAtlasSize ? AtlasCapCeiling : _maxAtlasSize;
                        int escalated = Common.NextPowerOfTwo(_maxAtlasSize + 1);
                        if (escalated <= ceiling)
                        {
                            Console.WriteLine(
                                $" [HLOD cap-raise] {Name} clusters={_clusterInfos.Count} pack-stall cap {_maxAtlasSize} -> {escalated} (ceiling {ceiling})");
                            _maxAtlasSize = escalated;
                            edgeLength = _maxAtlasSize;
                            scale = Math.Min(1.0, (double)_maxAtlasSize / Math.Max(_naturalAtlasEdgeLength, 1));
                            computedJumpUsed = false;
                            postJumpRetries = 0;
                            iterations++;
                            continue;
                        }
                        throw new InvalidOperationException(
                            $"Atlas packing did not converge after cap escalation to ceiling " +
                            $"({_clusterInfos.Count} clusters, scale={scale:F4}, cap={_maxAtlasSize}, ceiling={ceiling}). " +
                            "Bin-pack fragmentation exceeds available area even at the leaf cap. " +
                            "Split this tile (deeper LOD) or raise --max-atlas-size.");
                    }
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
    /// Fills the atlas with the clusters belonging to <paramref name="material"/>.
    /// Call once per material after PrepareRepackTextures(); fills incrementally.
    /// </summary>
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

        // Skip the texture decode entirely when this tile doesn't reference the
        // material — the decode is the dominant per-tile cost on large fixtures.
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

        // Hold a read lease on this material's source paths so a sibling tile's
        // eviction can't dispose the shared decoded source while we sample it.
        // Both AcquireRead calls are inside the try so the finally always
        // releases on any exit path; a leaked lease would defer eviction forever.
        try
        {
            TexturesCache.AcquireRead(material.Texture);
            TexturesCache.AcquireRead(material.NormalMap);
            long decodeTicks = 0, resampleTicks = 0, stepStartTicks;
            Image<Rgba32> tex = null;

            if (!string.IsNullOrEmpty(material.Texture))
            {
                stepStartTicks = Stopwatch.GetTimestamp();
                tex = TexturesCache.GetTexture(material.Texture, _maxAtlasSize);
                decodeTicks += Stopwatch.GetTimestamp() - stepStartTicks;
            }

            if (_saveVertexColor && tex != null)
            {
                Debug.WriteLine($"Extracting vertex colors for material {material.Name} [{Name}]");

                var texWidth = tex.Width;
                var texHeight = tex.Height;

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
                    norm = TexturesCache.GetTexture(material.NormalMap, _maxAtlasSize);
                    decodeTicks += Stopwatch.GetTimestamp() - stepStartTicks;
                }

                if (tex == null && norm == null)
                {
                    Debug.WriteLine($"No textures available for material {material.Name}");
                    return;
                }

                var texWidth = tex?.Width ?? norm.Width;
                var texHeight = tex?.Height ?? norm.Height;

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
        finally
        {
            TexturesCache.ReleaseRead(material.Texture);
            TexturesCache.ReleaseRead(material.NormalMap);
        }
    }

    /// <summary>
    /// Rewrites UVs to atlas space, writes the atlas files, and merges materials.
    /// Call after PrepareRepackTextures() and FillAtlases().
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

            var newTextureVertices = new Dictionary<Vertex2, int>(_textureVertices.Count);

            foreach (var info in _clusterInfos)
            {
                // Map source UVs to atlas UVs via the cluster's actual packed-rect
                // extent, not the source texture size — when PackedRects are scaled
                // down to fit the cap, a texture-size scale would spill UVs into the
                // neighbouring clusters.
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

            _textureVertices = newTextureVertices.OrderBy(item => item.Value).Select(item => item.Key).ToList();

            // The caller creates this dir, but a bake race has dropped it before
            // the Save below; re-create defensively (no-op if it exists).
            Directory.CreateDirectory(folderPath);
            var textureFileName = $"{Name}-texture-diffuse-atlas.jpg";
            var normalFileName = $"{Name}-texture-normal-atlas.png"; // normal maps stay PNG (lossless)

            var pathTexture = Path.Combine(folderPath, textureFileName);
            var pathNormal = Path.Combine(folderPath, normalFileName);

            var hasAtlasTexture = _atlasTexture != null;
            var hasAtlasNormalMap = _atlasNormalMap != null;

            if (hasAtlasTexture)
            {
                // Bleed cluster edge pixels into the surrounding empty atlas space
                // so bilinear/mip sampling at cluster edges doesn't pick up black
                // and show dark fringes ("cracks") along tile boundaries.
                Common_Hlod.DilateAtlasBleed(_atlasTexture, bleed: 16);
                var compressedTextureWidth = (int)(_atlasTexture.Width * _textureQuality);
                // Guard against an off-by-one from resize or a non-power-of-2 pack.
                var targetSize = Math.Min(compressedTextureWidth, _maxAtlasSize);
                // KTX2/basisu (and s3tc-only WebGL clients) require dims to be
                // multiples of 4, else the tile samples opaque black. Floor to mult-4.
                targetSize = Math.Max(targetSize & ~3, 4);
                var mode = _useSingleResamplePath ? "single-resample" : "per-cluster";
                Console.WriteLine(
                    $" [HLOD atlas] {FilePath} natural={_naturalAtlasEdgeLength} cap={_maxAtlasSize} final={targetSize} mode={mode} clusters={_clusterInfos?.Count ?? 0}");

                if (_atlasTexture.Width != targetSize)
                {
                    var quality = (float)targetSize / _atlasTexture.Width;
                    Debug.WriteLine(
                        $"Downscale {_atlasTexture.Width} => {targetSize} {quality:F2}% (target Quality {_textureQuality:F2}) [{Name}]");
                    _atlasTexture.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Size = new Size(targetSize, targetSize),
                        Mode = ResizeMode.Max,
                        Sampler = KnownResamplers.Lanczos3,
                    }));
                }

                // Optional unsharp before JPEG encode; strength 0..1 maps to
                // GaussianSharpen sigma 0..3.0.
                if (AtlasUnsharpAmount > 0.0)
                {
                    float sigma = (float)(AtlasUnsharpAmount * 3.0);
                    _atlasTexture.Mutate(x => x.GaussianSharpen(sigma));
                }
                int _jq = int.TryParse(System.Environment.GetEnvironmentVariable("HLOD_JPEG_QUALITY"), out var _qv) && _qv is > 0 and <= 100 ? _qv : _jpegQuality;
                _atlasTexture.Save(pathTexture, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = _jq });
                Debug.WriteLine($"Saved texture atlas to {pathTexture}");
            }

            if (_atlasNormalMap != null)
            {
                var normalTargetSize = Math.Min((int)(_atlasNormalMap.Width * _textureQuality), _maxAtlasSize);
                // Same multiple-of-4 floor as the diffuse atlas above (KTX2/basisu).
                normalTargetSize = Math.Max(normalTargetSize & ~3, 4);
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

            var firstMaterial = _materials[_clusterInfos[0].MaterialIndex];
            var mergedMaterial = new Material($"{Name}-material", null, null,
                firstMaterial.AmbientColor, firstMaterial.DiffuseColor, firstMaterial.SpecularColor,
                firstMaterial.SpecularExponent, firstMaterial.Dissolve, firstMaterial.IlluminationModel);

            if (hasAtlasTexture)
                mergedMaterial.Texture = textureFileName;

            if (hasAtlasNormalMap)
                mergedMaterial.NormalMap = normalFileName;

            _materials.Clear();
            _materials.Add(mergedMaterial);

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
    /// Bin-packs clusters (no texture load), filling each ClusterInfo.PackedRect.
    /// <paramref name="scale"/> is the final-atlas-pixel / source-pixel ratio: 1.0
    /// when the natural pack fits the cap, smaller when clusters must shrink to fit.
    /// Insertion size is inflated by 2 × gutter; the inner rect is stored.
    /// </summary>
    // Above this cluster count, use the O(N × W) SkylineBinPack instead of
    // MaxRectanglesBinPack, whose RectangleBestAreaFit is O(N²) per insert and
    // stalls on tiles with thousands of UV islands.
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
                var textureInfo = TexturesCache.GetCappedDims(texturePath, _maxAtlasSize);
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
            var textureInfo = TexturesCache.GetCappedDims(texturePath, _maxAtlasSize);
            var clusterW = (int)Math.Max(Math.Ceiling(info.UvRect.Width * textureInfo.Width * scale), 1);
            var clusterH = (int)Math.Max(Math.Ceiling(info.UvRect.Height * textureInfo.Height * scale), 1);
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


    /// <summary>UV-space bounding box of a cluster's faces.</summary>
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

    // BFS over face adjacency: the cluster list doubles as the queue (index walk),
    // and a single Visited set keeps it O(F + edges) per material.
    private static List<List<int>> GetFacesClusters(IEnumerable<int> facesIndexes,
        IReadOnlyDictionary<int, List<int>> facesMapper)
    {
        var clusters = new List<List<int>>();
        var visited = new HashSet<int>();

        foreach (var seed in facesIndexes)
        {
            if (!visited.Add(seed)) continue;
            var cluster = new List<int> { seed };
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
        PrepareRepackTextures(removeUnused);

        for (var i = 0; i < _materials.Count; i++)
        {
            var material = _materials[i];
            FillAtlases(material);
        }

        FilePath = path;

        SaveAtlasesAndUpdateMaterial();
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

        if (hasTextures)
        {
            var materialsPath = Path.ChangeExtension(FilePath, "mtl");
            writer.WriteLine("mtllib {0}", Path.GetFileName(materialsPath));
        }

        for (var i = 0; i < _vertices.Count; i++)
        {
            var vertex = _vertices[i];

            writer.Write("v ");
            writer.Write(vertex.X);
            writer.Write(" ");
            writer.Write(vertex.Y);
            writer.Write(" ");
            writer.Write(vertex.Z);

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

        if (hasTextures)
        {
            var materialFaces = _faces
                .GroupBy(f => f.MaterialIndex)
                .OrderBy(g => g.Key);

            foreach (var group in materialFaces)
            {
                writer.WriteLine($"usemtl {_materials[group.Key].Name}");

                foreach (var face in group)
                    writer.WriteLine(face.ToObj(_saveUv));
            }
        }
        else
        {
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
