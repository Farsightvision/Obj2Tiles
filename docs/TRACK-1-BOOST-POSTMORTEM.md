# Track 1 — Perf-"Boost" Postmortem (why the optimizations didn't beat baseline)

**Date:** 2026-05-29
**Author:** audit pass (Opus 4.8, max effort), systematic-debugging discipline.
**Scope:** All Phase-5 and Phase-7 HLOD perf-optimization candidates. **Analysis
only — no code shipped.**
**Mandate:** verify every claim against raw timing data; do **not** trust the
prior summaries (they are the thing being audited).

---

## 0. Method & evidence provenance (read this first)

Each claim below is tagged with how it was verified:

- **[LOG]** — re-derived from a surviving `*.bake.log` `[perf:*]` line on disk.
- **[CODE]** — read from current source on this branch.
- **[DOC]** — taken from a per-candidate writeup that lives **only on its feature
  branch** (`git show feat/perf-optim-7N-*:docs/...`). No Phase-7 bake log
  survives on disk, so Phase-7 *timings* are not independently re-runnable from
  artifacts; they are corroborated where possible against surviving D logs + code.

**Reproducibility gap (audit finding 0):** the Phase-7 summary's doc-map lists
`docs/TRACK-1-PERF-OPTIM-P7N-*.md` as if present, but **none of them, nor any
Phase-7 bake log, exist on `feat/perf-optim-7-summary`.** They are recoverable
only via `git show <branch>`. The Phase-7 numbers are therefore as trustworthy as
those branch docs — internally consistent and mechanism-corroborated, but the raw
P7 timings cannot be re-measured from disk. Surviving logs: `HLOD-noExt-perf-*`
(Phase-4 baseline) and `PERF-D-hlod-*` (Candidate D) only.

**Verified foundation (all [LOG]):**

| Fixture | Baseline HLOD wall | `fillAtlases` | fill share | post-D wall | D speedup |
|---|---:|---:|---:|---:|---:|
| small2 | 32 544 ms | 2 610 ms | 8% | 28 418 ms | 1.15× |
| hd | 1 774 243 ms | 1 651 736 ms | **93.1%** | 379 053 ms | **4.68×** |
| vlrg | 2 415 931 ms | 2 238 837 ms | **92.7%** | 1 253 489 ms | **1.93×** |

The bottleneck localization (Phase-1 `fillAtlases` ≈ 93% of hd/vlrg wall) and D's
speedup are **confirmed from raw logs**, not inherited from the summaries.

---

## 1. Every boost attempt, what it targeted, measured result

"Target" = the cost the candidate tried to move. "Result" vs the relevant baseline
(Phase-5 vs serial Phase-4 baseline; Phase-7 vs shipped D).

### Phase 5 (off serial Phase-4 baseline `59f5cd9`)

| # | Candidate | Targeted | hd | vlrg | small2 | Verdict | Verify |
|---|---|---|---:|---:|---:|---|---|
| A | `CopyImage` no-clone | per-copy alloc | — | — | — | REJECT (negative) | [DOC] |
| E | Parallel `AssignMeasuredGeometricError` | a <2% stage | <1% | <1% | <1% | REJECT (Amdahl) | [DOC] |
| C | Mip-pyramid in `TexturesCache` | per-cluster shrink ratio | OOM | OOM | n/a | REJECT (OOM) | [DOC] |
| B | SkiaSharp `Resize` swap | bicubic kernel | −0.7% | −0.3% | +2.2% | REJECT (noise) | [DOC] |
| **D** | **Material-aware parallel Phase-1 batching** | **dispatch model (serialization)** | **4.68×** | **1.93×** | **1.15×** | **SHIP** | **[LOG]** |

### Phase 7 (standalone, off shipped D `414da7e`)

