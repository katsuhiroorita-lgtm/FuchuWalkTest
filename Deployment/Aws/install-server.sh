#!/usr/bin/env bash
# Run manually on the approved test VM after unpacking the verified Linux bundle.
set -euo pipefail
if [[ $EUID -ne 0 ]]; then echo 'Run with sudo on the approved test server.' >&2; exit 1; fi
bundle=$(cd -- "$(dirname -- "$0")/.." && pwd -P)
[[ -f "$bundle/FuchuServer.x86_64" && -d "$bundle/FuchuServer_Data" && -f "$bundle/UnityPlayer.so" ]] || {
    echo 'This is not the Linux server bundle.' >&2; exit 1;
}
[[ -f "$bundle/SHA256SUMS" ]] || { echo 'Missing SHA256SUMS.' >&2; exit 1; }
(cd -- "$bundle" && sha256sum --check --strict SHA256SUMS)
id fuchu >/dev/null 2>&1 || { echo 'Wait for cloud-init / bootstrap to finish first.' >&2; exit 1; }
[[ -d /opt/fuchu/releases && "$(realpath /opt/fuchu/releases)" == /opt/fuchu/releases ]] || { echo 'Invalid releases directory.' >&2; exit 1; }
release="/opt/fuchu/releases/$(date -u +%Y%m%dT%H%M%SZ)"
[[ ! -e "$release" ]] || { echo 'Release already exists; retry later.' >&2; exit 1; }
install -d -m 0755 "$release"
cp -a -- "$bundle/." "$release/"
chown -R root:root "$release"
find "$release" -type d -exec chmod 0755 {} +
find "$release" -type f -exec chmod 0644 {} +
chmod 0755 "$release/FuchuServer.x86_64" "$release/StartLocalServer.sh"
if [[ -f "$release/UnityCrashHandler64" ]]; then chmod 0755 "$release/UnityCrashHandler64"; fi
install -m 0644 "$release/deployment/fuchu-server.service" /etc/systemd/system/fuchu-server.service
install -m 0644 "$release/deployment/fuchu-server.logrotate" /etc/logrotate.d/fuchu-server
if [[ ! -f /etc/fuchu/server.env ]]; then
    install -m 0644 "$release/deployment/server.env" /etc/fuchu/server.env
fi
if [[ -e /opt/fuchu/current && ! -L /opt/fuchu/current ]]; then
    echo '/opt/fuchu/current is not a symlink; do not overwrite it.' >&2; exit 1
fi
systemctl daemon-reload
systemctl stop fuchu-server.service
ln -sfn -- "$release" /opt/fuchu/current
systemctl enable --now fuchu-server.service
systemctl --no-pager status fuchu-server.service
echo 'Installation finished. Check /var/log/fuchu/server.log for LISTENING.'
