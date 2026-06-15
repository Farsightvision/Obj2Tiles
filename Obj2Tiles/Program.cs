using System.Diagnostics;
using System.Linq;
using CommandLine;
using Newtonsoft.Json;
using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Stages;
using Obj2Tiles.Stages.Model;

namespace Obj2Tiles
{
    internal class Program
    {
        private static async Task<int> Main(string[] args)
        {
            var parserResult = await Parser.Default.ParseArguments<Options>(args).WithParsedAsync(Run);
            if (parserResult.Tag == ParserResultType.NotParsed) Console.WriteLine("Usage: obj2tiles [options]");
            return 0;
        }

        private static async Task Run(Options options)
        {
            Console.WriteLine();
            Console.WriteLine(" *** OBJ to Tiles ***");
            Console.WriteLine();

            if (!TryGetConfig(options, out var config))
                return;
            
            Console.WriteLine(JsonConvert.SerializeObject(config));
            Console.WriteLine();

            if (config.HierarchicalLods)
            {
                await RunHierarchicalPipeline(config);
            }
            else
            {
                await RunFlatGridPipeline(config);
            }
        }

        private static async Task RunHierarchicalPipeline(AppConfig config)
        {
            Console.WriteLine(" *** Hierarchical pipeline (Phase 1) ***");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            long perfLastMs = 0;
            void Stage(string name)
            {
                long now = sw.ElapsedMilliseconds;
                long elapsedMs = now - perfLastMs;
                Console.WriteLine($" [perf] {name}: {elapsedMs / 1000.0:F2}s (T+{now / 1000.0:F2}s)");
                string tag = System.Text.RegularExpressions.Regex.Replace(name, @"^\d+[a-z]?\.\s*", "");
                tag = System.Text.RegularExpressions.Regex.Replace(tag, @"\s*\(.*", "");
                tag = tag.Replace(" ", "_");
                Console.WriteLine($"[perf:hlod:{tag}] elapsed={elapsedMs}ms t+{now}ms");
                perfLastMs = now;
            }

            if (Directory.Exists(config.Output)) Directory.Delete(config.Output, true);
            Directory.CreateDirectory(config.Output);

            if (config.Latitude is null || config.Longitude is null)
            {
                Console.Error.WriteLine(
                    " !! No --lat/--lon provided; placing tileset at default Milan coordinates. " +
                    "Production builds must specify lat/lon to avoid wrong georeferencing.");
            }
            double lat = config.Latitude ?? 45.46424200394995;
            double lon = config.Longitude ?? 9.190277486808588;

            var mesh = MeshUtils.LoadMesh(config.Input, false, true, config.PackingThreshold,
                config.LODs[0].Quality, config.LODs[0].JpegQuality, config.LODs[0].MaxAtlasSize);
            var bounds = mesh.Bounds;
            Stage("1. LoadMesh");

            if (mesh is not MeshT mt)
                throw new InvalidOperationException(
                    "Hierarchical pipeline requires a textured input mesh (MeshT). " +
                    "The input OBJ has no texture coordinates — drop --hierarchical-lods " +
                    "to use the default flat-grid pipeline (which handles position-only meshes).");

            MeshSanitizer.RequireUvsInUnitRange(mt.TextureVertices);

            var meshVerts = mt.Vertices;
            var meshUvs = mt.TextureVertices;
            var meshFaces = new List<MeshFace>(mt.Faces.Count);
            foreach (var f in mt.Faces)
            {
                var a = meshVerts[f.IndexA];
                var b = meshVerts[f.IndexB];
                var c = meshVerts[f.IndexC];
                double abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
                double acx = c.X - a.X, acy = c.Y - a.Y, acz = c.Z - a.Z;
                double cx = aby * acz - abz * acy;
                double cy = abz * acx - abx * acz;
                double cz = abx * acy - aby * acx;
                double area2 = Math.Sqrt(cx * cx + cy * cy + cz * cz);
                if (area2 < 1e-9) continue;
                meshFaces.Add(new MeshFace(
                    f.IndexA, f.IndexB, f.IndexC,
                    f.TextureIndexA, f.TextureIndexB, f.TextureIndexC,
                    f.MaterialIndex));
            }
            // Longest source edge: clipping only shortens edges, so leaf tiles must stay ≤ this (post-build gate below).
            double sourceMaxEdge = 0.0;
            foreach (var f in meshFaces)
            {
                var a = meshVerts[f.IndexA];
                var b = meshVerts[f.IndexB];
                var c = meshVerts[f.IndexC];
                double e1 = Distance(a, b);
                double e2 = Distance(b, c);
                double e3 = Distance(a, c);
                double m = e1 > e2 ? (e1 > e3 ? e1 : e3) : (e2 > e3 ? e2 : e3);
                if (m > sourceMaxEdge) sourceMaxEdge = m;
            }
            Stage("2. Sanitize + zero-area drop + maxEdge");

            var modelMetrics = ModelMetrics.Compute(
                triangleCount: meshFaces.Count,
                vertexCount: meshVerts.Count,
                bounds: bounds,
                materials: mt.Materials,
                objDirectory: Path.GetDirectoryName(Path.GetFullPath(config.Input)));
            Console.WriteLine($" -> ModelMetrics: {modelMetrics}");
            int phase1Mdop = config.Phase1BatchesPerMaterial > 0
                ? config.Phase1BatchesPerMaterial
                : Math.Max(1, Environment.ProcessorCount / 2);
            config.ParallelPhase1 = phase1Mdop > 1;
            Console.WriteLine($" -> ParallelPhase1: {(config.ParallelPhase1 ? "ON" : "OFF")} (tex={modelMetrics.TextureBytes / 1_048_576.0:F1} MiB, mdop={phase1Mdop}, material-aware batching)");
            // Auto-enable the source-cache cap when the decoded texture footprint would blow Phase-1 RAM.
            // Gate keys on DECODED bytes, not compressed TextureBytes (which under-reports >50x).
            long _availBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            int effectiveCap = config.SourceCacheCap;
            if (Phase1AutoCachePolicy.ShouldAutoEnable(
                    config.HierarchicalLods, config.SourceCacheCap,
                    modelMetrics.DecodedTextureBytes, _availBytes))
            {
                effectiveCap = config.MaxAtlasSize;
                config.SourceCacheCapAutoEnabled = true;
                Console.WriteLine($" -> AUTO source-cache-cap={effectiveCap}px (decoded tex {modelMetrics.DecodedTextureBytes / 1_048_576} MiB > 50% avail {_availBytes / 1_048_576} MiB) — activating RAM-aware Phase-1 degradation");
            }
            Obj2Tiles.Library.TexturesCache.MaxResidentEdge = effectiveCap;
            // Bound the resident decoded-texture set at 60% of available memory so peak RAM stays scale-safe.
            var _budgetEnv = System.Environment.GetEnvironmentVariable("HLOD_CACHE_BUDGET_MIB");
            Obj2Tiles.Library.TexturesCache.MaxResidentBytes =
                effectiveCap <= 0 ? 0
                : (!string.IsNullOrEmpty(_budgetEnv) && long.TryParse(_budgetEnv, out var _bmib) && _bmib > 0)
                    ? _bmib * 1024L * 1024L
                    : (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes * 0.60);
            if (effectiveCap > 0)
                Console.WriteLine($" -> SourceCacheCap: decode-once <= {effectiveCap}px; resident budget {Obj2Tiles.Library.TexturesCache.MaxResidentBytes / 1_048_576} MiB (over-budget -> bounded per-chunk re-decode)");
            Stage("3a. ModelMetrics.Compute");

            var shape = OctreeSplitter.ChooseShape(bounds, forceOctree: config.ForceZSplit);

            double bEff = ModelMetrics.EstimateEffectiveBranching(meshVerts, meshFaces, bounds, shape);
            Console.WriteLine($" -> B_eff (2-level dry run, shape={shape}): {bEff:F3}");
            Stage("3b. EstimateEffectiveBranching");
            int maxDepth;
            if (config.MaxDepthOverride > 0)
            {
                maxDepth = config.MaxDepthOverride;
                Console.WriteLine($" -> --max-depth override: maxDepth={maxDepth} (bypassing --auto-depth)");
            }
            else if (config.AutoDepth)
            {
                maxDepth = ModelMetrics.OptimalDepthsClosedForm(modelMetrics, bEff,
                    triLeafTarget: config.TLeafTri,
                    vertLeafTarget: (int)(config.TLeafTri * 0.6),
                    textureBytesLeafTarget: config.TLeafTextureBytes);
                Console.WriteLine($" -> --auto-depth ON: selected maxDepth={maxDepth} (B_eff={bEff:F2}, T_leaf_tri={config.TLeafTri}, T_leaf_tex={config.TLeafTextureBytes / 1_048_576.0:F0}MiB)");
            }
            else
            {
                maxDepth = 5;
                Console.WriteLine($" -> --auto-depth OFF: using hardcoded maxDepth={maxDepth}");
            }

            if (config.LeafVramBudgetMb > 0)
            {
                long budgetBytes = (long)config.LeafVramBudgetMb * 1024 * 1024;
                int pxLinearMax = (int)Math.Sqrt(budgetBytes / 4.0);
                int pow2Cap = 1;
                while (pow2Cap * 2 <= pxLinearMax) pow2Cap *= 2;
                pow2Cap = Math.Clamp(pow2Cap, 256, 8192);
                long actualVramBytes = (long)pow2Cap * pow2Cap * 4;
                Console.WriteLine($" -> --leaf-vram-budget-mb={config.LeafVramBudgetMb}: chose MaxAtlasSize={pow2Cap} (per-tile decoded RGBA = {actualVramBytes / 1024.0 / 1024.0:F1} MB)");
                config.MaxAtlasSize = pow2Cap;
            }

            if (!config.UserProvidedLods)
            {
                config.LODs = new[]
                {
                    new LodConfig { Quality = 1.0f, JpegQuality = 90, MaxAtlasSize = config.MaxAtlasSize },
                    new LodConfig { Quality = 0.7f, JpegQuality = 85, MaxAtlasSize = config.MaxAtlasSize },
                    new LodConfig { Quality = 0.5f, JpegQuality = 80, MaxAtlasSize = config.MaxAtlasSize },
                };
            }

            var root = ConformalHierarchyStage.BuildTreeConformal(
                meshVerts, meshUvs, meshFaces, bounds,
                shape, maxDepth, config.LODs);
            Stage("4. BuildTreeConformal (clip + simplify)");

            var texBytesPerMaterial = ConformalHierarchyStage.ComputeMaterialTextureBytes(
                mt.Materials, Path.GetDirectoryName(Path.GetFullPath(config.Input)));
            int collapsedCount = ConformalHierarchyStage.PruneAdaptive(root, maxDepth,
                tLeafTri: config.TLeafTri,
                tLeafTextureBytes: config.TLeafTextureBytes,
                texBytesPerMaterial: texBytesPerMaterial);
            Console.WriteLine($" -> adaptive prune: collapsed {collapsedCount} nodes (criteria: tri≤{config.TLeafTri}, texBytes≤{config.TLeafTextureBytes / 1_048_576.0:F0}MiB; ceiling=maxDepth={maxDepth})");
            Stage("4b. PruneAdaptive");

            if (!config.NoAdaptiveExtend)
            {
                int ceiling = config.AdaptiveExtendMaxDepth > 0
                    ? config.AdaptiveExtendMaxDepth
                    : maxDepth + 3;
                int addedCount = ConformalHierarchyStage.ExtendAdaptive(
                    root, autoDepth: maxDepth, hardCeiling: ceiling,
                    maxAtlasSize: config.MaxAtlasSize,
                    leafDensity: config.AtlasLeafDensityPxPerM,
                    shape: shape);
                Console.WriteLine($" -> adaptive extend: added {addedCount} children (criteria: ideal_side > {config.MaxAtlasSize} at current depth; hard ceiling {ceiling})");
            }
            else
            {
                Console.WriteLine($" -> adaptive extend: SKIPPED (--no-adaptive-extend)");
            }
            int totalNodes = ConformalHierarchyStage.CountAllNodes(root);
            const int DEEP_TREE_WARN_NODES = 2000;
            if (totalNodes >= DEEP_TREE_WARN_NODES)
            {
                Console.WriteLine($" -> WARN: tree has {totalNodes} nodes (≥ {DEEP_TREE_WARN_NODES}). Bake will be long and disk-heavy. Set --max-tile-count > 0 to hard-abort on this case.");
            }
            if (config.MaxTileCount > 0 && totalNodes > config.MaxTileCount)
            {
                throw new InvalidOperationException(
                    $"--max-tile-count {config.MaxTileCount} guard tripped: tree has {totalNodes} nodes. " +
                    $"Operator explicitly set this hard abort. Either raise/clear --max-tile-count, " +
                    $"raise --leaf-vram-budget-mb (bigger cap → fewer tiles), " +
                    $"set --adaptive-extend-max-depth to a smaller value, or use --no-adaptive-extend.");
            }
            Stage("4c. ExtendAdaptive");

            AssignMeasuredGeometricError(root, meshVerts, bounds);
            Stage("5. AssignMeasuredGeometricError");

            ApplyTextureAwareGeometricError(root, config, maxDepth, config.TextureErrorFactor);
            Stage("5a. ApplyTextureAwareGeometricError");

            HierarchicalPruneStage.PruneZeroErrorSubtrees(root);
            Stage("6. PruneZeroErrorSubtrees");

            var report = new BuildReport();
            HierarchicalTilingStage.WriteAllGlbs(
                root,
                config.Output,
                isQuadtree: shape == SubdivisionShape.Quadtree,
                materials: mt.Materials,
                config: config,
                report: report);
            Stage("7. WriteAllGlbs (Phase 1 atlas + Phase 2 glb)");

            HierarchicalTilingStage.WriteTilesetJson(root, config.Output, lat, lon, config.Altitude, shape);
            Stage("8. WriteTilesetJson");

            var nodes = new List<HierarchicalNode>();
            WalkAll(root, nodes);
            report.TotalNodes = nodes.Count;
            report.MaxDepth = nodes.Count > 0 ? nodes.Max(n => n.Depth) : 0;
            foreach (var depthGrp in nodes.GroupBy(n => n.Depth))
            {
                int d = depthGrp.Key;
                report.NodesPerDepth[d] = depthGrp.Count();
                var verts = depthGrp.Select(n => n.TileContentT?.Vertices.Length ?? 0).ToArray();
                report.VerticesP50[d] = verts.Length > 0 ? verts.OrderBy(v => v).ElementAt(verts.Length / 2) : 0;

                int zeroErrorCount = 0;
                foreach (var n in depthGrp)
                {
                    if (!n.IsLeaf && n.GeometricError == 0) zeroErrorCount++;
                }
                report.ZeroErrorInteriorPerDepth[d] = zeroErrorCount;
            }
            report.RootGeometricError = root.GeometricError;

            report.SourceMaxEdgeLength = sourceMaxEdge;
            double maxLeafEdge = 0.0;
            // Walk every node: pruning turns interior nodes into leaves, so both must be checked.
            foreach (var n in nodes)
            {
                var c = n.TileContentT;
                if (c == null) continue;
                double localMax = 0.0;
                foreach (var f in c.Faces)
                {
                    var pa = c.Vertices[f.IndexA];
                    var pb = c.Vertices[f.IndexB];
                    var pc = c.Vertices[f.IndexC];
                    double e1 = Distance(pa, pb);
                    double e2 = Distance(pb, pc);
                    double e3 = Distance(pa, pc);
                    double m = e1 > e2 ? (e1 > e3 ? e1 : e3) : (e2 > e3 ? e2 : e3);
                    if (m > localMax) localMax = m;
                }
                if (n.IsLeaf && localMax > maxLeafEdge) maxLeafEdge = localMax;
            }
            report.MaxLeafEdgeLength = maxLeafEdge;

            report.BoundaryEdgeCountRoot = CountBoundaryEdges(root.TileContentT);
            // Warn, not fail: simplification can legitimately produce edges longer than the source max.
            const double leafEdgeWarnTolerance = 1.5;
            if (sourceMaxEdge > 0 && maxLeafEdge > leafEdgeWarnTolerance * sourceMaxEdge)
            {
                Console.Error.WriteLine(
                    $" -> WARN: max leaf edge {maxLeafEdge:F2}m exceeds {leafEdgeWarnTolerance:F1}× " +
                    $"source max edge {sourceMaxEdge:F2}m (ratio {maxLeafEdge / sourceMaxEdge:F2}×). " +
                    $"Expected when simplification + crease-vertex protection leave longer edges " +
                    $"than the source max — not a splitter bug.");
            }

            report.WriteTo(config.Output);

            if (!config.KeepIntermediateFiles)
            {
                var tempRoot = Path.Combine(config.Output, ".temp");
                if (Directory.Exists(tempRoot))
                {
                    try { Directory.Delete(tempRoot, recursive: true); } catch { }
                }
            }

            Console.WriteLine($" => Pipeline completed in {sw.Elapsed}");
            Console.WriteLine($"[perf:hlod:PipelineTotal] elapsed={sw.ElapsedMilliseconds}ms");
            await Task.CompletedTask;
        }

