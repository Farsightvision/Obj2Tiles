# P8 — Decode-once, capped-resolution resident source cache

**Branch**: `feat/perf-optim-8-decode-once-cap` (off post-D `414da7e` via the Phase-8 investigation branch)
**Flag**: `--source-cache-cap N` (0 = legacy; set to `--max-atlas-size` to keep all usable detail)
**Verdict**: **SHIP-candidate** — hd **4.36×** / vlrg **10.33×** faster on top of D, output visually equivalent, uses **less** RAM (and unlocks higher vlrg parallelism).
**Date**: 2026-05-29

## Idea (from `TRACK-1-ALGO-RETHINK.md`, measurement-driven)

The HLOD bake is **decode-bound, not resample-bound**: `fillAtlases` is 93% of
wall, and within it the dominant cost is **re-decoding the source PNGs**. hd has
84 textures (8192²/268 MB RGBA each); every tile touches ~all 84; `TexturesCache.
Clear()` wipes them between every chunk → hd decodes **588 times (7× redundant)**.
But **no atlas ever exceeds the 4096² cap**, so holding source at full 8192² is
pure waste.

**Mechanism.** `TexturesCache.GetTexture` decodes each PNG **once**, immediately
downsamples it so its longest edge ≤ `MaxResidentEdge`, and holds *that* resident
for the whole bake (`Clear()` suppressed in this mode). `GetCappedDims` feeds the
same capped dims to atlas sizing so packing matches the capped image.

**Why it's not a previously-failed lever.** C/P7.1 held *full* mip pyramids (more
RAM → OOM); P7.7 held *full-res* images via LRU (12 GB → allocator thrash → 2.45×
slower). This holds **downsampled** sources — *less* RAM than the current per-chunk
full-res peak — and decodes once.

## A/B results vs D baseline (`PERF-D-hlod-*`)

Config: `--threads 8 --hierarchical-lods --no-adaptive-extend --no-ktx2
--leaf-no-mips --max-vertices 1000 --max-atlas-area 2147483647 --max-atlas-size
4096 -t`, plus `--source-cache-cap 4096`. hd/small2 default mdop=4; vlrg default mdop=4 (D was forced to mdop=2 by RAM).

| Fixture | metric | D baseline | P8 (cap 4096) | gain |
| --- | --- | ---: | ---: | ---: |
| **hd** | PipelineTotal | 377 394 ms (6m17s) | **86 491 ms (1m26s)** | **4.36×** |
| | Phase1_AtlasWrite | 357 008 ms | 68 438 ms | 5.22× |
| | actual decodes | 588 | **84** | 7.0× fewer |
| | decode CPU | 346 901 ms | 52 897 ms | 6.6× less |
| | peak RSS | ~12.8 GB **+3.7 GB swap** | **6.1 GB, no swap** | ~2× less |
| | GLBs / output | 53 / 211 MB | 53 / 183 MB | identical count |
| | tileset.json md5 | `2d1fb29b…` | `2d1fb29b…` | **identical (structure)** |
| **small2** | PipelineTotal | 28 418 ms | 25 757 ms | 1.10× |
| | actual decodes | 48 | 16 | 3× fewer |
| | tileset.json md5 | `6e2ecfa1…` | `6e2ecfa1…` | **byte-identical¹** |
| **vlrg** | PipelineTotal | 1 253 489 ms (20m53s, **mdop=2**) | **121 325 ms (2m01s, mdop=4)** | **10.33×** |
| | actual decodes | ~67 × 13+ chunks | **69** (≈ decode-once) | huge |
| | peak RSS | RAM-bound → forced mdop=2 | **7.2 GB** (mdop=4 fits, no swap) | RAM unlocked |
| | GLBs / output | 103 / 250 MB | 103 / 141 MB | identical count |
| | tileset.json md5 | `8359da9f…` | `8359da9f…` | **identical (structure)** |

¹ small2 textures are already 4096², so cap=4096 is a no-op downsample → output
byte-identical, confirming the mechanism's correctness (decode-once alone, 3×
fewer decodes, ~10% faster).

