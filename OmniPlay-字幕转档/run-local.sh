#!/bin/sh
# 不开容器，本机直接跑。改一下 LIBRARY 就行。
set -e
cd "$(dirname "$0")"
LIBRARY="${1:-$HOME/Media}"
PORT="${2:-45733}"
exec python3 sidecar.py --roots "$LIBRARY" --db ./state.db --port "$PORT" --nice 5 --verbose
