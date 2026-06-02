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

        /// <summary>
        /// Hierarchical (Phase 1) pipeline. End-to-end:
        ///   1. Load OBJ as MeshT (UV+material aware).
        ///   2. Sanitize (zero-area faces; UV-in-unit-range).
        ///   3. Build textured tree (UVs + materials threaded through every clip).
        ///   4. Assign measured Hausdorff geometric error per node (monotonic-corrected).
        ///   5. Per-tile atlas pack + GLB write (capped at per-depth tier from spec §5.1).
        ///   6. tileset.json referencing the now-existing GLBs.
        ///   7. report.json with per-depth diagnostics.
        /// </summary>
        private static async Task RunHierarchicalPipeline(AppConfig config)
        {
            Console.WriteLine(" *** Hierarchical pipeline (Phase 1) ***");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Per-stage timing. Each stage emits TWO lines: the original
            // human-readable `[perf] <stage>: 12.34s (T+45.67s)` line for
            // continuity, plus a grep-friendly `[perf:hlod:<tag>] elapsed=Xms`
            // line consumed by the timing aggregator (TRACK-1-PERF-TIMING).
            long perfLastMs = 0;
            void Stage(string name)
            {
                long now = sw.ElapsedMilliseconds;
                long elapsedMs = now - perfLastMs;
                Console.WriteLine($" [perf] {name}: {elapsedMs / 1000.0:F2}s (T+{now / 1000.0:F2}s)");
                // Strip leading numeric prefix (e.g. "1. ", "4c. ") for the tag.
                string tag = System.Text.RegularExpressions.Regex.Replace(name, @"^\d+[a-z]?\.\s*", "");
                tag = System.Text.RegularExpressions.Regex.Replace(tag, @"\s*\(.*", ""); // drop parenthetical
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

            // 1. Load.
            var mesh = MeshUtils.LoadMesh(config.Input, false, true, config.PackingThreshold,
                config.LODs[0].Quality, config.LODs[0].JpegQuality, config.LODs[0].MaxAtlasSize);
            var bounds = mesh.Bounds;
            Stage("1. LoadMesh");

            // 2. Sanitize. The hierarchical pipeline requires a MeshT (textured
            //    geometry) — there is no per-tile atlas to pack without UVs
            //    and materials, so a position-only Mesh input is rejected here.
            if (mesh is not MeshT mt)
                throw new InvalidOperationException(
                    "Hierarchical pipeline requires a textured input mesh (MeshT). " +
                    "The input OBJ has no texture coordinates — drop --hierarchical-lods " +
                    "to use the default flat-grid pipeline (which handles position-only meshes).");

            MeshSanitizer.RequireUvsInUnitRange(mt.TextureVertices);

            // Keep faces+positions+uvs; convert FaceT → MeshFace and drop zero-area.
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
            // Longest edge in the sanitized source mesh. Triangle clipping at
            // AABB planes can only shorten edges, so every downstream leaf tile
            // must have max-edge ≤ this value (modulo float drift). Used by the
            // post-build gate below.
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

            // Input metrics fed to the dynamic depth selector below.
            var modelMetrics = ModelMetrics.Compute(
                triangleCount: meshFaces.Count,
                vertexCount: meshVerts.Count,
                bounds: bounds,
                materials: mt.Materials,
                objDirectory: Path.GetDirectoryName(Path.GetFullPath(config.Input)));
            Console.WriteLine($" -> ModelMetrics: {modelMetrics}");
            // Phase-1 parallelism: always enable when more than one core is
            // available. The HLOD Phase-1 path batches tiles by their
            // material-touch fingerprint and processes batches serially with
            // an explicit TexturesCache.Clear() between them, so peak RAM is
            // bounded by the largest single batch's resident-material total
            // rather than the whole model's texture footprint. The earlier
            // unconditional 500 MiB → serial fallback (Candidate D, TRACK-1
            // Phase 5, 2026-05-28) under-served hd / vlrg by forcing them to
            // a single core for 92-93% of bake wall.
            int phase1Mdop = config.Phase1BatchesPerMaterial > 0
                ? config.Phase1BatchesPerMaterial
                : Math.Max(1, Environment.ProcessorCount / 2);
            config.ParallelPhase1 = phase1Mdop > 1;
            Console.WriteLine($" -> ParallelPhase1: {(config.ParallelPhase1 ? "ON" : "OFF")} (tex={modelMetrics.TextureBytes / 1_048_576.0:F1} MiB, mdop={phase1Mdop}, material-aware batching)");
            Obj2Tiles.Library.TexturesCache.MaxResidentEdge = config.SourceCacheCap;
            // G2-SAFE: bound the resident decoded-texture set at 60% of the GC's available-
            // memory view, so peak RAM is scale-safe (degrades to per-chunk re-decode on models
            // with far more distinct textures than fit). Fixtures fit the budget -> never clears
            // -> identical decode-once speed; huge models stay bounded, never OOM.
            var _budgetEnv = System.Environment.GetEnvironmentVariable("HLOD_CACHE_BUDGET_MIB");
            Obj2Tiles.Library.TexturesCache.MaxResidentBytes =
                config.SourceCacheCap <= 0 ? 0
                : (!string.IsNullOrEmpty(_budgetEnv) && long.TryParse(_budgetEnv, out var _bmib) && _bmib > 0)
                    ? _bmib * 1024L * 1024L
                    : (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes * 0.60);
            if (config.SourceCacheCap > 0)
                Console.WriteLine($" -> SourceCacheCap: decode-once <= {config.SourceCacheCap}px; resident budget {Obj2Tiles.Library.TexturesCache.MaxResidentBytes / 1_048_576} MiB (over-budget -> bounded per-chunk re-decode)");
            Stage("3a. ModelMetrics.Compute");

            // 3. Choose shape + build tree (textured).
            var shape = OctreeSplitter.ChooseShape(bounds, forceOctree: config.ForceZSplit);

            // 2-level centroid dry run → effective branching B_eff.
            double bEff = ModelMetrics.EstimateEffectiveBranching(meshVerts, meshFaces, bounds, shape);
            Console.WriteLine($" -> B_eff (2-level dry run, shape={shape}): {bEff:F3}");
            Stage("3b. EstimateEffectiveBranching");
            // Pick maxDepth: explicit --max-depth N override wins; otherwise
            // OptimalDepthsClosedForm when --auto-depth is set (default);
            // otherwise the legacy hardcoded 5.
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

            // --leaf-vram-budget-mb sets MaxAtlasSize from a per-tile decoded-
            // RGBA budget: cap = round_pow2(sqrt(MB * 1024² / 4)). Density
            // px/m is preserved by ExtendAdaptive's idealSide predicate.
            if (config.LeafVramBudgetMb > 0)
            {
                long budgetBytes = (long)config.LeafVramBudgetMb * 1024 * 1024;
                int pxLinearMax = (int)Math.Sqrt(budgetBytes / 4.0);
                // round DOWN to nearest power of 2 so per-tile decoded RGBA ≤ budget
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

            // Adaptive-prune pass. After uniform-depth partition, collapse
            // interior nodes whose subtree has so-little content (≤ T_leaf_tri
            // AND ≤ T_leaf_tex) that further subdivision wastes streaming
            // budget. Result: tree depth is non-uniform — dense regions keep
            // going to hardCeiling (maxDepth), sparse regions collapse earlier.
            var texBytesPerMaterial = ConformalHierarchyStage.ComputeMaterialTextureBytes(
                mt.Materials, Path.GetDirectoryName(Path.GetFullPath(config.Input)));
            int collapsedCount = ConformalHierarchyStage.PruneAdaptive(root, maxDepth,
                tLeafTri: config.TLeafTri,
                tLeafTextureBytes: config.TLeafTextureBytes,
                texBytesPerMaterial: texBytesPerMaterial);
            Console.WriteLine($" -> adaptive prune: collapsed {collapsedCount} nodes (criteria: tri≤{config.TLeafTri}, texBytes≤{config.TLeafTextureBytes / 1_048_576.0:F0}MiB; ceiling=maxDepth={maxDepth})");
            Stage("4b. PruneAdaptive");

            // Mirror of PruneAdaptive, operating in the OTHER direction. Walk
            // the (now-pruned) leaf set and subdivide any leaf whose ideal_side
            // exceeds MaxAtlasSize — these are the "stretch-marked" tiles
            // where the texel budget couldn't keep up with the surface area
            // at the chosen LOD density.
            if (!config.NoAdaptiveExtend)
            {
                // Ceiling default = autoDepth + 3. Set explicitly via
                // --adaptive-extend-max-depth to constrain finer-tiling growth
                // so each new leaf's atlas stays within the per-depth schedule
                // cap.
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
            // Post-ExtendAdaptive tile-count behavior. Always WARN on deep
            // trees so the operator sees it; ABORT only if the operator
            // explicitly set --max-tile-count > 0. Refusing to bake by default
            // is a bug — always produce a tileset.
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

            // 4. Geometric error: measured Hausdorff per non-leaf node, with
            //    monotonicity correction. Leaves get 0.
            AssignMeasuredGeometricError(root, meshVerts, bounds);
            Stage("5. AssignMeasuredGeometricError");

            // 4a. Texture-aware geometric error with strict monotonicity.
            //     Formula:
            //       effectiveGE = max(meshError, (worldExtent / atlasSide) × factor)
            //     followed by strict monotonicity (parent ≥ max child × 1.01).
            //     `factor` is the calibration knob (--texture-error-factor).
            //     A tile-intrinsic quantity (meters per texel) scaled by a
            //     single factor — renderer-agnostic, so the same calibration
            //     holds across CesiumJS (maximumScreenSpaceError) and
            //     3DTilesRendererJS (errorTarget).
            ApplyTextureAwareGeometricError(root, config, maxDepth, config.TextureErrorFactor);
            Stage("5a. ApplyTextureAwareGeometricError");

            // 4b. Phase C: prune zero-error subtrees. If a node ended up
            //     with geometricError = 0 and it has children, those
            //     children's geometry is identical to the parent's — no
            //     point loading them. Collapse them so the tile becomes a
            //     leaf in the emitted tree.
            HierarchicalPruneStage.PruneZeroErrorSubtrees(root);
            Stage("6. PruneZeroErrorSubtrees");

            // 5. Per-tile atlas pack + GLB write. The atlas cap fires per node
            //    based on distance-from-leaves (spec §5.1: 8192/4096/2048/1024).
            var report = new BuildReport();
            HierarchicalTilingStage.WriteAllGlbs(
                root,
                config.Output,
                isQuadtree: shape == SubdivisionShape.Quadtree,
                materials: mt.Materials,
                config: config,
                report: report);
            Stage("7. WriteAllGlbs (Phase 1 atlas + Phase 2 glb)");

            // 6. Tileset.json — references GLBs that now exist on disk.
            HierarchicalTilingStage.WriteTilesetJson(root, config.Output, lat, lon, config.Altitude, shape);
            Stage("8. WriteTilesetJson");

            // 7. Build report.
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

                // AchievedSimplifyRatio is no longer populated here (the
                // conformal builder does not produce per-cell SimplifyMetrics).
                // ZeroErrorInteriorPerDepth is computed directly from
                // GeometricError below.
                int zeroErrorCount = 0;
                foreach (var n in depthGrp)
                {
                    if (!n.IsLeaf && n.GeometricError == 0) zeroErrorCount++;
                }
                report.ZeroErrorInteriorPerDepth[d] = zeroErrorCount;
            }
            report.RootGeometricError = root.GeometricError;

            // Leaf-tile geometry quality gate. The splitter clips triangles at
            // cell planes — clipping alone can only shorten edges, so no leaf
            // tile should contain triangles with edges longer than the source
            // mesh's longest edge. Simplification (below) can violate this
            // legitimately; a hard fail here would point to a splitter bug
            // (welding non-coincident verts, missed clip plane, etc.).
            report.SourceMaxEdgeLength = sourceMaxEdge;
            double maxLeafEdge = 0.0;
            // Walk EVERY node — pruning collapses interior nodes into leaves
            // by keeping their (parent-welded) TileContentT, so the gate
            // must check both original leaves and pruned-leaves.
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

            // Count boundary edges in the root tile's mesh: edges used by
            // exactly 1 triangle (open boundaries). For a closed photogrammetry
            // scan, this should match the source mesh's natural perimeter
            // count; the conformal hierarchy must not introduce extras at
            // clip planes.
            report.BoundaryEdgeCountRoot = CountBoundaryEdges(root.TileContentT);
            // WARN (not fail) when leaf max-edge exceeds the source max edge
            // by more than the tolerance. The "clipping only shortens edges"
            // invariant is true for pure clipping but FALSE for simplification:
            // meshopt collapsing multiple short edges into one longer edge is
            // normal QEM behavior, and crease-vertex protection further shifts
            // merge targets. Surfaced as a signal to inspect, not a hard gate.
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

            // Cleanup: drop the per-tile temp folder if not kept.
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
        /// Per-spec §6.1: for each non-leaf node, measured one-direction
        /// Hausdorff = max distance from the original (leaf-descendant)
        /// geometry's vertices to the node's simplified surface;
        /// monotonicity-corrected so each parent is strictly greater than
        /// every child by at least <c>1e-6 × scene_diagonal</c>. Leaves get 0.
        ///
        /// "Leaf-descendant" matters: a node only covers its own quadtree
        /// cell, so we restrict the sample set to original vertices whose
        /// position falls inside this node's tight bounds (with a tolerance
        /// for the cell-boundary clipping created during the split). Without
        /// this filter the max distance is dominated by vertices on the
        /// other side of the scene — meaningless and orders of magnitude
        /// too large.
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

            // C6 (TRACK-1 P8): per-depth bottom-up parallel. Siblings/cousins at a
            // depth are independent; the parent->children dependency is satisfied by a
            // barrier per depth (process deepest first). Byte-identical to the prior
            // recursive Walk — ComputeSampled (deterministic stride sampling) and
            // MonotonicCorrection are deterministic, and each node writes only its own
            // GeometricError after all deeper depths are done.
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
            // G10-GEOMERR: the MEASURED Hausdorff per node is INDEPENDENT of its children
            // (it compares the node's own simplified mesh to the original verts inside its
            // bounds); only the final MonotonicCorrection needs child errors. The old
            // per-depth parallel gave ~no parallelism at shallow depths (root = 1 node) even
            // though those nodes hold the MOST verts (whole model) and dominate the cost.
            // Split it: measure ALL content nodes in ONE flat parallel pass (full core use,
            // no depth barriers — the root now overlaps every other node), then correct
            // bottom-up (cheap: max + add). Output-IDENTICAL (ComputeSampled is deterministic
            // stride sampling + MonotonicCorrection is deterministic; each node's measured
            // value and final error are unchanged).
            var contentNodes = new List<HierarchicalNode>();
            for (int d = 0; d <= maxDepth; d++)
                foreach (var n in byDepth[d])
                    if (!n.IsLeaf && n.TileContentT != null && n.TileContentT.Faces.Length > 0)
                        contentNodes.Add(n);
            var measuredErr = new System.Collections.Concurrent.ConcurrentDictionary<HierarchicalNode, double>();
            // G10: measure ALL content nodes in ONE flat parallel pass (full core use, no depth barriers —
            // the giant root node overlaps every other node). Output-identical to the old per-depth-barrier
            // scheduling: ComputeSampled is deterministic stride-sampling, each node's measured value unchanged.
            System.Threading.Tasks.Parallel.ForEach(contentNodes, n => measuredErr[n] = MeasureNode(n));

            // Bottom-up correction (cheap; a parent needs its children's final errors).
            for (int d = maxDepth; d >= 0; d--)
                foreach (var n in byDepth[d])
                {
                    if (n.IsLeaf) { n.GeometricError = 0; continue; }
                    if (n.TileContentT == null || n.TileContentT.Faces.Length == 0)
                    {
                        double maxChild0 = 0;
                        foreach (var c in n.Children) if (c.GeometricError > maxChild0) maxChild0 = c.GeometricError;
                        // ε = 1e-3 × diag (matches HausdorffMetric.MonotonicCorrection).
                        n.GeometricError = maxChild0 + 1e-3 * diag;
                        continue;
                    }
                    var childErrors = new List<double>(n.Children.Count);
                    foreach (var c in n.Children) childErrors.Add(c.GeometricError);
                    n.GeometricError = HausdorffMetric.MonotonicCorrection(measuredErr[n], childErrors, diag);
                }

            // Measure (independent per node): AABB-filter the original verts to this node's
            // bounds, then sampled one-direction Hausdorff against the node's simplified mesh.
            double MeasureNode(HierarchicalNode n)
            {
                // Tolerance handles verts that landed exactly on a cell-boundary clip plane
                // during OctreeSplitter (bit-equal to a clip-plane snap).
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
        /// Apply texture-aware geometric error. Pass 1 walks non-leaf nodes
        /// and bumps each node's GE to <c>max(meshError, textureError)</c>
        /// where
        ///   <c>textureError = (worldExtent / atlasSide) × textureErrorFactor</c>
        /// and <c>worldExtent</c> is the longest horizontal axis of the
        /// tile's bounding box (max of SizeX, SizeY — not the diagonal, which
        /// would include Z and overestimate horizontal extent for tall tiles).
        /// <c>atlasSide</c> is predicted via the same logic the pack pipeline
        /// uses (<c>PredictAtlasSide</c>).
        ///
        /// Pass 2 enforces STRICT bottom-up monotonicity:
        /// <c>parent.GE = max(parent.GE, max child.GE × 1.01)</c> so
        /// descendants always have lower GE → the renderer refines
        /// parent→child→grandchild in order without leapfrogging.
        ///
        /// The textureError term is a tile-intrinsic quantity (meters per
        /// texel) scaled by a single factor, so the same calibration holds
        /// across renderers with different refinement thresholds.
        /// </summary>
        // TEXGE-V2 calibration knobs read from env (so they can be swept without a rebuild).
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

            // TEXGE-V2 (Qg41, opt-in HLOD_TEXGE_V2=1): principled texture-resolution-deficit GE.
            //   textureGE = metersPerTexel × (maxSSE / pMax)   [Codex Qg40 derivation]
            // metersPerTexel = worldExtent / atlasSidePx; pMax = max acceptable projected texel size in
            // screen px. pMax=1.0 → factor=maxSSE = the OLD inert behaviour (amplified 2/13); pMax=0.5 →
            // factor=2×maxSSE ≈ Nyquist. V2 TARGETS: fire only where (a) texture is the bottleneck
            // (textureGE>meshGE×1.05) AND (b) children actually raise texel density (parentMPT/minChildMPT
            // ≥ dGain), and CAP at meshGE×maxAmp so the monotonicity pass can't broadly refine.
            // TEXGE-V3 (Qg72): DEFAULT-ON — operator visual-gate PASSED on all 3 fixtures at default params
            // (self-calibrating, fixes the real production LOD-selection blur, self-limits on sharp tiles,
            // bbox+GE-invariant audits clean). Opt OUT via HLOD_TEXGE_V2=0 (safety/legacy).
            bool textureAwareGeEnabled = System.Environment.GetEnvironmentVariable("HLOD_TEXGE_V2") != "0";
            double maxSse = ParseEnvDouble("HLOD_TEXGE_MAXSSE", 16.0);
            double pMax   = ParseEnvDouble("HLOD_TEXGE_PMAX", 0.5);
            // V3 self-calibrating defaults: maxAmp 0 = NO cap (uncapped textureGE; the cap was the per-model
            // knob — Qg57); dGain 1.0 = physics "any child-density improvement" (no magic constant).
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

            // Pass 1: per non-leaf, bump GE to include textureError.
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
                // Qg65: arbitrary-model robustness — a non-finite upstream Hausdorff GE (NaN/Inf from a
                // malformed/degenerate tile) would propagate through max(meshError, textureGE) into the
                // written geometricError → broken tileset. Skip the texture bump on such nodes (leave GE
                // as-is; the monotonic pass + renderer handle it) rather than amplify garbage.
                if (!double.IsFinite(meshError)) return;

                if (!textureAwareGeEnabled)
                {
                    // Legacy path (default): fixed per-tile m/texel × factor, max() with mesh.
                    double textureError = (worldExtent / tileSide) * textureErrorFactor;
                    double effective = Math.Max(meshError, textureError);
                    if (effective > meshError) amplified++;
                    n.GeometricError = effective;
                    return;
                }

                // V3 (Qg58): SELF-CALIBRATING — NO maxAmp cap. textureGE = metersPerTexel × (maxSSE/pMax) is
                // already the physically-correct GE (meters; Codex Qg57 dimensional proof) at which the tile's
                // texel density projects to pMax screen-px/texel at the refine distance. effective =
                // max(meshGE, textureGE) takes the binding constraint — no model-dependent knob. The old
                // min(.,meshGE×maxAmp) cap clipped legitimately-large texture deficits (needed per-model MAXAMP).
                double parentMpt = worldExtent / tileSide;
                double textureGe = TextureGeometricError.FromTexelDensity(parentMpt, maxSse, pMax, textureErrorFactor);
                bool textureBottleneck = textureGe > meshError * 1.05;
                // Physics gate (replaces the magic dGain=1.25): refine only if SOME child actually has finer
                // texel density (else refining gains no texture detail). "any improvement" — no model constant.
                double minChildMpt = double.MaxValue;
                foreach (var c in n.Children)
                {
                    double cm = MetersPerTexel(c);
                    if (cm > 0 && cm < minChildMpt) minChildMpt = cm;
                }
                bool childrenImprove = (minChildMpt < double.MaxValue) && (minChildMpt < parentMpt);
                // HLOD_TEXGE_DGAIN (default 1.0 = the physics "any improvement" check above). >1.0 only for A/B;
                // never needs raising per-model in the self-calibrating design.
                if (dGain > 1.0 && minChildMpt < double.MaxValue && minChildMpt > 0)
                    childrenImprove = (parentMpt / minChildMpt) >= dGain;
                // SELF-CALIBRATING candidate: the physical textureGE, NO cap. (maxAmp retained ONLY as an
                // opt-in safety ceiling for pathological inputs; default 0 = OFF = uncapped.)
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

            // Pass 2: strict bottom-up monotonicity. parent ≥ max(children)
            // × 1.01 ensures GE strictly decreases depth-wise so the
            // renderer's SSE calculation refines in order.
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

        /// <summary>
        /// Parse <c>"0:512,1:1024,2:1536,3:2048,4:4096"</c> →
        /// <c>Dictionary&lt;int,int&gt;</c>. Empty/whitespace string returns an
        /// empty dict (callers fall back to <c>MaxAtlasSize</c> /
        /// <c>MaxAtlasSizeInternal</c>). Malformed entries are skipped with a
        /// warning.
        /// </summary>
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

        /// <summary>
        /// True when <c>--ktx2-encoder</c> selects the in-binary basisu encoder
        /// (case-insensitive). Any other value (including the default
        /// "gltfpack") is treated as the gltfpack post-process path.
        /// </summary>
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

            // Pipeline-aware defaults. The flat-grid pipeline (the default)
            // preserves the master defaults so output stays byte-equivalent
            // given the same explicit args. The hierarchical opt-in uses the
            // tuned hierarchical defaults.
            int effectiveMaxVerts = options.MaxVerticesPerTile > 0
                ? options.MaxVerticesPerTile
                : (options.HierarchicalLods ? 1500 : 4000);

            // The flat-grid pipeline requires --lods (its code path NREs on
            // null). The hierarchical opt-in injects a safe stub so
            // MeshUtils.LoadMesh's LODs[0] access doesn't NRE; the
            // hierarchical pipeline overrides config.LODs internally below.
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
                // HLOD profile: --hierarchical-lods defaults adaptive-extend OFF
                // (opt back in with --adaptive-extend). Flat pipeline keeps the raw flag.
                NoAdaptiveExtend = options.HierarchicalLods ? !options.AdaptiveExtend : options.NoAdaptiveExtend,
                AdaptiveExtendMaxDepth = options.AdaptiveExtendMaxDepth,
                LeafVramBudgetMb = options.LeafVramBudgetMb,
                MaxTileCount = options.MaxTileCount,
                AtlasUnsharpAmount = options.AtlasUnsharpAmount,
                // HLOD profile: --hierarchical-lods defaults leaf-no-mips ON (opt out with --leaf-mips).
                LeafNoMips = options.HierarchicalLods ? !options.LeafMips : options.LeafNoMips,
                MaxDepthOverride = options.MaxDepth,
                // HLOD profile: --hierarchical-lods defaults quantize-glbs + meshopt-compress ON
                // (opt out with --no-quantize-glbs / --no-meshopt-compress). Flat keeps the raw flags.
                //
                // EXCEPTION — basisu KTX2 mode (--ktx2-encoder basisu): quantize-glbs and
                // meshopt-compress BOTH require gltfpack, which the prod image does not ship.
                // So in basisu mode they default to the RAW flags (off unless the operator
                // explicitly passes --quantize-glbs / --meshopt-compress). leaf-no-mips and
                // adaptive-extend-off stay HLOD defaults regardless (they don't need gltfpack).
                QuantizeGlbs = (options.HierarchicalLods && !IsBasisuEncoder(options.Ktx2Encoder))
                    ? !options.NoQuantizeGlbs
                    : options.QuantizeGlbs,
                GltfpackPath = options.GltfpackPath ?? "",
                MeshoptCompress = (options.HierarchicalLods && !IsBasisuEncoder(options.Ktx2Encoder))
                    ? !options.NoMeshoptCompress
                    : options.MeshoptCompress,
                // --no-ktx2 is the ergonomic inverse alias; either it OR
                // --ktx2-hierarchical=false disables the KTX2 step.
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