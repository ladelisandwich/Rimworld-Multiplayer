# Multiplayer for RimWorld 1.7 (personal port)
## Goal
Make this fork of rwmt/Multiplayer run on RimWorld 1.7 with no dependency on 
the Prepatcher mod. Target 1.7 only; 1.6 players keep the official Workshop 
mod. Keep packageId rwmt.Multiplayer so Multiplayer Compatibility and 
MultiplayerAPI users still recognize it.
## Facts about 1.7 (verified Oct 2026)
- Engine: Ludeon's Waylith (Vulkan) replaced Unity. Unity's API comes from 
UnityStubs.dll, a reimplementation that forwards to Waylith. Its coverage is 
undocumented: check the DLL before assuming a Unity member exists or does 
anything.
- Runtime: .NET 10 (CoreCLR), not Mono. Every mono_* P/Invoke throws 
DllNotFoundException; Mono-only members (Thread.internal_thread, 
RuntimeAssembly._mono_assembly) don't exist; Thread.Abort throws 
PlatformNotSupportedException.
- string.GetHashCode(), HashCode.Combine and tuple GetHashCode give different 
values in every process. Never send them over the network, save them, or use 
them in join, sync or desync checks. Use Verse.GenText.StableStringHash.
- Assembly.Load(byte[]) puts each assembly in its own load context.
- Private access through Krafs.Publicizer needs its IgnoresAccessChecksTo 
runtime strategy (on by default). Don't turn it off.
- Mods build for net10.0 and reference Assembly-CSharp.dll, UnityStubs.dll 
and Waylith.dll from the game folder with Private=false. Never ship game 
DLLs.
- Harmony: the Harmony mod (3.0.1.0) ships 0Harmony 3.0.0-rc.3. Reference 
Lib.Harmony 3.0.0-rc.3 with ExcludeAssets="runtime" and never ship 
0Harmony.dll. At install time Harmony 3 refuses patches whose injected 
parameters don't match (__instance, __result, ___fields, ref on 
__originalMethod or __args) and patches that produce invalid IL.
- The .NET JIT can inline small methods, so a prefix on a tiny method may 
never run. Verify important patches actually fire.
- Unity Jobs and Burst are replaced by Waylith.Jobs (WaylithJobRuntime). 
Parallel game code is a desync suspect.
- Prepatcher can't work on 1.7. Don't use it, Mono.Cecil assembly rewriting, 
or mono_* calls.
- Launch flags: -Headless or -NoWindow (replace Unity's -batchmode -
nographics), -mod=<packageIds> (only those mods plus Core and DLCs), -
savedatafolder=<path>, -console.
- Player.log moved into a Logs subfolder of the old log folder.
## This machine
- OS: Bazzite (immutable Fedora). Never use dnf or rpm-ostree; the .NET SDK 
is in ~/.dotnet.
- Claude Code runs inside an Ubuntu 24.04 distrobox (container "claude") that 
shares $HOME with the host. Installing packages in the container with sudo 
apt is fine (git, gh, git-lfs were installed that way). Git pushes over SSH 
as GitHub user ladelisandwich.
- Repo clone: ~/Documents/Rimworld-Multiplayer (not ~/src).
- .NET: SDK 10.0.402 in ~/.dotnet (DOTNET_ROOT and PATH set in ~/.bashrc); 
ilspycmd 11.1.0.9782 is a global dotnet tool in ~/.dotnet/tools.
- RimWorld 1.7 folder: /run/media/ahearn/Big and Slow guy/SteamLibrary/steamapps/common/RimWorld 
(Steam library on the "Big and Slow guy" drive, unstable beta branch). 
Game version 1.7.4982 rev1280 (Steam build 26031547, Oct 19 2026), runtime 
.NET 10.0.13-waylithclr.dev.
- Game binary: <RimWorld folder>/RimWorld (native apphost that runs 
RimWorld.dll)
- Game DLL folder (Assembly-CSharp, UnityStubs, Waylith): the RimWorld folder 
root (RimWorld.dll is there too). Application.dataPath is 
<RimWorld folder>/RimWorld_Data.
- RimWorld.runtimeconfig.json: tfm net10.0, framework Microsoft.NETCore.App 
10.0.13-waylithclr.dev, plus WaylithClr compatibility switches. The ones that 
matter for this port: MonoStringHashCode=true (string.GetHashCode may use 
Mono's algorithm and be stable across processes; verify before relying on it, 
and keep using StableStringHash for anything networked), 
JitPatchableAssemblies=Assembly-CSharp with 
JitNoInlinePatchableIntoDynamicMethods=true (affects the JIT-inlining 
concern; BCL methods like HashCode.Combine are not covered), 
SkipAccessChecks=true, IsolatedLoadsResolveByName=true, 
LaxAssemblyBinding=true, AssemblyNameRedirects 
UnityEngine*/Unity.*/Assembly-CSharp-firstpass -> UnityStubs, 
EnableUnsafeBinaryFormatterSerialization=true.
- Mods folder: /run/media/ahearn/Big and Slow guy/SteamLibrary/steamapps/common/RimWorld/Mods 
(GenFilePaths: parent of dataPath; the game creates it at startup. 
Recreated by the first 1.7.4982 launch on Oct 19, currently empty). Workshop mods: /run/media/ahearn/Big and Slow guy/SteamLibrary/steamapps/workshop/content/294100
- Player.log: ~/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Logs/Player.log 
(RimWorld.dll Bootstrap.ResolveLogDataRoot: honors XDG_CONFIG_HOME, but NOT 
-savedatafolder, which only moves the cache root. Two instances started 
with different -savedatafolder still share this log folder. The older 
../Player.log is from 1.6.)
- Harmony mod 1.7 folder and 0Harmony version: <Workshop folder>/2009463077/1.7/Assemblies, 
mod 3.0.1.0, 0Harmony 3.0.0-rc.3 (AssemblyVersion 3.0.0.0)
- DLCs: Royalty, Ideology, Biotech, Odyssey. No Anomaly, so the psychic 
ritual half of the task 10 ritual test can't be run here.
- Switching between 1.6 and 1.7 resets Config/ModsConfig.xml (the active mod 
list is version-stamped). The 1.6 list was lost on Oct 19; restore it from 
ModLists/*.rml or a save's mod list when going back to 1.6.
- Saved 1.6 Assembly-CSharp.dll for diffing: ~/rw-builds/1.6.4871/Assembly-CSharp.dll
## Build and install
- Build: dotnet build Source/Multiplayer.sln -c Release
- Install into the game: ./scripts/install_local.sh (added in task 5)
## Rules
- Work on branch port-1.7. Commit after each task with a message saying what 
changed and why. Push only to origin (my fork). Never push to rwmt or open 
pull requests there unless I ask.
- Never upload to the Steam Workshop. About/PublishedFileId.txt stays deleted 
once task 5 removes it.
- You can't play the game. When a step needs an in-game check, stop and tell 
me exactly what to do and what to report. You can read Player.log yourself.
- Change only what the task needs: no reformatting, renames or refactors on 
the side.
- Don't hide errors with empty catch blocks or by deleting patches or checks. 
If a feature can't work on 1.7 yet, disable just that feature, log it once, 
and add it to Known gaps.
- Check 1.7's real code with ilspycmd instead of guessing names, signatures 
or IL.
- End every task by updating Progress and Known gaps below.
## Progress
- [x] 1 Branch, tools, this file
- [x] 2 Prepatcher removed, ritual sync on Harmony (in-game check is task 10)
- [ ] 3 Builds for net10.0 with Harmony 3 and 1.7 DLLs
- [ ] 4 Mono layer off
- [ ] 5 Packaged for 1.7 and installed; game loads it
- [ ] 6 First-launch log clean
- [ ] 7 Host and join on one PC
- [ ] 8 Determinism scan and fixes
- [ ] 9 Arbiter, logs, Steam, UI
- [ ] 10 Rituals verified, desync tools
- [ ] 11 Release zip
## Known gaps
- DontSetResolution (debug builds only) is now a Harmony patch applied in the 
mod constructor, so it can't stop resolution changes the game makes before 
mods load. Prepatcher's version ran before game startup.
- Ritual role sync (SyncRituals, 8 hooks) only has build-time checks. Confirm 
in game in task 10 by looking for "MP ritual sync hook hit" lines in a dev-mode 
log.
