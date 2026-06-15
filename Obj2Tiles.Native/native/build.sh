#!/usr/bin/env bash
# Build meshoptimizer as a shared library for the host platform.
# Output:
#   - macOS:  Obj2Tiles.Native/native/runtimes/osx-{arm64,x64}/native/libmeshoptimizer.dylib
#   - Linux:  Obj2Tiles.Native/native/runtimes/linux-x64/native/libmeshoptimizer.so
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
SRC="$HERE/meshoptimizer"

case "$(uname -s)" in
  Darwin)
    ARCH=$(uname -m); [[ $ARCH == arm64 ]] && RID=osx-arm64 || RID=osx-x64
    OUT="$HERE/runtimes/$RID/native"
    EXT=dylib
    ;;
  Linux)
    RID=linux-x64; OUT="$HERE/runtimes/$RID/native"; EXT=so
    ;;
  *) echo "use build.ps1 on Windows"; exit 2;;
esac

mkdir -p "$OUT"
build_dir="$HERE/_build_$RID"
cmake -S "$SRC" -B "$build_dir" -DCMAKE_BUILD_TYPE=Release -DMESHOPT_BUILD_SHARED_LIBS=ON >/dev/null
cmake --build "$build_dir" --config Release -j

if [ -f "$build_dir/libmeshoptimizer.$EXT" ]; then
  cp "$build_dir/libmeshoptimizer.$EXT" "$OUT/"
else
  echo "Could not find built library in $build_dir"; ls "$build_dir"; exit 3
fi
echo "OK: $OUT/libmeshoptimizer.$EXT"
