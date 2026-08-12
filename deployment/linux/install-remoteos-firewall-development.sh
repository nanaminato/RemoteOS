#!/usr/bin/env bash
set -euo pipefail

# Installs only the narrow privilege boundary needed to debug Firewall from an
# IDE. It deliberately does not create or start any RemoteOS services: run the
# Server, Guardian Agent, and Desktop projects normally from Rider/your IDE.
if [[ ${EUID} -ne 0 ]]; then
  echo "Run as root, for example: sudo $0 \"\$USER\"" >&2
  exit 1
fi

DEVELOPMENT_USER="${1:-${SUDO_USER:-}}"
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
FIREWALL_HELPER_SOURCE="$SCRIPT_DIR/remoteos-firewall-helper"
FIREWALL_HELPER=/usr/local/lib/remoteos/remoteos-firewall-helper
SUDOERS_FILE=/etc/sudoers.d/remoteos-firewall-development

usage() {
  echo "usage: sudo $0 DEVELOPMENT_USER" >&2
  exit 1
}

[[ -n "$DEVELOPMENT_USER" ]] || usage
[[ "$DEVELOPMENT_USER" =~ ^[a-z_][a-z0-9_-]*$ ]] || { echo "Invalid development user." >&2; exit 1; }
id -u "$DEVELOPMENT_USER" >/dev/null 2>&1 || { echo "User does not exist: $DEVELOPMENT_USER" >&2; exit 1; }
[[ "$DEVELOPMENT_USER" != root ]] || { echo "Specify the unprivileged account that runs your IDE." >&2; exit 1; }
[[ -f "$FIREWALL_HELPER_SOURCE" ]] || { echo "Missing executable: $FIREWALL_HELPER_SOURCE" >&2; exit 1; }
command -v sudo >/dev/null || { echo "sudo is required for the firewall helper." >&2; exit 1; }
command -v visudo >/dev/null || { echo "visudo is required for validating the firewall sudoers rule." >&2; exit 1; }

# The installed helper is root-owned and its parent directory is not writable by
# the development account. The sudoers wildcard is safe only because the helper
# independently accepts a fixed, validated UFW command grammar.
install -d -o root -g root -m 0755 /usr/local/lib/remoteos
install -o root -g root -m 0755 "$FIREWALL_HELPER_SOURCE" "$FIREWALL_HELPER"

SUDOERS_TEMP="$(mktemp /etc/sudoers.d/remoteos-firewall-development.XXXXXX)"
trap 'rm -f "$SUDOERS_TEMP"' EXIT
cat >"$SUDOERS_TEMP" <<EOF
# Managed by RemoteOS development setup. Do not edit: rerun this script.
$DEVELOPMENT_USER ALL=(root) NOPASSWD: $FIREWALL_HELPER *
EOF
chmod 0440 "$SUDOERS_TEMP"
visudo -cf "$SUDOERS_TEMP"
install -o root -g root -m 0440 "$SUDOERS_TEMP" "$SUDOERS_FILE"
rm -f "$SUDOERS_TEMP"
trap - EXIT

if [[ -x /usr/sbin/ufw ]]; then
  "$FIREWALL_HELPER" verify >/dev/null
  sudo -u "$DEVELOPMENT_USER" "$(command -v sudo)" -n "$FIREWALL_HELPER" verify >/dev/null
  echo "RemoteOS Firewall development access is ready for $DEVELOPMENT_USER."
else
  echo "Development access was configured for $DEVELOPMENT_USER, but UFW is not installed."
  echo "Install UFW before testing Firewall operations."
fi