        private static void WalkAll(HierarchicalNode n, List<HierarchicalNode> out_)
        {
            out_.Add(n);
            foreach (var c in n.Children) WalkAll(c, out_);
        }

        private static double Distance(Vertex3 a, Vertex3 b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static int CountBoundaryEdges(ClipResultT? mesh)
        {
            if (mesh == null || mesh.Faces.Length == 0) return 0;
            var edgeUses = new Dictionary<(long, long), int>();
            long Key(Vertex3 v) =>
                ((long)((v.X + 1e6) * 1e5) << 40) ^ ((long)((v.Y + 1e6) * 1e5) << 20) ^ (long)((v.Z + 1e6) * 1e5);
            foreach (var f in mesh.Faces)
            {
                long ka = Key(mesh.Vertices[f.IndexA]);
                long kb = Key(mesh.Vertices[f.IndexB]);
                long kc = Key(mesh.Vertices[f.IndexC]);
                var pairs = new[] { (ka, kb), (kb, kc), (ka, kc) };
                foreach (var (u, v) in pairs)
                {
                    var key = u < v ? (u, v) : (v, u);
                    edgeUses[key] = edgeUses.GetValueOrDefault(key) + 1;
                }
            }
            int boundary = 0;
            foreach (var c in edgeUses.Values) if (c == 1) boundary++;
            return boundary;
        }

        /// <summary>
        /// Per non-leaf node, measured one-direction Hausdorff against its simplified surface,
        /// monotonicity-corrected so each parent exceeds every child; leaves get 0. Original verts
        /// are filtered to the node's own bounds, else the max distance is dominated by far-side geometry.
        /// </summary>
        private static void AssignMeasuredGeometricError(
            HierarchicalNode root,
            IReadOnlyList<Vertex3> originalVerts,
            Box3 sceneBounds)
        {
            double diag = Math.Sqrt(
                sceneBounds.Width * sceneBounds.Width
                + sceneBounds.Height * sceneBounds.Height
                + sceneBounds.Depth * sceneBounds.Depth);

            var byDepth = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<HierarchicalNode>>();
            int maxDepth = 0;
            void Collect(HierarchicalNode n, int d)
            {
                if (d > maxDepth) maxDepth = d;
                if (!byDepth.TryGetValue(d, out var list)) { list = new System.Collections.Generic.List<HierarchicalNode>(); byDepth[d] = list; }
                list.Add(n);
                foreach (var c in n.Children) Collect(c, d + 1);
            }
            Collect(root, 0);
            var contentNodes = new List<HierarchicalNode>();
            for (int d = 0; d <= maxDepth; d++)
                foreach (var n in byDepth[d])
                    if (!n.IsLeaf && n.TileContentT != null && n.TileContentT.Faces.Length > 0)
                        contentNodes.Add(n);
            var measuredErr = new System.Collections.Concurrent.ConcurrentDictionary<HierarchicalNode, double>();
            System.Threading.Tasks.Parallel.ForEach(contentNodes, n => measuredErr[n] = MeasureNode(n));

            for (int d = maxDepth; d >= 0; d--)
                foreach (var n in byDepth[d])
                {
                    if (n.IsLeaf) { n.GeometricError = 0; continue; }
                    if (n.TileContentT == null || n.TileContentT.Faces.Length == 0)
                    {
                        double maxChild0 = 0;
                        foreach (var c in n.Children) if (c.GeometricError > maxChild0) maxChild0 = c.GeometricError;
                        n.GeometricError = maxChild0 + 1e-3 * diag;
                        continue;
                    }
                    var childErrors = new List<double>(n.Children.Count);
                    foreach (var c in n.Children) childErrors.Add(c.GeometricError);
                    n.GeometricError = HausdorffMetric.MonotonicCorrection(measuredErr[n], childErrors, diag);
                }

            double MeasureNode(HierarchicalNode n)
            {
                // Tolerance admits verts snapped exactly onto a cell-boundary clip plane during the split.
                double tol = 1e-6 * diag;
                var b = n.Bounds;
                double bMnX = b.Min.X - tol, bMnY = b.Min.Y - tol, bMnZ = b.Min.Z - tol;
                double bMxX = b.Max.X + tol, bMxY = b.Max.Y + tol, bMxZ = b.Max.Z + tol;
                var nodeVerts = new List<Vertex3>(originalVerts.Count / 4);
                for (int i = 0; i < originalVerts.Count; i++)
                {
                    var v = originalVerts[i];
                    if (v.X < bMnX || v.X > bMxX) continue;
                    if (v.Y < bMnY || v.Y > bMxY) continue;
                    if (v.Z < bMnZ || v.Z > bMxZ) continue;
                    nodeVerts.Add(v);
                }
                if (nodeVerts.Count == 0) return 0;
                return HausdorffMetric.ComputeSampled(
                    originalVerts: nodeVerts,
                    simplifiedVerts: n.TileContentT.Vertices,
                    simplifiedFaces: ToFaceArray(n.TileContentT.Faces),
                    maxSamples: 50_000);
            }
        }

        /// <summary>
        /// Bumps each non-leaf GE to <c>max(meshError, textureError)</c> where
        /// <c>textureError = (worldExtent / atlasSide) × factor</c> and worldExtent is the longest
        /// horizontal axis (max of SizeX, SizeY — not the diagonal, which Z would inflate), then
        /// enforces strict bottom-up monotonicity so children always refine before parents.
        /// </summary>
        private static double ParseEnvDouble(string name, double dflt)
        {
            var v = System.Environment.GetEnvironmentVariable(name);
            return !string.IsNullOrEmpty(v) && double.TryParse(v,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out var d) && d > 0 ? d : dflt;
        }

        private static void ApplyTextureAwareGeometricError(
            HierarchicalNode root, AppConfig config, int maxDepth, double textureErrorFactor)
        {
            int amplified = 0, totalInterior = 0;

            bool textureAwareGeEnabled = System.Environment.GetEnvironmentVariable("HLOD_TEXGE_V2") != "0";
            double maxSse = ParseEnvDouble("HLOD_TEXGE_MAXSSE", 16.0);
            double pMax   = ParseEnvDouble("HLOD_TEXGE_PMAX", 0.5);
            double maxAmp = ParseEnvDouble("HLOD_TEXGE_MAXAMP", 0.0);
            double dGain  = ParseEnvDouble("HLOD_TEXGE_DGAIN", 1.0);

            double MetersPerTexel(HierarchicalNode m)
            {
                double sx = m.Bounds.Max.X - m.Bounds.Min.X;
                double sy = m.Bounds.Max.Y - m.Bounds.Min.Y;
                double we = Math.Max(sx, sy);
                int side = ConformalHierarchyStage.PredictAtlasSide(m, config, maxDepth);
                return (we > 0 && side > 0) ? we / side : -1.0;
            }

            Walk(root);
            void Walk(HierarchicalNode n)
            {
                foreach (var c in n.Children) Walk(c);
                if (n.IsLeaf) return;
                if (n.Children.Count == 0) return;
                if (n.TileContentT == null || n.TileContentT.Faces.Length == 0) return;
                totalInterior++;

                double sizeX = n.Bounds.Max.X - n.Bounds.Min.X;
                double sizeY = n.Bounds.Max.Y - n.Bounds.Min.Y;
                double worldExtent = Math.Max(sizeX, sizeY);
                int tileSide = ConformalHierarchyStage.PredictAtlasSide(n, config, maxDepth);
                if (worldExtent <= 0 || tileSide <= 0) return;
                double meshError = n.GeometricError;
                // Skip the bump on a non-finite upstream GE, which would otherwise corrupt the tileset.
                if (!double.IsFinite(meshError)) return;

                if (!textureAwareGeEnabled)
                {
                    double textureError = (worldExtent / tileSide) * textureErrorFactor;
                    double effective = Math.Max(meshError, textureError);
                    if (effective > meshError) amplified++;
                    n.GeometricError = effective;
                    return;
                }

                double parentMpt = worldExtent / tileSide;
                double textureGe = TextureGeometricError.FromTexelDensity(parentMpt, maxSse, pMax, textureErrorFactor);
                bool textureBottleneck = textureGe > meshError * 1.05;
                // Only refine when some child actually has finer texel density; otherwise refining gains nothing.
                double minChildMpt = double.MaxValue;
                foreach (var c in n.Children)
                {
                    double cm = MetersPerTexel(c);
                    if (cm > 0 && cm < minChildMpt) minChildMpt = cm;
                }
                bool childrenImprove = (minChildMpt < double.MaxValue) && (minChildMpt < parentMpt);
                if (dGain > 1.0 && minChildMpt < double.MaxValue && minChildMpt > 0)
                    childrenImprove = (parentMpt / minChildMpt) >= dGain;
                double candidate = textureGe;
                if (maxAmp > 0) candidate = Math.Min(candidate, meshError * maxAmp);
                bool fire = textureBottleneck && childrenImprove && candidate > meshError;
                if (fire)
                {
                    n.GeometricError = candidate;
                    amplified++;
                    Console.WriteLine($"   [texge-v3 FIRE] depth={n.Depth} meshGE={meshError:F3} mpt={parentMpt:F4} textureGE={textureGe:F3} candidate={candidate:F3} childMpt={(minChildMpt<double.MaxValue?minChildMpt:-1):F4}");
                }
            }

            // Strict bottom-up monotonicity: parent ≥ max(children) × 1.01 so GE decreases with depth.
            void Monotonic(HierarchicalNode n)
            {
                foreach (var c in n.Children) Monotonic(c);
                if (n.Children.Count == 0) return;
                double maxChild = 0;
                foreach (var c in n.Children) if (c.GeometricError > maxChild) maxChild = c.GeometricError;
                if (maxChild > 0)
                {
                    double needed = maxChild * 1.01;
                    if (n.GeometricError < needed) n.GeometricError = needed;
                }
            }
            Monotonic(root);

            Console.WriteLine($" -> texture-aware geom-error: amplified {amplified}/{totalInterior} interior nodes (factor={textureErrorFactor}, formula: max(meshError, worldExtent/atlasSide × factor) + monotonic)");
        }

        private static Face[] ToFaceArray(MeshFace[] mfs)
        {
            var arr = new Face[mfs.Length];
            for (int i = 0; i < mfs.Length; i++)
                arr[i] = new Face(mfs[i].IndexA, mfs[i].IndexB, mfs[i].IndexC);
            return arr;
        }

        private static async Task RunFlatGridPipeline(AppConfig config)
        {
            if (Directory.Exists(config.Output))
            {
                Directory.Delete(config.Output, true);
            }

            Directory.CreateDirectory(config.Output);

            var pipelineId = Guid.NewGuid().ToString();
            var sw = new Stopwatch();
            var swg = Stopwatch.StartNew();
            var tempFolder = Path.Combine(config.Output, ".temp");

            string? destFolderDecimation = null;
            string? destFolderSplit = null;

            try
            {
                destFolderDecimation = CreateTempFolder($"{pipelineId}-obj2tiles-decimation", tempFolder);
                Console.WriteLine($" => Decimation stage with {config.LODs.Length} LODs");
                sw.Restart();
                var decimateRes = StagesFacade.Decimate(config.Input, destFolderDecimation, config.LODs);
                Console.WriteLine(" ?> Decimation stage done in {0}", sw.Elapsed);
                Console.WriteLine($"[perf:flat:Decimate] elapsed={sw.ElapsedMilliseconds}ms lods={config.LODs.Length}");
                Console.WriteLine();

                Console.WriteLine($" => Splitting stage with {config.MaxVerticesPerTile} vertices per tile");
                destFolderSplit = CreateTempFolder($"{pipelineId}-obj2tiles-split", tempFolder);
                sw.Restart();
                var meshes = StagesFacade.Split(decimateRes.DestFiles, destFolderSplit,
                    config.MaxVerticesPerTile, decimateRes.Bounds, config.PackingThreshold, config.LODs, config.ThreadsCount, config.MaxTotalAtlasArea);

                Console.WriteLine(" ?> Splitting stage done in {0}", sw.Elapsed);
                int meshesFirstLod = meshes.Count > 0 ? meshes.Values.First().Count : 0;
                Console.WriteLine($"[perf:flat:Split] elapsed={sw.ElapsedMilliseconds}ms meshes_per_lod={meshesFirstLod} lods={meshes.Count}");
                Console.WriteLine();

                if (config.UseKtxTextures)
                {
                    sw.Restart();
                    Console.WriteLine(" ?> Compressing png to ktx2");
                    await StagesFacade.Compress(meshes, config.ThreadsCount);
                    Console.WriteLine(" ?> Compressing done in {0}", sw.Elapsed);
                    Console.WriteLine($"[perf:flat:CompressKtx2] elapsed={sw.ElapsedMilliseconds}ms threads={config.ThreadsCount}");
                    Console.WriteLine();
                }

                sw.Restart();
                Console.WriteLine(" ?> Converting to glb");
                await StagesFacade.Convert(destFolderSplit, config.Output, config.LODs, config.ThreadsCount);
                Console.WriteLine(" ?> Converting done in {0}", sw.Elapsed);
                Console.WriteLine($"[perf:flat:ObjToGlb] elapsed={sw.ElapsedMilliseconds}ms threads={config.ThreadsCount}");
                Console.WriteLine();

                sw.Restart();
                GenerateTileset(sw, meshes, config);
                Console.WriteLine($"[perf:flat:GenerateTileset] elapsed={sw.ElapsedMilliseconds}ms");

            }
            catch (Exception ex)
            {
                Console.WriteLine(" !> Exception:");
                Console.WriteLine(ex.ToString());
            }
            finally
            {
                Console.WriteLine();
                Console.WriteLine(" => Pipeline completed in {0}", swg.Elapsed);
                Console.WriteLine($"[perf:flat:PipelineTotal] elapsed={swg.ElapsedMilliseconds}ms");

                var tmpFolder = Path.Combine(config.Output, ".temp");

                if (config.KeepIntermediateFiles)
                {
                    Console.WriteLine(
                        $" ?> Skipping cleanup, intermediate files are in '{tmpFolder}' with pipeline id '{pipelineId}'");

                    Console.WriteLine(" ?> You should delete this folder manually, it is only for debugging purposes");
                }
                else
                {
                    Console.WriteLine(" => Cleaning up");

                    if (destFolderDecimation != null && destFolderDecimation != config.Output)
                        Directory.Delete(destFolderDecimation, true);

                    if (destFolderSplit != null && destFolderSplit != config.Output)
                        Directory.Delete(destFolderSplit, true);

                    if (Directory.Exists(tmpFolder))
                        Directory.Delete(tmpFolder, true);

                    Console.WriteLine(" ?> Cleaning up ok");
                }
            }
        }

        /// <summary>Parse a <c>depth:cap</c> schedule like <c>"0:512,1:1024,2:2048"</c>; malformed entries are skipped.</summary>
        private static Dictionary<int, int> ParseDepthSchedule(string s)
        {
            var result = new Dictionary<int, int>();
            if (string.IsNullOrWhiteSpace(s)) return result;
            foreach (var pair in s.Split(','))
            {
                var t = pair.Trim();
                if (t.Length == 0) continue;
                var parts = t.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var d) || !int.TryParse(parts[1], out var cap))
                {
                    Console.WriteLine($" !! --atlas-max-depth-schedule: skipping malformed entry '{t}'");
                    continue;
                }
                result[d] = cap;
            }
            return result;
        }

