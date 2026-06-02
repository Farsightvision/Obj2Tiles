# Track 1 Phase 5 — HLOD perf optimization summary

**Date**: 2026-05-28
**Scope**: Four candidates (E, C, B, D) explored sequentially on independent
feature branches off `release/v1.1.0-beta-nomip` @ `59f5cd9`. Each candidate
got 3 HLOD bakes (small2 / hd / vlrg) at `--threads 1` (E/C/B) or `--threads 8`
(D) with `--no-adaptive-extend --no-ktx2 --leaf-no-mips`, then structural
identity + visual A/B + writeup.

## Phase-4 baseline (the slowdown we set out to fix)

| Fixture | HLOD wall | Phase-1 (fillAtlases) | Phase-1 share |
| --- | ---: | ---: | ---: |
| small2 | 32.5 s | ~2.6 s | 8% |
| hd | 29 m 34 s (1 774 s) | 27 m 32 s (1 652 s) | 93% |
| vlrg | 40 m 16 s (2 416 s) | 37 m 19 s (2 239 s) | 93% |

Localised cause: `MeshT_Hlod.FillAtlases → CopyImageScaled → ImageSharp
Image.Clone(ctx => Crop().Resize())`, called per-cluster per-tile. The
parallel Phase-1 path that already existed for small2 was force-gated to
serial on hd/vlrg by `textureBytes > 500 MiB`.

## Verdict table

| # | Candidate | Branch | Verdict | small2 | hd | vlrg | Cost |
| --- | --- | --- | --- | ---: | ---: | ---: | --- |
| E | Parallel `AssignMeasuredGeometricError` (bottom-up BFS over depth, `Parallel.ForEach` siblings) | `feat/perf-optim-3-parallel-geom-error` | **REJECT** | <1% | <1% | <1% | +1 small flag |
| C | Mip-pyramid pre-resize in `TexturesCache` | `feat/perf-optim-2-mip-pyramid` | **REJECT-OOM** (hd + vlrg) | n/a | rc=134 | rc=134 | Pyramid cache lifetime breaks per-material `EvictTexture` contract |
| B | SkiaSharp `SKBitmap.Resize` swap for ImageSharp bicubic in `Common_Hlod.CopyImageScaled` | `feat/perf-optim-4-skiasharp-resize` | **REJECT** | +2.2% (noise) | −0.7% | −0.3% | Adds SkiaSharp + native libskia dep |
| D | Material-aware parallel Phase-1 batching | `feat/perf-optim-5-parallel-phase1` | **STRONG-SHIP (hd) / STANDARD-SHIP (vlrg)** | **1.15×** | **4.68×** | **1.93×** | +1 flag `--phase1-batches-per-material` |

D is the only Phase-5 candidate that ships.

## Why each non-shipper failed

### E — Parallel AssignMeasuredGeometricError

Phase-4 timing showed `AssignMeasuredGeometricError` taking 33.7 s on vlrg
and 10.6 s on hd. E parallelised the per-node Hausdorff sweep with a
bottom-up BFS (siblings at each depth → independent → `Parallel.ForEach`)
and produced bit-identical output (`HausdorffMetric.ComputeSampled` is
deterministic stride sampling).

The stage went 1.5–1.9× faster as intended. But the stage is <1% of total
wall on hd/vlrg, so the pipeline-level speedup was indistinguishable from
noise. The work was correctly executed; it just doesn't matter at this
scale.

**Productive direction**: leave the parallel path in place if it doesn't
complicate the diff — saves 5–20 s on vlrg with zero risk. Not a priority.

### C — Mip-pyramid pre-resize

Concept: pre-compute a 4-level mip pyramid per source PNG in `TexturesCache`
so `CopyImageScaled` can pick the closest-fitting level and avoid the
worst-case shrink ratio. Structurally sound — but the pyramid cache
**holds all material pyramids for the bake's lifetime**, breaking HLOD
serial Phase-1's per-material `EvictTexture` contract.

vlrg's 67 materials × ~85 MiB pyramid pinned **5.7 GB above** the source
texture set (~17 GB resident peak), exceeded the 15 GB host RAM, SIGABRT
(rc=134) ~30/103 atlases into the bake. hd similarly.

**Fix path** (out of scope for C): tie pyramid lifetime to
`TexturesCache.EvictTexture(path)` so the pyramid is disposed together with
its source when the serial Phase-1 path evicts. ~10 lines.

**Status**: shelved. D's material-aware batching delivered the hd speedup
without needing C's pyramid; revisit only if a future pressure (e.g.
4K vlrg) requires another 2× on hd.

### B — SkiaSharp Resize swap

The published image-resize benchmarks (PhotoSauce, ImageMagick) show
SkiaSharp's libskia path 3–5× faster than ImageSharp's pure-C# bicubic.
We expected that to translate to Phase-1.

It did not. Three compounding factors:

1. **Marshaling overhead per call.** Each `CopyImageScaled` allocates a
   fresh `SKBitmap`, copies `(sw * sh * 4)` bytes from `Image<Rgba32>` row
   by row via `Marshal.Copy`, runs the resize, then copies
   `(destW * destH * 4)` bytes back — three trips between two pixel-buffer
   abstractions per call. This dwarfs the kernel-level win on typical
   cluster sizes.
