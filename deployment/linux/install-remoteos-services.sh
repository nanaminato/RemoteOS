#!/usr/bin/env bash
set -euo pipefail

# Called by the signed package installer, not by RemoteOS HTTP endpoints. It registers
# both units, generates the local IPC secret, and leaves end users no Agent setup step.
# It also installs the one-shot firewall helper; that helper is not a daemon.
if [[ ${EUID} -ne 0 ]]; then
  echo "Run as root through the host's approved elevation flow." >&2
  exit 1
fi

INSTALL_ROOT="${1:?usage: install-remoteos-services.sh INSTALL_ROOT SERVER_EXECUTABLE GUARDIAN_EXECUTABLE SERVER_PORT [SERVICE_USER]}"
SERVER_EXECUTABLE="${2:?missing SERVER_EXECUTABLE}"
GUARDIAN_EXECUTABLE="${3:?missing GUARDIAN_EXECUTABLE}"
SERVER_PORT="${4:?missing SERVER_PORT}"
SERVICE_USER="${5:-remoteos-server}"
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
FIREWALL_HELPER_SOURCE="$SCRIPT_DIR/remoteos-firewall-helper"
FIREWALL_HELPER=/usr/local/lib/remoteos/remoteos-firewall-helper
SUDOERS_FILE=/etc/sudoers.d/remoteos-firewall-helper

for file in "$SERVER_EXECUTABLE" "$GUARDIAN_EXECUTABLE" "$FIREWALL_HELPER_SOURCE"; do
  [[ -f "$file" ]] || { echo "Missing executable: $file" >&2; exit 1; }
done
[[ "$SERVER_PORT" =~ ^[0-9]+$ ]] && (( SERVER_PORT >= 1 && SERVER_PORT <= 65535 )) || { echo "Invalid server port." >&2; exit 1; }
[[ "$SERVICE_USER" =~ ^[a-z_][a-z0-9_-]*$ ]] || { echo "Invalid service user." >&2; exit 1; }
command -v sudo >/dev/null || { echo "sudo is required for the firewall helper." >&2; exit 1; }
command -v visudo >/dev/null || { echo "visudo is required for validating the firewall sudoers rule." >&2; exit 1; }

if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
  useradd --system --user-group --home-dir /var/lib/remoteos --shell /usr/sbin/nologin "$SERVICE_USER"
fi
SERVICE_GROUP="$(id -gn "$SERVICE_USER")"

install -d -m 0700 /etc/remoteos /var/lib/remoteos/guardian
install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0750 /var/lib/remoteos/docker-compose
install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0750 "$INSTALL_ROOT/data"
SECRET="$(openssl rand -base64 48)"

cat >/etc/remoteos/guardian.env <<EOF
REMOTEOS_GUARDIAN_SHARED_SECRET=$SECRET
REMOTEOS_GUARDIAN_PIPE=remoteos-guardian
REMOTEOS_GUARDIAN_DATA_DIR=/var/lib/remoteos/guardian
REMOTEOS_GUARDIAN_SERVER_SERVICE=remoteos-server.service
REMOTEOS_GUARDIAN_SERVER_HEALTH_URL=http://127.0.0.1:$SERVER_PORT/healthz
EOF
cat >/etc/remoteos/server.env <<EOF
GuardianAgent__SharedSecret=$SECRET
GuardianAgent__PipeName=remoteos-guardian
Storage__DatabasePath=$INSTALL_ROOT/data/remoteos.db
DockerCompose__DataDirectory=/var/lib/remoteos/docker-compose
Firewall__HelperPath=$FIREWALL_HELPER
Firewall__SudoPath=$(command -v sudo)
EOF
chmod 0600 /etc/remoteos/guardian.env /etc/remoteos/server.env

# The helper is root-owned and has no writable parent for the service account.
# sudoers may pass arbitrary arguments, but the helper independently accepts only
# its small, structured UFW command grammar.
install -d -o root -g root -m 0755 /usr/local/lib/remoteos
install -o root -g root -m 0755 "$FIREWALL_HELPER_SOURCE" "$FIREWALL_HELPER"
SUDOERS_TEMP="$(mktemp /etc/sudoers.d/remoteos-firewall-helper.XXXXXX)"
trap 'rm -f "$SUDOERS_TEMP"' EXIT
cat >"$SUDOERS_TEMP" <<EOF
# Managed by RemoteOS. Do not edit: reinstall to regenerate.
$SERVICE_USER ALL=(root) NOPASSWD: $FIREWALL_HELPER *
EOF
chmod 0440 "$SUDOERS_TEMP"
visudo -cf "$SUDOERS_TEMP"
install -o root -g root -m 0440 "$SUDOERS_TEMP" "$SUDOERS_FILE"
rm -f "$SUDOERS_TEMP"
trap - EXIT

if [[ -x /usr/sbin/ufw ]]; then
  "$FIREWALL_HELPER" verify >/dev/null
  sudo -u "$SERVICE_USER" "$(command -v sudo)" -n "$FIREWALL_HELPER" verify >/dev/null
else
  echo "UFW is not installed; Firewall will remain unavailable until UFW is installed."
fi

cat >/etc/systemd/system/remoteos-guardian.service <<EOF
[Unit]
Description=RemoteOS Guardian Agent
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
EnvironmentFile=/etc/remoteos/guardian.env
ExecStart=$GUARDIAN_EXECUTABLE
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF
cat >/etc/systemd/system/remoteos-server.service <<EOF
[Unit]
Description=RemoteOS Server
After=network-online.target remoteos-guardian.service
Wants=network-online.target remoteos-guardian.service

[Service]
Type=simple
EnvironmentFile=/etc/remoteos/server.env
User=$SERVICE_USER
Group=$SERVICE_GROUP
WorkingDirectory=$(dirname "$SERVER_EXECUTABLE")
ExecStart=$SERVER_EXECUTABLE
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now remoteos-guardian.service remoteos-server.service
echo "Installed RemoteOS Server and Guardian services (Server user: $SERVICE_USER)."