        private static bool IsBasisuEncoder(string encoder) =>
            string.Equals(encoder?.Trim(), "basisu", StringComparison.OrdinalIgnoreCase);

        private static bool TryGetConfig(Options options, out AppConfig config)
        {
            config = null;

            if (!string.IsNullOrEmpty(options.Config))
            {
                using (var reader = File.OpenText(options.Config))
                using (var jsonReader = new JsonTextReader(reader))
                {
                    config = JsonSerializer.CreateDefault().Deserialize<AppConfig>(jsonReader);
                }

                return true;
            }

            if (string.IsNullOrEmpty(options.Input))
            {
                Console.WriteLine("Input parameter missing!");
                return false;
            }
            
            if (string.IsNullOrEmpty(options.Output))
            {
                Console.WriteLine("Output parameter missing!");
                return false;
            }

            int effectiveMaxVerts = options.MaxVerticesPerTile > 0
                ? options.MaxVerticesPerTile
                : (options.HierarchicalLods ? 1500 : 4000);

            // Flat-grid requires --lods; the hierarchical path injects a stub LOD (overridden later) to avoid an NRE.
            LodConfig[] effectiveLods;
            if (!string.IsNullOrEmpty(options.LODs))
            {
                effectiveLods = JsonConvert.DeserializeObject<LodConfig[]>(options.LODs);
            }
            else if (!options.HierarchicalLods)
            {
                Console.WriteLine("Flat-grid pipeline requires --lods. Pass --hierarchical-lods to use the HLOD pipeline (auto-derives Q schedule).");
                return false;
            }
            else
            {
                effectiveLods = new[]
                {
                    new LodConfig
                    {
                        Quality = 1.0f,
                        JpegQuality = 90,
                        MaxAtlasSize = options.MaxAtlasSize,
                        SaveUv = true,
                    }
                };
            }

            config = new AppConfig
            {
                Input = options.Input,
                Output = options.Output,
                MaxVerticesPerTile = effectiveMaxVerts,
                MaxAtlasSize = options.MaxAtlasSize,
                SourceCacheCap = options.SourceCacheCap,
                MaxAtlasSizeInternal = options.MaxAtlasSizeInternal,
                AtlasMaxDepthSchedule = ParseDepthSchedule(options.AtlasMaxDepthSchedule),
                PackingThreshold = options.PackingThreshold,
                ThreadsCount = options.ThreadsCount,
                Phase1BatchesPerMaterial = options.Phase1BatchesPerMaterial,
                MaxTotalAtlasArea = options.MaxTotalAtlasArea,
                KeepIntermediateFiles = options.KeepIntermediateFiles,
                UseKtxTextures = options.UseKtxTextures,
                BaseError = options.BaseError,
                HierarchicalLods = options.HierarchicalLods,
                ForceZSplit = options.ForceZSplit,
                NoMeshoptCompression = options.NoMeshoptCompression,
                AutoDepth = options.AutoDepth,
                TLeafTri = options.TLeafTri,
                TLeafTextureBytes = options.TLeafTextureBytes,
                NoAdaptiveExtend = options.HierarchicalLods ? !options.AdaptiveExtend : options.NoAdaptiveExtend,
                AdaptiveExtendMaxDepth = options.AdaptiveExtendMaxDepth,
                LeafVramBudgetMb = options.LeafVramBudgetMb,
                MaxTileCount = options.MaxTileCount,
                AtlasUnsharpAmount = options.AtlasUnsharpAmount,
                LeafNoMips = options.HierarchicalLods ? !options.LeafMips : options.LeafNoMips,
                MaxDepthOverride = options.MaxDepth,
                // quantize-glbs/meshopt both need gltfpack, absent in the basisu prod image, so basisu mode falls back to the raw flags.
                QuantizeGlbs = (options.HierarchicalLods && !IsBasisuEncoder(options.Ktx2Encoder))
                    ? !options.NoQuantizeGlbs
                    : options.QuantizeGlbs,
                GltfpackPath = options.GltfpackPath ?? "",
                MeshoptCompress = (options.HierarchicalLods && !IsBasisuEncoder(options.Ktx2Encoder))
                    ? !options.NoMeshoptCompress
                    : options.MeshoptCompress,
                Ktx2Hierarchical = options.Ktx2Hierarchical && !options.NoKtx2,
                Ktx2Quality = options.Ktx2Quality,
                Ktx2Encoder = IsBasisuEncoder(options.Ktx2Encoder) ? "basisu" : "gltfpack",
                AtlasStrategy = Enum.TryParse<AtlasStrategy>(options.AtlasStrategy, true, out var _atlasStrategy) ? _atlasStrategy : AtlasStrategy.Natural,
                AtlasLeafDensityPxPerM = options.AtlasLeafDensityPxPerM,
                AtlasUseSourceDetailFloor = options.AtlasUseSourceDetailFloor,
                AtlasMinSize = options.AtlasMinSize,
                AtlasSourceDetailCapPxPerM = options.AtlasSourceDetailCapPxPerM,
                TextureErrorFactor = options.TextureErrorFactor,
                Latitude = options.Latitude,
                Longitude = options.Longitude,
                Altitude = options.Altitude,
                Scale = options.Scale,
                YUpToZUp = options.YUpToZUp,
                UserProvidedLods = !string.IsNullOrEmpty(options.LODs),
                LODs = effectiveLods,
            };

            return true;
        }

        private static string CreateTempFolder(string folderName, string baseFolder)
        {
            var tempFolder = Path.Combine(baseFolder, folderName);
            Directory.CreateDirectory(tempFolder);
            return tempFolder;
        }

        private static void GenerateTileset(Stopwatch sw, Dictionary<LodConfig, List<IMesh>> meshes, AppConfig config)
        {
            sw.Restart();
            Console.WriteLine(" ?> Generating tileset.json");

            var boundsMapper = TilesetGenerationHelper.PrepareBoundsMapper(meshes, config.LODs);
            var coords = TilesetGenerationHelper.CreateGpsCoords(
                config.Latitude, config.Longitude, config.Altitude, config.Scale, config.YUpToZUp);

            StagesFacade.Tile(config.Output, config.LODs.Length, config.BaseError, boundsMapper, coords);

            Console.WriteLine(" ?> Tileset generation done in {0}", sw.Elapsed);
            Console.WriteLine();
        }
    }
}