# Obj2Tiles Hierarchical Pipeline — Architecture

## Goal

Convert textured photogrammetry OBJ files (ODM output, typically 50K–1M triangles, multi-MB texture clusters) into **3D Tiles 1.1** hierarchies that:

- Refine cleanly in Cesium, 3DTilesRendererJS, and MapLibre/deck.gl
- Have crack-free, manifold tile boundaries by construction
- Use per-tile atlas packing capped by a configurable size budget
- Preserve geometric accuracy through measured Hausdorff geometric error
- Stream efficiently with REPLACE refinement

Non-goals:
- Procedural / synthetic / non-photogrammetry meshes (the default flat-grid pipeline handles those; the hierarchical pipeline is opt-in via `--hierarchical-lods`)
- Real-time geometric editing during build
- KTX2 / EXT_meshopt_compression encoding (decode-only for now)

---

## High-level pipeline

```
OBJ + textures (input)
    |
    v
[1] MeshUtils.LoadMesh             ─ parse OBJ, build MeshT
    |
    v
[2] MeshSanitizer                  ─ UVs in unit range, drop zero-area tris
    |
    v
[3] OctreeSplitter.ChooseShape     ─ Quadtree if input flat, Octree if cubic
    |
    v
[4] ConformalHierarchyStage.BuildTreeConformal
    ├── BoundarySkeleton.BuildAndEnrich          (pre-insert plane intersections)
    ├── for d in 0..maxDepth:
    │     ├── SimplifyLocked  (lock skeleton verts, simplify if Quality<1)
    │     └── OctreeSplitter.PartitionAtDepth  (clip into 4^d / 8^d cells)
    ├── ExpandBoundsBottomUp  (parent AABB ⊇ union of children)
    └── wire parent/child relationships
    |
    v
[5] AssignMeasuredGeometricError   ─ Hausdorff per non-leaf, monotonic
    |
    v
[6] HierarchicalPruneStage         ─ collapse zero-error subtrees to leaves
    |
    v
[7] HierarchicalTilingStage.WriteAllGlbs
    ├── Phase 1 (serial): HierarchicalAtlasStage.PackAndWrite per tile
    │       ├── MeshT.PrepareRepackTextures   (bin-pack clusters, scale to cap)
    │       ├── MeshT.FillAtlases             (copy/resample source pixels)
    │       ├── Common.DilateAtlasBleed       (fix bilinear-filter fringes)
    │       └── MeshT.SaveAtlasesAndUpdateMaterial  (downscale to cap, save JPEG)
    └── Phase 2 (parallel): ConvertObjToGlb per tile
            └── Obj2Gltf.Converter.Convert (ApplyMeshoptOptimization=false)
    |
    v
[8] HierarchicalTilingStage.WriteTilesetJson
    └── emits 3D Tiles 1.1 tileset.json with:
        - asset.gltfUpAxis = "Z"
        - root.transform = ENU→ECEF
        - REPLACE refinement
        - per-tile boundingVolume.box
        - doubleSided=true on every material
    |
    v
[9] BuildReport.WriteTo            ─ report.json: depth stats, gates, max-edge
```

---

## Conformal hierarchy — the load-bearing design

The pipeline is **top-down per-depth** with a pre-inserted boundary skeleton, not bottom-up welding of child meshes. Bottom-up welders produce T-junctions and unmanifold boundaries between sibling tiles, which render as dark cracks at every grid line. Top-down conformal partitioning makes adjacent tiles share boundary vertices by construction.

### Algorithm

1. **Enrich** (`BoundarySkeleton.BuildAndEnrich`): for every cell-boundary plane at every depth from 1 to maxDepth, run `SplitAtPlane` to insert intersection vertices into the source mesh. Mark each new vertex as locked at the depth where its plane lives. Result: enriched verts + faces + skeleton.

2. **Per-depth simplify + partition**: for `d` from 0 to maxDepth:
   - Compute lock mask = `skeleton.LockMaskFor(d, vertCount)` (inherits depths 0..d).
   - `simpFaces = SimplifyLocked(enriched, lockMask, lods[d].Quality)` — meshopt with vertex_lock; no-op when Quality=1.
   - `cells = OctreeSplitter.PartitionAtDepth(simpFaces, sceneBounds, shape, d)` — recursively clips at axis midpoints to exactly depth d.

3. **Wire children**: for each non-root cell, find parent at `(level-1, X/2, Y/2, Z/2)` and append to parent's `Children` list.

