using System.Diagnostics;
using System.Globalization;
using Obj2Tiles.Library.Algos;
using Obj2Tiles.Library.Materials;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Path = System.IO.Path;
using Rectangle = Obj2Tiles.Library.Algos.Model.Rectangle;

namespace Obj2Tiles.Library.Geometry;

public class MeshT : IMesh
{
    public const string DefaultName = "Mesh";
    private const int DefaultMaxAtlasSize = 4096;
    private const int DefaultJpegQuality = 90;

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

    public IReadOnlyList<Vertex3> Vertices => _vertices;
    public IReadOnlyList<Vertex2> TextureVertices => _textureVertices;
    public IReadOnlyList<FaceT> Faces => _faces;
    public IReadOnlyList<Material> Materials => _materials;
    public int AtlasEdgeLength { get; private set; }
    public string FilePath { get; set; }

    public MeshT(
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
        _maxAtlasSize = maxAtlasSize;
        _vertices = [..vertices];
        _textureVertices = [..textureVertices];
        _faces = [..faces];
        _materials = [..materials];
        _vertexColors = new List<RGB>();
        _saveVertexColor = saveVertexColor;
        _saveUv = saveUv;
    }

    public string Name { get; set; } = DefaultName;

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

        left = new MeshT(orderedLeftVertices, orderedLeftTextureVertices, leftFaces, _materials, _saveVertexColor,
            _saveUv, _packingThreshold, _textureQuality, _jpegQuality, _maxAtlasSize)
        {
            Name = $"{Name}-{utils.Axis}L"
        };
        right = new MeshT(orderedRightVertices, orderedRightTextureVertices, rightFaces, _materials,
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
                var info = TexturesCache.GetTextureInfo(texturePath);
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

        // Perform bin packing to find optimal layout and fill PackedRect
        var iterations = 1;

        while (!TryPackClusterInfos(_clusterInfos, edgeLength))
        {
            var newEdgeLength = Math.Max(edgeLength + 10, (int)(edgeLength * 1.02));
            edgeLength = newEdgeLength;
            iterations++;
        }

        AtlasEdgeLength = edgeLength;

        Console.WriteLine(
            $"Prepared atlas {edgeLength}x{edgeLength} [{Name}] ({totalTextureArea / edgeLength / edgeLength:F2} filled) ({iterations} iterations)");
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

        Image<Rgba32> tex = null;

        if (!string.IsNullOrEmpty(material.Texture))
        {
            tex = TexturesCache.GetTexture(material.Texture);
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
                norm = TexturesCache.GetTexture(material.NormalMap);
            }

            if (tex == null && norm == null)
            {
                Debug.WriteLine($"No textures available for material {material.Name}");
                return;
            }

            var texWidth = tex?.Width ?? norm.Width;
            var texHeight = tex?.Height ?? norm.Height;

            // Copy texture regions to atlas for this material's clusters
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

                var adjustedDestY = Math.Max(AtlasEdgeLength - (info.PackedRect.Y + clusterH), 0);

                if (tex != null)
                {
                    if (_atlasTexture == null)
                        _atlasTexture = new Image<Rgba32>(AtlasEdgeLength, AtlasEdgeLength);
                    
                    Common.CopyImage(tex, _atlasTexture, clusterX, adjustedSourceY, clusterW, clusterH,
                        info.PackedRect.X, adjustedDestY);
                }

                if (norm != null)
                {
                    if (_atlasNormalMap == null)
                        _atlasNormalMap = new Image<Rgba32>(AtlasEdgeLength, AtlasEdgeLength);
                    
                    Common.CopyImage(norm, _atlasNormalMap, clusterX, adjustedSourceY, clusterW, clusterH,
                        info.PackedRect.X, adjustedDestY);
                }
            }

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
                var material = _materials[info.MaterialIndex];
                var texturePath = string.IsNullOrEmpty(material.Texture) ? material.NormalMap : material.Texture;
                var textureInfo = TexturesCache.GetTextureInfo(texturePath);
                var texWidth = textureInfo.Width;
                var texHeight = textureInfo.Height;

                var scaleX = (double)texWidth / AtlasEdgeLength;
                var scaleY = (double)texHeight / AtlasEdgeLength;

