# Track 1 Phase 7 — Standalone sweep summary

**Date**: 2026-05-29
**Scope**: All 10 brainstorm candidates baked standalone on `release/v1.1.0-beta-nomip`
post-D (`414da7e`). hd + vlrg HLOD at `--threads 8 --phase1-batches-per-material 2`.
Per-candidate writeup at `docs/TRACK-1-PERF-OPTIM-P7N-*.md`. No cumulative-stack
bake performed — zero SHIP candidates.

## Baselines (post-D)

| Fixture | Wall | GLBs | Size |
| --- | ---: | ---: | ---: |
| small2 | 28.4 s | 21 | 39 MB |
| hd | 6 m 19 s (379 s) | 53 | 211 MB |
| vlrg | 20 m 53 s (1 253 s) | 103 | 250 MB |

## Verdict table

| # | Candidate | Verdict | hd | vlrg | small2 | md5 hd / vlrg |
| --- | --- | --- | ---: | ---: | ---: | --- |
| P7.1 | C-revised pyramid (per-material, EvictTexture-tied) | **REJECT** | 2.45× slower | OOM | n/a | stable / — |
| P7.2 | PhotoSauce MagicScaler drop-in | **REJECT** | 1.81× slower | parity (+1.1%) | 1.27× slower | stable / stable |
| P7.3 | TurboJPEG DCT-domain decode | **SKIPPED** — fixtures are PNG only | — | — | — | — |
| P7.4 | NetVips streaming bicubic | **REJECT** | 1.85× slower | parity (+2%) | 1.35× slower | stable / stable |
| P7.5 | fast_image_resize Rust cdylib | **REJECT** | 1.81× slower | OOM | 1.32× slower | stable / — |
| P7.6 | Channels<T> 3-stage pipeline | **SKIPPED** — D already overlaps decode/resize via Parallel.ForEach | — | — | — | — |
| P7.7 | Budgeted LRU TexturesCache | **REJECT** | 2.45× slower | 1.15× slower | parity | stable / stable |
| P7.8 | Pre-tabulated bicubic coefficient cache | **SKIPPED** — ImageSharp's `Resize` API is internal; would need a fork | — | — | — | — |
| P7.9 | Skip empty FillAtlases calls | **NO-OP** — already done by `MeshT_Hlod:789-800` early-exit | — | — | — | — |
| P7.10 | Natural-size atlas, resize once (raised threshold) | **REJECT** | 1.87× slower | OOM | 1.26× slower | stable / — |

**SHIP count: 0.** All standalone tests regressed on hd; on vlrg either parity, regression, or OOM.

## The unifying lesson — six independent kernel/structure swaps fail the same way

Three groups of candidates, each failing at the same structural ceiling.

### Group 1 — Drop-in resize-library swaps (P7.2, P7.4, P7.5)

Three SIMD-tuned bicubic backends (PhotoSauce MagicScaler, NetVips/libvips,
fast_image_resize Rust). All three regress hd at 1.81–1.85× and either hit
parity or OOM on vlrg.

**Why**: published "2.5× / 4× faster than ImageSharp" benchmarks measure
*whole-image throughput* on 4928×3279→852×567 sizes. Obj2Tiles runs ~8 000
per-cluster sub-rect operations per hd bake, where source rects are
~256–1024 px and the per-call overhead (FFI, buffer marshal, library
lifecycle) is comparable to the kernel work. Even raw P/Invoke to a Rust
cdylib (P7.5, the minimum-FFI design) regressed identically.

### Group 2 — Per-material / cross-tile amortisation (P7.1, P7.7)

Pre-computing per-material mip pyramids (C-revised) or keeping hot
materials resident across chunks via byte-budgeted LRU. Both proposed to
trade memory for fewer redundant decodes.

