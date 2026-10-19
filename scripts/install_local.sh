#!/bin/bash
# Builds Release and installs the mod into this PC's RimWorld Mods folder as Multiplayer-1.7,
# replacing the previous copy. Set RIMWORLD_DIR to use another RimWorld install.

source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

MODS_DIR="$RIMWORLD_DIR/Mods"
DEST="$MODS_DIR/Multiplayer-1.7"
WORKSHOP_DIR="$RIMWORLD_DIR/../../workshop/content/294100"

[ -d "$MODS_DIR" ] || { echo "No Mods folder at $MODS_DIR (start the game once to create it, or set RIMWORLD_DIR)"; exit 1; }

build_mod || exit 1

# Delete first, then copy: a running game keeps the old dlls mapped, and overwriting them in place would
# corrupt that game's copy. A deleted file stays readable for the process that has it open.
rm -rf "$DEST"
stage_mod "$DEST" || { echo 'Installing the mod FAILED'; exit 1; }
echo "Installed $(mp_version) to $DEST"

[ -d "$WORKSHOP_DIR/2606448745" ] && echo "WARNING: the Workshop Multiplayer mod (2606448745) is still installed. Unsubscribe from it, it has the same packageId."
[ -d "$WORKSHOP_DIR/2934420800" ] && echo "WARNING: the Workshop Prepatcher mod (2934420800) is still installed. Unsubscribe from it, it can't work on 1.7."

# Keep this game build's Assembly-CSharp.dll for diffing after game updates
GAME_VERSION="$(cut -d' ' -f1 "$RIMWORLD_DIR/Version.txt" 2>/dev/null)"
if [ -n "$GAME_VERSION" ] && [ ! -f "$HOME/rw-builds/$GAME_VERSION/Assembly-CSharp.dll" ]; then
    mkdir -p "$HOME/rw-builds/$GAME_VERSION"
    cp "$RIMWORLD_DIR/Assembly-CSharp.dll" "$HOME/rw-builds/$GAME_VERSION/" && echo "Saved Assembly-CSharp.dll to ~/rw-builds/$GAME_VERSION/"
fi
