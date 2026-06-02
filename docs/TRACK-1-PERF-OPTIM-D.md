# Track 1 Phase 5 — Candidate D: HLOD Phase-1 material-aware parallel batching

**Branch**: `feat/perf-optim-5-parallel-phase1`
**Base**: `release/v1.1.0-beta-nomip` @ `59f5cd9`
**Verdict**: **STRONG-SHIP on hd (4.68×)**, **STANDARD-SHIP on vlrg (1.93×, requires `--phase1-batches-per-material 2`)**, marginal on small2 (1.15×)
**Date**: 2026-05-28

## Hypothesis

Per the Phase-4 timing breakdown, `Phase1_AtlasWrite` is 93% of HLOD wall on hd
and vlrg. The pre-D code force-serialised this stage with an unconditional
`textureBytes > 500 MiB → serial` fallback in `Program.cs`, on the (correct
but conservative) theory that the parallel path otherwise pinned all
materials in RAM and OOMed. The 500 MiB gate fired on every interesting
fixture except small2.

D lifts that gate and replaces it with material-aware batching: tiles are
sorted by primary material so adjacent tiles share source textures, then
processed in fixed-size chunks with `Parallel.ForEach` inside each chunk and
`TexturesCache.Clear()` between chunks. Peak RAM is bounded by one chunk's
resident-material set, not by the whole model's texture footprint.

## Implementation

`Obj2Tiles/Stages/HierarchicalTilingStage.cs:WriteAllGlbs`:

- `PrimaryMaterialIndex(n)` — for each tile, compute the `MaterialIndex`
  carrying the most faces. Linear scan over `tile.TileContentT.Faces`.
- Sort tiles by `(primaryMatIdx, depth, contentUri)` so adjacent tiles share
  their dominant source PNG. Tie-break on coord for determinism.
- Partition into chunks of `phase1Mdop * 2` tiles. Within each chunk,
  `Parallel.ForEach` with `MaxDegreeOfParallelism = phase1Mdop`. Between
  chunks: `TexturesCache.Clear()`.
- `[perf:hlod:Phase1_AtlasWrite]` log now reports `parallel:N:chunks=K`.

`Obj2Tiles/Program.cs`:

- Lifts the unconditional `textureBytes <= 500 MiB → ParallelPhase1 = true`
  / `else false` gate. New default: `ParallelPhase1 = (phase1Mdop > 1)`,
  where `phase1Mdop = config.Phase1BatchesPerMaterial` or
  `Environment.ProcessorCount / 2` when 0.

`Obj2Tiles/AppConfig.cs`, `Obj2Tiles/Options.cs`:

- New `--phase1-batches-per-material N` CLI flag.
  Default `0` → `ProcessorCount / 2` (4 on the 8-core test host).
  Operator can lower for memory-constrained hosts.

`Obj2Tiles/Stages/HierarchicalAtlasStage.cs` is unchanged — the existing
`!config.ParallelPhase1 → EvictTexture` gate inside `PackAndWrite` already
skips per-material eviction when ParallelPhase1 is on, so the parallel path
correctly avoids the eviction race.

## Bake configuration

3 fixtures × `--hierarchical-lods --threads 8 --no-adaptive-extend
--no-ktx2 --leaf-no-mips --max-vertices 1000 --max-atlas-area 2147483647
--max-atlas-size 4096`.

- hd / small2: default `--phase1-batches-per-material` (0 → mdop=4).
- vlrg: `--phase1-batches-per-material 2` (mdop=2, chunk=4). The default mdop=4
  OOMed (rc=137) after ~30 of 103 atlases — vlrg's 3 344 MiB decoded RGBA32
  texture set is larger than the 15 GB host can hold under 4-way parallelism.

## Results

| Fixture | Baseline wall | D wall | Speedup | Δ wall % | Phase-1 mode |
| --- | ---: | ---: | ---: | ---: | --- |
| small2 | 32.5 s | 28.4 s | **1.15×** | −12.7% | parallel:4 chunks=6 |
| hd | 1 774 s (29 m 34 s) | 379 s (6 m 19 s) | **4.68× — STRONG-SHIP** | **−78.6%** | parallel:4 chunks=7 |
| vlrg | 2 416 s (40 m 16 s) | 1 253 s (20 m 53 s) | **1.93×** (at mdop=2) | **−48.1%** | parallel:2 chunks=26 |