                foreach (var faceIndex in info.Cluster)
                {
                    var face = _faces[faceIndex];

                    var vtA = _textureVertices[face.TextureIndexA];
                    var vtB = _textureVertices[face.TextureIndexB];
                    var vtC = _textureVertices[face.TextureIndexC];

                    // Calculate offset from cluster origin
                    var dxA = Math.Max(0, vtA.X - info.UvRect.X) * scaleX;
                    var dyA = Math.Max(0, vtA.Y - info.UvRect.Y) * scaleY;
                    var dxB = Math.Max(0, vtB.X - info.UvRect.X) * scaleX;
                    var dyB = Math.Max(0, vtB.Y - info.UvRect.Y) * scaleY;
                    var dxC = Math.Max(0, vtC.X - info.UvRect.X) * scaleX;
                    var dyC = Math.Max(0, vtC.Y - info.UvRect.Y) * scaleY;

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

            // Save atlases to disk
            var textureFileName = $"{Name}-texture-diffuse-atlas.jpg";
            var normalFileName = $"{Name}-texture-normal-atlas.png"; // normal maps stay PNG (lossless)

            var pathTexture = Path.Combine(folderPath, textureFileName);
            var pathNormal = Path.Combine(folderPath, normalFileName);

            var hasAtlasTexture = _atlasTexture != null;
            var hasAtlasNormalMap = _atlasNormalMap != null;

            if (hasAtlasTexture)
            {
                var compressedTextureWidth = (int)(_atlasTexture.Width * _textureQuality);
                var targetPowerOfTwo = Math.Min(Common.PreviousPowerOfTwo(compressedTextureWidth), _maxAtlasSize);

                if (_atlasTexture.Width != targetPowerOfTwo)
                {
                    var quality = (float)targetPowerOfTwo / _atlasTexture.Width;
                    Debug.WriteLine(
                        $"Downscale {_atlasTexture.Width} => {targetPowerOfTwo} {quality:F2}% (target Quality {_textureQuality:F2}) [{Name}]");
                    _atlasTexture.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Size = new Size(targetPowerOfTwo, targetPowerOfTwo),
                        Mode = ResizeMode.Max
                    }));
                }

                _atlasTexture.Save(pathTexture, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = _jpegQuality });
                Debug.WriteLine($"Saved texture atlas to {pathTexture}");
            }

            if (_atlasNormalMap != null)
            {
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

            Console.WriteLine($"Material updated and atlases saved for {Name}");
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
    /// </summary>
    private bool TryPackClusterInfos(List<ClusterInfo> clusterInfos, int edgeLength)
    {
        var binPack = new MaxRectanglesBinPack(edgeLength, edgeLength, false);

        for (var i = 0; i < clusterInfos.Count; i++)
        {
            var info = clusterInfos[i];
            var material = _materials[info.MaterialIndex];
            var texturePath = string.IsNullOrEmpty(material.Texture) ? material.NormalMap : material.Texture;
            var textureInfo = TexturesCache.GetTextureInfo(texturePath);
            var clusterW = (int)Math.Max(Math.Ceiling(info.UvRect.Width * textureInfo.Width), 1);
            var clusterH = (int)Math.Max(Math.Ceiling(info.UvRect.Height * textureInfo.Height), 1);

            var packedRect = binPack.Insert(clusterW, clusterH, FreeRectangleChoiceHeuristic.RectangleBestAreaFit);

            if (packedRect.Width == 0)
                return false;

            info.PackedRect = packedRect;
        }

        return true;
    }

    private bool TryPackClusters(List<ClusterInfo> clusterInfos, int edgeLength,
        Dictionary<int, (int width, int height)> textureDimensions, out Image<Rgba32> atlasTexture,
        out Image<Rgba32> atlasNormalMap)
    {
        var binPack = new MaxRectanglesBinPack(edgeLength, edgeLength, false);
        atlasTexture = null;
        atlasNormalMap = null;
        var hasTex = false;
        var hasNorm = false;

        for (var i = 0; i < clusterInfos.Count; i++)
        {
            var info = clusterInfos[i];
            var (texWidth, texHeight) = textureDimensions[info.MaterialIndex];

            var clusterW = (int)Math.Max(Math.Ceiling(info.UvRect.Width * texWidth), 1);
            var clusterH = (int)Math.Max(Math.Ceiling(info.UvRect.Height * texHeight), 1);

            var packedRect = binPack.Insert(clusterW, clusterH, FreeRectangleChoiceHeuristic.RectangleBestAreaFit);

            if (packedRect.Width == 0)
                return false;

            info.PackedRect = packedRect;
        }

        atlasTexture = new Image<Rgba32>(edgeLength, edgeLength);
        atlasNormalMap = new Image<Rgba32>(edgeLength, edgeLength);

        var clusterInfoByMaterial = clusterInfos.GroupBy(c => c.MaterialIndex);

        foreach (var group in clusterInfoByMaterial)
        {
            var materialIndex = group.Key;
            Image<Rgba32> tex = null;
            Image<Rgba32> norm = null;

            try
            {
                var material = _materials[materialIndex];
                var (texWidth, texHeight) = textureDimensions[materialIndex];

                if (!string.IsNullOrEmpty(material.Texture))
                {
                    tex = Image.Load<Rgba32>(material.Texture);
                }

                if (!string.IsNullOrEmpty(material.NormalMap))
                {
                    norm = Image.Load<Rgba32>(material.NormalMap);
                }

                foreach (var info in group)
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

                    var adjustedDestY = Math.Max(edgeLength - (info.PackedRect.Y + clusterH), 0);

                    if (tex != null)
                    {
                        hasTex = true;
                        Common.CopyImage(tex, atlasTexture, clusterX, adjustedSourceY, clusterW, clusterH,
                            info.PackedRect.X, adjustedDestY);

                        if (_saveVertexColor)
                            foreach (var faceIndex in info.Cluster)
                            {
                                var face = _faces[faceIndex];
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
                    }

                    if (norm != null)
                    {
                        hasNorm = true;
                        Common.CopyImage(norm, atlasNormalMap, clusterX, adjustedSourceY, clusterW, clusterH,
                            info.PackedRect.X, adjustedDestY);
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
            finally
            {
                tex?.Dispose();
                norm?.Dispose();
            }
        }

        if (!hasTex)
        {
            atlasTexture.Dispose();
            atlasTexture = null;
        }

        if (!hasNorm)
        {
            atlasNormalMap.Dispose();
            atlasNormalMap = null;
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

    private static List<List<int>> GetFacesClusters(IEnumerable<int> facesIndexes,
        IReadOnlyDictionary<int, List<int>> facesMapper)
    {
        var clusters = new List<List<int>>();
        var remainingFacesIndexes = new List<int>(facesIndexes);

        var currentCluster = new List<int> { remainingFacesIndexes[0] };
        var currentClusterCache = new HashSet<int> { remainingFacesIndexes[0] };
        remainingFacesIndexes.RemoveAt(0);

        var lastRemainingFacesCount = remainingFacesIndexes.Count;

        while (remainingFacesIndexes.Count > 0)
        {
            var cnt = currentCluster.Count;

            for (var index = 0; index < currentCluster.Count; index++)
            {
                var faceIndex = currentCluster[index];

                if (!facesMapper.TryGetValue(faceIndex, out var connectedFaces))
                    continue;

                for (var i = 0; i < connectedFaces.Count; i++)
                {
                    var connectedFace = connectedFaces[i];
                    if (currentClusterCache.Contains(connectedFace)) continue;

                    currentCluster.Add(connectedFace);
                    currentClusterCache.Add(connectedFace);
                    remainingFacesIndexes.Remove(connectedFace);
                }
            }

            // No new face was added
            if (cnt == currentCluster.Count)
            {
                // Add the cluster
                clusters.Add(currentCluster);

                // If no more faces, exit
                if (remainingFacesIndexes.Count == 0) break;

                // Let's continue with the next cluster
                currentCluster = [remainingFacesIndexes[0]];
                currentClusterCache = [remainingFacesIndexes[0]];
                remainingFacesIndexes.RemoveAt(0);
            }

            if (lastRemainingFacesCount == remainingFacesIndexes.Count)
            {
                Debug.WriteLine("Discarding " + remainingFacesIndexes.Count + " faces.");
                break;
            }

            lastRemainingFacesCount = remainingFacesIndexes.Count;
        }

        // Add the cluster
        clusters.Add(currentCluster);
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

    private void RemoveUnusedVerticesAndUvs()
    {
        var newVertexes = new Dictionary<Vertex3, int>(_vertices.Count);
        var newUvs = new Dictionary<Vertex2, int>(_textureVertices.Count);
        var newMaterials = new Dictionary<Material, int>(_materials.Count);

        for (var f = 0; f < _faces.Count; f++)
        {
            var face = _faces[f];

            // Vertices

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

            // Texture vertices

            var uvA = _textureVertices[face.TextureIndexA];
            var uvB = _textureVertices[face.TextureIndexB];
            var uvC = _textureVertices[face.TextureIndexC];

            if (!newUvs.TryGetValue(uvA, out var newUvA))
                newUvA = newUvs.AddIndex(uvA);

            face.TextureIndexA = newUvA;

            if (!newUvs.TryGetValue(uvB, out var newUvB))
                newUvB = newUvs.AddIndex(uvB);

            face.TextureIndexB = newUvB;

            if (!newUvs.TryGetValue(uvC, out var newUvC))
                newUvC = newUvs.AddIndex(uvC);

            face.TextureIndexC = newUvC;

            // Materials

            var material = _materials[face.MaterialIndex];

            if (!newMaterials.TryGetValue(material, out var newMaterial))
                newMaterial = newMaterials.AddIndex(material);

            face.MaterialIndex = newMaterial;
        }

        _vertices = newVertexes.Keys.ToList();
        _textureVertices = newUvs.Keys.ToList();
        _materials = newMaterials.Keys.ToList();
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