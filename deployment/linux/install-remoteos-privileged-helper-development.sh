#!/usr/bin/env bash
set -euo pipefail

# Installs the Debug build of the unified Helper into a root-owned directory for
# Server → sudo → Helper integration testing. It does not install RemoteOS services.
if [[ ${EUID} -ne 0 ]]; then
  echo "Run as root, for example: sudo $0 \"\$USER\"" >&2
  exit 1
fi

DEVELOPMENT_USER="${1:-${SUDO_USER:-}}"
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd -- "$SCRIPT_DIR/../.." && pwd)"
SOURCE_HELPER="${2:-$PROJECT_ROOT/RemoteOS.PrivilegedHelper/bin/Debug/net10.0/RemoteOS.PrivilegedHelper}"
INSTALL_DIRECTORY=/usr/local/lib/remoteos/privileged-helper-development
INSTALLED_HELPER="$INSTALL_DIRECTORY/RemoteOS.PrivilegedHelper"
SUDOERS_FILE=/etc/sudoers.d/remoteos-privileged-helper-development

usage() {
  echo "usage: sudo $0 DEVELOPMENT_USER [HELPER_APPHOST]" >&2
  exit 1
}

[[ -n "$DEVELOPMENT_USER" ]] || usage
[[ "$DEVELOPMENT_USER" =~ ^[a-z_][a-z0-9_-]*$ ]] || { echo "Invalid development user." >&2; exit 1; }
id -u "$DEVELOPMENT_USER" >/dev/null 2>&1 || { echo "User does not exist: $DEVELOPMENT_USER" >&2; exit 1; }
[[ "$DEVELOPMENT_USER" != root ]] || { echo "Specify the unprivileged account that runs your IDE." >&2; exit 1; }
SOURCE_HELPER="$(readlink -f -- "$SOURCE_HELPER")"
[[ -x "$SOURCE_HELPER" ]] || { echo "Build the Helper first or pass its apphost path: $SOURCE_HELPER" >&2; exit 1; }
[[ "$(basename -- "$SOURCE_HELPER")" == "RemoteOS.PrivilegedHelper" ]] || { echo "HELPER_APPHOST must be the RemoteOS.PrivilegedHelper apphost." >&2; exit 1; }
command -v sudo >/dev/null || { echo "sudo is required for the privileged helper." >&2; exit 1; }
command -v visudo >/dev/null || { echo "visudo is required for validating the sudoers rule." >&2; exit 1; }

# Copy the complete .NET output (apphost, runtimeconfig, deps, assemblies and PDB) before
# granting sudo. The development account cannot modify this target after installation.
install -d -o root -g root -m 0755 /usr/local/lib/remoteos "$INSTALL_DIRECTORY"
cp -a "$(dirname -- "$SOURCE_HELPER")/." "$INSTALL_DIRECTORY/"
chown -R root:root "$INSTALL_DIRECTORY"
chmod -R go-w "$INSTALL_DIRECTORY"
chmod 0755 "$INSTALLED_HELPER"

SUDOERS_TEMP="$(mktemp /etc/sudoers.d/remoteos-privileged-helper-development.XXXXXX)"
trap 'rm -f "$SUDOERS_TEMP"' EXIT
cat >"$SUDOERS_TEMP" <<EOF
# Managed by RemoteOS development setup. Re-run this script after rebuilding the Helper.
$DEVELOPMENT_USER ALL=(root) NOPASSWD: $INSTALLED_HELPER
EOF
chmod 0440 "$SUDOERS_TEMP"
visudo -cf "$SUDOERS_TEMP"
install -o root -g root -m 0440 "$SUDOERS_TEMP" "$SUDOERS_FILE"
rm -f "$SUDOERS_TEMP"
trap - EXIT

set +e
sudo -u "$DEVELOPMENT_USER" "$(command -v sudo)" -n "$INSTALLED_HELPER" </dev/null >/dev/null 2>&1
STATUS=$?
set -e
[[ $STATUS -eq 64 ]] || { echo "The sudoers rule did not start the Helper as expected (exit $STATUS)." >&2; exit 1; }

echo "Unified privileged Helper development access is ready for $DEVELOPMENT_USER."
echo "Use PrivilegedHelper__HelperPath=$INSTALLED_HELPER in the Server launch profile."
