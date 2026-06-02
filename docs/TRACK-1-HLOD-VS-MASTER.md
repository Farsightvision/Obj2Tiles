# Track 1 — HLOD-noExt vs MASTER-prod structural comparison

**Date:** 2026-05-28
**Branch:** `release/v1.1.0-beta-nomip` at `55c3521` (Track 4 only; no R1/R2 auto-depth code in tree).
**User visual gate MET (2026-05-28) on HLOD-noExt across all 3 fixtures via Mykyta's demo viewer** — Cesium A/B harness skipped per operator confirmation that visual quality is acceptable.

**Rebake note (2026-05-28):** master rows relabeled `MASTER-prod` and re-baked sequentially (vlrg → hd → small2) to match Mykyta's Python-wrapper production defaults (4 LODs: Q={1.0, 0.8, 0.5, 0.1} / jpegQ={95, 90, 85, 70}). The original `MASTER-allLODs` rows used the same config; the earlier "5 LODs" entry was a tileset-walk off-by-one in the quant tooling (root walk depth N for an N-LOD flat-grid tileset is just N — there's no implicit extra root level). HLOD-noExt rows are unchanged (user-verified).

## Configuration

Both bake configurations share the operator-specified flags:
`--max-vertices 1000 --max-atlas-area 2147483647 --threads 1 --y-up-to-z-up --max-atlas-size 4096`, georef `--lat 45.46424200394995 --lon 9.190277486808588 --alt 0`, no KTX2 (JPEG textures).

| | MASTER-prod (flat-grid, 4 LODs) | HLOD-noExt (--no-adaptive-extend) |
| --- | --- | --- |
| Pipeline flag | _default_ (Track 4 default) | `--hierarchical-lods --no-adaptive-extend --no-ktx2` |
| LOD schedule | operator `--lods` JSON: Q={1.0, 0.8, 0.5, 0.1}, jpegQ={95, 90, 85, 70} | auto-derived 3-tier {1.0, 0.7, 0.5} per HLOD pipeline (LOD count varies by fixture) |
| Output structure | `LOD-{0..3}/*.glb` flat dirs | `content/{depth}/*.glb` tree |

## Per-fixture quantitative comparison

| Fixture | Bake | Tiles | Tree depth | LOD count | Disk (MB) | Bake wall | Atlas p50 (px) | Atlas p95 (px) | Atlas max (px) | Total decoded RGBA (MB) | Max leaf edge (m) |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| **small2** | HLOD-noExt  | **21**  | 2 | 3 | **39** | **33 s** | 2048 | 4096 | 4096 | **453.6** | — |
|             | MASTER-prod | 364     | 4 | 4 | 53     | 30 s     | 256  | 1024 | 2048 | 502.2     | — |
| **hd**     | HLOD-noExt  | **53**  | 3 | 4 | **211**| **29 m 48 s** | 4096 | 4096 | 4096 | **2 567.0** | 58.52 |
|             | MASTER-prod | 292     | 4 | 4 | 563    | 5 m 26 s | 1024 | 4096 | 4096 | 6 395.6   | — |
| **vlrg**   | HLOD-noExt  | **103** | 4 | 5 | **250**| **40 m 24 s** | 4096 | 4096 | 4096 | **4 941.2** | 116.18 |
|             | MASTER-prod | 368     | 4 | 4 | 565    | 10 m 33 s | 2048 | 4096 | 4096 | 10 919.2 | — |

_Notes on missing data:_
- `Max leaf edge` is reported by `BuildReport` for the HLOD pipeline only; the flat-grid path doesn't emit that gate.

### HLOD ÷ MASTER-prod ratios

| Metric | small2 | hd | vlrg | Direction |
| --- | ---: | ---: | ---: | --- |
| Tile count | **0.058** (−94%) | **0.182** (−82%) | **0.280** (−72%) | HLOD wins big |
| Disk MB | 0.74 (−26%) | **0.37** (−63%) | **0.44** (−56%) | HLOD wins (less on small2) |
| Decoded RGBA MB | 0.90 (−10%) | **0.40** (−60%) | **0.45** (−55%) | HLOD wins (≈disk ratio) |
| Atlas p50 | 8.0× larger | 4.0× larger | 2.0× larger | HLOD packs denser per-tile (lower master spreads detail) |
| Atlas p95 | 4.0× larger | 1.0× (same) | 1.0× (same) | At p95 both saturate at cap=4096 |
| Bake wall | 1.10× slower | **5.49× slower** | **3.83× slower** | HLOD significantly slower; per-tile atlas pack dominates |