4. **Expand bounds bottom-up**: per-depth simplification removes different verts at each depth, so a parent's surviving-vert AABB can be tighter than its children's. A final bottom-up pass widens each parent's `Bounds` to the union of its children's. The 3D Tiles spec requires parent bounding volume to contain all descendants.

### Why this is crack-free by construction

Adjacent cells at any depth are partitioned from the *same simplified mesh* and reference the *same vertex coordinates*. The skeleton-locked boundary verts are preserved through simplification, so adjacent tiles have bit-identical boundary vert sets. Verified by `BuildTreeConformal_synthetic_grid_produces_manifold_root` test.

---

## Winding preservation (CW/CCW source handling)

Photogrammetry input typically has ~3% CW-relative-to-Z-up triangles (walls, undersides). The clip/split formulas in `BoundarySkeleton.SplitAtPlane` and `OctreeSplitter.ClipAtAxisT` use fixed vertex-ordering formulas that preserve CCW winding for CCW input. CW source triangles would flip to back-facing in output and get face-culled by renderers (visible as small green triangular holes scattered across the model).

**Fix — generic post-pass.** Both splitters record each source triangle's normal, then for each emitted sub-triangle verify `dot(sub_normal, src_normal) > 0`; if not, swap `IndexB ↔ IndexC`. Applied symmetrically to `leftF` and `rightF` via shared `FixWindingT` helper. This is generic across all SplitAtPlane / ClipAtAxisT branches and works for any sign mix of source triangles. Per-branch CW handling would be 3× the code with no benefit.

**Simplifier output winding.** Meshopt's simplifier outputs triangle indices in an arbitrary permutation of the original face — half of permutations preserve winding, half flip it. `SimplifyLocked`:
- **Fast path** (output triple sorts to an input face): emit the **original** `MeshFace` directly. Positions and UVs are bit-identical and winding is bit-preserved at zero cost.
- **Synth path** (no matching source face): compare against an accumulated per-position normal `posNormal[p]` (sum of normals of all input faces containing `p`) and flip the synth tri if its normal dots negative. Best-effort heuristic on flat regions.

**`doubleSided=true` safety net.** At aggressive simplification ratios, meshopt emits a high fraction of synthesized triangles whose "correct" winding is mathematically undefined (no source face to inherit from). The heuristic above catches obvious cases but isn't perfect. Since photogrammetry is unlit (baked colors, no normal-based shading), enabling `doubleSided` in glTF materials disables back-face culling at the renderer level — no visual cost, no remaining back-face artifacts. Implemented as a small JSON-patch step (`PatchGltfDoubleSided`) between `Converter.Convert` and `Gltf2GlbConverter.Convert`. Cesium ion Reality Tiler and NVIDIA Omniverse do the same.

---

## UV-aware simplification

Default simplification (`attributeCount=0` to meshopt) has no UV awareness. On multi-cluster meshes the simplifier collapses verts across cluster boundaries; the synthesized-triangle fallback then arbitrarily picks one cluster's UVs, producing faces with `MaterialIndex=M` carrying UVs in cluster `N`'s space. This blows up cluster `M`'s atlas UV-rect to span both regions and can OOM the atlas packer.

**Two coordinated fixes:**

- **Attribute-weighted simplification.** Pass canonical UV per position as `attributeCount=2, weights=[2.0, 2.0]` to bias meshopt against UV-distorting collapses. Attribute weights are a continuous penalty, not a hard barrier.

- **Cluster-seam vertex lock.** OR cluster-seam positions (those touching ≥2 distinct materials) into the `vertex_lock` mask so the simplifier *cannot* collapse across clusters. This is the hard barrier.

- **Synth-triangle UV fallback.** Find a cluster shared by all 3 corner positions (`posClusters[a] ∩ posClusters[b] ∩ posClusters[c]`); use that cluster's per-corner canonical UVs. If no shared cluster exists, drop the triangle (rare in practice since seam verts are locked).

---

## Atlas pipeline

### Bin-pack with cap enforcement

`PrepareRepackTextures` bin-packs cluster rectangles into a single square atlas. On large fixtures (many materials × multi-MB clusters) the natural pack edge can exceed ImageSharp's 4 GB image limit (32K×32K).

**Solution:** after the natural pack succeeds, if `naturalEdge > maxAtlasSize`, compute `scale = cap / naturalEdge`, scale every `PackedRect` down by `scale`, and set `AtlasEdgeLength = cap`. `FillAtlases` then uses `Common.CopyImageScaled` (ImageSharp `Clone(ctx → Crop + Resize)`) to resample each cluster's source pixels into the scaled destination rect.