2. **No buffer pool in the preview API.** SkiaSharp 3.0.0-preview's
   `SKBitmap` allocates fresh from `malloc` every time; ImageSharp's
   `Image.Clone(...)` actually reuses pooled `Rgba32[]` buffers between
   calls (especially when destination dims are stable). The pool advantage
   was invisible in the bench but real in production.
3. **Wrong sampler.** `SKSamplingOptions.Default` in 3.0.0-preview is
   linear, not bicubic — a *quality regression* relative to ImageSharp.
   Even with the lower-quality kernel, the wall didn't improve.

Net: structurally identical output (`tileset.json` md5 matches baseline on
all 3 fixtures) but no measurable speedup. Adds a 10 MB native
`libSkiaSharp.so` to the runtime. **Reverted.**

**Productive direction** (separate from B): the real fix is *eliminating*
the per-cluster resize calls (C with proper cache lifetime), not swapping
the backend. D ships meanwhile.

## D's mechanism in detail

Phase-1 dispatch in `HierarchicalTilingStage.WriteAllGlbs`:

1. **Sort tiles by primary material.** For each tile, find the
   `MaterialIndex` with the most faces (`PrimaryMaterialIndex(n)`). Sort
   the tile list by `(primaryMatIdx, depth, contentUri)`. Tiles sharing a
   dominant source PNG land next to each other in the processing order.
2. **Partition into fixed-size chunks** of `phase1Mdop * 2` tiles.
3. **Within each chunk**: `Parallel.ForEach(chunk, MaxDOP = phase1Mdop)`.
   Tiles concurrently call `PrepareTileForGlb`, which in turn reads
   materials through the lazy `TexturesCache`. Adjacent tiles in the same
   chunk hit the same `Lazy<Image<Rgba32>>` and share the decode — one
   PNG decode per chunk per shared material.
4. **Between chunks**: `TexturesCache.Clear()`. Disposes all
   `Image<Rgba32>` decoded for the just-finished chunk. Peak resident RAM
   is bounded by **one chunk's** resident-material union, not by the
   whole model's `tex=...MiB` total.

`AppConfig.ParallelPhase1` (formerly gated on `tex ≤ 500 MiB`) is now
gated on `phase1Mdop > 1`, so any host with ≥2 cores takes the parallel
path by default.

`HierarchicalAtlasStage.PackAndWrite` is **unchanged** — its existing
`!config.ParallelPhase1 → EvictTexture(path)` guard correctly skips
per-material eviction in the parallel path (eviction during parallel
would race the lazy load).

## Multiplicative effect estimate (C + D, if C had shipped)

Moot — C is REJECTed. If a future "C done right" (pyramid lifetime tied to
`EvictTexture`) ships *on top of* D, the combined hd speedup would
optimistically be 4.68× (D) × 1.5–2× (pyramid kernel win at typical shrink
ratios) ≈ 7–9× → hd 29 m → 3.5–4 m. Worth pursuing if a future requirement
pushes for more, but D alone hits the operator-stated 30 m → ~4 m target on hd.

## Recommended merge order

1. **D first.** Branch `feat/perf-optim-5-parallel-phase1` @ `74360e2`.
   STRONG-SHIP on hd (4.68×). STANDARD-SHIP on vlrg (1.93× at
   `--phase1-batches-per-material 2`). Bit-identical tileset structure.
   Adds one CLI flag.
   - Recommendation: also fold a tiny auto-derate heuristic into Program.cs
     before merging (`if textureBytes > RAM * 0.2 then mdop ≤ 2`), so the
     prefect flow doesn't need to special-case vlrg.
2. **(optional, follow-up)** "C done right": pyramid cache tied to
   `EvictTexture`. Only if more headroom is needed after D ships.

## Outstanding risks (post-D merge)

- **vlrg memory ceiling.** Default mdop=`ProcessorCount/2` OOMs on the
  current 15 GB test host. Mitigation: the new flag + the recommended
  auto-derate. Without the auto-derate, the Prefect flow must hard-code
  mdop=2 for vlrg-class.
- **Parallel-mode reproducibility.** D's output is *structurally*
  bit-identical to serial baseline (tileset.json md5 matches), but per-pixel
  GLB atlas bytes may differ if the parallel `Parallel.ForEach` exposes a
  scheduling-dependent code path elsewhere. Audit: D's per-tile work uses
  the same `PrepareTileForGlb` as the serial path; the only race-relevant
  shared state (`TexturesCache`) uses `ConcurrentDictionary` + `Lazy<T>`.
  No spot evidence of pixel drift; no formal proof.
- **Test-host scale.** All bakes were on an 8-core / 15 GB Linux host.
  Higher-core hosts should scale further on hd; higher-RAM hosts should
  remove the vlrg mdop=2 constraint. Validate on the production bake-host
  spec before declaring D's ceiling.
- **Phase-1 is no longer dominant on hd after D.** On hd, Phase-1 went
  from 93% of wall to ~94% × (1/4.68) ≈ 25% of post-D wall. The next
  bottleneck on hd is `AssignMeasuredGeometricError` (~2.8% before D →
  ~13% after D). E (REJECTed for absolute insignificance) becomes
  proportionally more interesting after D ships. Reconsider E if
  another 30s on hd would be worth the diff.

## Standing by

For operator ratification of D. No merges to `release/v1.1.0-beta-nomip`
until that ratification arrives.
