#!/bin/bash
# LEGACY flat-grid pipeline zero-regression gate.
#
# WHY: the legacy flat pipeline (the DEFAULT bake — no --hierarchical-lods) is still used by a
# production app and must stay byte-identical. The HLOD byte-identical gate (docs/rc-v3-baseline)
# does NOT exercise the legacy path, so a change to SHARED Library code (TexturesCache, MeshT,
# Common, SplitStage, OctreeSplitter, MeshSanitizer, Box3, Vertex*) could regress legacy unnoticed.
# Run this on any such change. Context: docs/TRACK-1-PHASE8-PROGRESS.md Qg88.
#
# Bakes small2 through the flat pipeline with a FIXED --lods schedule and md5-compares every
# GLB + tileset.json against the committed baseline (baseFP-small2-flat.txt). Output is deterministic
# (two bakes verified byte-identical). Exits 0 = identical, 1 = regression.
set -u
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
FIX="${1:-/home/terrarium/work/small2-fixture/odm_textured_model_geo.obj}"   # fixture: override via $1
LODS='[{"Quality":1.0,"SaveVertexColor":false,"SaveUv":true,"MaxAtlasSize":4096,"JpegQuality":90},{"Quality":0.5,"SaveVertexColor":true,"SaveUv":false,"MaxAtlasSize":2048,"JpegQuality":85}]'
BASE="$REPO/docs/legacy-baseline/baseFP-small2-flat.txt"
OUT=/tmp/legacy-verify-small2
rm -rf "$OUT"
/opt/dotnet/dotnet run --project "$REPO/Obj2Tiles" -c Release -- \
  --input "$FIX" --output "$OUT" --lods "$LODS" \
  --lat 45.46424200394995 --lon 9.190277486808588 --alt 0 -t --threads 4 >/dev/null 2>&1
fp(){ find "$1" -type f \( -name '*.glb' -o -name '*.json' \) | sed "s#$1/##" | sort | while read f; do echo "$(md5sum "$1/$f"|cut -d' ' -f1)  $f"; done; }
fp "$OUT" > /tmp/legacy-verify-small2.fp
if diff -q "$BASE" /tmp/legacy-verify-small2.fp >/dev/null 2>&1; then
  echo "LEGACY small2-flat: IDENTICAL ($(wc -l <"$BASE") files) — legacy pipeline byte-identical ✓"
  rm -rf "$OUT"; exit 0
else
  echo "LEGACY small2-flat: DIFFERS — REGRESSION in the legacy flat pipeline! ✗"
  diff "$BASE" /tmp/legacy-verify-small2.fp | head -20
  rm -rf "$OUT"; exit 1
fi
