#!/usr/bin/env bash
# Assemble a minimal LViz.app bundle for local dev on macOS.
# The purpose is not distribution — it's to give the TCC (Accessibility)
# prompt a stable bundle identity + display name + usage description, so
# users see "LViz" and an explanation instead of "dotnet".
#
# Usage:
#   scripts/build-mac-app.sh                      # Release build for host arch
#   scripts/build-mac-app.sh -c Debug             # Debug publish
#   scripts/build-mac-app.sh -r osx-x64           # Cross-publish for Intel
#   scripts/build-mac-app.sh -c Release -r osx-arm64
#   scripts/build-mac-app.sh -v 1.2.3             # Stamp version into binary
#
# Output: <repo>/src/LViz.App/bin/macos-bundle/LViz.app

set -euo pipefail

CONFIG=Release
RID=""
VERSION=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    -c) CONFIG="$2"; shift 2 ;;
    -r) RID="$2"; shift 2 ;;
    -v) VERSION="$2"; shift 2 ;;
    -h|--help)
      echo "Usage: $0 [-c Debug|Release] [-r osx-arm64|osx-x64] [-v <version>]"
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 1
      ;;
  esac
done

if [[ -z "$RID" ]]; then
  ARCH=$(uname -m)
  case "$ARCH" in
    arm64) RID="osx-arm64" ;;
    x86_64) RID="osx-x64" ;;
    *) echo "Unsupported arch: $ARCH" >&2; exit 1 ;;
  esac
fi

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$REPO_ROOT/src/LViz.App/LViz.App.csproj"
PUBLISH_DIR="$REPO_ROOT/src/LViz.App/bin/$CONFIG/net10.0/$RID/publish"
BUNDLE_ROOT="$REPO_ROOT/src/LViz.App/bin/macos-bundle"
APP_BUNDLE="$BUNDLE_ROOT/LViz.app"
INFO_PLIST="$REPO_ROOT/build/macos/Info.plist"

VERSION_ARGS=()
if [[ -n "$VERSION" ]]; then
    VERSION_ARGS=(-p:Version="$VERSION")
fi

echo "[1/4] Publishing ($CONFIG, $RID, self-contained${VERSION:+, v$VERSION})..."
dotnet publish "$PROJECT" \
  -c "$CONFIG" -r "$RID" --self-contained true \
  -f net10.0 \
  -p:PublishSingleFile=false \
  ${VERSION_ARGS[@]+"${VERSION_ARGS[@]}"} \
  --nologo

LSREGISTER=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister

echo "[2/5] Unregistering any stale copies from Launch Services ..."
# A previous bundle at this path (with a different exe name / signature)
# lingers in LS and confuses TCC. Evict it before re-registering.
if [[ -d "$APP_BUNDLE" ]]; then
    "$LSREGISTER" -u "$APP_BUNDLE" 2>/dev/null || true
fi

echo "[3/5] Assembling bundle at $APP_BUNDLE ..."
rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources"

# Copy published output into Contents/MacOS.
cp -R "$PUBLISH_DIR/." "$APP_BUNDLE/Contents/MacOS/"
# Rename the .NET apphost from "LViz.App" to "LViz" so
# the executable name matches standard Mac conventions (no period). The
# apphost embeds its dll name at build-time and doesn't derive it from the
# executable filename, so renaming is safe.
mv "$APP_BUNDLE/Contents/MacOS/LViz.App" "$APP_BUNDLE/Contents/MacOS/LViz"
cp "$INFO_PLIST" "$APP_BUNDLE/Contents/Info.plist"

# Generate an .icns from the existing PNG so the Accessibility pane shows
# a proper icon (macOS has been known to fall back to the bundle directory
# name for entries with no icon).
ICON_SRC="$REPO_ROOT/src/LViz.App/Assets/icon.png"
if [[ -f "$ICON_SRC" ]]; then
    sips -s format icns "$ICON_SRC" --out "$APP_BUNDLE/Contents/Resources/AppIcon.icns" >/dev/null 2>&1 || true
fi

echo "[4/5] Clearing quarantine + signing ..."
# Strip quarantine so double-click works without Gatekeeper pestering.
xattr -cr "$APP_BUNDLE" || true

# Prefer a self-signed Keychain cert named "LViz Local Dev" over
# ad-hoc. Ad-hoc signatures on modern macOS (26+) don't reliably persist
# TCC grants — the Accessibility pane shows the bundle directory name and
# the grant silently fails to apply at runtime. A real cert gives the
# bundle a stable Authority that TCC treats as a durable identity.
#
# Set up (once): Keychain Access → Certificate Assistant → Create a
# Certificate, name "LViz Local Dev", type Code Signing, self
# signed root. The cert doesn't need to be in the system trust store;
# codesign accepts it via its SHA-1 hash even if marked "not trusted".
#
# NOTE: Do NOT pass --options runtime — hardened runtime enforces Library
# Validation, which blocks loading libhostfxr.dylib (different Team IDs).
# `security find-certificate` exits 44 (errSecItemNotFound) when the cert
# is missing — e.g. on CI. The trailing `|| true` keeps `set -euo pipefail`
# from killing the script before we reach the ad-hoc fallback below.
SIGN_CERT_HASH=$(security find-certificate -c "LViz Local Dev" -Z 2>/dev/null \
    | awk '/SHA-1 hash/ {print $NF; exit}' || true)
if [[ -n "$SIGN_CERT_HASH" ]]; then
    echo "  Using cert SHA-1: $SIGN_CERT_HASH"
    codesign --force --deep --sign "$SIGN_CERT_HASH" "$APP_BUNDLE" >/dev/null
else
    echo "  No 'LViz Local Dev' cert found — falling back to ad-hoc."
    echo "  (TCC grants may not persist; see Keychain setup notes above.)"
    codesign --force --deep --sign - "$APP_BUNDLE" >/dev/null
fi

echo "[5/5] Registering with Launch Services ..."
"$LSREGISTER" -f "$APP_BUNDLE"

echo "Done."
echo ""
echo "Bundle: $APP_BUNDLE"
echo "Launch: open \"$APP_BUNDLE\""
echo ""
echo "First run will prompt for Accessibility permission — the prompt should"
echo "now say \"LViz\" with an explanation. If you previously"
echo "granted (or denied) permission to a different identity (e.g. 'dotnet'),"
echo "remove stale entries in System Settings → Privacy & Security →"
echo "Accessibility before launching."