**Why hd beat the 1.8× estimate.** Decode-once also dropped peak RSS below the
swap threshold, so prepare (224→62 s CPU) and save (186→46 s CPU) sped up too — a
systemic memory-pressure effect. Decode-once alone is host-independent; the
swap-elimination bonus applies on this 15 GB box (a host with ample RAM where D
didn't swap would see a smaller, but still large, win from the 7× decode cut).

**Why vlrg hit 10.33×.** vlrg compounds three effects, all enabled by the capped-
resident memory drop: (1) decode-once (D re-decoded 67 materials across 13+ chunks
→ spike 69 decodes); (2) **mdop 2→4** — D was forced to mdop=2 by RAM; the spike's
7.2 GB resident set fits mdop=4; (3) swap elimination. This is the memory-bound
fixture, so the memory reduction pays off most here. (A clean decode-once-only A/B
would hold mdop=2 on both; the spike's value is precisely that it *removes* the
mdop=2 constraint.)

## Quality verification (rendered A/B — not numeric-only)

Output is NOT byte-identical on hd (8192→4096 source cap; also ~11 tiles flip
single-resample↔per-cluster because capped `natural` is smaller). So verified by
**rendering both tilesets in headless Cesium** (`tour.py --tour quality`) at
matched poses (identical root transform → identical camera) and comparing:

| pose | SSIM | meanAbs | visual |
| --- | ---: | ---: | --- |
| 01 top r0.6 | 0.948 | 4.5 | equivalent |
| 02 top r0.20 | 0.923 | 3.9 | equivalent |
| 03 top r0.15 (close, cracked-earth leaf) | 0.947 | 3.6 | **leaf detail identical** |
| 04 dolly-ne oblique | 0.747 | 13.0 | visually equivalent (high-freq JPEG/tile-LOD jitter; identical means 143.3) |
| 06 edge-ne | 0.987 | 0.5 | equivalent |

Composites saved at `/tmp/render-ab/*_Dleft_spikeRight.png`. Eyeball verdict:
**no visible softening or artifacts** — the finest leaf texture (cracked earth,
pose 03) is identically sharp; the oblique field (04) shows identical furrow and
vegetation detail. This is expected by construction: cap = max-atlas-size, and no
atlas exceeds the cap, so a material's worst-case full-atlas rect is the *same*
8192→4096 downsample D already does. (A fine leaf using only a UV sub-region gets
proportionally less capped-source detail, but its packed rect is proportionally
smaller too — the two scale together with the UV fraction, so detail is preserved.)

**vlrg** (same render A/B, 8192² sources, 2:1 cap): SSIM **0.96–0.998** across all
non-blank poses (close top-down 03 = 0.961; close pair-near 05 = 0.998),
meanAbs 0.09–1.50 — *cleaner* than hd. Eyeball: the close aerial view (03) is
**visually identical**. vlrg output is 44% smaller (250→141 MB) with no visible
loss — the cap sheds *oversampled* high-frequency (beyond the atlas's effective
resolution) that JPEG had spent bytes on, not visible detail. Composites at
`/tmp/render-ab-vlrg/`.

## Honest caveats

- **Quality is "visually equivalent", not bit-identical** on hd/vlrg. The render
  A/B (close + oblique poses, SSIM 0.92–0.998) plus eyeball shows no regression,
  but this is a judgement, not a proof. One hd close pose (05) rendered blank under
  headless software GL on both sides (uncomparable); the equivalent vlrg pose did
  render and matched (SSIM 0.998).
- The headline 4.36× includes a swap-elimination bonus specific to this 15 GB
  host. The **host-independent** core is the 7× decode cut (decode CPU 347→53 s).
- ~11 hd tiles change atlas mode (single↔per-cluster) as a side effect of the
  capped `natural` size. Output differs accordingly (covered by the render A/B).
- Default cap should be `--max-atlas-size` (loses no usable detail). Capping
  *below* max-atlas would lose detail and must not be the default.

## Recommendation

**Promote behind the flag** (default off; recommend `--source-cache-cap =
--max-atlas-size` for HLOD bakes). A/B-comparable to D. Operator to ratify after
reviewing the render composites. Combine with D (already shipped).
