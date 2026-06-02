# Track 1 Phase 4 — Per-stage timing breakdown: HLOD vs flat-grid

**Date:** 2026-05-28
**Branch:** `release/v1.1.0-beta-nomip` at `a759f32` (perf-logs commit).
**Bakes:** `tests/visual/out/{MASTER-prod-perf,HLOD-noExt-perf}-{small2,hd,vlrg}/`
**Config:** identical to TRACK-1-HLOD-VS-MASTER.md; HLOD adds `--leaf-no-mips` (per project memory).

## Bake totals (from `[perf:*:PipelineTotal]`)

| Fixture | MASTER-prod-perf | HLOD-noExt-perf | HLOD ÷ MASTER |
| --- | ---: | ---: | ---: |
| small2 | 28.8 s | 32.5 s | 1.13× |
| hd     | 316 s (5 m 16 s) | **1 774 s (29 m 34 s)** | **5.62×** |
| vlrg   | 623 s (10 m 23 s) | **2 416 s (40 m 16 s)** | **3.88×** |

The HLOD slowdown reproduces the TRACK-1 finding (5.5× hd / 3.8× vlrg) to within bake-time noise; the new perf logs let us localise it.

## MASTER (flat-grid) per-stage

| Stage | small2 (ms) | hd (ms) | vlrg (ms) | hd share | vlrg share |
| --- | ---: | ---: | ---: | ---: | ---: |
| Decimate | 4 350 | 17 126 | 24 529 | 5% | 4% |
| **Split** | **17 745** | **288 459** | **586 785** | **91%** | **94%** |
| ObjToGlb | 6 647 | 10 362 | 11 647 | 3% | 2% |
| GenerateTileset | 30 | 49 | 62 | <1% | <1% |
| **Total** | **28 775** | **316 014** | **623 043** | | |

`StagesFacade.Split` (recursive XY split + per-cell decimation + texture repack) dominates flat-grid wall by 91–94% on hd/vlrg.

## HLOD per-stage

| Stage | small2 (ms) | hd (ms) | vlrg (ms) | hd share | vlrg share |
| --- | ---: | ---: | ---: | ---: | ---: |
| LoadMesh | 1 575 | 1 761 | 1 600 | <1% | <1% |
| Sanitize + maxEdge | 70 | 75 | 154 | <1% | <1% |
| ModelMetrics.Compute | 3 | 5 | 16 | <1% | <1% |
| EstimateEffectiveBranching | 15 | 14 | 21 | <1% | <1% |
| BuildTreeConformal | 2 265 | 3 637 | 5 515 | <1% | <1% |
| PruneAdaptive | 6 | 7 | 8 | <1% | <1% |
| ExtendAdaptive | 1 | 0 | 0 | 0% | 0% |
| **AssignMeasuredGeometricError** | 9 523 | 10 715 | 34 091 | <1% | 1% |
| ApplyTextureAwareGeometricError | 11 | 11 | 13 | <1% | <1% |
| PruneZeroErrorSubtrees | 1 | 0 | 0 | 0% | 0% |
| **Phase1_AtlasWrite** | 17 104 | **1 752 065** | **2 366 690** | **99%** | **98%** |
| Phase2_ObjToGlb | 1 799 | 5 756 | 7 532 | <1% | <1% |
| WriteTilesetJson | 9 | 7 | 26 | <1% | <1% |
| **Total** | **32 544** | **1 774 243** | **2 415 931** | | |

`Phase1_AtlasWrite` is **99% of hd / 98% of vlrg**.

## Phase1 internal breakdown (CPU-sec, summed across tile work)

The Phase-1 breakdown reports `ctor + prepare + fillAtlases + saveAtlases + writeGeom` summed over the per-tile work loop. For `mode=serial` (hd, vlrg) the sum ≈ wall; for `mode=parallel:N` the sum ≥ wall.

| Stage (Phase-1 sub) | small2 (ms) | hd (ms) | vlrg (ms) | hd share of Phase1 | vlrg share of Phase1 |
| --- | ---: | ---: | ---: | ---: | ---: |
| ctor | 14 | 4 | 6 | <1% | <1% |
| prepare (cluster build + UV bbox) | 5 570 | 38 172 | 14 271 | 2% | <1% |
| **fillAtlases (source-pixel copy / resample)** | 2 610 | **1 651 736** | **2 238 837** | **94%** | **95%** |
| saveAtlases (encode + dilate + write JPEG) | 8 098 | 61 235 | 112 406 | 3% | 5% |
| writeGeom (write OBJ tile + materials) | 757 | 874 | 1 046 | <1% | <1% |

**`fillAtlases` is the entire HLOD slow-path.** On hd it consumes 1 652 s of the 1 774 s pipeline (**93%** of total wall); on vlrg it consumes 2 239 s of 2 416 s (**93%**).

## What `fillAtlases` does

From `Obj2Tiles.Library/Geometry/MeshT_Hlod.cs` (`FillAtlases` + `CopyImageScaled` in `Common.cs`):

