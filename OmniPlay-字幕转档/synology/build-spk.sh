#!/bin/sh
# 打群晖套件包：产出 dist/OmniPlaySidecar_<版本>-<修订>_noarch.spk
set -e
cd "$(dirname "$0")/.."

PKG="OmniPlaySidecar"
REV="${REV:-1}"
VERSION=$(sed -n 's/^VERSION = "\(.*\)"$/\1/p' sidecar.py | head -1)
[ -n "${VERSION}" ] || { echo "读不到版本号"; exit 1; }

WORK=$(mktemp -d)
trap 'rm -rf "${WORK}"' EXIT INT TERM
mkdir -p "${WORK}/pkg/target/web" "${WORK}/pkg/scripts" "${WORK}/spk"

# 包体：DSM 会把 package.tgz 解压到 /var/packages/<套件>/，target 下面放程序
cp sidecar.py "${WORK}/pkg/target/sidecar.py"
cp web/index.html "${WORK}/pkg/target/web/index.html"
cp synology/sidecar.conf "${WORK}/pkg/target/sidecar.conf.default"
cp synology/info "${WORK}/pkg/info"
cp synology/conf "${WORK}/pkg/conf"
cp synology/scripts/* "${WORK}/pkg/scripts/"
chmod 755 "${WORK}/pkg/scripts/"*

TAR="tar"
export COPYFILE_DISABLE=1                      # macOS 上别把 ._ 分叉文件塞进去
(cd "${WORK}/pkg" && ${TAR} --format ustar --uid 0 --gid 0 --uname '' --gname '' \
    -czf "${WORK}/spk/package.tgz" info conf scripts target)

python3 synology/makeicon.py "${WORK}/spk/PACKAGE_ICON.PNG" 64
python3 synology/makeicon.py "${WORK}/spk/PACKAGE_ICON_256.PNG" 256
cp synology/info "${WORK}/spk/INFO"            # DSM 装包前读的是这份元数据
cp synology/conf "${WORK}/spk/conf"

mkdir -p dist
SPK="$(pwd)/dist/${PKG}_${VERSION}-${REV}_noarch.spk"
(cd "${WORK}/spk" && ${TAR} --format ustar --uid 0 --gid 0 --uname '' --gname '' \
    -czf "${SPK}" INFO conf PACKAGE_ICON.PNG PACKAGE_ICON_256.PNG package.tgz)

echo "打好：${SPK}"
tar tzf "${SPK}" | sed 's/^/    /'
