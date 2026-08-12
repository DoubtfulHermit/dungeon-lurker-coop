#!/usr/bin/env bash
# Launch Dungeon Lurker Demo under GE-Proton with BepInEx enabled.
# Usage: ./run-game.sh [extra unity args]
set -euo pipefail

GAME_DIR="$HOME/.local/share/Steam/steamapps/common/Dungeon Lurker Demo"
PROTON="$HOME/.local/share/Steam/compatibilitytools.d/GE-Proton10-34/proton"
XAUTH_FILE="$(ls /run/user/1000/xauth_* 2>/dev/null | head -1)"

export STEAM_COMPAT_CLIENT_INSTALL_PATH="$HOME/.local/share/Steam"
export STEAM_COMPAT_DATA_PATH="$HOME/.local/share/Steam/steamapps/compatdata/5010970"
export WINEDLLOVERRIDES="winhttp=n,b"
export DISPLAY="${DISPLAY:-:0}"
export XAUTHORITY="${XAUTHORITY:-$XAUTH_FILE}"

exec "$PROTON" run "$GAME_DIR/DungeonLurker.exe" "$@"
