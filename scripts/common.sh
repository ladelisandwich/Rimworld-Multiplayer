# Shared helpers for build_release.sh and install_local.sh. Source this file, don't run it.

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RIMWORLD_DIR="${RIMWORLD_DIR:-/run/media/ahearn/Big and Slow guy/SteamLibrary/steamapps/common/RimWorld}"
export RIMWORLD_DIR

# The .NET SDK lives in ~/.dotnet on this machine
if ! command -v dotnet >/dev/null && [ -x "$HOME/.dotnet/dotnet" ]; then
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
fi

mp_version() {
    grep -Po '(?<=SimpleVersion = ")[0-9.]+' "$REPO_ROOT/Source/Common/Version.cs"
}

# Builds Release, which also fills 1.7/Assemblies and 1.7/AssembliesCustom
build_mod() {
    git -C "$REPO_ROOT" submodule update --init --recursive || { echo 'git submodule update FAILED'; return 1; }
    dotnet build "$REPO_ROOT/Source/Multiplayer.sln" -c Release || { echo 'dotnet build FAILED'; return 1; }
}

# Copies the mod (1.7 only, no Source, .git, bin or obj) into $1, which must not exist yet
stage_mod() {
    local dest="$1"

    mkdir -p "$dest/1.7" || return 1
    cp -r "$REPO_ROOT/About" "$REPO_ROOT/Defs" "$REPO_ROOT/Textures" "$REPO_ROOT/LoadFolders.xml" "$REPO_ROOT/LICENSE" "$dest/" || return 1
    rsync -a --exclude ".*" --exclude LICENSE --exclude README.md --exclude "*.py" "$REPO_ROOT/Languages/" "$dest/Languages/" || return 1
    cp -r "$REPO_ROOT/1.7/Assemblies" "$REPO_ROOT/1.7/AssembliesCustom" "$dest/1.7/" || return 1
    stage_isorim "$dest" || return 1

    local version commit isorim
    version="$(mp_version)"
    commit="$(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null)"
    isorim="$(isorim_commit)"
    [ -n "$commit" ] && version="$version ($commit${isorim:+, IsoRim $isorim})"
    sed -i "s|<modVersion>[^<]*</modVersion>|<modVersion>$version</modVersion>|" "$dest/About/About.xml"

    check_staged "$dest"
}

# The upstream IsoRim commit the IsoRim/ subtree was last pulled from
isorim_commit() {
    git -C "$REPO_ROOT" log -1 --grep='^git-subtree-dir: IsoRim$' --format=%B 2>/dev/null \
        | sed -n 's/^git-subtree-split: \([0-9a-f]\{7\}\).*/\1/p' | head -n 1
}

# IsoRim Waylith (the subtree at IsoRim/) ships inside the mod (CLAUDE.md, IsoRim):
# - Models/, LUTs/ and Tools/PackGate go to the mod root, because IsoRim reads them relative to the mod folder.
# - Defs, Languages, Shaders, Textures and IsoRim.dll go to the IsoRim load folder (LoadFolders.xml). Its defs sit one
#   level deeper (IsoRim/Defs/IsoRim/), or its Defs/KeyBindings.xml would hide Multiplayer's (the game keeps one file
#   per path relative to a load folder). Its languages stay in that load folder, or its "ChineseSimplified (...)"
#   folder would hide Multiplayer's ChineseSimplified.
# - Never IsoRim/About (it holds a Workshop PublishedFileId), IsoRim/1.7 (upstream's prebuilt dll), Source or Docs.
stage_isorim() {
    local d="$1" src="$REPO_ROOT/IsoRim" bin="$REPO_ROOT/Source/IsoRim/bin"
    [ -f "$bin/IsoRim.dll" ] || { echo "IsoRim.dll isn't built ($bin)"; return 1; }
    cp -r "$src/Models" "$src/LUTs" "$d/" || return 1
    mkdir -p "$d/Tools/PackGate" "$d/IsoRim/Assemblies" "$d/IsoRim/Defs/IsoRim" || return 1
    cp "$src/Tools/PackGate/IsoRimPackGate.dll" "$d/Tools/PackGate/" || return 1
    cp -r "$src/Defs/." "$d/IsoRim/Defs/IsoRim/" || return 1
    cp -r "$src/Languages" "$src/Textures" "$d/IsoRim/" || return 1
    rsync -a --exclude README.txt "$src/Shaders/" "$d/IsoRim/Shaders/" || return 1
    cp "$bin/IsoRim.dll" "$bin/IsoRim.pdb" "$d/IsoRim/Assemblies/" || return 1
    cp "$src/LICENSE" "$d/IsoRim/LICENSE" || return 1
}

# Fails if the staged mod contains Harmony, game or .NET framework dlls
check_staged() {
    local bad
    bad="$(find "$1" -iname '*.dll' \( -iname '0Harmony.dll' -o -iname 'Assembly-CSharp*.dll' -o -iname 'UnityStubs.dll' \
        -o -iname 'Waylith*.dll' -o -iname 'RimWorld.dll' -o -iname 'UnityEngine*.dll' -o -iname 'com.rlabrecque.steamworks.net.dll' \
        -o -iname 'Brutal.*.dll' \
        -o -iname 'System.*.dll' -o -iname 'Microsoft.*.dll' -o -iname 'mscorlib.dll' -o -iname 'netstandard.dll' \))"
    if [ -n "$bad" ]; then
        echo "Refusing to ship these dlls:"
        echo "$bad"
        return 1
    fi

    if [ -n "$(find "$1" -name PublishedFileId.txt)" ]; then
        echo "Refusing to ship a Workshop PublishedFileId.txt"
        return 1
    fi

    # The game keeps one file per path relative to a load folder, so the same relative path in two load folders
    # silently drops one of them (for example Defs/KeyBindings.xml from Multiplayer and from IsoRim)
    local kind dups
    for kind in Defs Patches Textures Assemblies; do
        dups="$(for lf in . 1.7 IsoRim; do [ -d "$1/$lf/$kind" ] && (cd "$1/$lf/$kind" && find . -type f); done | sort | uniq -d)"
        if [ -n "$dups" ]; then
            echo "These $kind files exist in more than one load folder, the game would load only one of each:"
            echo "$dups"
            return 1
        fi
    done
}
