#!/usr/bin/env bash
set -Eeuo pipefail

configuration="Release"
runtime_identifier="win-x64"
output_directory=""
no_restore=0

usage() {
    cat <<'EOF'
用法：
  ./package-setup.sh [选项]

选项：
  -c, --configuration <Debug|Release>  构建配置，默认 Release
  -r, --runtime <win-x64>              Windows 运行时，当前仅支持 win-x64
  -o, --output <目录>                  输出目录，默认 ./dist
      --no-restore                     发布时不执行 restore
  -h, --help                           显示帮助
EOF
}

die() {
    printf '错误：%s\n' "$1" >&2
    exit 1
}

while (($# > 0)); do
    case "$1" in
        -c|--configuration)
            (($# >= 2)) || die "$1 缺少参数"
            configuration="$2"
            shift 2
            ;;
        -r|--runtime)
            (($# >= 2)) || die "$1 缺少参数"
            runtime_identifier="$2"
            shift 2
            ;;
        -o|--output)
            (($# >= 2)) || die "$1 缺少参数"
            output_directory="$2"
            shift 2
            ;;
        --no-restore)
            no_restore=1
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            die "未知参数：$1"
            ;;
    esac
done

case "$configuration" in
    Debug|Release) ;;
    *) die "configuration 必须是 Debug 或 Release" ;;
esac

if [[ "$runtime_identifier" != "win-x64" ]]; then
    die "当前 Windows 原生 mpv 依赖是 x64，只支持 win-x64。"
fi

command -v dotnet >/dev/null 2>&1 || die "未找到 dotnet SDK。"
command -v zip >/dev/null 2>&1 || die "未找到 zip 命令。"
command -v unzip >/dev/null 2>&1 || die "未找到 unzip 命令。"

windows_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
desktop_project="$windows_root/src/OmniPlay.Desktop/OmniPlay.Desktop.csproj"
setup_project="$windows_root/installer/OmniPlay.Setup/OmniPlay.Setup.csproj"
package_root="$windows_root/tmp/package"
app_stage="$package_root/app"
payload_directory="$package_root/payload"
payload_zip="$payload_directory/OmniPlayPayload.zip"
setup_stage="$package_root/setup"
dist_directory="${output_directory:-$windows_root/dist}"
setup_exe_name="览影-OmniPlay-x64-setup.exe"
setup_exe="$dist_directory/$setup_exe_name"
portable_zip="$dist_directory/OmniPlay-win-x64-portable.zip"

[[ -f "$desktop_project" ]] || die "找不到 Windows 桌面项目：$desktop_project"
[[ -f "$setup_project" ]] || die "找不到安装器项目：$setup_project"
[[ -f "$windows_root/src/OmniPlay.Desktop/Native/mpv/libmpv-2.dll" ]] || die "缺少 x64 libmpv-2.dll。"

case "$package_root" in
    "$windows_root/tmp/package") ;;
    *) die "打包目录安全校验失败。" ;;
esac

export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$windows_root/.dotnet}"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1"
export DOTNET_NOLOGO="1"

rm -rf "$package_root"
mkdir -p "$app_stage" "$payload_directory" "$setup_stage" "$dist_directory"

publish_app_args=(
    publish "$desktop_project"
    -c "$configuration"
    -r "$runtime_identifier"
    --self-contained true
    -o "$app_stage"
    -p:PublishSingleFile=false
    -p:NuGetAudit=false
)
if ((no_restore)); then
    publish_app_args+=(--no-restore)
fi

echo "[1/5] 发布 Windows x64 应用..."
dotnet "${publish_app_args[@]}"

find "$app_stage" -type f -name '*.pdb' -delete
[[ -f "$app_stage/OmniPlay.Desktop.exe" ]] || die "发布结果缺少 OmniPlay.Desktop.exe。"

echo "[2/5] 创建应用载荷..."
(
    cd "$app_stage"
    zip -q -r "$payload_zip" . -x '*.DS_Store' '*/.DS_Store' '._*' '*/._*' '*.pdb' '*/.pdb'
)
unzip -tq "$payload_zip"
unzip -Z1 "$payload_zip" | grep -Fxq 'OmniPlay.Desktop.exe' || die "载荷中缺少 OmniPlay.Desktop.exe。"

echo "[3/5] 创建便携版压缩包..."
rm -f "$portable_zip"
(
    cd "$app_stage"
    zip -q -r "$portable_zip" . -x '*.DS_Store' '*/.DS_Store' '._*' '*/._*' '*.pdb' '*/.pdb'
)
unzip -tq "$portable_zip"

setup_publish_args=(
    publish "$setup_project"
    -c "$configuration"
    -r "$runtime_identifier"
    --self-contained true
    -p:EnableWindowsTargeting=true
    -p:PublishSingleFile=true
    -p:EnableCompressionInSingleFile=true
    -p:IncludeNativeLibrariesForSelfExtract=true
    -p:NuGetAudit=false
    -o "$setup_stage"
)
if ((no_restore)); then
    setup_publish_args+=(--no-restore)
fi

echo "[4/5] 生成 Windows 安装程序..."
dotnet "${setup_publish_args[@]}"
[[ -f "$setup_stage/setup.exe" ]] || die "安装器发布结果缺少 setup.exe。"

rm -f "$setup_exe"
cp "$setup_stage/setup.exe" "$setup_exe"

echo "[5/5] 校验 Windows PE 输出..."
file_description="$(file -b "$setup_exe")"
printf '%s\n' "$file_description"
grep -Eq 'PE32\+.*(x86-64|x86_64)' <<<"$file_description" || die "输出不是 Windows x64 PE 文件。"

echo "已生成：$setup_exe"
echo "便携版：$portable_zip"
echo "说明：ARM Mac 无法执行 Windows 安装程序；安装包载荷和 PE 架构已在本机校验。"
