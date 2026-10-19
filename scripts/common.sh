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
    cp -r "$REPO_ROOT/About" "$REPO_ROOT/Defs" "$REPO_ROOT/Textures" "$REPO_ROOT/LoadFolders.xml" "$dest/" || return 1
    rsync -a --exclude ".*" --exclude LICENSE --exclude README.md --exclude "*.py" "$REPO_ROOT/Languages/" "$dest/Languages/" || return 1
    cp -r "$REPO_ROOT/1.7/Assemblies" "$REPO_ROOT/1.7/AssembliesCustom" "$dest/1.7/" || return 1

    local version commit
    version="$(mp_version)"
    commit="$(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null)"
    [ -n "$commit" ] && version="$version ($commit)"
    sed -i "s|<modVersion>[^<]*</modVersion>|<modVersion>$version</modVersion>|" "$dest/About/About.xml"

    check_staged "$dest"
}

# Fails if the staged mod contains Harmony, game or .NET framework dlls
check_staged() {
    local bad
    bad="$(find "$1" -iname '*.dll' \( -iname '0Harmony.dll' -o -iname 'Assembly-CSharp*.dll' -o -iname 'UnityStubs.dll' \
        -o -iname 'Waylith*.dll' -o -iname 'RimWorld.dll' -o -iname 'UnityEngine*.dll' -o -iname 'com.rlabrecque.steamworks.net.dll' \
        -o -iname 'System.*.dll' -o -iname 'Microsoft.*.dll' -o -iname 'mscorlib.dll' -o -iname 'netstandard.dll' \))"
    if [ -n "$bad" ]; then
        echo "Refusing to ship these dlls:"
        echo "$bad"
        return 1
    fi
}