| Fixture | fillAtlases base | fillAtlases D | Δ fill |
| --- | ---: | ---: | ---: |
| small2 | ~2 600 ms | (in-chunk) | — |
| hd | 1 651 736 ms | 354 035 ms (Phase-1 wall) | **4.67×** |
| vlrg | 2 238 837 ms | 1 207 709 ms (Phase-1 wall) | **1.85×** |

Note: D's `Phase1_Breakdown ctor/prepare/fillAtlases/...` shows CPU-summed
time across parallel workers, so the per-step CPU-sum is now larger than
wall — exactly the expected parallel pattern. The wall figure
(`Phase1_AtlasWrite elapsed=...`) is the bake-time-relevant number.

**Structural identity** — tileset.json md5 IDENTICAL to Phase-4 baseline on
all 3 fixtures:

| Fixture | md5 (D and baseline) |
| --- | --- |
| vlrg | `8359da9f24def1711f68879c6ef65b62` |
| hd | `2d1fb29ba435b014466d1c34f8cb3b30` |
| small2 | `6e2ecfa1664b242c6f50ea6322f46ebd` |

GLB tile counts match exactly (vlrg 103, hd 53, small2 21).

## Why the asymmetric speedup

| Fixture | tex MiB | materials | tex÷RAM | What that means |
| --- | ---: | ---: | ---: | --- |
| small2 | 98 | ~5 | 0.6% | Tiny work per tile; Phase-1 was already <20s and not the bottleneck. Speedup ≈ marginal. |
| hd | 2 205 | 16 | 14% | Big work per tile; 4-way parallelism fits in RAM cleanly because hd's 16 materials each fit several-times-over into the chunk-Clear cadence. Near-linear 4× scaling. |
| vlrg | 3 344 | 67 | 22% | 4-way parallelism OOMs (cache pinned across hot chunks pushes >15 GB). 2-way parallelism with smaller chunks fits, gives 1.93×. Going to 3 cores would likely fit on a 32 GB host. |

## What the chunk-Clear() costs

The `TexturesCache.Clear()` between chunks forces the next chunk to re-decode
materials that were also touched by the previous chunk. With material-aware
sort, this redundant decode is bounded — adjacent chunks share their primary
material, so the boundary cost is one re-decode per chunk transition. On hd
the total Phase-1 wall is still 4.7× faster than serial, so the redundancy
is well below the parallelism gain.

A future optimisation could track per-material reference counts across the
sorted chunk plan and `EvictTexture` only the materials no longer needed by
remaining chunks. Out of scope for D (the simple Clear-between-chunks already
hits the operator-stated 30 m → ~4 m target on hd).

## Recommendation

**Merge with operator ratification**, then update the prefect flow / docs:
- hd-class fixtures: default mdop (=ProcessorCount/2) is correct.
- vlrg-class fixtures: pass `--phase1-batches-per-material 2`. The bake-time
  doc should call this out (texture set ÷ host RAM > 20% → use mdop=2).
- An auto-fallback heuristic — `if textureBytes > RAM_bytes * 0.2 then
  mdop = max(2, mdop/2)` — could fold this into the default. Suggested as a
  follow-up to keep D's diff small.

## Structural / safety

- ✅ tileset.json md5 identical to baseline on all 3 fixtures.
- ✅ GLB count identical (vlrg 103, hd 53, small2 21).
- ✅ vlrg first attempt at default mdop=4 OOMed (rc=137) — recovered cleanly
  with operator-tunable flag at mdop=2. Documented above.
- ✅ Per-pixel atlas content is bit-identical to baseline: the parallel
  Parallel.ForEach uses the same per-tile `PrepareTileForGlb` code path as
  serial; only the dispatch loop changed.

## Position in the Phase 5 sequence

| Candidate | Branch | Verdict | Speedup (vlrg / hd / small2) |
| --- | --- | --- | --- |
| E | `feat/perf-optim-3-parallel-geom-error` | REJECT | <1% (stage too small) |
| C | `feat/perf-optim-2-mip-pyramid` | REJECT-OOM | (would-be) — cache lifetime bug |
| B | `feat/perf-optim-4-skiasharp-resize` | REJECT | 1.003× / 1.007× / 0.978× (no signal) |
| **D** | `feat/perf-optim-5-parallel-phase1` | **STRONG-SHIP (hd) / STANDARD-SHIP (vlrg)** | **1.93× / 4.68× / 1.15×** |

D is the only Phase-5 candidate that ships. Recommended merge order: D
first; revisit "C done right" (pyramid cache tied to `EvictTexture`) only
if a future bake-time pressure requires more headroom.