**Why**: D's chunk-Clear is so cheap (disposes 5–10 images, frees
~0.5–1 GB) that any per-access bookkeeping (P7.7's MaybeEvict snapshot)
or eager build cost (P7.1's pyramid build per material) exceeds the
amortised savings. The chunk-Clear baseline IS the right tuning at this
texture-set-size × host-RAM ratio.

### Group 3 — Whole-atlas instead of per-cluster (P7.10)

Pack the atlas at natural size, do ONE bicubic on the whole atlas
instead of N per-cluster bicubics. Counter-intuitively, one big bicubic
on a (8×cap)² atlas costs more than N small per-cluster bicubics on
sub-rects.

**Why**: ImageSharp's `Image.Clone(Crop().Resize())` allocates per-call
but its bicubic kernel is well-tuned for small inputs. The big bicubic
scales superlinearly in atlas area while the per-cluster path scales
linearly in *cluster surface*. D's existing `_useSingleResamplePath`
threshold (4×cap, 12288) IS the sweet spot.

## What does still hold from Phase 5

**D shipped (commit `414da7e`) and remains the only Phase-7-relevant
candidate that delivers on hd/vlrg.** Material-aware parallel batching
moves the dispatch model from serial-locked to parallel, and the
remaining post-D wall is *the kernel work itself* — which Phase 7
showed cannot be moved by the ten brainstorm candidates.

## Recommended merge order — NONE

No Phase-7 candidate ships. The post-D production tuning stays:
- `--threads 8 --phase1-batches-per-material 2` for vlrg-class.
- Default mdop (= `ProcessorCount / 2`) for hd / small2.

## What would change the picture

The Phase-7 sweep ruled out kernel-level and per-call-amortisation
candidates. The candidates left untested (and not in the original
brainstorm) target the *workload shape* instead of the per-call cost:

- **Reduce cluster count per tile.** Coarser UV packing produces fewer
  but larger clusters, where the per-call overhead amortises. Estimated
  hd 1.3–1.5× / vlrg 1.2–1.3×.
- **Switch atlas output from JPEG to KTX2 BC7** (P7.x not in brainstorm).
  Cuts the `saveAtlases` 5–7% of wall + ~80% runtime VRAM win. Requires
  viewer support audit (Cesium ✓; three.js needs the transcoder).
- **GPU-batched resize.** Phase 6 ranked this highest-ceiling
  (3-5× w/ HW GPU), hardest-deployment. Current host lacks usable GPU
  (Virtio paravirt only); test would need a CUDA-equipped runner.

These three are sketched for Phase 8 if the operator wants more wins.

## Standing by

For operator review of the verdict and decision on Phase 8 direction.
No merges. No further candidates dispatched.

## Phase 7 doc map

```
docs/
  PHASE6-CODEX-BRAINSTORM-RAW.md
  PHASE6-WEBRESEARCH-BRAINSTORM-RAW.md
  PHASE6-WALLE-ASSUMPTIONS.md
  TRACK-1-PERF-OPTIM-BRAINSTORM.md       (Phase 6 synthesis)
  TRACK-1-PERF-OPTIM-P71-C-REVISED.md         REJECT
  TRACK-1-PERF-OPTIM-P72-PHOTOSAUCE.md        REJECT
  TRACK-1-PERF-OPTIM-P73-TURBOJPEG-SKIP.md    SKIPPED
  TRACK-1-PERF-OPTIM-P74-NETVIPS.md           REJECT
  TRACK-1-PERF-OPTIM-P75-FIR-RUST.md          REJECT
  TRACK-1-PERF-OPTIM-P76-CHANNELS-SKIP.md     SKIPPED
  TRACK-1-PERF-OPTIM-P77-LRU-CACHE.md         REJECT
  TRACK-1-PERF-OPTIM-P78-COEFF-CACHE-SKIP.md  SKIPPED
  TRACK-1-PERF-OPTIM-P79-SKIP-EMPTY-FILLATLASES.md   NO-OP
  TRACK-1-PERF-OPTIM-P710-NATURAL-ATLAS.md    REJECT
  TRACK-1-PERF-OPTIM-PHASE7-SUMMARY.md       (this)
```

Per-candidate feature branches:
- `feat/perf-optim-71-c-revised` — `f4b5ddd`
- `feat/perf-optim-72-photosauce` — `84a1841`
- `feat/perf-optim-73-turbojpeg-skip` — `d3af7d2`
- `feat/perf-optim-74-netvips` — `df55720`
- `feat/perf-optim-75-fir-rust` — `0606508`
- `feat/perf-optim-76-channels` — `bc04f7d`
- `feat/perf-optim-77-lru-cache` — `afc4b89`
- `feat/perf-optim-78-coeff-cache-skip` — `b0baa40`
- `feat/perf-optim-79-skip-empty-fillatlases` — `10c4aab`
- `feat/perf-optim-710-natural-atlas` — `c5cae19`
