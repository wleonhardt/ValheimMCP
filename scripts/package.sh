#!/usr/bin/env bash
# Build ValheimMCP in Release and assemble the release zips in dist/.
#
#   dist/ValheimMCP-v<core version>.zip   Thunderstore package: the core plugin (BepInEx/plugins/ValheimMCP/)
#   dist/HubnerExt-v<ext version>.zip     the Hubner extension: client and server DLLs (BepInEx/hubner-ext/), example configs, Python client
#
# Run this locally: compiling needs Valheim's managed assemblies (ext/lib or -p:LibDir via LIBDIR=...), which are NOT redistributable and so are
# not available on a stock CI runner. The versions are read from the project files (single source of truth): <Version> in ValheimMCP.csproj for the
# core (also synced into the packaged manifest.json) and in ext/Version.props for the extension.
# Usage: scripts/package.sh [--core-only]
set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$(pwd)"

# portable (BSD and GNU): no grep -P
version_of() { sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$1" | head -1; }

VERSION="$(version_of ValheimMCP.csproj)"
EXTVERSION="$(version_of ext/Version.props)"
if [[ -z "${VERSION}" || -z "${EXTVERSION}" ]]; then
  echo "error: could not read <Version> from ValheimMCP.csproj / ext/Version.props" >&2
  exit 1
fi

DOTNET_ARGS=(-c Release --nologo -v q)
[[ -n "${LIBDIR:-}" ]] && DOTNET_ARGS+=("-p:LibDir=${LIBDIR}")

echo "Packaging ValheimMCP core v${VERSION}"
dotnet build ValheimMCP.csproj "${DOTNET_ARGS[@]}"
DLL="${ROOT}/out/ValheimMCP.dll"
if [[ ! -f "${DLL}" ]]; then
  echo "error: ${DLL} was not built" >&2
  exit 1
fi

STAGE="$(mktemp -d)"
trap 'rm -rf "${STAGE}"' EXIT
mkdir -p dist

# 1. Thunderstore package: core plugin only.
CORE="${STAGE}/core"
mkdir -p "${CORE}/plugins/ValheimMCP"
cp Thunderstore/icon.png README.md CHANGELOG.md LICENSE "${CORE}/"
sed -E "s/(\"version_number\"[[:space:]]*:[[:space:]]*\")[^\"]*\"/\1${VERSION}\"/" Thunderstore/manifest.json > "${CORE}/manifest.json"
cp "${DLL}" "${CORE}/plugins/ValheimMCP/"
OUT="${ROOT}/dist/ValheimMCP-v${VERSION}.zip"
rm -f "${OUT}"
( cd "${CORE}" && zip -r -q "${OUT}" . )
echo "Wrote ${OUT}"

[[ "${1:-}" == "--core-only" ]] && exit 0

# 2. Extension package (the host loads BepInEx/hubner-ext/*.dll; Thunderstore has no such folder, so it ships as its own zip).
echo "Packaging HubnerExt v${EXTVERSION}"
dotnet build ext/HubnerExt.csproj "${DOTNET_ARGS[@]}"
dotnet build ext/HubnerExtServer.csproj "${DOTNET_ARGS[@]}"
EXT="${STAGE}/ext"
mkdir -p "${EXT}/client" "${EXT}/server" "${EXT}/config" "${EXT}/python"
cp out/HubnerExt.dll "${EXT}/client/"
cp out/server/HubnerExt.dll "${EXT}/server/"
cp config/*.yml "${EXT}/config/"
cp python/hubner.py python/hubner_mcp.py "${EXT}/python/"
cp README.md CHANGELOG.md LICENSE "${EXT}/"
OUT="${ROOT}/dist/HubnerExt-v${EXTVERSION}.zip"
rm -f "${OUT}"
( cd "${EXT}" && zip -r -q "${OUT}" . )
echo "Wrote ${OUT}"
