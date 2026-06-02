# Track 1 — Algorithmic Rethink of the HLOD Bake (Phase 8)

**Date:** 2026-05-29
**Base:** post-D (`414da7e`) on `feat/perf-optim-8-investigation`.
**Method:** dual independent brainstorm (Opus 4.8 + Codex via codex:rescue), then
**measure every premise before betting** (per operator directive). All numbers
below are from instrumented bakes on this branch or the surviving D logs — not
inferred. Builds on `docs/TRACK-1-BOOST-POSTMORTEM.md`.

---

## 0. TL;DR — the measurement overturned the obvious idea

The "obvious" structural idea (and the one both brainstorms ranked #1) was
**cross-depth reuse to avoid re-resampling source pixels**. Measuring the premise
killed the *resample* framing but revealed a bigger, adjacent win:

> **The HLOD bake is not resample-bound. It is DECODE-bound.** `fillAtlases` is
> 93% of wall; within it, the dominant cost is **re-decoding the source PNGs** —
> hd decodes its 84 textures **588 times (7× redundant)** because every tile
> touches ~all 84 materials and `TexturesCache.Clear()` wipes them between every
> chunk. The bicubic resample kernel is only ~8% of `fillAtlases`.

The winning lever is therefore **decode the source once and hold it resident at a
capped resolution** — which attacks both walls at once and, crucially, is
*distinct from* the candidates that already failed (it uses *less* memory than the
current design, not more). Spiking it next.

---

## 1. Premise measurements (the evidence)

All instrumentation is throwaway, on `feat/perf-optim-8-investigation`.

### 1a. Bottleneck is decode, not resample (the headline)

hd, parallel (D config, mdop=4), instrumented `[perf:hlod:DecodeStats]`:

| Metric | Value | Note |
|---|---:|---|
| Source textures | **84** | each **8192² PNG → 268 MB RGBA** (not "16 × 1 MiB" as old docs claimed) |
| Actual decodes | **588** | = 84 × 7 chunks → **7.0× redundant re-decode** |
| True decode CPU | **346 901 ms** | clean (Lazy-factory timer; no parallel-wait inflation) |
| decode ÷ fillAtlases CPU | **38%** | fillAtlases CPU-sum = 907 686 ms |
| decode-**wait** (blocked on shared `Lazy`) | **~489 000 ms** | the per-tile timer measured 836 s "in GetTexture"; 836−347 = wait |
| cluster-loop resample CPU | **75 454 ms** | **8%** of fillAtlases |

**Why 7× redundant:** hd tiles touch a **median of 76 (max 84) of the 84
materials** — even shallow tiles. So the material-aware sort cannot localize
decodes; *every* chunk pulls ~all 84 textures, and `Clear()` between chunks forces
a full re-decode each chunk (7 chunks → 7×).

### 1b. small2 corroborates (decode-bound), and isolates the effect

| small2 | decode | resample(cluster-loop) | re-decode factor |
|---|---:|---:|---:|
| serial (re-decode per material) | 29 652 ms (**98%**) | 623 ms (2%) | 311 decodes / 16 mats |
| parallel (D, mdop=4) | 5 546 ms (~42% of Phase-1 wall) | — | **48 / 16 = 3×** (3 chunks) |

small2 is *entirely* decode-bound; its resample is negligible (small atlases,
little downsampling). This is why D's decode-amortisation alone made small2 fast,
and why **small2 will barely move under any resample-side idea** (relevant to
Phase-3 A/B: expect the win to show on hd/vlrg, not small2).

### 1c. Cross-depth redundancy is real — but it's redundant DECODE, not resample

From the D logs, summing `natural²` (source pixels sampled) per depth:

| hd depth | tiles | srcMpx | dstMpx | src/dst waste |
|---|---:|---:|---:|---:|
| 0 | 1 | 2165 | 1 | **2064×** |
| 1 | 4 | 2176 | 17 | 130× |
| 2 | 16 | 2278 | 137 | 17× |
| 3 (leaf) | 32 | 2065 | 518 | 4× |

**76% of source reads happen above the leaf** (vlrg: 82%). Shallow tiles decode
full 8192² PNGs only to shrink them to near-nothing. The *cost* of that waste is
decode (full PNG load), not the bicubic.

### 1d. ImageSharp downsample IS source-bound (micro-bench), so capping the resident source is free speed

`Configuration.MaxDegreeOfParallelism=1`, ImageSharp 3.1.5:

| op | ms/op |
|---|---:|
| 2048²→1024² | 15.5 |
| 4096²→1024² | 38.3 |
| 8192²→1024² | 124.2 |
| **8192²→1024² from a prebuilt 1024² mip** | **0.34 (369× faster)** |
| per-call floor (256²→200²) | 0.59 |

Cost scales with **source** pixels, and the per-call floor is trivial (~5 s across
hd's ~8000 calls). So a *capped* resident source also makes the residual 8%
resample cheaper — a tailwind, not the main course.

### 1e. Memory reality (Wall B)

Container = **15 GB**. The live hd bake peaks at **12.8 GB RSS and swaps 3.7 GB** —
it runs at the edge because a chunk holds ~40-48 of the 84 full-res (268 MB)
textures at once. vlrg is worse (why D forces mdop=2). **Memory is the binding
constraint, and the current design spends it on full-res source it never needs at
full res.**

---

## 2. The two independent brainstorms (convergent)

Both passes (mine + Codex) independently ranked **cross-depth source-space reuse**
#1 and both flagged the same trap. Full Codex output preserved in §6.

| Idea | Me | Codex | Post-measurement verdict |
|---|---|---|---|
| Cross-depth **source-space** reuse | A | #3, #5 | **Reframed → #1 (decode-once capped resident)** |
| Cap-space fill, kill natural intermediate | E | #1 | **#2** (Wall B + save cost) |
| Direct fused source→atlas raster (no ImageSharp ctx) | C | #2 | **#3, demoted** (resample is only 8%) |
| Coarser/coalesced UV clustering | D | #4 | **#4** (cuts materials/tile → fewer decodes; fixture-dependent, quality risk) |
| Resample-side mip pyramid | A(orig) | — | **Folded into #1** (capping the resident source already shrinks resample) |
| Skip near-unity / raise single-resample threshold | B | rejected | **Rejected** (9/53 hd tiles near-unity; P7.10 already showed threshold-raise is a trap) |

Both skeptic verdicts agreed: near-optimal for *independent per-tile full-res
sampling*; the structural mover is to stop re-sampling/re-decoding the same source
across the quadtree.

---

## 3. Ranked candidates (mechanism · wall · why · quality · measure · effort)

### #1 — Decode-once, capped-resolution resident source cache  ★ SPIKE THIS

- **Mechanism.** Replace `TexturesCache.GetTexture` (decode full 8192², re-decoded
  every chunk) with: on first access, decode once, **immediately downsample to
  ≤ `--max-atlas-size`**, hold *that* resident for the whole bake; do **not**
  `Clear()` it between chunks. Every tile samples the capped resident copy.
- **Wall it attacks.** **Both.** Wall A: decode 38% + decode-wait ~54% of
  fillAtlases CPU collapse from 7× to 1× (588→84 decodes). Wall B: resident set =
  84 × min(8192,4096)² × 4 = **5.6 GB** (hd) / ~4.5 GB (vlrg) — *less* than the
  current 12.8 GB per-chunk peak, so it removes swapping and may allow **higher
  vlrg mdop**.
- **Why wall-clock, not constant factor.** Converts decode work from
  O(materials × chunks) to O(materials). The redundancy factor (7× hd, ~3× small2,
  likely higher on vlrg's 26 chunks) is eliminated structurally.
- **Quality.** Source is 8192² but **no atlas exceeds the 4096² cap**, so a resident
  cap = max-atlas-size loses **no usable detail** (a material's packed rect ≤ cap ≤
  resident). Pixels differ slightly (two-step downsample 8192→4096→rect vs one-step
  8192→rect; ≈ identical for a good filter). **Not byte-identical → MUST visually
  verify** (roof canary + tour vs D baseline).
- **Cheapest validation.** Already done (§1a): 588 decodes, 347 s decode CPU, 7×
  redundant, capped source loses no usable detail. The spike measures realised
  wall + memory + decode-count drop.
- **Distinct from the graveyard.** C/P7.1 held *full* pyramids (more memory→OOM);
  P7.7 held *full-res* LRU (12 GB→allocator thrash→2.45× slower). **This holds
  *downsampled* sources (less memory than today) and decodes once** — the one
  decode-dedup variant nobody tried.
- **Effort.** M (localised to `TexturesCache` + a flag + skip-Clear gate).
- **Honest ceiling.** ~1.8× on hd (after decode dies, prepare 224 s + save 186 s
  CPU become the floor). Potentially more on vlrg (memory unlock). Not another
  4.68×.

### #2 — Cap-space atlas fill (eliminate the natural-size intermediate)

- **Mechanism.** Single-resample path packs at *natural* size (up to 12288²,
  median 8912²) then one whole-atlas Lanczos to cap. Compose the natural→cap scale
  into each cluster copy and write **directly into the capped atlas** — no giant
  intermediate. (Codex #1.)
- **Wall.** B (kills the largest transient allocation; helps swapping) + trims the
  `saveAtlases` 186 s downsample. Composes with #1 (cap the source *and* the atlas
  fill).
- **Why.** O(naturalArea + capArea) → O(capArea touched). On hd the natural median
  is 8912² vs cap 4096² → ~4.7× less transient area per single-resample tile.
- **Quality.** Per-cluster resample at cap vs one whole-atlas Lanczos changes
  cluster-boundary filtering → **visual verify**.
- **Measure.** Time the single-resample downsample separately (add a `saveMs`
  split) to confirm its share before building.
- **Effort.** M/L. **Order:** after #1 (which already shrinks the natural size when
  the resident source is capped).

### #3 — Direct fused source→atlas raster pass (no ImageSharp context) — DEMOTED

- **Mechanism.** Replace `Clone(Crop.Resize)`→`CopyImage` with one hand-written
  pass sampling resident source → atlas buffer, no intermediate `Image`, no
  processing context. (Codex #2; the postmortem's "untried quadrant".)
- **Why demoted.** Measurement shows resample is only **8%** of fillAtlases and the
  per-call floor is ~5 s total — the prize here is small now that decode is the
  real cost. Revisit only if #1 lands and resample becomes the new floor.
- **Effort.** L. **Quality.** visual verify.

### #4 — Coarser / coalesced UV clustering

- **Mechanism.** Merge near-adjacent same-material UV islands → fewer clusters →
  fewer per-call invocations, and (relevant here) potentially **fewer materials
  with work per tile** → fewer decodes. (Codex #4, my D.)
- **Why measure first.** Could *increase* sampled source area (Codex's trap); and
  it changes UV packing/bleed → quality risk (architecture doc warns of UV-rect
  inflation / packer OOM). Cheap measure: simulate "merge islands within N texels"
  on the cluster logs, report cluster-count drop vs source-area growth.
- **Effort.** M. Quality: **visual verify** (changes packing).

### Rejected (with evidence)

- **Faster resize kernel** — postmortem (6 attempts, dead).
- **Naive keep-resident / skip-Clear** — P7.7 already did this with full-res images
  → 2.45× slower (allocator/GC thrash at 12 GB). #1 avoids this by capping resident
  size to 5.6 GB.
- **Skip near-unity resample** — only 9/53 hd, 9/103 vlrg tiles qualify.
- **Raise single-resample threshold** — P7.10, area+memory trap.

---

## 4. Spike plan (Phase 2)

Spike **#1** on `feat/perf-optim-8-decode-once-cap`:
- Add `--source-cache-cap N` (0 = current behaviour). When set: `GetTexture`
  decodes then downsamples to ≤ N; chunk-`Clear()` skipped for the source cache.
- Bake hd (mandatory) + small2 (mandatory) at the D config; vlrg only if disk/RAM
  safe (abort if it would swap to death or fill disk).
- **Measure:** `actualDecodes` (expect 84), Phase-1 wall, peak RSS, total wall vs D
  baseline (`PERF-D-hlod-*`).
- **Quality gate:** output is NOT byte-identical → render hd + small2 and compare
  the roof canary + tour poses against the D baseline. md5 parity is *not*
  sufficient and not expected.
- **Kill criteria:** if hd wall does not improve ≥1.3×, or RSS doesn't drop, or any
  visible quality regression at close zoom → REJECT and document (a clean negative
  is a fine result).

---

## 5. Honest framing

D already captured the big win (4.68× hd). This rethink targets the *next* lever,
and measurement says it exists but is bounded: **~1.8× on hd, possibly more on
vlrg (memory-bound).** It is worth a spike because (a) it's a real structural
redundancy (7× re-decode, measured), (b) it reduces memory rather than adding it,
sidestepping the wall that killed C/P7.1/P7.7, and (c) the operator asked for an
empirical answer. If the spike underperforms the estimate, the verdict
"near-optimal; decode-once is the only structural lever and it yields ~Nx" is
itself the deliverable.

---

## 6. Codex independent pass (verbatim, for the record)

Codex ranked: (1) cap-space atlas fill / no natural intermediate; (2) direct
source→atlas raster pass; (3) cross-depth **source-space** resample cache (keyed on
source coords, NOT child-atlas packing — "naively downscaling child atlases is a
trap"); (4) cluster coalescing; (5) depth-aware parent synthesis from canonical
source coverage. Skeptic verdict: *"After Candidate D, this pipeline is close to
optimal for CPU + 15 GB RAM + independent per-tile full-res source sampling. The
remaining wall is structural: every tile/depth still rebuilds texture content from
source-coordinate regions… What would move it: change workload shape (reuse
source-space resamples across depths, reduce cluster count, fill directly into
final atlases) or much more RAM."* Both passes converged; the decode-bound
measurement is what reframed the shared #1 into the capped-resident-decode-once
spike above.