### Filter-edge dilation

Each per-tile atlas has ~30% empty black space between packed UV clusters. Bilinear filtering at cluster edges samples the black, producing dark fringes around every triangle edge. Fixed by `Common.DilateAtlasBleed(atlas, bleed=16)` after `FillAtlases` — grows non-empty pixels outward by 16 px via ping-pong morphological dilation. 16 px (not 4) is needed to cover screen-space sampling at distance 1.5–3 and mip levels 2–3.

### Phase-3 UV remapping

When `PackedRect` is scaled to fit the cap, the Phase-3 UV math (`SaveAtlasesAndUpdateMaterial`) cannot assume source pixels copy 1:1 into atlas pixels. UV scale is derived from `PackedRect.Width / AtlasEdgeLength` and `UvRect.Width` ratios so the formula tracks the actual atlas-UV extent of the packed rect. This reduces to the simple `texWidth / AtlasEdgeLength` formula when no scaling happened.

---

## Auto-pick depth & Quality schedule

The hierarchical pipeline is config-free: depth and Q schedule are derived from two budgets.

- `maxDepth` is auto-picked to satisfy both per-tile vertex budget (`--max-vertices`) *and* per-tile atlas budget: `materialCount × source_cluster_area / divisor^d ≤ maxAtlasSize²`.
- Q schedule is a linear lerp from `Q=1.0` at leaves to `Q=0.5` at root (`lods.Length == maxDepth + 1`, one entry per depth).
- The single `MaxAtlasSize` cap applies at every depth. Phase-3 downscale enforces it via `PreviousPowerOfTwo`.

Defaults: `--max-vertices 1500`, `--max-atlas-size 4096`.

Typical invocation:
```bash
dotnet run --project Obj2Tiles -- \
  --input model.obj --output out/ \
  --lat 45.46424 --lon 9.19028 --alt 0
```

`--lods` (explicit Q-ratio JSON array) is required by the default flat-grid pipeline and accepted as a power-user override on the hierarchical pipeline.

---

## Key files & responsibilities

### Core pipeline

| File | Responsibility |
|---|---|
| `Obj2Tiles/Program.cs` | Entry point; CLI parsing; pipeline orchestration; `BuildReport` gates |
| `Obj2Tiles/Stages/ConformalHierarchyStage.cs` | `BuildTreeConformal`, `SimplifyLocked` (UV-aware, cluster-seam vertex_lock, winding-preserving), `ExpandBoundsBottomUp` |
| `Obj2Tiles/Stages/BoundarySkeleton.cs` | `BuildAndEnrich`, `SplitAtPlane` (winding-preserving), `LockMaskFor` |
| `Obj2Tiles.Library/Geometry/OctreeSplitter.Textured.cs` | `ClipAtAxisT` (fp-safety snap + `FixWindingT` post-pass), `PartitionAtDepth`, `RecursiveSplitT` |
| `Obj2Tiles/Stages/HierarchicalAtlasStage.cs` | Per-tile atlas pack orchestration |
| `Obj2Tiles.Library/Geometry/MeshT.cs` | `PrepareRepackTextures` (post-pack scale-down to cap), `FillAtlases` (`CopyImageScaled` for source resampling), `SaveAtlasesAndUpdateMaterial` (dilation + PackedRect-based UV scale) |
| `Obj2Tiles.Library/Common.cs` | `DilateAtlasBleed`, `CopyImage`, `CopyImageScaled`, sRGB helpers |
| `Obj2Tiles/Stages/HierarchicalTilingStage.cs` | `WriteAllGlbs`, `WriteTilesetJson` (`gltfUpAxis=Z`, `ApplyMeshoptOptimization=false`, `PatchGltfDoubleSided`) |
| `Obj2Tiles/Stages/HierarchicalPruneStage.cs` | Zero-error subtree collapse |

### Diagnostics & gates

| Field on `BuildReport` | Threshold | Catches |
|---|---|---|
| `SourceMaxEdgeLength` | informational | source mesh sanity |
| `MaxLeafEdgeLength` | `≤ 1.5 × SourceMaxEdgeLength` (throws) | splitter creating spurious large triangles |
| `BoundaryEdgeCountRoot` | informational | crack regression |
| `AchievedSimplifyRatio[d]` | informational | simplifier not reducing as expected |
| `ZeroErrorInteriorPerDepth[d]` | should be 0 after prune | refinement chains not collapsing |