## Verdict

- **HLOD-noExt delivers a 4–17× tile reduction** with proportional disk-MB and decoded-RGBA savings on hd/vlrg (−55 to −63%). On small2 the disk/RGBA savings are smaller (−10 to −26%) because each HLOD tile carries a larger per-tile atlas — the byte count per tile is mostly conserved at small2's depth-2 root.
- **Atlas p50 4–8× larger under HLOD.** The HLOD pipeline packs whole-region content into per-tile atlases, where master's flat-grid path splits detail across many small atlases. Larger per-tile atlases mean fewer HTTP requests at runtime (good for streaming) but mean each tile decodes a larger working set into VRAM.
- **Atlas p95 saturates the 4096² cap on hd and vlrg under both pipelines.** Master hits the cap on its top LOD (Q=1.0); HLOD hits it across many leaves because `--no-adaptive-extend` doesn't deepen the tree where density demands it. This is the explicit density-vs-tile-count trade Codex's Option F surfaced. **Mykyta's visual gate confirms the density trade is acceptable in his demo viewer (2026-05-28).**
- **HLOD bake wall is significantly longer**: 1.1× on small2 (33 s vs 30 s), **5.5× on hd** (29:48 vs 5:26), **3.8× on vlrg** (40:24 vs 10:33). Per-tile atlas pack at `--threads 1` is the dominant cost. Tunable; not a structural blocker but a real production-pipeline cost.
- **Tree depth is comparable** (HLOD picks via `OptimalDepthsClosedForm`; master fixed at depth 4). hd and small2 HLOD picks one shallower (3 vs 4 and 2 vs 4 respectively).
- **Max leaf edge** reported only by HLOD: 58.52 m on hd, 116.18 m on vlrg. Both above the flat-grid 1.5× source-max-edge gate but expected when simplification + crease-vertex protection leave longer edges than the source max — not a splitter bug (per the WARN line in `bake.log`).

### Recommendations

- **Ship HLOD-noExt as the default production output** for the texture-heavy fixtures (hd, vlrg) where the −60%/−55% disk+RGBA savings dominate over the per-tile atlas-density trade. Mykyta's visual gate confirms the quality is acceptable.
- **`ExtendAdaptive` is now opt-in / experimental.** Operator decision 2026-05-28: the production ship path is `--no-adaptive-extend`. The auto-depth tuning work (`d968ecc` geometry predicate, R1 N=2 multiplier, R2 per-material, R2 form (f) per-cluster) remains in tree but is NOT invoked by default flags. Full investigation chain preserved at `obj2tiles-lab/investigations/AUTODEPTH-*.md` for future revisit; Track 2.5 is closed as a dead lever.
- **Capture bake.log adjacent to output dir** going forward (not inside) — `Program.cs:67` orphans logs inside the output. Pattern: `--output X --output-log X.bake.log` (caller-side via shell redirect; no code change needed).
- **Reduce bake wall under HLOD** by raising `--threads` once Phase-1 atlas pack is parallel-safe on the target fixture. Currently the operator forced `--threads 1` for determinism; production can relax.

### Pending

- **Track 3** (perf comparison flat vs HLOD via Cesium): OPEN pending operator direction.
- **Track 2.5** (auto-depth code): **ABANDONED** as a dead lever per operator 2026-05-28. R2-form-(f) Option I/II/III decision no longer relevant. Branch stays at `55c3521`.

## Files

- Source bakes:
  - `tests/visual/out/{HLOD-noExt,MASTER-prod}-{small2,hd,vlrg}/`
  - Adjacent logs: `tests/visual/out/{HLOD-noExt-{small2,hd,vlrg},MASTER-prod-{small2,hd,vlrg}}.bake.log`
- Quantitative dump: `/tmp/t1-quant.json` (raw per-fixture/per-bake measurements)
- Atlas histograms: each `<bake>/atlas-dims.txt` (W×H buckets per LOD via `tmp/atlas_dims2.py`)
- Investigation chain (auto-depth context): `obj2tiles-lab/investigations/AUTODEPTH-{OVERSPLIT-INVESTIGATION,CONFLICT-FINDING,DISTRIBUTION-ANALYSIS,R2-FINDING,R2-FORM-F-FINDING,EXTERNAL-RESEARCH-{claude,chatgpt}}.md`