| # | Candidate | Targeted | hd | vlrg | Verdict | Verify |
|---|---|---|---:|---:|---|---|
| P7.1 | C-revised mip pyramid (EvictTexture-tied) | per-cluster shrink ratio | 2.45× slower | OOM | REJECT | [DOC] |
| P7.2 | PhotoSauce MagicScaler | bicubic kernel | 1.81× slower | parity | REJECT | [DOC] |
| P7.3 | TurboJPEG DCT decode | decode | — | — | SKIP (PNG fixtures) | [DOC] |
| P7.4 | NetVips (libvips) | bicubic kernel | 1.85× slower | parity | REJECT | [DOC] |
| P7.5 | fast_image_resize (Rust cdylib, min-FFI) | bicubic kernel | 1.81× slower | OOM | REJECT | [DOC] |
| P7.6 | `Channels<T>` 3-stage pipeline | decode/resize overlap | — | — | SKIP (D already overlaps) | [DOC] |
| P7.7 | Budgeted LRU `TexturesCache` | redundant decode | 2.45× slower | 1.15× slower | REJECT | [DOC]+[CODE] |
| P7.8 | Bicubic coeff cache | kernel setup | — | — | SKIP (ImageSharp API internal) | [DOC] |
| P7.9 | Skip empty `FillAtlases` | dead calls | — | — | NO-OP (already done) | [CODE] |
| P7.10 | Natural-atlas, resize once (raised threshold) | call count | 1.87× slower | OOM | REJECT | [DOC]+[CODE] |

**Tally:** 9 of 10 distinct ideas (A, E, C, B + P7.1/2/4/5/7/10) failed; the lone
shipper, D, attacked something different from all the others — the **dispatch
model**, not the per-unit cost.

---

## 2. Per-REJECT / NO-OP root cause — where the time actually goes

### 2a. The three resize-kernel swaps (P7.2, P7.4, P7.5) — *near-identical* regression

[DOC] hd +81% / +85% / +81%; vlrg parity / parity / OOM. **Three different
SIMD-tuned bicubics (managed PhotoSauce, native libvips, raw-FFI Rust) all land at
the same hd regression.** That identity is the tell: it is structural, not
library-specific.

**[CODE] What each swap actually does** (verified from the diffs, not the summary):

- P7.2 wraps the kernel in **two scalar per-pixel RGBA↔BGRA permute passes** — one
  over the source rect (`ImageSharpBgra32PixelSource.CopyPixels`), one over the
  dest rect — **plus a fresh `MagicImageProcessor.BuildPipeline` per call.**
- P7.4 extracts the source rect **row-by-row into an `ArrayPool` buffer**
  (`NewFromMemory`), FFIs `Resize`, then `WriteToMemory` **allocates a fresh
  `byte[]` per call**, memcpy'd back.
- P7.5 (the "minimum-FFI control") `to_vec()`s the source into a Rust `Vec`,
  resizes, copies out. (v1 even leaked via `.leak()` → 8 GB hd OOM; fixed to owned
  Vec.)

**Root cause:** every swap pays a **marshalling tax** — copy the sub-rect *out of*
`Image<Rgba32>` into the foreign buffer layout, run the kernel, copy the result
*back in* — work the in-process ImageSharp pipeline (`Clone(ctx ⇒ Crop().Resize())`)
never incurs because it reads/writes ImageSharp's own memory directly. That tax is
≈ one extra full pass over the (large) pixel region **plus** per-call FFI/pipeline
construction, ×~thousands of calls. It equals or exceeds the kernel time it was
meant to save → net regression.