---

## Key invariants

1. **Adjacent same-depth tiles share boundary verts bit-exactly.** Tested in `BuildTreeConformal_synthetic_grid_produces_manifold_root`. Verifiable per-tile with `BoundaryEdgeCountRoot` close to source's natural perimeter count.

2. **No leaf triangle has an edge longer than the source max edge.** Enforced by `MaxLeafEdgeLength` gate. Catches splitter bugs that create spurious cross-cell triangles.

3. **Back-facing triangle ratio stays low on photogrammetry input** (winding-preservation working). Mitigated end-to-end by `doubleSided=true`.

4. **`asset.gltfUpAxis = "Z"` in every tileset.json.** GLB content is Z-up post-`-t` flag; renderers must skip their default Y→Z rotation.

5. **`ApplyMeshoptOptimization = false` in Obj2Gltf converter.** Re-enabling silently corrupts geometry on multi-cluster inputs.

---

## Settled design constraints

These are baked into the pipeline; revisit only with strong cause:

- **Top-down conformal hierarchy** (not bottom-up welding) — crack-free by construction.
- **Octree/Quadtree auto-pick** by AABB aspect ratio (`OctreeSplitter.ChooseShape`).
- **REPLACE refinement** (not ADD) — photogrammetry meshes are continuous, not discrete buildings.
- **Per-tile atlas with `MaxAtlasSize` cap** — single user-facing knob; depth/Q derive from it plus vertex budget.
- **AABB box bounding volumes** (not OBB or sphere — see `OctreeSplitter.AabbBox`).
- **meshoptimizer P/Invoke for simplification** (`Obj2Tiles.Native`).
- **Hausdorff geometric error** via `HausdorffMetric` with `TriangleBvh`.
- **`gltfUpAxis = "Z"`** — pipeline keeps coords in source frame (Z-up for ODM); the hint stops renderers from applying the default Y→Z rotation.
- **`doubleSided=true` materials** — synth triangles from aggressive simplification have undefined winding; photogrammetry is unlit so doubleSided is free.

---

## Known limitations & future work

| Area | Issue | Status |
|---|---|---|
| Atlas dilation cost | 16 px × 4096² atlas × per-tile-per-depth can be slow on very large fixtures. Consider single-frontier dilation (O(boundary) not O(W·H)) if profiling shows it dominant | Not started |
| EXT_meshopt_compression encoding | Decode-only ships now. Adding encoding requires the upstream meshopt-optimize chain to be safe on multi-cluster inputs | Not started |
| KTX2/ETC1S textures | Industry standard (Cesium ion Reality Tiler). Parent and child sample the same compressed texture grid, eliminating per-tile atlas resampling shifts at LOD transitions, cluster-UV-rect inflation that blocks aggressive simplification, and tile-boundary color seams from independent JPEG quantization | Not started — highest-leverage future item |
| UV-misalignment artifacts at aggressive Q (≤0.3) | "Snake" runs of texture-garbage triangles when synthesized triangles sample across intra-cluster UV seams. Currently mitigated by recommending `Q ≥ 0.5` in the auto-schedule. Real fix needs either a UV-preserving simplifier or KTX2 | Mitigated |

---

## Verification

### Build & test

```bash
# Build
dotnet build Obj2Tiles.sln -c Release

# Library tests
dotnet test Obj2Tiles.Library.Test -c Release --no-build

# E2E
dotnet test Obj2Tiles.Test --filter ConformalHierarchyEndToEndTests -c Release --no-build
```

### Run pipeline

```bash
rm -rf /tmp/out
dotnet run --project Obj2Tiles -c Release -- \
  --input /path/to/odm_textured_model_geo.obj \
  --output /tmp/out \
  --lat 45.46424200394995 --lon 9.190277486808588 --alt 0

# Validate
dotnet run --project Obj2Tiles -c Release --no-build -- \
  validate /tmp/out/tileset.json
```

---

## Default pipeline (flat-grid LOD)

The default pipeline (no `--hierarchical-lods` flag) is the flat-grid LOD path that the master binary uses (`HierarchicalSplitStage.BuildTreeT` and friends — name retained for code-history reasons; it actually produces a flat grid, not a hierarchy). It produces OBJ → 3D Tiles 1.0 output byte-equivalent to master for the same `--lods` schedule. The hierarchical pipeline described above is opt-in via `--hierarchical-lods`; the two pipelines do not share fixes — design changes to one do not propagate to the other.
