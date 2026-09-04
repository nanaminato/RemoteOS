#!/usr/bin/env bash
set -euo pipefail

# Called by the signed package installer, not by RemoteOS HTTP endpoints. It registers
# both units, generates the local IPC secret, and leaves end users no Agent setup step.
if [[ ${EUID} -ne 0 ]]; then
  echo "Run as root through the host's approved elevation flow." >&2
  exit 1
fi

INSTALL_ROOT="${1:?usage: install-remoteos-services.sh INSTALL_ROOT SERVER_EXECUTABLE GUARDIAN_EXECUTABLE PRIVILEGED_HELPER_EXECUTABLE SERVER_PORT [SERVICE_USER]}"
SERVER_EXECUTABLE="${2:?missing SERVER_EXECUTABLE}"
GUARDIAN_EXECUTABLE="${3:?missing GUARDIAN_EXECUTABLE}"
PRIVILEGED_HELPER_EXECUTABLE="${4:?missing PRIVILEGED_HELPER_EXECUTABLE}"
SERVER_PORT="${5:?missing SERVER_PORT}"
SERVICE_USER="${6:-remoteos-server}"
PRIVILEGED_HELPER_SOURCE_DIR="$(dirname -- "$PRIVILEGED_HELPER_EXECUTABLE")"
PRIVILEGED_HELPER_INSTALL_DIR=/usr/local/lib/remoteos/privileged-helper
PRIVILEGED_HELPER="$PRIVILEGED_HELPER_INSTALL_DIR/$(basename -- "$PRIVILEGED_HELPER_EXECUTABLE")"
SUDOERS_FILE=/etc/sudoers.d/remoteos-helpers

for file in "$SERVER_EXECUTABLE" "$GUARDIAN_EXECUTABLE" "$PRIVILEGED_HELPER_EXECUTABLE"; do
  [[ -f "$file" ]] || { echo "Missing executable: $file" >&2; exit 1; }
done
[[ "$SERVER_PORT" =~ ^[0-9]+$ ]] && (( SERVER_PORT >= 1 && SERVER_PORT <= 65535 )) || { echo "Invalid server port." >&2; exit 1; }
[[ "$SERVICE_USER" =~ ^[a-z_][a-z0-9_-]*$ ]] || { echo "Invalid service user." >&2; exit 1; }
command -v sudo >/dev/null || { echo "sudo is required for the privileged helper." >&2; exit 1; }
command -v visudo >/dev/null || { echo "visudo is required for validating the privileged-helper sudoers rule." >&2; exit 1; }

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
PrivilegedHelper__HelperPath=$PRIVILEGED_HELPER
PrivilegedHelper__SudoPath=$(command -v sudo)
EOF
chmod 0600 /etc/remoteos/guardian.env /etc/remoteos/server.env

# This is a Helper policy, not Server configuration. Keep the first deployment scope small;
# additional protected Explorer roots must be explicitly reviewed and added by the installer.
cat >/etc/remoteos/privileged-helper-roots <<EOF
/etc/remoteos
/var/lib/remoteos
EOF
chown root:root /etc/remoteos/privileged-helper-roots
chmod 0600 /etc/remoteos/privileged-helper-roots
cat >/etc/remoteos/privileged-services <<EOF
remoteos-server.service
remoteos-guardian.service
remoteos-mihomo.service
EOF
chown root:root /etc/remoteos/privileged-services
chmod 0600 /etc/remoteos/privileged-services

# Helpers are root-owned and have no writable parent for the service account. The only
# sudo rule permits the published apphost with no caller-supplied arguments; the .NET Helper
# independently accepts only its versioned, structured operation protocol.
install -d -o root -g root -m 0755 /usr/local/lib/remoteos
# The published .NET helper has a companion runtimeconfig/deps file (and may have managed
# assemblies). Copy its whole publish directory, then make it root-owned and immutable to the
# Server account. The fourth installer argument must therefore point at the helper apphost from
# `dotnet publish`, not merely the .dll produced by `dotnet build`.
install -d -o root -g root -m 0755 "$PRIVILEGED_HELPER_INSTALL_DIR"
cp -a "$PRIVILEGED_HELPER_SOURCE_DIR/." "$PRIVILEGED_HELPER_INSTALL_DIR/"
chown -R root:root "$PRIVILEGED_HELPER_INSTALL_DIR"
chmod -R go-w "$PRIVILEGED_HELPER_INSTALL_DIR"
chmod 0755 "$PRIVILEGED_HELPER"
SUDOERS_TEMP="$(mktemp /etc/sudoers.d/remoteos-helpers.XXXXXX)"
trap 'rm -f "$SUDOERS_TEMP"' EXIT
cat >"$SUDOERS_TEMP" <<EOF
# Managed by RemoteOS. Do not edit: reinstall to regenerate.
$SERVICE_USER ALL=(root) NOPASSWD: $PRIVILEGED_HELPER
EOF
chmod 0440 "$SUDOERS_TEMP"
visudo -cf "$SUDOERS_TEMP"
install -o root -g root -m 0440 "$SUDOERS_TEMP" "$SUDOERS_FILE"
rm -f "$SUDOERS_TEMP"
trap - EXIT

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
