#!/bin/bash
# Builds Release and installs the mod into this PC's RimWorld Mods folder as Multiplayer-1.7,
# replacing the previous copy. Set RIMWORLD_DIR to use another RimWorld install.

source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

MODS_DIR="$RIMWORLD_DIR/Mods"
DEST="$MODS_DIR/Multiplayer-1.7"
WORKSHOP_DIR="$RIMWORLD_DIR/../../workshop/content/294100"

[ -d "$MODS_DIR" ] || { echo "No Mods folder at $MODS_DIR (start the game once to create it, or set RIMWORLD_DIR)"; exit 1; }

# SKIP_BUILD=1 installs what the last build produced. On this PC the distrobox can see the game drive read-only; then
# build in the container and install from the host, from the repo root: distrobox-host-exec env SKIP_BUILD=1 ./scripts/install_local.sh
# (the variable has to be set on the host side; building on the host clashes with the container's NuGet restore,
# which sees /home where the host sees /var/home).
[ -n "$SKIP_BUILD" ] || build_mod || exit 1

# Delete first, then copy: a running game keeps the old dlls mapped, and overwriting them in place would
# corrupt that game's copy. A deleted file stays readable for the process that has it open.
rm -rf "$DEST"
stage_mod "$DEST" || { echo 'Installing the mod FAILED'; exit 1; }
echo "Installed $(mp_version) to $DEST"

[ -d "$WORKSHOP_DIR/2606448745" ] && echo "WARNING: the Workshop Multiplayer mod (2606448745) is still installed. Unsubscribe from it, it has the same packageId."
[ -d "$WORKSHOP_DIR/2934420800" ] && echo "WARNING: the Workshop Prepatcher mod (2934420800) is still installed. Unsubscribe from it, it can't work on 1.7."
[ -d "$WORKSHOP_DIR/3819577715" ] && echo "WARNING: the Workshop IsoRim Waylith (3819577715) is still installed. IsoRim is built into this mod now: turn the standalone one off (or unsubscribe), or the built-in copy stays unloaded."
[ -d "$WORKSHOP_DIR/3819266222" ] && echo "WARNING: the Workshop IsoRim (3819266222) is still installed. IsoRim is built into this mod now: turn the standalone one off (or unsubscribe)."

# IsoRim's settings file is named after the mod folder (Mod_<folder>_IsoRimMod.xml), so settings saved by a
# standalone IsoRim don't follow it into Multiplayer-1.7. Copy the newest one over once, for the native game and for
# Proton's Windows build (Steam Play keeps its own save data folder inside the game's compatdata prefix).
for cfg in "${XDG_CONFIG_HOME:-$HOME/.config}/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Config" \
           "$RIMWORLD_DIR/../../compatdata/294100/pfx/drive_c/users/steamuser/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Config"; do
    [ -d "$cfg" ] || continue
    target="$cfg/Mod_Multiplayer-1.7_IsoRimMod.xml"
    [ -e "$target" ] && continue
    newest="$(ls -t "$cfg"/Mod_*_IsoRimMod.xml 2>/dev/null | head -n 1)"
    [ -n "$newest" ] && cp -p "$newest" "$target" && echo "Copied IsoRim settings $(basename "$newest") to $(basename "$target") in $cfg"
done

# Keep this game build's Assembly-CSharp.dll, Waylith.dll and UnityStubs.dll for diffing after game updates (1.7.4990
# moved Waylith types the built-in IsoRim uses, and no older Waylith.dll was left to compare with)
GAME_VERSION="$(cut -d' ' -f1 "$RIMWORLD_DIR/Version.txt" 2>/dev/null)"
if [ -n "$GAME_VERSION" ]; then
    for dll in Assembly-CSharp.dll Waylith.dll UnityStubs.dll; do
        if [ ! -f "$HOME/rw-builds/$GAME_VERSION/$dll" ]; then
            mkdir -p "$HOME/rw-builds/$GAME_VERSION"
            cp "$RIMWORLD_DIR/$dll" "$HOME/rw-builds/$GAME_VERSION/" && echo "Saved $dll to ~/rw-builds/$GAME_VERSION/"
        fi
    done
fi
