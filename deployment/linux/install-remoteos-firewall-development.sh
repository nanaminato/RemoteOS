#!/usr/bin/env bash
set -euo pipefail

echo "install-remoteos-firewall-development.sh is deprecated; using the unified PrivilegedHelper setup." >&2
exec "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/install-remoteos-privileged-helper-development.sh" "$@"