1. Per cluster in the tile: open the source PNG texture file (`TexturesCache.GetTexture(path)`), allocate an `Image<Rgba32>` of the cluster's source-pixel region, **resample** it down to the cluster's packed atlas rect using ImageSharp's bicubic resize, **copy** into the tile atlas via `CopyImageScaled`.
2. Each tile that touches material `M` decodes / loads `M`'s full PNG (lazy-cached in `TexturesCache`), then resizes a sub-rect of it.
3. `mode=serial` is auto-picked when `ModelMetrics.TextureBytes > 500 MiB` (the parallel-phase threshold in Program.cs:148). hd has 16 materials × ~1 MiB; vlrg has 67 materials × ~9 MiB each. Both fall on the serial side (the previous parallel path would have RAM-blown by holding all decoded RGBA32 buffers simultaneously).

The cost is dominated by **ImageSharp's `Resize` + per-pixel `Mutate`** inside the per-tile `CopyImageScaled` calls, multiplied by `cluster_count × tile_count`. Per-material redundancy is enormous: the same source PNG is re-resampled into many tile atlases at slightly different rects.

## Dominant slow-path summary

| Pipeline | Fixture | Dominant stage | Share of wall |
| --- | --- | --- | ---: |
| flat-grid | hd | `StagesFacade.Split` | 91% |
| flat-grid | vlrg | `StagesFacade.Split` | 94% |
| HLOD | hd | `Phase1_AtlasWrite/fillAtlases` | 93% |
| HLOD | vlrg | `Phase1_AtlasWrite/fillAtlases` | 93% |

## Optimization hypotheses (Phase 5 input)

For Phase 5, ranked by expected speedup-to-risk ratio:

1. **Parallelize Phase-1 atlas pack across cores.** The `mode=serial` decision is RAM-driven (>500 MiB total decoded). For hd (16 materials × few MB) the RAM concern is real; for vlrg (67 materials × ~9 MB = 600 MB decoded RGBA32) it's borderline. A **smart partitioning** that groups tiles by which materials they reference and runs each group in parallel without holding all materials resident at once would still produce a large speedup, since most tiles touch a small material subset. **Estimated impact: 4–8× on 8 cores; threading-data-race risk on `TexturesCache`.**

2. **Cache the resampled source-pixel buffers per material** (LRU). The current code re-resamples the same source PNG for every tile that intersects its UV area. Per-material resampled-once-then-copied flow could amortize the decode cost. **Estimated impact: 2–4× on hd (16 materials × dozens of tiles each); risk: memory growth if cache size unbounded.** Matches Codex's Novel-2 sketch.

3. **Per-cluster UV bbox precompute** in Phase-0 (Obj2Tiles.Library.Geometry.UvClusterUtil — already exists from R2-form-(f) experiment, but currently unused on this branch). Instead of recomputing UV bboxes per tile inside `prepare`, compute once during tree build. **Estimated impact: small on hd (38 s prepare → maybe 20 s); large on vlrg if cluster count scales (currently 14 s prepare).**

4. **ImageSharp Resize → SkiaSharp or custom bicubic.** ImageSharp's Resize is known-slow vs SkiaSharp (often 3–5× on linear bicubic). Drop-in replacement, risk: pixel-exact output may shift slightly (mitigate with visual A/B at close-zoom).

5. **`AssignMeasuredGeometricError`** is 34 s on vlrg / 10 s on hd. Currently a single-threaded per-node Hausdorff sweep. Parallel `Parallel.ForEach` over nodes would cut to ~5 s on vlrg. Low risk: pure measurement, no I/O.

6. **Defer per-material `EvictTexture` in serial Phase-1.** Currently every per-tile loop evicts after use; if the next tile re-uses the same material, we decode again. Group tiles by material first, then process in batches. **Estimated impact: medium; risk: increased peak RAM.**

The first three are the high-value targets. Candidates 1 + 2 together could plausibly drive HLOD hd from 30 m to 5–7 m (matching or beating master flat-grid on the same hardware).

## What's NOT worth optimizing

- `BuildTreeConformal` (2–6 s): already cheap.
- `PruneAdaptive` / `ExtendAdaptive` / `PruneZeroErrorSubtrees`: sub-10 ms each.
- `Phase2_ObjToGlb` (2–8 s): cheap; ConvertObjToGlb is already glTF-stream optimised.
- `WriteTilesetJson` (~10 ms): trivial.
- Flat-grid `ObjToGlb` (6–12 s): cheap compared to Split.

## Files

- Raw logs (per-pipeline `[perf:*]` lines extractable via `grep -E '^\[perf:(hlod|flat):'`):
  - `tests/visual/out/MASTER-prod-perf-{small2,hd,vlrg}.bake.log`
  - `tests/visual/out/HLOD-noExt-perf-{small2,hd,vlrg}.bake.log`
- Tabulated extract: `/tmp/perf-extract.txt`
- Perf-instrumentation commit: `a759f32` on `release/v1.1.0-beta-nomip`.
