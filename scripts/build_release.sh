#!/bin/bash
# Builds Release and zips the mod for RimWorld 1.7 as Multiplayer-1.7-v<version>.zip in the repo root.
# The zip holds one folder, Multiplayer-1.7, to unzip into RimWorld's Mods folder.

source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

build_mod || exit 1

VERSION="$(mp_version)"
ZIP="$REPO_ROOT/Multiplayer-1.7-v$VERSION.zip"
STAGE_DIR="$(mktemp -d)"
trap 'rm -rf "$STAGE_DIR"' EXIT

stage_mod "$STAGE_DIR/Multiplayer-1.7" || { echo 'Staging the mod FAILED'; exit 1; }

rm -f "$ZIP"
(cd "$STAGE_DIR" && zip -r -q "$ZIP" Multiplayer-1.7) || { echo 'zip FAILED'; exit 1; }

echo "Ok, $ZIP is ready"
