# HLOD Bake — Architecture (maintainer overview, rc-v3)

A bird's-eye map of the Hierarchical-LOD bake pipeline so a new reader can orient before diving into a
stage. Scope = the `--hierarchical-lods` path (the production RC). The legacy flat-grid / octree path
(`OctreeSplitter`, `RunFlatGridPipeline`) is separate and not covered here.

Companion docs: **HLOD-FLAGS.md** (every CLI/env knob) · **TRACK-1-PHASE8-PROGRESS.md** (the full
attempt/verdict ledger) · **TRACK-1-ATTEMPT-LEDGER-SUMMARY.md** (kept/rejected levers).

## Entry point
`Obj2Tiles/Program.cs` — `if (config.HierarchicalLods)` selects the HLOD pipeline. Each step below is wrapped
in a `Stage("…")` timing marker (grep `Stage("` in Program.cs to see them in order).

## Pipeline stages (in execution order)
| # | Stage | Where | What it does |
|---|---|---|---|
| 1 | LoadMesh | Program + ObjMesh | Parse the source OBJ/MTL into vertices, UVs, faces, materials. |
| 2 | Sanitize + zero-area drop + maxEdge | MeshSanitizer | Drop degenerate/zero-area faces, weld, optional max-edge split. |
| 3a | ModelMetrics.Compute | ModelMetrics | Scene bounds, diagonal, triangle/texture statistics. |
| 3b | EstimateEffectiveBranching | Program | Pick `maxDepth` (autoDepth) for the conformal tree from model size/detail. |
| 4 | BuildTreeConformal (clip + simplify) | ConformalHierarchyStage | Build the depth-uniform tree: `BoundarySkeleton` enriches cut planes, `PartitionAtDepth` clips the mesh into per-depth cells, each depth simplified to its LOD. Parallel per depth. |
| 4b | PruneAdaptive | ConformalHierarchyStage | Collapse interior nodes whose subtree is too sparse to be worth subdividing (≤ `TLeafTri` triangles AND ≤ `TLeafTextureBytes` of UV-claimed source texture, via `ComputeTileTextureBytes`). Non-uniform depth. |
| 4c | ExtendAdaptive | ConformalHierarchyStage | The inverse: subdivide any leaf whose `ideal_side` (from `PredictAtlasSide`) exceeds `MaxAtlasSize` — texel budget can't keep up. Off with `--no-adaptive-extend`. |
| 5 | AssignMeasuredGeometricError | Program + HausdorffMetric | Per non-leaf node, measured one-direction Hausdorff distance from original verts to the simplified surface (`TriangleBvh` nearest-point), with monotonic correction. Parallel per node. |
| 5a | ApplyTextureAwareGeometricError | Program + TextureGeometricError | TEXGE-V3: `effectiveGE = max(meshGE, textureGE)` so a texture-under-resolved-but-flat tile still refines at the default SSE. Strict monotonicity after. |
| 6 | PruneZeroErrorSubtrees | HierarchicalPruneStage | Collapse children whose geometry equals the parent's (GE 0) — no point streaming them. |
| 7 | WriteAllGlbs | HierarchicalTilingStage | The heavy stage — atlas + GLB emission (Phases 1–3 below). |
| 8 | WriteTilesetJson | HierarchicalTilingStage | Emit `tileset.json` referencing the GLBs (ECEF root transform from lat/lon/alt). |

### Stage 7 internals (WriteAllGlbs)
- **Phase 1 — atlas pack (serial-or-material-parallel):** `HierarchicalAtlasStage.PackAndWrite` sizes the
  atlas (`PredictAtlasSide`), packs UV clusters (Skyline/MaxRects), fills + dilates edge-bleed
  (`Common_Hlod.DilateAtlasBleed`), writes OBJ/MTL/atlas via `MeshT_Hlod`. ImageSharp internals here are
  not concurrency-safe, hence the careful batching.
- **Phase 2 — OBJ→glTF→GLB (parallel):** each tile read off disk independently (Obj2Gltf + Gltf2Glb). The
  dominant cost on big fixtures.
- **Phase 3 — gltfpack quantize / KTX2 (parallel, optional):** only with `--quantize-glbs`. KTX2/ETC1S
  encode is memory-heavy → a **memory-adaptive worker cap** (see scale-safety) + `-tj 1` prevent OOM /
  thread oversubscription. JPEG-default path skips this entirely.

## Key data types
- **`HierarchicalNode`** (Obj2Tiles/Stages): the tree node. `Coord` (`CellCoord`), `Depth => Coord.Level`,
  `Bounds`, `TileContentT` (the clipped mesh), `Children`, `GeometricError`, `IsLeaf => Children.Count == 0`.
- **`ClipResultT`** (Obj2Tiles.Library/Geometry): a tile's clipped mesh — `Vertices` (Vertex3[]),
  `TexVertices` (Vertex2[]), `Faces` (MeshFace[] with vertex + tex indices + material index).
- **`MeshT_Hlod`** (Obj2Tiles.Library/Geometry): the per-tile mesh that repacks textures into a per-tile
  atlas and writes OBJ/MTL. Holds `AtlasEdgeLength` (set internally during packing).
- **`TilePrepared`** (HierarchicalTilingStage, private): the Phase-1 → Phase-2 handoff (depth, atlas edge,
  obj/gltf/final-glb paths).

## Core formulas (extracted to Obj2Tiles.Library/Geometry, unit-tested)
- **`TextureGeometricError.FromTexelDensity(mpt, maxSse, pMax, fallback)`** = `mpt × (pMax>0 ? maxSse/pMax :
  fallback)`. The TEXGE-V3 texture-resolution GE. `pMax` (Nyquist 0.5) is the one principled dial.
