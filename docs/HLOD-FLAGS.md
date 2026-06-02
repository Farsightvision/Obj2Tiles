# HLOD Pipeline — Flag Reference (rc-v3 / v1.1.2-hlod-rc1)

After the Phase-8 de-overengineering cleanup (Obj1), the HLOD bake exposes a **minimal** flag surface. All
experiment-only A/B toggles whose verdict was final have been removed (folded to the shipped path); the
operator-rejected quality levers were deleted entirely. What remains:

## Production CLI knobs (genuine, documented, KEPT)
| Flag | Default | Purpose |
|---|---|---|
| `--source-cache-cap N` | 0 (off) | decode-once: decode each source texture once, downsample longest edge ≤ N px, hold resident. Set ≥ source res for full quality. 0 = legacy re-decode-per-chunk. Drives the G2-SAFE resident budget. **Memory note (Obj2b):** a higher cap (e.g. 8192 native) → bigger per-tile working set → tighter Phase-1 RAM margin on memory-bound hosts (the bake clamps Phase-1 mdop to fit + never OOMs, but runs slower); lower the cap for more headroom (smaller decoded sources). |
| `--max-atlas-size N` | (config) | per-tile atlas edge cap (px). Production = 4096 (operator visual-gated; smaller = too soft). |
| `--no-ktx2` | off | JPEG-only HLOD (skip KTX2/Basis). All other HLOD features stay on. |
| `--quantize-glbs` | off | gltfpack KHR_mesh_quantization (needs gltfpack on PATH). |
| `--ktx2-quality N` | 8 | gltfpack KTX2/ETC1S quality (1-10). |
| `--threads N` | cores | parallelism (Phase-1 mdop + Phase-2). |

## Env knobs (KEPT — advanced/safety, not everyday)
| Env | Default | Purpose |
|---|---|---|
| `HLOD_TEXGE_V2` | on (`!="0"`) | texture-aware geometric error (TEXGE-V3), default-ON (operator visual-gate confirmed). Set `=0` to disable (safety/legacy: restores pre-TEXGE geomError). |
| `HLOD_TEXGE_PMAX` | 0.5 | TEXGE max acceptable projected texel size in screen px (Nyquist 0.5). The one principled TEXGE quality dial; lower = more aggressive refinement (more margin), higher = less. NOT per-model. |
| `HLOD_CACHE_BUDGET_MIB` | 60% avail RAM | G2-SAFE resident-decoded-texture budget override (bytes). Bound peak RAM on memory-constrained hosts. |
| `HLOD_JPEG_QUALITY` | 90 | atlas JPEG quality (1-100). q90 is the shipped default; >90 not beneficial (Track-1 finding: rewards ringing, +bytes). |
| `HLOD_FASTPACK_THRESHOLD` | 256 | G9 cluster-count threshold to route a tile to the Skyline packer (vs MaxRects). Tuning; rarely changed. |
| `HLOD_KTX2_WORKERS` | adaptive | Obj2 graceful-degradation override: pins the number of concurrent gltfpack KTX2/ETC1S encodes. Default = memory-adaptive (see below). Set to a positive int to override when the heuristic misjudges a host (clamped to `--threads`). Only affects the `--quantize-glbs` + KTX2 path. |

## KTX2 Phase-3 encode — speed + graceful degradation (Obj2 + Obj2b)
Only relevant with `--quantize-glbs` and KTX2 enabled (i.e. NOT `--no-ktx2`). The JPEG default path is untouched
(byte-identical to baseline). gltfpack is auto-detected (PATH → `$HOME/bin` → `$HOME/.local/bin` → /usr/local/bin
→ /usr/bin), so KTX2 "just works" without `--gltfpack-path`; a gltfpack built WITHOUT BasisU (which would silently
emit JPEG) is caught by checking the converted GLB for `KHR_texture_basisu` and warned about. Behaviors:
- **Free the Phase-1 cache before Phase-3.** gltfpack reads the GLBs from disk, not the decode-once cache, so the
  (large) Phase-1 resident cache is evicted before Phase-3 — otherwise it starves the encode workers' live-RAM
  budget (measured: 1 worker / 30-min vlrg before this; 5 workers / 10-min after).
- **Memory-adaptive worker cap (default).** Each gltfpack ETC1S process holds ~0.9 GiB for a 4096²-capped atlas
  (MEASURED). Default workers = `clamp(0.55 × LIVE-MemAvailable / perWorker, 1, --threads)`, `perWorker =
  1300 MiB × (min(maxAtlasEdge, --max-atlas-size)/4096)²`. On the 15 GB box this runs ~5 workers; relaxes to the
  full `--threads` on roomy hosts. Largest-atlas-first scheduling. Override with `HLOD_KTX2_WORKERS`.
- **`-tj 1` (always, when KTX2 on).** Each gltfpack process uses ONE texture-encode thread — without it, N parallel
  processes each spawn hardware-concurrency BasisU threads → N×cores oversubscription. ETC1S output is bit-identical
  regardless of thread count (pure scheduling, no quality loss).

> ✅ **VERIFIED (Obj2b, 15 GB box, gltfpack-with-BasisU auto-detected):** all 3 fixtures convert to valid KTX2 q10
> — small2 21/21 (76 s), hd 53/53 (566 s), vlrg 103/103 (602 s), all 5 Phase-3 workers; peaks Phase-1-bound. NOTE:
> the real OOM risk is Phase-1 texture-fill, NOT Phase-3 — see the Phase-1 graceful-degradation memory model in
> HLOD-ARCHITECTURE.md (vlrg native peaks ~13 GiB on 15 GB; lower `--source-cache-cap` for more margin).

## REMOVED in Obj1 cleanup (final verdicts — see TRACK-1-ATTEMPT-LEDGER-SUMMARY.md)
- **Experiment A/B toggles folded to shipped path** (output-identical): `HLOD_SINGLE_RESAMPLE`/`HLOD_PER_CLUSTER`
  (single-resample default + size-based per-cluster fallback), `HLOD_LEGACY_DILATE` (frontier dilation; dead
  `DilatePingPong` deleted), `HLOD_FORCE_CHUNK` (`_predecodeFits` gate kept), `HLOD_GEOMERR_PERDEPTH` /
  `HLOD_GEOM_SERIAL` / `HLOD_BUILDTREE_SERIAL` (parallel paths, output-identical), `HLOD_TILE_MATSORT`
  (heavy-first unconditional).
- **Operator-rejected quality levers DELETED** (code + flags): `HLOD_COMPAND`, `HLOD_RESAMPLE_KERNEL`/lanczos8,
  `HLOD_JPEG_444` — all visual-gate-rejected (no visible benefit on real photogrammetry).

## NOTE for operator review (Obj1 judgment call)
`HLOD_TEXGE_MAXAMP` (default 0 = uncapped) and `HLOD_TEXGE_DGAIN` (default 1.0 = physics gate) are TEXGE-V3
tuning overrides that the self-calibrating design made INERT at their defaults (the per-model cap was REMOVED
in Qg57-58 — that was the whole point of self-calibration). They were NOT on the Obj1 removal list, and they
still function if set (opt-in tuning), so they're KEPT for now — but they're vestigial under the shipped
self-calibrating path. **Candidate for removal if you want the absolute-minimal surface** (they're the same
family as the kept pMax dial; removing them would force pMax as the sole TEXGE knob). Flagged, not removed
unilaterally (outside the explicit Obj1 scope).