**The decisive discriminator (verified, and *buried* by the summaries):
hd regresses but vlrg is parity.** If the bottleneck were per-call overhead on
"tiny rects" (the docs' story), **vlrg — more, similar calls — would regress
too.** It doesn't. Parity on vlrg means the faster kernel there *does* offset the
marshalling, i.e. **the kernel is a real share of vlrg's cost and the swap is a
wash.** Conclusion either way: **the bicubic kernel is not the lever.** Swapping it
cannot win; it can only add a marshalling tax that hurts wherever per-call cost is
relatively high (hd).

> **Audit correction to the P7.2/4/5 docs & Phase-7 summary.** They make
> "clusters are ~256–1024 px, so per-call overhead dominates" the load-bearing
> explanation. That specific rect-size figure is **asserted, never logged**, and
> is contradicted at the aggregate by primary data: [LOG] hd natural-atlas packs
> are **median 8912 px, max 46 525 px** (`grep natural= PERF-D-hlod-hd.bake.log`).
> The per-tile resample *work* is large. The honest, verified reason the swaps
> fail is the **marshalling tax + hd/vlrg asymmetry**, not "small rects."

### 2b. The two amortization/caching swaps (P7.1 pyramid, P7.7 LRU) — D already ate the headroom

Both tried to cut **redundant PNG *decodes*** (keep mips / hot textures resident).
Both regressed. The reason is the single most important finding of this audit:

**[LOG] D's win is *not* pure parallelism. It decomposes as
≈3.7× parallelism × ≈1.33× redundant-decode elimination.**

Derivation (hd, all from logs): serial Phase-1 CPU ≈ wall = 1 752 021 ms. D
Phase-1 CPU-sum = 38 + 207 628 + 931 025 + 175 288 + 2 108 = **1 316 087 ms** at
**3.7× effective parallelism** (1 316 087 / wall 354 035). So D does **0.75× the
*total CPU work* of serial** *and* runs it 3.7-way:
`1752021 / 354035 = 4.95×` Phase-1 = `(1752021/1316087)=1.33 × 3.7`. The work drop
is concentrated in `fillAtlases` (serial 1 651 736 → D 931 025 CPU-ms, **1.77×
less**). That drop is **eliminated redundant decodes**: the serial path's
`!ParallelPhase1 ⇒ EvictTexture` guard re-decodes each ~6072² source PNG every time
a new tile touches it; D's primary-material sort + chunk cache shares the decode
within a chunk.

> Under parallel timing, per-step Stopwatch values *inflate* with contention, so
> the measured 1.77× fill-work drop is a **lower bound** on the true decode saving.

**Therefore P7.1 and P7.7 were chasing a lever D had already pulled.** With no
decode headroom left, they could only add cost:

- **P7.1 (pyramid)** [DOC]+[LOG-in-doc]: (i) **[CODE]-confirmed via mode logs that
  31/53 hd tiles run `mode=single-resample`** — which calls `CopyImage`, *never*
  `CopyImageScaled`, so **the pyramid is untouched on 58% of tiles** yet its build
  cost is still paid; (ii) eager 3-pass `Resize` build per material (16 × ~147 MiB);
  (iii) the extra resident bytes forced **mdop 4→2** (half parallelism) on hd and
  **OOM on vlrg**. Its hd `fillAtlases` CPU went 931 s → **1 351 s** (+420 s of pure
  pyramid-build work, unrecouped) and wall 354 s → 908 s. The 2.45× is mostly the
  forced mdop halving + eager build, not a wrong concept.

- **P7.7 (LRU)** — **this doc's stated root cause is provably wrong.** It blames a
  "~6 ms" O(N) `MaybeEvict` dictionary walk for a **+550 s** (2.45×) hd regression
  — off by ~5 orders of magnitude. [CODE] confirms `MaybeEvict` is a **lock-free
  `foreach`** over ~16 entries per `GetTexture`, i.e. the walk really *is* ~ms —
  which **refutes the doc's own explanation.** The genuine mechanism was never
  isolated. Leading hypothesis (unverifiable from surviving artifacts): with budget
  4096 MiB > hd's 2567 MiB **nothing ever evicts**, so skip-Clear pins the entire
  texture set live, **defeating ImageSharp's `MemoryAllocator` buffer recycling**
  that chunk-Clear enables — every per-cluster crop/resize intermediate must then
  allocate fresh → allocation/GC churn. **Chunk-Clear turns out to be load-bearing
  for the allocator, not just for the RAM cap.** *(Stated as the leading candidate,
  not asserted — no P7.7 log survives to confirm.)*

### 2c. The structural swap (P7.10 natural-atlas) — superlinear area + memory wall

[DOC]+[CODE]. The "one big resize instead of N small" idea is **already in
production** as `_useSingleResamplePath` (threshold `min(maxAtlasSize×4, 12288)`),
firing on 58% of hd / 63% of vlrg tiles [LOG]. P7.10 merely *raised* the threshold
to `×8 / 16384`. That backfires: a single whole-atlas bicubic scales with
**atlas area**, and a 16384² intermediate is **1 GiB** — slower than the
per-cluster sum it replaced (hd +87%) and **OOM on vlrg**. The existing ×4/12288
threshold is already the sweet spot.

### 2d. NO-OP / SKIP / Amdahl rejects (not regressions, but no gain)

- **P7.9 [CODE]** genuinely a NO-OP: the early-exit already exists at
  `MeshT_Hlod.cs:789–800` (`hasWork` check before `GetTexture`).
- **P7.6 / P7.8 / P7.3** SKIP: D already overlaps decode/resize (P7.6); ImageSharp
  `Resize` internals are not forkable without vendoring (P7.8); fixtures are PNG so
  DCT-domain JPEG decode is N/A (P7.3). All reasonable.
- **E [DOC]** real ~1.5–1.9× *stage* speedup on `AssignMeasuredGeometricError`, but
  that stage is [LOG] 10 715 ms hd / 34 091 ms vlrg = **<1% of wall** → invisible at
  pipeline scale. Pure Amdahl. Correct work, irrelevant target.
- **B / A** REJECT at **noise level** (±0.3–2.2%): see §2e.

### 2e. Noise vs real signal (audit Q on measurement)

- **Regressions are real, not noise.** +81% to +145% on hd, reproduced *identically*
  across three independent kernel swaps. Far outside run-to-run variance.
- **"Parity" (P7.2/P7.4 vlrg +1.1%/+2%) is noise** — correctly read as no signal.
- **B (±0.3–2.2%) and E (<1%) are noise / Amdahl-dominated** — "no gain" is real,
  not a missed win.

---

## 3. Cross-cutting: is the true bottleneck even where the attempts aimed?

**Mostly no — after D, the candidates attacked already-pulled levers or
non-bottlenecks.** There are **two distinct walls**, and the summaries blur them
into one ("the kernel is well-tuned"):

**Wall A — hd is compute + per-call bound.** `fillAtlases` is 93% of wall [LOG];
within it the cost is the resample/decode *machinery invoked per cluster*, where
ImageSharp's in-process pipeline is already near-optimal **because it avoids
marshalling.** Evidence: every faster kernel that *adds* marshalling regresses
(§2a); vlrg parity proves the kernel arithmetic isn't the lever. **D won by
dividing this across cores (3.7×) and cutting redundant decodes (1.33×).** Nothing
in Phase 7 attacked the *remaining* per-call machinery without also adding cost.

**Wall B — vlrg is memory bound.** The 15 GB host caps D at mdop=2 → sub-linear
1.93×. **Every candidate that grows resident memory hits this wall:** P7.1 (pyramids)
OOM, P7.5 (Vec churn) OOM, P7.10 (1 GiB atlases) OOM, P7.7 (skip-Clear) slow. On
vlrg the binding resource is **RAM, not CPU** — which is *why* kernel swaps are
"parity" there (they don't touch the actual constraint).

**The Phase-7 target map vs the actual bottleneck:**

| What was attacked | Candidates | Was it the bottleneck? |
|---|---|---|
| Bicubic kernel arithmetic | B, P7.2, P7.4, P7.5, P7.8 | **No** — vlrg parity proves it; swaps only add marshalling |
| Redundant decode | C, P7.1, P7.7 | **Already captured by D's material-aware sort** (1.33×) — no headroom |
| Call count / area | P7.10 | Already tuned (`_useSingleResamplePath`); raising it hits area+memory wall |
| A sub-1% stage | E, A, P7.9 | **No** — Amdahl-irrelevant |
| **Dispatch model (serialization)** | **D** | **Yes — the one real, untaken lever, and it shipped** |

So the headline framing "the boosts failed" is half the story. **One structural
boost (D) succeeded and captured ~all the easily-available win** (hd 29m34s →
6m19s; vlrg 40m → 21m). What *failed* is the long tail of follow-on micro-opts,
and they failed because **they aimed at the kernel (not a bottleneck) or at decode
dedup (already done by D), or they tripped the memory wall.**

---

## 4. What's genuinely left on the table (evidence-backed)

Ranked by *fit to the measured bottleneck*, with honesty about ceilings.

1. **Direct zero-allocation downsampler for the per-cluster path — the one untried
   quadrant.** Every Phase-7 kernel attempt either *swapped* the kernel (adds
   marshalling) or *cached* (no headroom). **Nobody tried replacing ImageSharp's
   per-call `Clone(ctx ⇒ Crop().Resize())` context machinery with a hand-written
   box/triangle resampler that reads `tex` memory → writes `_atlasTexture` memory
   directly**, zero intermediate `Image`, zero processing-context, zero marshalling.
   This is the *only* lever aimed squarely at Wall A's per-call overhead without
   re-incurring it. Note P7.8 (coeff cache) was SKIPPED for the wrong reason —
   the point isn't to reuse ImageSharp's coefficients, it's to **stop using
   ImageSharp's pipeline on this hot path at all.** Estimated upside modest and
   *uncertain* (the kernel arithmetic is real on the big per-cluster tiles); worth
   a spike precisely because it's cheap to prototype and it's the only thing left
   that targets the actual cost. **Verify with a one-tile micro-benchmark before
   committing.**

2. **Shrink the vlrg resident footprint to unlock mdop > 2.** vlrg's lever is RAM
   (Wall B), not the kernel. Options: decode-on-demand per strip instead of full
   `Image<Rgba32>` (4 B/px) resident; a more compact in-cache representation; or
   simply a larger host (the D doc estimates a 32 GB host lifts vlrg to mdop≥3).
   This is the highest-confidence vlrg win because it attacks the *binding*
   constraint. "Gains must come from elsewhere" here means **more RAM or a leaner
   decode**, not a faster kernel.

3. **LOD-aware atlas reuse — the only lever that reduces *total* work.** Today every
   tile resamples from **source**; parent tiles re-resample regions their children
   already resampled, at a coarser scale. Building a parent atlas from its
   **children's already-resampled atlases** (a true texture mip chain shared across
   the hierarchy, used by *both* single-resample and per-cluster paths) would cut
   the resample work itself rather than redistribute it. This is what C/P7.1 *almost*
   did but degenerately (per-material, eager, chunk-disposed). High-effort: it fights
   D's chunk-Clear cadence and needs the pyramid to outlive a chunk. **Unquantified
   — measure the cross-depth resample overlap first** (instrument source-pixel reads
   per material across depths); only pursue if the overlap is large.

4. **Workload-shape change: coarser UV clustering → fewer, larger clusters.** The
   Phase-7 summary's own best idea, and it's sound: amortizes per-call overhead
   structurally (est. hd 1.3–1.5×). Touches the atlas/UV-packing stage, with
   visual-quality risk (the tile-quality gate must hold). Higher leverage than any
   kernel swap because it reduces *call count* without the area blow-up of P7.10.

5. **Output format / hardware (separate benefit class).** KTX2/BC7 output cuts
   `saveAtlases` (5–7% of wall) and ~80% runtime VRAM, but is mostly a *VRAM/runtime*
   win, not a bake-CPU win, and needs a viewer audit. GPU-batched resize is the
   highest theoretical ceiling (3–5×) but the host has no usable GPU (Virtio
   paravirt) — it's an "elsewhere/hardware" answer, not an algorithm one.

### Honest verdict

The **algorithm is near its structural floor for this CPU+RAM envelope.** D already
took the two real levers (parallelism + decode dedup) and hit the headline target.
The bicubic kernel is *proven* not to be the lever (six independent attempts).
Remaining bake-CPU wins are **bounded and uncertain** and must come from: the
per-call *pipeline machinery* (#1), the *memory ceiling* (#2, mostly hardware/decode),
*total-work redundancy* (#3, high-effort, unmeasured), or the *workload shape* (#4).
Anything framed as "make the resize faster" is a dead lever — that has been
established to a high standard of evidence.

---

## Appendix — verified raw numbers

**Phase-1 dominance [LOG]:** hd `fillAtlases=1 651 736ms / PipelineTotal=1 774 243ms`
= 93.1%; vlrg `2 238 837 / 2 415 931` = 92.7%.

**D speedup [LOG]:** hd `379 053ms` (4.68×, parallel:4:chunks=7); vlrg `1 253 489ms`
(1.93×, parallel:2:chunks=26); small2 `28 418ms` (1.15×). tileset.json md5 identical
to baseline on all three [DOC].

**D win decomposition [LOG]:** hd Phase-1 CPU-sum 1 316 087 ms at 3.7× parallel;
serial 1 752 021 ms → D does 0.75× the CPU work (decode dedup) × 3.7× parallel ≈
4.95× on Phase-1.

**Mode split [LOG]:** hd 31 single / 22 per-cluster; vlrg 65 / 38; small2 6 / 15.

**hd natural-atlas edge [LOG]:** count 53, min 699, median 8912, max 46 525,
mean 10 500 px. (Refutes the "256–1024 px small rect" framing at the aggregate.)

**Resize-swap regressions [DOC]:** hd P7.2 685 s / P7.4 699 s / P7.5 685 s vs D
379 s (+81/+85/+81%); vlrg P7.2 +1.1% / P7.4 +2% / P7.5 OOM.

**Config [LOG]:** `AtlasMaxDepthSchedule {0:512,1:1024,2:1536,3:2048,4:4096}`,
`MaxAtlasSize 4096`, source materials hd 16 / vlrg 67, decoded RGBA hd 2567 MiB /
vlrg 3344–4941 MiB, host 8-core / 15 GB.

**Hot path [CODE]:** `MeshT_Hlod.FillAtlases` → per cluster →
`_useSingleResamplePath ? Common.CopyImage : Common_Hlod.CopyImageScaled`;
`CopyImageScaled` = `sourceImage.Clone(ctx ⇒ ctx.Crop(rect).Resize(w,h))` then
`CopyImage` into atlas (ImageSharp 3.1.5).
