#!/bin/zsh
set -eu

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
BIN="$ROOT_DIR/Libavformat.xcframework/macos-arm64_x86_64/Libavformat.framework/Libavformat"

if [[ ! -f "$BIN" ]]; then
  echo "FAIL: lib not found: $BIN"
  exit 1
fi

echo "[PoC] checking SMB protocol markers in Libavformat..."

MARKERS=(
  "_ff_libsmbclient_protocol"
  "enable-libsmbclient"
  "enable-protocol=libsmbclient"
  "libsmbc"
)

PASS_COUNT=0
for marker in "${MARKERS[@]}"; do
  if strings "$BIN" | rg --quiet --fixed-strings "$marker"; then
    echo "  OK: $marker"
    PASS_COUNT=$((PASS_COUNT + 1))
  else
    echo "  MISS: $marker"
  fi
done

if [[ $PASS_COUNT -ge 2 ]]; then
  echo "[PoC] RESULT: SMB protocol support markers detected."
  exit 0
fi

echo "[PoC] RESULT: SMB protocol markers insufficient."
exit 2
