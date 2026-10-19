#!/bin/bash
# Starts a host and a client instance of RimWorld 1.7 on this PC for Multiplayer testing.
#
#   scripts/run_two.sh           host first; once you've hosted, press Enter and the client starts and auto-joins
#   scripts/run_two.sh host      only the host instance
#   scripts/run_two.sh client    only the client instance (joins 127.0.0.1:30502)
#
# Each instance gets its own save data folder and log:
#   ~/.rw-mp-test/host/   and   ~/.rw-mp-test/client/
#     config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Logs/Player.log   <- the log
#     Saves, MpReplays, MpDesyncs, ...                                          <- the save data
# RimWorld 1.7 puts the log under XDG_CONFIG_HOME, not under -savedatafolder, so both are set.
# Both instances run outside Steam (Steam features off), with -mod= limited to Harmony and this mod, and
# with tiered compilation off (see CLAUDE.md).

source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

GAME_BIN="$RIMWORLD_DIR/RimWorld"
TEST_ROOT="$HOME/.rw-mp-test"
REAL_CONFIG="${XDG_CONFIG_HOME:-$HOME/.config}/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Config"
MODS="brrainz.harmony,Multiplayer-1.7"
CONNECT="127.0.0.1:30502"

[ -x "$GAME_BIN" ] || { echo "Game binary not found at $GAME_BIN (set RIMWORLD_DIR)"; exit 1; }
[ -d "$RIMWORLD_DIR/Mods/Multiplayer-1.7" ] || echo "WARNING: the mod isn't installed, run scripts/install_local.sh first"

start_instance() {
    local name="$1"; shift
    local root="$TEST_ROOT/$name"
    local config="$root/config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Config"

    mkdir -p "$root" "$config"
    # First run: take the real prefs (resolution, dev mode, key bindings, mod settings) so the test instance
    # behaves like the normal game. ModsConfig.xml is left out, -mod= decides the mod list.
    if [ ! -f "$config/Prefs.xml" ] && [ -d "$REAL_CONFIG" ]; then
        find "$REAL_CONFIG" -maxdepth 1 -type f ! -name ModsConfig.xml -exec cp {} "$config/" \;
    fi

    echo "Starting $name instance (log: $root/config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Logs/Player.log)"
    (
        cd "$RIMWORLD_DIR" || exit 1
        XDG_CONFIG_HOME="$root/config" DOTNET_TieredCompilation=0 \
            "$GAME_BIN" -savedatafolder="$root" -mod="$MODS" "$@" > "$root/stdout.log" 2>&1 &
        echo $! > "$root/pid"
    )
}

case "${1:-both}" in
    host)
        start_instance host ;;
    client)
        start_instance client -connect="$CONNECT" ;;
    both)
        start_instance host
        echo
        echo "In the host instance: load or start a colony, then Multiplayer > Host (keep 'Run arbiter' off)."
        read -r -p "Press Enter once the host says 'Server started' to launch the client... "
        start_instance client -connect="$CONNECT"
        echo "The client joins $CONNECT on its own. If it doesn't, use Multiplayer > Join > Direct connect > $CONNECT." ;;
    *)
        echo "usage: $0 [host|client|both]"; exit 1 ;;
esac
