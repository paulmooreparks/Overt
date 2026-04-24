#!/usr/bin/env bash
# publish-channel.sh
#
# Bash twin of publish-channel.ps1. Same model: read VERSION, bump the
# per-channel build counter, `dotnet pack` Overt and Overt.Build with
# a prerelease suffix, and on the local channel reinstall the global
# tool. See publish-channel.ps1 for the full documentation.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE="$SCRIPT_DIR/publish.env"

# Load .env-style file (env vars win over file)
if [ -f "$ENV_FILE" ]; then
    set -a
    # shellcheck disable=SC1090
    . "$ENV_FILE"
    set +a
fi

ROOT="${OVERT_PUBLISH_REPO_ROOT:-$(cd "$SCRIPT_DIR/.." && pwd)}"
DIST="${OVERT_PUBLISH_DIST_DIR:-$ROOT/dist}"

CHANNEL_FILE="$SCRIPT_DIR/channel.txt"
if [ -z "${OVERT_PUBLISH_CHANNEL:-}" ]; then
    if [ ! -f "$CHANNEL_FILE" ]; then echo "local" > "$CHANNEL_FILE"; fi
    CHANNEL="$(tr -d '[:space:]' < "$CHANNEL_FILE")"
else
    CHANNEL="$OVERT_PUBLISH_CHANNEL"
fi

if ! [[ "$CHANNEL" =~ ^[A-Za-z0-9-]+$ ]]; then
    echo "Invalid channel name '$CHANNEL'. Use [A-Za-z0-9-] only." >&2
    exit 1
fi

BASE="$(tr -d '[:space:]' < "$ROOT/VERSION")"
COUNTER_FILE="$SCRIPT_DIR/$CHANNEL-build-counter"
N=1
if [ -f "$COUNTER_FILE" ]; then
    current="$(tr -d '[:space:]' < "$COUNTER_FILE")"
    N=$((current + 1))
fi
echo "$N" > "$COUNTER_FILE"

SUFFIX="$CHANNEL.$N"
VERSION="$BASE.0-$SUFFIX"
TAG="v$VERSION"
# FileVersion reuses the counter as the 4th numeric field. See the ps1
# twin for the why.
FILE_VERSION="$BASE.0.$N"

SKIP_INSTALL_VAL="${OVERT_PUBLISH_SKIP_INSTALL:-}"
if [ -n "$SKIP_INSTALL_VAL" ]; then
    case "$SKIP_INSTALL_VAL" in
        1|true|yes) DO_INSTALL=0 ;;
        *) DO_INSTALL=1 ;;
    esac
else
    if [ "$CHANNEL" = "local" ]; then DO_INSTALL=1; else DO_INSTALL=0; fi
fi

mkdir -p "$DIST"

echo ""
echo "==> Packing Overt $VERSION (channel: $CHANNEL, tag: $TAG)"
echo "    dist: $DIST"
echo ""

cd "$ROOT"
for proj in "src/Overt.Cli/Overt.Cli.csproj" "src/Overt.Build/Overt.Build.csproj"; do
    echo "    pack $(basename "$(dirname "$proj")")"
    dotnet pack "$proj" -c Release -o "$DIST" --version-suffix "$SUFFIX" -p:FileVersion="$FILE_VERSION" --nologo
done

CLI_PKG="$DIST/Overt.$VERSION.nupkg"
BUILD_PKG="$DIST/Overt.Build.$VERSION.nupkg"
for p in "$CLI_PKG" "$BUILD_PKG"; do
    if [ ! -f "$p" ]; then
        echo "Expected package not produced: $p" >&2
        exit 1
    fi
done

echo ""
echo "==> Produced"
echo "    $CLI_PKG"
echo "    $BUILD_PKG"

if [ "$DO_INSTALL" -eq 1 ]; then
    echo ""
    echo "==> Reinstalling global tool: overt $VERSION"
    dotnet tool uninstall --global Overt >/dev/null 2>&1 || true
    dotnet tool install --global Overt --version "$VERSION" --add-source "$DIST" --configfile "$SCRIPT_DIR/nuget.tool.config" --ignore-failed-sources
    echo ""
    echo "==> overt is on PATH as version $VERSION"
    echo "    Try: overt --version"
fi

echo ""
echo "Channel counter: $COUNTER_FILE -> $N"
