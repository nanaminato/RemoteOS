#!/usr/bin/env bash
set -euo pipefail

VERSION=${1:?usage: package-relaxkonos.sh VERSION [linux-x64|linux-arm64] [Release|Debug] [OUTPUT_DIRECTORY]}
RUNTIME=${2:-linux-x64}
CONFIGURATION=${3:-Release}
OUTPUT_DIRECTORY=${4:-"$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/artifacts"}
case "$RUNTIME" in linux-x64|linux-arm64) ;; *) echo 'Linux package script supports linux-x64 and linux-arm64.' >&2; exit 64 ;; esac
case "$CONFIGURATION" in Release|Debug) ;; *) echo 'Configuration must be Release or Debug.' >&2; exit 64 ;; esac
[[ "$VERSION" =~ ^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$ ]] || { echo 'Invalid version.' >&2; exit 64; }

SCRIPT_DIRECTORY="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd -- "$SCRIPT_DIRECTORY/../.." && pwd)"
BUNDLE_NAME="RelaxKonOS-$VERSION-$RUNTIME"
BUNDLE="$OUTPUT_DIRECTORY/$BUNDLE_NAME"
ARCHIVE="$OUTPUT_DIRECTORY/$BUNDLE_NAME.zip"
mkdir -p "$OUTPUT_DIRECTORY"
rm -rf -- "$BUNDLE"; rm -f -- "$ARCHIVE" "$ARCHIVE.sha256" "$ARCHIVE.json"

publish() {
  local project="$1" name="$2" executable="$3" destination="$BUNDLE/payload/linux/$name"
  dotnet publish "$PROJECT_ROOT/$project" --configuration "$CONFIGURATION" --runtime "$RUNTIME" --self-contained true --output "$destination"
  [[ -f "$destination/$executable" ]] || { echo "Publish output does not contain $executable." >&2; exit 1; }
}
publish 'Client/RelaxKonOS.Client.Desktop/RelaxKonOS.Client.Desktop.csproj' client RelaxKonOS.Client.Desktop
publish 'RelaxKonOS.Server/RelaxKonOS.Server.csproj' server RelaxKonOS.Server
publish 'RelaxKonOS.Guardian.Agent/RelaxKonOS.Guardian.Agent.csproj' guardian RelaxKonOS.Guardian.Agent
publish 'RelaxKonOS.PrivilegedHelper/RelaxKonOS.PrivilegedHelper.csproj' privileged-helper RelaxKonOS.PrivilegedHelper

mkdir -p "$BUNDLE/deployment"
cp -a "$PROJECT_ROOT/deployment/bootstrap" "$BUNDLE/deployment/bootstrap"
cp -a "$PROJECT_ROOT/deployment/linux" "$BUNDLE/deployment/linux"
cat >"$BUNDLE/manifest.json" <<EOF
{"schemaVersion":1,"version":"$VERSION","runtime":"$RUNTIME","payload":{"linux":{"client":"payload/linux/client/RelaxKonOS.Client.Desktop","server":"payload/linux/server/RelaxKonOS.Server","guardian":"payload/linux/guardian/RelaxKonOS.Guardian.Agent","privilegedHelper":"payload/linux/privileged-helper/RelaxKonOS.PrivilegedHelper"}}}
EOF
(cd "$BUNDLE" && zip -qr "$ARCHIVE" .)
HASH="$(sha256sum "$ARCHIVE" | awk '{print $1}')"
printf '%s  %s\n' "$HASH" "$(basename -- "$ARCHIVE")" > "$ARCHIVE.sha256"
printf '{"schemaVersion":1,"version":"%s","runtime":"%s","url":"https://downloads.relaxkon.com/relaxkonos/stable/%s/%s/%s","sha256":"%s"}\n' "$VERSION" "$RUNTIME" "$VERSION" "$RUNTIME" "$(basename -- "$ARCHIVE")" "$HASH" > "$ARCHIVE.json"
printf 'Bundle: %s\nArchive: %s\nSHA-256: %s\n' "$BUNDLE" "$ARCHIVE" "$HASH"
