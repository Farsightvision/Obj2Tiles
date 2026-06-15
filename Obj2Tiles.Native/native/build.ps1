# Build meshoptimizer as a shared library for Windows x64.
# Output: Obj2Tiles.Native/native/runtimes/win-x64/native/meshoptimizer.dll
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $here "meshoptimizer"
$out  = Join-Path $here "runtimes/win-x64/native"
$build = Join-Path $here "_build_win-x64"
New-Item -ItemType Directory -Force -Path $out | Out-Null
cmake -S $src -B $build -DCMAKE_BUILD_TYPE=Release -DMESHOPT_BUILD_SHARED_LIBS=ON
cmake --build $build --config Release
$dll = Join-Path $build "Release/meshoptimizer.dll"
if (-Not (Test-Path $dll)) { throw "DLL not found: $dll" }
Copy-Item $dll -Destination $out -Force
Write-Host "OK: $out/meshoptimizer.dll"