- **`LodDensitySchedule.DensityAtDepth(leafDensity, refDepth, depth)`** = `leafDensity / 2^clamp(refDepth-depth,
  0, 16)`. The per-LOD texel-density schedule. Used by `PredictAtlasSide`, `ExtendAdaptive`, and the area sizer.
- **`HausdorffMetric`**: `Compute`/`ComputeSampled` (measured GE via `TriangleBvh` nearest-point) +
  `MonotonicCorrection` (parent ≥ maxChild + ε, ε = 1e-3 × scene diagonal — dominates FP noise so adjacent
  same-depth tiles don't render at mismatched LOD).
- **`ConformalHierarchyStage.PredictAtlasSide`** = `clamp(NextPow2(round(sqrt(A_world × r_d²))), AtlasMinSize,
  cap)`; `cap` = MaxAtlasSize (leaf) / per-depth schedule / MaxAtlasSizeInternal. Drives atlas sizing + the
  TEXGE meters-per-texel input. Supporting: `ComputeTileWorldArea`, `ComputeTileTextureBytes`.

## Memory & scale-safety (must hold for arbitrarily large, texture-diverse models)
- **`TexturesCache`** (Library): decode-once, resident, **capped** source-texture cache. `Clear()` evicts
  per-chunk once the resident set exceeds the RAM budget (`HLOD_CACHE_BUDGET_MIB`, default 60% of total) —
  bounded peak (G2-SAFE). Decode/dilate timing feed `[perf:hlod:DecodeStats]` / `[perf:hlod:DilateMs]`.
- **Phase-1 graceful degradation (Obj2b — never-OOM on a memory-bound host).** At a large `--source-cache-cap`
  (native, e.g. 8192²) the per-worker native working set is ~2 GiB (decode + resample + atlas), so the static
  G7 mdop (≈ cores/2) would OOM (budget + workers + OS > RAM). Two HLOD-only, output-neutral backoffs keyed on
  LIVE `/proc/meminfo MemAvailable` (re-checked per chunk, not just at startup):
  - `ClampWorkersToMemory` (unit-tested): Phase-1 mdop = `clamp(floor((0.75·liveAvail − residentReserve) /
    perWorker), 1, --threads)`, `perWorker = capEdge²×4×8`. Degrades to mdop=1, which always fits.
  - Over-budget-native budget tighten: resident budget → ≤ 55% of live RAM so concurrent workers fit.
  The FLOOR is the single widest tile's native working set (a tile spanning many 8192² source materials must
  hold them all to fill its atlas). On a 15 GiB host vlrg native peaks ~13 GiB at mdop=1 — completes, but a
  THIN margin under concurrent load. For more headroom, lower `--source-cache-cap` (downsamples sources →
  smaller per-tile working set) — a quality/headroom config choice, not a regression.
- **Phase-3 KTX2 encode (Obj2b).** Before Phase-3 the Phase-1 decode-once cache is freed (gltfpack reads the
  GLBs from disk, not the cache) so the gltfpack workers get the RAM. Worker count is budgeted against live RAM
  using the MEASURED ~0.9 GiB/worker (4096²-capped atlas; `HLOD_KTX2_WORKERS` overrides). gltfpack is
  auto-detected (PATH → `$HOME/bin` → common locations); a non-BasisU gltfpack (which would silently emit JPEG)
  is caught by checking the converted GLB for `KHR_texture_basisu`.
- **The rule:** footprint is a function of available RAM and degrades gracefully (fewer workers, harder
  eviction) — never a fixed resident set, never OOM; slower if needed.

## Test coverage map
- **Obj2Tiles.Library.Test:** `HausdorffMetricTests` (TriangleBvh nearest-point incl. degenerate triangles,
  MonotonicCorrection ε-contract, ComputeSampled stride), `CommonHlodTests` (DilateAtlasBleed edge-bleed band),
  `TextureGeometricErrorTests`, `LodDensityScheduleTests`, plus pre-existing mesh/octree/sanitizer/meshopt tests.
- **Obj2Tiles.Test:** `ConformalHierarchyAreaTests` (ComputeTileWorldArea + ComputeTileTextureBytes),
  `PredictAtlasSideTests` (atlas sizing branches), `ClampWorkersToMemoryTests` (Phase-1 mdop graceful-
  degradation clamp), `RequireTileableSceneTests` (degenerate-input tree-build guard: zero-triangle /
  non-finite / zero-diagonal scenes rejected with a clear error; flat + sub-mm models still build), plus
  pre-existing dispatcher/stage tests.

## Zero-regression gate
The shippable artifacts are the GLBs + `tileset.json`. The byte-identical gate bakes all 3 fixtures
(small2/hd/vlrg) and md5-compares every GLB + tileset.json against `docs/rc-v3-baseline/baseFP-*.txt`
(`report.json` excluded — nondeterministic Dictionary key-order, a diagnostics sidecar). Pure-function /
test-only changes are byte-identical by construction.

That gate covers ONLY this HLOD path. The **legacy flat-grid pipeline** (the default, no `--hierarchical-lods`)
is a separate live production app and has its own byte-identical gate — `docs/legacy-baseline/` (run
`verify-legacy-flat.sh`). Run it on any change to code SHARED by both pipelines (`TexturesCache`, `MeshT`,
`Common`, `SplitStage`, `OctreeSplitter`, `MeshSanitizer`, `Box3`, `Vertex*`); HLOD-only files
(`Hierarchical*`/`Conformal*`, `MeshT_Hlod`, `Common_Hlod`) can't affect it.
