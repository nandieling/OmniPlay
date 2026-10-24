#!/bin/sh
# 不开群晖，在电脑上把套件脚本整条走一遍：装 -> 起 -> 看页面 -> 停
set -e
cd "$(dirname "$0")/.."

ROOT=$(mktemp -d)
PKG="OmniPlaySidecar"
DEST="${ROOT}/target"
VAR="${ROOT}/var"
mkdir -p "${VAR}"
trap 'rm -rf "${ROOT}"' EXIT INT TERM

# 照群晖的目录结构铺开：target 放程序，var 放配置和进度表
tar xzf "$(ls -t dist/${PKG}_*_noarch.spk | head -1)" -C "${ROOT}" package.tgz
tar xzf "${ROOT}/package.tgz" -C "${ROOT}"

MEDIA=${1:-/volume1/video}
cat > "${VAR}/sidecar.conf" <<EOF
SIDECAR_ROOTS=${MEDIA}
SIDECAR_LANGS=zh
SIDECAR_PORT=${PORT:-45811}
SIDECAR_TOKEN=
SIDECAR_INTERVAL=0
SIDECAR_SLEEP=0
SIDECAR_NICE=5
SIDECAR_MAX_OUT_MB=512
EOF

export SYNOPKG_PKGDEST="${DEST}" SYNOPKG_PKGVAR="${VAR}"
echo "== preinstall"; "${ROOT}/scripts/preinstall"
echo "== postinstall"; "${ROOT}/scripts/postinstall"
echo "== start"; "${ROOT}/scripts/start"
sleep 2
echo "== status"; if "${ROOT}/scripts/status"; then echo "  在跑"; else echo "  没在跑"; exit 1; fi
echo "== health"
curl -s --noproxy '*' "http://127.0.0.1:${PORT:-45811}/health" | head -c 220
echo
echo "== 日志"
tail -4 "${VAR}/sidecar.log" || true
echo "== stop"; "${ROOT}/scripts/stop"
"${ROOT}/scripts/status" && echo "  停完还在？" || echo "  已经停了"
