#!/usr/bin/env bash
# 本地按 CI（.github/workflows/macos-release.yml）同样的步骤打 mac 安装包：
# Release 编译 → zip → dmg → sha256，并做签名/校验。
# 用法：scripts/pack_mac.sh arm64|x64
set -euo pipefail

id="${1:?用法: pack_mac.sh arm64|x64}"
root="$(cd "$(dirname "$0")/.." && pwd)"
case "$id" in
  arm64) project="OmniPlay开源-mac-ARM"; arch="arm64" ;;
  x64)   project="OmniPlay开源-mac-x64"; arch="x86_64" ;;
  *) echo "未知目标：$id（应为 arm64 或 x64）" >&2; exit 2 ;;
esac

cd "$root/$project"
xcodebuild -project OmniPlay.xcodeproj -scheme OmniPlay \
  -configuration Release \
  -destination "platform=macOS,arch=${arch}" \
  -derivedDataPath build/ci/DerivedData \
  -clonedSourcePackagesDirPath build/ci/SourcePackages \
  -skipPackagePluginValidation -skipMacroValidation \
  CODE_SIGN_IDENTITY="-" CODE_SIGN_STYLE=Manual PROVISIONING_PROFILE_SPECIFIER="" DEVELOPMENT_TEAM="" \
  build | tail -3

app="$(ls -d build/ci/DerivedData/Build/Products/Release/*.app | head -1)"
version="$(plutil -extract CFBundleShortVersionString raw -o - "$app/Contents/Info.plist")"
base="OmniPlay-${version}-mac-${id}"
out="$root/dist"
mkdir -p "$out"
rm -f "$out/${base}.zip" "$out/${base}.dmg" "$out/${base}.sha256"

ditto -c -k --keepParent "$app" "$out/${base}.zip"

stage="$(mktemp -d)"
ditto "$app" "$stage/$(basename "$app")"
ln -s /Applications "$stage/Applications"
hdiutil create -quiet -volname "觅影 ${version}" -srcfolder "$stage" -ov -format UDZO "$out/${base}.dmg"
rm -rf "$stage"

( cd "$out" && shasum -a 256 "${base}.zip" "${base}.dmg" > "${base}.sha256" )

echo "== 产物自检（${id}）=="
codesign --verify --strict "$app" && echo "codesign: OK"
lipo -info "$app/Contents/MacOS/"*
hdiutil verify "$out/${base}.dmg" >/dev/null && echo "dmg: 校验通过"
ls -lh "$out/${base}."*
