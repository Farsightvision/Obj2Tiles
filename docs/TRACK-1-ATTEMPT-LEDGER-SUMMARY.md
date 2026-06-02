# Track-1 Phase-8 — Attempt Ledger Summary (what we tried & why: KEPT / REJECTED)

**Purpose:** distilled verdict table over the full Phase-8 evolution so future work does not re-tread dead
levers. Full chronological detail (per-generation, with numbers) lives in `TRACK-1-PHASE8-PROGRESS.md`
(authoritative). Per-candidate code is preserved on the `feat/perf-optim-8-*` branches (code-memory). This
file loses nothing — it indexes.

Champion as of v1.1.2-hlod-rc1 (HEAD ~40a17da): **8 speed wins (G6–G13) ≈ 28× faster bake + PLAIN quality
(operator-locked) + TEXGE-V3 texture-aware geomError default-on**, all operator-confirmed.

## KEPT — shipped wins (mechanism → why it works)
| Win | Mechanism | Evidence |
|---|---|---|
| G6-DILATE (frontier) | multi-source BFS atlas-bleed from non-empty boundary, one seed-scan + bleed waves | the `DilateTicks`-never-accumulated bug hid a 33.6s bucket; fixing it reopened the loop; render-equiv (Gen-6 canary) |
| G7-PARALLEL | adaptive Phase-1 mdop (all cores when resident fits budget, else /2) | byte-identical; the big bin-pack/fill parallelization |
| G8-NOCHUNK | single Parallel.ForEach over tiles when resident fits G2-SAFE budget | byte-identical |
| G9-FASTPACK | route >256-cluster tiles to Skyline packer (was >5000); MaxRects O(F²) was the tail | geometry md5==D; render-verified (Gen-19 fresh-browser) |
| G10/G11 GEOMERR/HAUSDORFF | flat-parallel Hausdorff measure + parallel within-node BVH nearest-dist | byte-identical (FP max order-independent) |
| G12-BUILDTREE | parallel per-depth simplify+partition, serial assembly in depth order | byte-identical (insertion order preserved) |
| G13-HEAVY-FIRST | sort Phase-1 tile loop by face-count desc | byte-identical |
| decode-once cache | decode each source texture once, downsample to cap, hold resident | the dominant texture-stage win |
| G2-SAFE budget | bound resident decoded set at 60% avail RAM; per-chunk Clear when over → graceful re-decode, never OOM | scale-safety; fixtures fit → no clear → full speed |
| single-resample (DEFAULT) | one Lanczos3 whole-atlas downsample vs cumulative per-cluster | per-cluster regressed −1.85/−1.88% sharp (hd/small2); single = pre-opt bar EXACT |
| PLAIN quality config | single-resample + native source (cap≥src) + atlas4096 + 4:2:0, NO levers | operator visual-gate: = pre-opt-HLOD quality EXACTLY, ~28× faster, ~same/smaller bytes |
| TEXGE-V3 (DEFAULT-ON) | textureGE = metersPerTexel×(maxSSE/pMax), effective=max(meshGE,textureGE), uncapped, self-calibrating | operator visual-gate PASSED all 3 fixtures at default params; fixes SSE-16 LOD blur; opt-out HLOD_TEXGE_V2=0 |

## REJECTED — dead levers (DO NOT RE-TRY without a NEW reason)
| Lever | Verdict | Why dead |
|---|---|---|
| **4:4:4 chroma** (HLOD_JPEG_444) | OPERATOR-REJECTED (visual gate) | metric showed +17/+52/+71% chroma_edge but INVISIBLE at operator's real zooms; +15-18% bytes for nothing. Color-lever signal ≠ perceptual benefit. |
| **lanczos8 kernel** (HLOD_RESAMPLE_KERNEL) | OPERATOR-REJECTED (visual gate) | +1.9% luma sharp metric, but no visible difference on operator's models |
| **Compand linear-light** (HLOD_COMPAND) | OPERATOR-REJECTED (visual gate) | +2.7% luma metric, invisible to operator |
| **JPEG quality >90** | REJECT (clean measure) | q95 luma −0.94% SOFTER (metric rewards q90's ringing) + over byte budget; chroma gain dominated by... nothing visible anyway. (NOTE: the Qg13 "+4.71% sharper" was FABRICATED + retracted — clean re-measure had opposite sign.) |
| **atlas downsize 1536/2048** | OPERATOR-REJECTED (visual gate) | visibly too SOFT at operator's zooms; ship 4096 |
| **C3 per-cluster resample** | REJECT | quality regression vs single-resample (above) |
| **aggressive cap-4096 < source** | REJECT (for quality) | caps source pre-atlas-sizing → −15.6% mpix on hd (14/53 tiles lose resolution); cap must be ≥ source res |
| **on-disk decoded-RGBA cache** (HLOD_DECODE_CACHE_DIR) | DEAD (tested) | no benefit; decode is unfilter/convert-bandwidth-bound not inflate-bound |
| **SkiaSharp decode/encode** (HLOD_SKIA_DECODE, G4) | REJECT | byte-identical but not faster (marshalling-confounded); pure-managed ImageSharp wins |
| **native inflate / GPU decode** | OPERATOR-LEVEL | decode is pixel-bandwidth-bound (unfilter+convert ~67%, inflate ~33%); only GPU or reduced-res helps |
| **TEXGE maxAmp cap** (per-model knob) | REMOVED (Qg57-58) | capping textureGE at meshGE×N is model-dependent (meshGE 6 vs 676 across models) → needed per-model MAXAMP; uncapping = self-calibrating |
| **TEXGE dGain=1.25** magic gate | REMOVED | replaced with physics check (minChildMpt<parentMpt); the 1.25 constant was model-dependent |
| auto-extend depth | BANNED | explodes small2 21→15,505 tiles |

## CONVERGENCE / META findings
- In-process speed floor confirmed across categories (algorithms, pre-decode, GC, runtime config); decode is
  pixel-bandwidth-bound. Further big speedups = operator-level (GPU / native dep / relax constraint).
- **Quality-max answer: quality levers don't help THIS content** (photogrammetry at operator zooms). The win is
  pure speed at unchanged (pre-opt-HLOD) quality.
- **Verify LOD/selection via DETERMINISTIC GE/SSE math from tileset.json (nearest-point dist), NOT render
  tileVisible-counts** (they double-count tiles mid-REPLACE-transition → false readings; cost 3 wrong commits).
- **The metric SCREENS; the operator's VISUAL GATE on real content+zoom DECIDES quality.** Hold render-gated
  levers (chroma/kernel/atlas-size/sharpen) for the operator, don't auto-ship on a metric.
- LOD-correctness audits (Qg69-71): bbox-tightness + GE-invariants CLEAN on all 3 fixtures.
- Production: ships KTX2 (not JPEG); speed/geom/parallel wins are encode-agnostic + transfer; KTX2 encode is
  ~9× slower than JPEG + OOM-prone on 15GB → the Objective-2 work (speed + graceful degradation).
- INTEGRITY: 3 fabrication slips this evolution (vlrg Qg8, JPEG Qg13, false-channel Qg15) ALL retracted; the
  discipline (file-verify every number, sentinel-wrap, build before bakes) held the rest of the way.
