#!/usr/bin/env bash
set -euo pipefail

# Called by the signed package installer, not by RemoteOS HTTP endpoints. It registers
# both units, generates the local IPC secret, and leaves end users no Agent setup step.
if [[ ${EUID} -ne 0 ]]; then
  echo "Run as root through the host's approved elevation flow." >&2
  exit 1
fi

INSTALL_ROOT="${1:?usage: install-remoteos-services.sh INSTALL_ROOT SERVER_EXECUTABLE GUARDIAN_EXECUTABLE SERVER_PORT}"
SERVER_EXECUTABLE="${2:?missing SERVER_EXECUTABLE}"
GUARDIAN_EXECUTABLE="${3:?missing GUARDIAN_EXECUTABLE}"
SERVER_PORT="${4:?missing SERVER_PORT}"

for file in "$SERVER_EXECUTABLE" "$GUARDIAN_EXECUTABLE"; do
  [[ -f "$file" ]] || { echo "Missing executable: $file" >&2; exit 1; }
done
[[ "$SERVER_PORT" =~ ^[0-9]+$ ]] && (( SERVER_PORT >= 1 && SERVER_PORT <= 65535 )) || { echo "Invalid server port." >&2; exit 1; }

install -d -m 0700 /etc/remoteos /var/lib/remoteos/guardian
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
EOF
chmod 0600 /etc/remoteos/guardian.env /etc/remoteos/server.env

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
WorkingDirectory=$(dirname "$SERVER_EXECUTABLE")
ExecStart=$SERVER_EXECUTABLE
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now remoteos-guardian.service remoteos-server.service
echo "Installed RemoteOS Server and Guardian services."
