#!/usr/bin/env bash
# Cross-publish the Windows x64 single-file build from Linux/macOS.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="${1:-$root/artifacts/win-x64}"

dotnet publish "$root/src/NetSwitch.App/NetSwitch.App.csproj" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o "$output"

echo "Published to $output"
