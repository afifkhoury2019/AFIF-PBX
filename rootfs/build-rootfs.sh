#!/usr/bin/env bash
# Build the AFIF-PBX WSL root filesystem on a Linux host with Docker.
# Usage: rootfs/build-rootfs.sh <output.tar.gz> [restore-bundle-dir]
set -euo pipefail
OUT=${1:?output tarball}
BUNDLE=${2:-}
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/.." && pwd)
NAME=afifpbx-build

docker build -t afifpbx-systemd -f "$HERE/Dockerfile.systemd" "$HERE"
docker rm -f $NAME >/dev/null 2>&1 || true
docker run -d --name $NAME --hostname afif-pbx --privileged --cgroupns=host \
  -v /sys/fs/cgroup:/sys/fs/cgroup:rw --tmpfs /run --tmpfs /run/lock \
  afifpbx-systemd
# wait for systemd
for i in $(seq 1 30); do
  docker exec $NAME systemctl is-system-running 2>/dev/null | grep -qE 'running|degraded' && break; sleep 2
done

docker exec $NAME mkdir -p /opt/afif-pbx
docker cp "$REPO/rootfs/." $NAME:/opt/afif-pbx/rootfs
docker cp "$REPO/branding/." $NAME:/opt/afif-pbx/branding
if [ -n "$BUNDLE" ] && [ -d "$BUNDLE" ]; then
  docker cp "$BUNDLE/." $NAME:/opt/afif-pbx/restore
fi

docker exec -e DEBIAN_FRONTEND=noninteractive $NAME bash /opt/afif-pbx/rootfs/provision.sh

# Smoke test inside the container before exporting
docker exec $NAME bash /opt/afif-pbx/rootfs/selftest.sh

# Clean shutdown of services, then export the filesystem
docker exec $NAME bash -c 'fwconsole stop >/dev/null 2>&1 || true; systemctl stop mariadb apache2 >/dev/null 2>&1 || true'
docker exec $NAME bash /opt/afif-pbx/rootfs/finalize.sh
docker stop $NAME >/dev/null
docker export $NAME | gzip -6 > "$OUT"
docker rm -f $NAME >/dev/null
ls -lh "$OUT"
