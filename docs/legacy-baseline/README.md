# Legacy flat-grid pipeline — zero-regression baseline

The **legacy flat-grid pipeline** (the DEFAULT bake path, no `--hierarchical-lods`) is still used by a
production app and must stay **byte-identical**. The HLOD baseline (`docs/rc-v3-baseline/`) only covers the
`--hierarchical-lods` path, so a change to **shared** Library code can regress legacy without the HLOD gate
noticing — this happened once (ledger Qg88: an Obj3 change to the shared `TexturesCache` altered the legacy
texture-load failure path; reverted). This baseline closes that gap.

## What it protects
Any change touching code reached by BOTH pipelines: `TexturesCache`, `MeshT`, `Common`, `SplitStage`,
`OctreeSplitter`, `MeshSanitizer`, `Box3`, `Vertex2/3`, `StagesFacade`, `ConversionStage`, etc. (Pure HLOD
files — `Hierarchical*`/`Conformal*` stages, `MeshT_Hlod`, `Common_Hlod` — do NOT affect legacy.)

## Run the gate
```
bash docs/legacy-baseline/verify-legacy-flat.sh
```
Pass = `LEGACY small2-flat: IDENTICAL (81 files) ✓` (exit 0). `DIFFERS` (exit 1) means the legacy flat
pipeline's output changed — investigate before merging/cherry-picking. Override the fixture with `$1`.

## Baseline
`baseFP-small2-flat.txt` — md5 of every GLB + tileset.json from a small2 flat bake with the FIXED schedule
baked into the script (`--lods` Q={1.0, 0.5}, MaxAtlasSize={4096, 2048}, JpegQuality={90, 85};
`-t --lat 45.464… --lon 9.190…`). Output is deterministic (verified: two bakes byte-identical; the flat
pipeline writes no nondeterministic sidecar, unlike HLOD's `report.json`).

After an **intentional** legacy change, re-capture the baseline: run the script to see the diff, confirm it's
expected, then regenerate `baseFP-small2-flat.txt` from the fresh bake's fingerprint (the script's `fp()`
function shows the exact md5 listing). small2 exercises the shared decode/split/atlas/convert paths; extend to
hd/vlrg if you want broader coverage.
