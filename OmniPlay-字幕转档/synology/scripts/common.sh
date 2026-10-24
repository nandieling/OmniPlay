#!/bin/sh
# 群晖套件版的公共部分：定位包目录、读配置、找 python 和 ffmpeg

PKG_NAME="OmniPlaySidecar"
PKG_ROOT="/var/packages/${PKG_NAME}"
DEST="${SYNOPKG_PKGDEST:-${PKG_ROOT}/target}"
VAR_DIR="${SYNOPKG_PKGVAR:-${PKG_ROOT}/var}"
CONF_FILE="${VAR_DIR}/sidecar.conf"
DEFAULT_CONF="${DEST}/sidecar.conf.default"
PID_FILE="${VAR_DIR}/sidecar.pid"
LOG_FILE="${VAR_DIR}/sidecar.log"
SCRIPT_LOG="${VAR_DIR}/scripts.log"

say() {
    echo "$@" | tee -a "${SCRIPT_LOG}"
}

# 首次启动时把打包里的默认配置铺一份到 var 下，之后升级就不动它了
ensure_conf() {
    mkdir -p "${VAR_DIR}"
    if [ ! -f "${CONF_FILE}" ] && [ -f "${DEFAULT_CONF}" ]; then
        cp "${DEFAULT_CONF}" "${CONF_FILE}"
    fi
    if [ -f "${CONF_FILE}" ]; then
        . "${CONF_FILE}"
    fi
    SIDECAR_ROOTS="${SIDECAR_ROOTS:-/volume1/video}"
    SIDECAR_LANGS="${SIDECAR_LANGS:-zh}"
    SIDECAR_PORT="${SIDECAR_PORT:-45733}"
    SIDECAR_INTERVAL="${SIDECAR_INTERVAL:-900}"
    SIDECAR_SLEEP="${SIDECAR_SLEEP:-2}"
    SIDECAR_NICE="${SIDECAR_NICE:-15}"
    SIDECAR_MAX_OUT_MB="${SIDECAR_MAX_OUT_MB:-512}"
}

first_of() {
    for candidate in "$@"; do
        [ -n "${candidate}" ] || continue
        for hit in $(ls -d ${candidate} 2>/dev/null); do
            if [ -x "${hit}" ]; then
                echo "${hit}"
                return 0
            fi
        done
    done
    return 1
}

find_python() {
    if [ -n "${SIDECAR_PYTHON}" ] && [ -x "${SIDECAR_PYTHON}" ]; then
        echo "${SIDECAR_PYTHON}"
        return 0
    fi
    first_of "${SIDECAR_PYTHON}" \
        /var/packages/python314/target/bin/python3 \
        /var/packages/python313/target/bin/python3 \
        /var/packages/python312/target/bin/python3 \
        /var/packages/python311/target/bin/python3 \
        /var/packages/Python311/target/bin/python3 \
        /usr/bin/python3 || return 1
}

find_tool() {
    # $1 = 覆盖用的配置值，$2 = 可执行文件名
    if [ -n "${SIDECAR_FFMPEG}" ] && [ "$2" = "ffmpeg" ] && [ -x "${SIDECAR_FFMPEG}" ]; then
        echo "${SIDECAR_FFMPEG}"
        return 0
    fi
    if [ -n "${SIDECAR_FFPROBE}" ] && [ "$2" = "ffprobe" ] && [ -x "${SIDECAR_FFPROBE}" ]; then
        echo "${SIDECAR_FFPROBE}"
        return 0
    fi
    first_of "/var/packages/ffmpeg8/target/bin/$2" \
        "/var/packages/ffmpeg6/target/bin/$2" \
        "/var/packages/ffmpeg4/target/bin/$2" \
        "/var/packages/ffmpeg/target/bin/$2" \
        "/var/packages/MediaServer/target/bin/$2" \
        "/opt/bin/$2" \
        "/usr/local/bin/$2" \
        "/usr/bin/$2" || return 1
}
