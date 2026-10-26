# Multiplayer for RimWorld 1.7 (personal port)
## Goal
Make this fork of rwmt/Multiplayer run on RimWorld 1.7 with no dependency on 
the Prepatcher mod. Target 1.7 only; 1.6 players keep the official Workshop 
mod. Keep packageId rwmt.Multiplayer so Multiplayer Compatibility and 
MultiplayerAPI users still recognize it. Since Oct 26 the mod also ships 
IsoRim Waylith (isometric 3D view, MIT) built in, as one mod (see Build and 
install, IsoRim, and Known gaps, IsoRim).
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
- Harmony 3's bundled MonoMod (Core100Runtime) disables inlining of every 
method it patches, at the right MethodDesc offset (checked in 0Harmony 
3.0.0-rc.3). It can't stop inlining into callers that were already JIT'ed. 
Never write into MethodHandle/TypeHandle memory: Mono's layouts corrupt 
CoreCLR's MethodDesc (found in task 6: MpUtil.MarkNoInlining).
- On .NET 10, Enum.GetHashCode is stable across processes; System.HashCode 
is not (random seed per process, GenerateGlobalSeed), and the game runtime's 
ValueTuple.GetHashCode and RuntimeMethodInfo.GetHashCode both go through 
HashCode.Combine, so they differ per process too (the Oct 19 "ValueTuple is 
stable" check was wrong: it ran while Multiplayer's old HashCode.Combine 
patch was active). Assembly-CSharp 1.7 makes no direct string.GetHashCode 
call. Its simulation-relevant HashCode.Combine uses are int overloads used 
as seeds (Map.NextGenSeed, Gravship.ConstantRandSeed, trap avoidance, 
LayoutWorker RandBlocks), replaced at those call sites by 
SeedHashCombinePatch. Never patch a BCL hash function process-wide or make 
it depend on session state: every hash table filled before the switch loses 
its keys (found Oct 20, see Known gaps, caravans).
- 1.6 -> 1.7: all 315 lambda lookups resolve to the same members with the 
same IL; the nine transpiled methods with changed IL are benign (see 
docs/rwdiff-1.6-1.7.md, Review).
- Tiered compilation drops Harmony patches (found in task 6, 1.7.4982). A 
patch runs until the runtime re-JITs the hot method at tier 1, then the 
original body runs again with no error logged. Seen on 
TickManager.TickManagerUpdate (1 patched frame, then 2125 vanilla), 
LoadedObjectDirectory.RegisterLoaded, Pawn_JobTracker.StartJob/EndCurrentJob, 
GenSpawn.Spawn, ThingWithComps.InitializeComps. MonoMod has a re-apply path 
(its compileMethod JIT hook; the game's libclrjit.so carries the JIT GUID 
MonoMod expects) but it doesn't take effect on WaylithClr; cause unknown. 
Workaround: launch with DOTNET_TieredCompilation=0 (Steam launch options 
"DOTNET_TieredCompilation=0 %command%"). HarmonyPatchWatchdog logs an error at 
startup when tiering is on, offers a restart with it off at the main menu 
(GenCommandLine.Restart -> Waylith RestartManager relaunches the same exe 
with the same args and inherits the environment; marker env var 
MultiplayerRestartedForTiering stops a loop), and logs an error in game 
when the TickManagerUpdate patch stops firing. To dig into MonoMod, set MONOMOD_LogToFile=<path> (and 
MONOMOD_LogSpam=1) in the environment and look for "Updating detour" lines.
- With tiered compilation off, every method is JIT'ed fully optimized on 
its first call, so a tiny method gets inlined into any caller compiled 
before Harmony patched it. MonoMod's NotInline flag only protects callers 
compiled after the patch. Found in task 6: LoadedObjectDirectory.Clear (24 
bytes of IL) inlined into CrossRefHandler.Clear, compiled during mod 
settings loading, so LoadedObjectsClearPatch never ran and every synced 
exposable wiped SharedCrossRefs. Fixed by doing the work in ScribeUtil 
itself. Other small targets reachable during startup (Prefs getters in 
SettingsPatches, DirectXmlSaver.XElementFromObject) deserve the same 
suspicion. Audit: list patch targets, read IL body sizes with 
System.Reflection.Metadata, look at anything under ~40 bytes.
- Harmony 3 verifies generated IL. Returning a struct needs 
Ldloca/Initobj/Ldloc, not just Initobj (SyncTemplates, fixed in task 6).
- Harmony 3 rejects __instance when the target is a lambda on a <>c 
display class (PatchGravshipPreLaunchCancel, fixed in task 6).
- ConstructorInfo.Invoke on a type initializer (TypeInitializer.Invoke) re-ran 
the static constructor on Mono. On .NET it's a no-op once the type is 
initialized. MultiplayerGame used it to reset DebugSettings and 
SymbolResolver_SingleThing; on 1.7 that left every DebugSettings bool false 
(no damage, no storyteller, and Effecter children skipped because 
anomalyDarkeningFX gates them, which crashed every progress-bar toil). Fixed 
in task 6 by snapshotting the defaults at startup. RuntimeHelpers.RunClassConstructor 
only ensures a cctor ran, it never re-runs it either.
- Unity Jobs and Burst are replaced by Waylith.Jobs (WaylithJobRuntime). 
Parallel game code is a desync suspect.
- Prepatcher can't work on 1.7. Don't use it, Mono.Cecil assembly rewriting, 
or mono_* calls.
- Launch flags: -Headless or -NoWindow (replace Unity's -batchmode -
nographics), -mod=<packageIds> (only those mods plus Core and DLCs; since 
1.7.4990 only mods in the game's own Mods folder, Workshop entries are 
skipped with "-mod: '<id>' is not in the local Mods folder, ignoring", so 
Harmony from the Workshop can't be picked that way), -
savedatafolder=<path> (moves the whole save data folder, Config included: 
GenFilePaths.ConfigFolderPath), -console.
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
Game version 1.7.4990 rev1069 (Steam update Oct 26 2026, 12:03; 1.7.4985 
rev1269 on Oct 21; 1.7.4984 rev1242 on Oct 20; 1.7.4982 rev1280, Steam build 
26031547, Oct 19), runtime .NET 10.0.13-waylithclr.dev (4990 replaced 
coreclr, clrjit, CoreLib and ~40 BCL dlls without changing that string; the 
hashing facts above still hold). The mod builds against 1.7.4990 with no 
errors. 1.7.4984 -> 1.7.4985: identical IL. 1.7.4985 -> 1.7.4990 (Oct 26, 
docs/rwdiff-1.7.4985-1.7.4990.md, Review): every lambda lookup and 
transpiled method unchanged; a whole-assembly compare found only Waylith's 
texture types moving from Waylith.Rendering to Waylith.Graphics.Textures 
(TexturePixelData, BcEncodeTarget, TextureCompressionSettings, 
ITextureCacheSource, Mipmapper to .Mips), GraphicData.uncompressed, the -mod= 
change above and two UI details; the determinism and realtime scans list the 
same call sites. Nothing in Multiplayer needed a change; the built-in 
IsoRim did (Known gaps, IsoRim). install_local.sh now also keeps Waylith.dll 
and UnityStubs.dll per version in ~/rw-builds/<version>/ (first: 1.7.4990), 
since no older copies existed to diff this time. 
RwDiff and DeterminismScan rerun for 1.7.4984 (Oct 20, 
docs/rwdiff-1.7.4982-1.7.4984.md): every lambda lookup and transpiled 
method unchanged, and a whole-assembly IL compare found only six changed 
methods (ResolutionUtility.Update, CultureInfoUtility.EnsureEnglish, 
SteamUtility.OpenUrl/OpenSteamWorkshopPage, WorldFeatures.CreateOrDestroyTexts, 
ProfilerVramTable.Export), none of them patched or simulation-relevant; the 
determinism and realtime scans list the same call sites. No patch changes 
needed. The update also replaced UnityStubs, Waylith, RimWorld.dll and the 
Steamworks dll (not diffed, no old copies), not the native runtime.
- Game binary: <RimWorld folder>/RimWorld (native apphost that runs 
RimWorld.dll). Since Oct 22 Steam forces Proton (config.vdf CompatToolMapping 
294100 = proton-stable), so the folder holds the Windows build (RimWorld.exe, 
coreclr.dll) and no native apphost: run_two.sh and native headless runs 
can't start until the user unticks "Force the use of a specific Steam Play 
compatibility tool" in RimWorld's Properties, Compatibility. Under Proton the 
save data folder (saves, MpReplays, Config, Logs/Player.log) is 
<library>/steamapps/compatdata/294100/pfx/drive_c/users/steamuser/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios 
(library = "Big and Slow guy"); Steam Cloud syncs only Saves/*.rws between it 
and the native folder, never MpReplays. A headless Windows-build run for 
tests: on the host, STEAM_COMPAT_CLIENT_INSTALL_PATH=$HOME/.local/share/Steam 
STEAM_COMPAT_DATA_PATH=<a folder under $HOME> python3 "<Fast_AF_Guy library>/steamapps/common/Proton 11.0/proton" 
run ./RimWorld.exe -Headless ... (the prefix must be under $HOME: the 
container's /tmp isn't the host's).
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
- The distrobox can see /run/media/ahearn/Big and Slow guy read-only (seen 
after a suspend and resume on Oct 26) while the host mounts it read-write 
with no disk errors. Then build in the container and install from the host, 
from the repo root: distrobox-host-exec env SKIP_BUILD=1 
./scripts/install_local.sh (the variable must be set on the host side). 
Don't build on the host: NuGet's restore data clashes (/var/home there, 
/home here).
- Harmony mod 1.7 folder and 0Harmony version: <Workshop folder>/2009463077/1.7/Assemblies, 
mod 3.0.1.0, 0Harmony 3.0.0-rc.3 (AssemblyVersion 3.0.0.0)
- DLCs: Royalty, Ideology, Biotech, Odyssey. No Anomaly, so the psychic 
ritual half of the task 10 ritual test can't be run here.
- Switching between 1.6 and 1.7 resets Config/ModsConfig.xml (the active mod 
list is version-stamped). The 1.6 list was lost on Oct 19; restore it from 
ModLists/*.rml or a save's mod list when going back to 1.6.
- Saved 1.6 Assembly-CSharp.dll for diffing: ~/rw-builds/1.6.4871/Assembly-CSharp.dll
## Build and install
- Build: dotnet build Source/Multiplayer.sln -c Release (RimWorldDir defaults 
to the game folder above; set RIMWORLD_DIR to override). Tests: dotnet test 
Source/Tests -c Release.
- Source/Directory.Build.props drops the Krafs.Rimworld.Ref 1.6 package that 
RimWorld.MultiplayerAPI 0.6.0 pulls in, so only the 1.7 game DLLs are 
referenced. Steamworks comes from com.rlabrecque.steamworks.net.dll.
- The GitHub workflows are disabled on this branch (.github/workflows/*.yml 
renamed to .yml.disabled): they build the old setup and have no game DLLs. 
Source/TestsOnMono is out of the solution (Mono only).
- Install into the game: ./scripts/install_local.sh (builds, then replaces 
<Mods folder>/Multiplayer-1.7; warns about leftover Workshop Multiplayer and 
Prepatcher; saves the game's Assembly-CSharp.dll, Waylith.dll and UnityStubs.dll to 
~/rw-builds/<game version>/).
- Release zip: ./scripts/build_release.sh -> Multiplayer-1.7-v<version>.zip in 
the repo root, holding one Multiplayer-1.7 folder. Both scripts share 
scripts/common.sh, which refuses to ship Harmony, game or .NET dlls.
- Tools (not part of the mod): dotnet run -c Release --project 
Source/Tools/DeterminismScan -- <Assembly-CSharp.dll> docs/determinism-scan.md 
(keeps the hand-written "## Multiplayer assessment" section; add --realtime 
before the dll for the frame/wall-clock/System.Random scan, 
docs/realtime-scan.md); 
python3 scripts/mp_targets.py . > targets.txt && dotnet run -c Release 
--project Source/Tools/RwDiff -- ~/rw-builds/<old>/Assembly-CSharp.dll 
<new Assembly-CSharp.dll> targets.txt docs/rwdiff-<old>-<new>.md (keeps 
"## Review"). Re-run both after a game update.
- In game, dev mode: Debug actions > Multiplayer > "Patch hit counts" logs 
how often each determinism patch ran (PatchHitCounter) and the ritual sync 
hook hits; "Patch hit counts: reset" zeroes them.
- Mod layout: About, Defs, Languages, Textures and LoadFolders.xml (v1.7 -> "/" 
and "1.7") at the root; the build writes 1.7/Assemblies (MultiplayerLoader and 
third-party dlls, loaded by the game) and 1.7/AssembliesCustom (Multiplayer and 
MultiplayerCommon with pdbs, loaded by MultiplayerLoader). Version is 
0.11.5.17 (MpVersion.SimpleVersion).
- IsoRim: IsoRim Waylith (https://github.com/peren717/IsoRim-1.7, a 1.7 port 
by Lin/peren717 of Connor/kndzio's IsoRim, MIT) is a squashed git subtree at 
IsoRim/ (remote "isorim"). Update it with git subtree pull --prefix=IsoRim 
isorim master --squash, then diff IsoRim/Source/IsoRim.csproj against 
Source/IsoRim/IsoRim.csproj (the port's wrapper, which compiles 
IsoRim/Source/**/*.cs plus Source/IsoRim/MpBridge.cs into its own IsoRim.dll 
with the port's RimWorldDir and Lib.Harmony; upstream's csproj and its 
tracked 1.7/Assemblies/IsoRim.dll are never built or shipped). Multiplayer 
fixes inside the subtree are one-line guards marked "MP:" calling MpBridge 
(port-owned, uses only 0MultiplayerAPI); fixes for game updates or port 
robustness are marked "Port". After every pull, re-check each new 
IsoRim hook that can run in a tick, command or load: no branch on the 3D 
view or IsoRim settings, no game-state writes, no Rand outside 
PushState/PopState. scripts/common.sh stage_isorim lays it out: 
Models/, LUTs/ and Tools/PackGate at the mod root (IsoRim reads them 
relative to the mod folder), and Defs (nested as IsoRim/Defs/IsoRim/), 
Languages, Shaders, Textures and Assemblies/IsoRim.dll in the IsoRim load 
folder, listed last in LoadFolders.xml with IfModNotActive 
"Peren.IsoRim17,connor.isorim". Never put IsoRim.dll in 1.7/Assemblies (a 
load failure there stops MultiplayerLoader) or AssembliesCustom (IsoRimMod 
would never be constructed). check_staged refuses duplicate paths across 
load folders, Brutal.*.dll and PublishedFileId.txt. The modVersion shows the 
IsoRim commit: "0.11.5.17 (<port commit>, IsoRim 3fff2ec)".
- Two instances on this PC: ./scripts/run_two.sh (host, then Enter, then a 
client that auto-joins 127.0.0.1:30502 with -connect=). Each instance has 
its own XDG_CONFIG_HOME and -savedatafolder under ~/.rw-mp-test/<host|client>; 
the log is <that>/config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Logs/Player.log 
and the Config folder <that>/Config. The mod list is a ModsConfig.xml the 
script writes there on every start (Harmony, Core, DLCs, rwmt.multiplayer; 
the game's Mods folder is listed before the Workshop, so this port wins 
over the Workshop Multiplayer), not -mod= (Workshop mods refused since 
4990). They run outside Steam, so Steam features are off. The same 
ModsConfig works for headless Proton runs: write it into the prefix's 
.../LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Config and drop -mod= 
(checked Oct 26: Harmony from the Workshop, Multiplayer from 
Mods/Multiplayer-1.7).
- Arbiter: the "Run arbiter" checkbox in the host window only exists in debug 
builds (HostWindow, MpVersion.IsDebug); release builds host without it. On 
1.7 HostUtil skips StartArbiter with a warning even when the setting is on.
- Test launch: Steam launch options "DOTNET_TieredCompilation=0 %command%" 
with the mod list set in game (Harmony, Multiplayer). The old 
"-mod=brrainz.harmony,Multiplayer-1.7" stopped working with 1.7.4990: -mod= 
now skips Workshop mods, so Harmony would be dropped. The user's Steam launch 
doesn't pass -mod= (no "Running with command line mod list" line in the 
Oct 26 log).
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
- [x] 3 Builds for net10.0 with Harmony 3 and 1.7 DLLs
- [x] 4 Mono layer off
- [x] 5 Packaged for 1.7 and installed; game loads it (main menu and hosting reached Oct 19)
- [x] 6 First-launch log clean (hosted session on 1.7.4982, Oct 19: 0 errors, no Multiplayer warnings)
- [x] 7 Host and join on one PC (Oct 19: client joined and created a faction, ~15 min of play, 0 errors, no desync; the long session is task 8's test)
- [ ] 8 Determinism scan and fixes (tools, assessment, hit counters, FastTileFinder.Closest done Oct 19). 65-min two-instance session Oct 19 13:52-14:57: 0 game errors from the mod, but the client desynced 4 times on its own map ("Wrong random state on map 1": 2 min after creating the faction, then at 14:44 and within a minute of each of the two rejoins). Reports hold no traces (desync tracing is Mono-only until task 10), so the cause is open; "Patch hit counts" wasn't run (dev mode off). Rerun with dev mode on and the hit counts dumped before quitting once task 10 brings traces back. Tools rerun for 1.7.4984 on Oct 20: no new call sites, no patch changes (see This machine).
- [ ] 9 Arbiter, logs, Steam, UI
- [ ] 10 Rituals verified, desync tools (Oct 19: .NET desync traces 
(ManagedStackTracing) and the JIT list (EventListener) built and checked in 
game at 16:30: both sides' reports carry readable traces and ~1500 jitted 
methods; a false "Trace hashes don't match" from InvokeStub_* frames fixed 
afterwards. Ritual test still pending (no "MP ritual sync hook hit" lines in 
any log yet; the Oct 19 18:04 hit counts show all ten hooks at 0 after a 
ritual test, so either the test didn't change roles or the hooks don't run 
on .NET; SyncRituals now also counts prefix runs that matched no target. 
Likely cause found Oct 20: SyncRituals keys its hooks by a (token, Type) 
tuple, and the old HashCode.Combine patch changed tuple hashes once a 
session started, so no lookup could match; fixed with the caravan bug, 
ritual test still to be redone in dev mode: the Oct 20 session that 
changed a ritual role ran with dev mode off, so no hook lines were logged. 
Oct 21: the hooks now fire, and dragging a pawn onto a role threw 
InvalidCastException; see Known gaps, ritual role sync). 
The recurring genuine desync was traced at 16:45 (Desync-10): the client's 
copy of a host colonist entered a cell a tick apart from the host's, cause 
the variable tick rate (Known gaps). Confirmed by the 18:04 hit counts: 
GenTicks.GetCameraUpdateRate 0 on both sides, the getter patches at 11.5M 
and 39M. That session ran 18 minutes instead of 5 and then desynced on map 
0 at tick 63012 (Desync-11): the client's copy of host colonist Human127 
had a hediff with hediffGivers (ToxicBuildup, DrugOverdose, 
HeartArteryBlockage, NuclearStomach or a stage of one) that the host's 
didn't, with every earlier random call identical. A per-tick pawn state 
digest (PawnStateTrace) now rides the trace hash list so the next report 
names the pawn and field that first differ.)
- [ ] 11 Release zip
- [ ] Extra (Oct 19, user request): multifaction fixes. Custom and fluid 
ideoligions for new factions (Page_ConfigureIdeo_Multifaction), quest raids 
no longer fire once per faction (StorytellerFactionTick), per-faction settle 
tick so a new faction gets the early-game threat ramp and grace periods 
(FactionWorldData.settleTick). First in-game check (Oct 19 15:20): the 
custom-ideo command was rejected on every client ("String too long"), fixed 
by sending the ideo gzipped in pieces; a preset-ideo faction was created 
fine. Second check (Oct 19 15:41): a client faction with a fluid ideo 
created end to end, 0 errors, no desync, but the editor was read-only 
(draft injected outside the window draw; fixed in e9d2d34). Third check 
(Oct 19 16:03): precepts editable, custom-ideo faction created, both logs 
with 0 errors and 0 exceptions, no desync. Not yet seen in game: a starting 
pawn with an addiction (the ReadExposable fix), a doubled quest raid, the 
settle clock.
- [ ] Extra (Oct 26, user request): IsoRim Waylith built into the mod as one 
mod, made safe for multiplayer, Multiplayer's overlays made to work in 3D 
(see Known gaps, IsoRim). Builds, tests pass, staged layout checked; in-game 
check pending. First two user-chosen improvements (Oct 26): idle drafted 
pawns keep their facing in 3D in multiplayer too, drawn only (MpFacing), 
and other players' cursors, drag boxes and pings sit on the surface the 3D 
view shows, with a click on a player in the chat window's list jumping to 
their cursor (MpView, IsoRimView). Checked headless on Proton (tracker 
transpilers present, binding complete, the facing memory kept and cleared as 
designed, 0 errors); in-game check pending. Oct 26 afternoon: Steam moved 
the game to 1.7.4990 a minute after that build, and the user saw walls no 
longer fold down in 3D (only doors); fixed by building IsoRim against 4990 
(Known gaps, IsoRim, 1.7.4990). Headless Proton check on 4990: every method 
of IsoRim and the Multiplayer dlls JIT-compiles, 0 errors. Confirmed in game 
by the user (Oct 26 12:46, hosted alone on 4990 with bbed3215): walls fold 
again, Player.log 0 errors; idle drafted pawns keep their facing in 3D in 
that hosted session (MpFacing, no "replaced N rotation writes" error). The 
two-player check of the IsoRim features is still pending.
## Known gaps
- DontSetResolution (debug builds only) is now a Harmony patch applied in the 
mod constructor, so it can't stop resolution changes the game makes before 
mods load. Prepatcher's version ran before game startup.
- Ritual role sync (SyncRituals): the targets (TryAssign, TryAssignReplace, 
TryAssignAnyRole, two DrawPawnListInternal lambdas) are private methods of 
the generic PawnRoleSelectionWidgetBase<T>, and its RitualRole and 
PsychicRitualRoleDef instantiations share one compiled body. Harmony's 
replacement is built for one instantiation with concrete types, and the 
second patch wins, so with both patched an ordinary ritual ran the psychic 
replacement: "Unable to cast Grouping<String, RitualRole> to 
IEnumerable<PsychicRitualRoleDef>" in OnGUI and again as "Map cmd exception 
(Sync)" on every client (Oct 21 host log, a drag and drop onto a role). Now 
only the ritual widget is patched when Anomaly is off (psychic rituals need 
Anomaly; checked headless: one prefix on the RitualRole instantiation, 
none on the psychic one, and a call with RitualRole arguments reaches the 
original body). With Anomaly active SyncRituals logs an error and patches 
nothing, so ritual role changes aren't synced for Anomaly owners until the 
hooks dispatch per instantiation (for example a prefix on one instantiation 
that calls a Harmony reverse patch of the other for the other widget 
type). Still to confirm in game: "MP ritual sync hook hit" lines in a 
dev-mode log and the role change applied on both sides.
- UnityStubs gaps found at compile time (task 3), each worked around in place 
(all still missing, along with TextEditor.m_RevealCursor, in the 1.7.4984 UnityStubs):
  - Font.fontNames is missing: the debug tools font tweak in VanillaTweaks 
  (UIRootPatch) skips the OS font fallback list and logs that once.
  - TextEditor.UpdateScrollOffsetIfNeeded is missing: the MpUI password field 
  no longer refreshes its scroll offset after masking (long passwords may not 
  scroll with the cursor).
  - SystemInfo.graphicsDeviceVendor is missing: desync reports show the PCI 
  vendor id (graphicsDeviceVendorID) as "GPU Family".
  - SystemInfo.deviceUniqueIdentifier is missing: the main menu animation seeds 
  its Random from deviceName.
- MpUtil.GetOriginalFromHarmonyReplacement returns null when not on Mono (it 
maps native addresses from the Mono stack walker; Harmony's public API can't).
- Mono-only features switched off on .NET (task 4, Native.IsMono is false):
  - Deferred stack tracing (DeferredStackTracingImpl, the native stack walker) 
  stays Mono only. On .NET, ManagedStackTracing (task 10) records a 
  System.Diagnostics.StackTrace for the first 200 random calls of each tick 
  while the "Desync tracing: slow (.NET)" setting is on (measured ~20 µs per 
  capture at the game's stack depth, so ~4 ms per tick; off by default; the 
  setting is now persisted). Frames are hashed by declaring type and method 
  name with GenText.StableStringHash; frames from System.*, HarmonyLib, 
  MonoMod and the tracer itself are dropped (their IL differs per OS and the 
  frame count is part of the hash); Harmony's _PatchN suffix is stripped. 
  Trace hash lists are only compared when both sides recorded some, so one 
  player tracing alone can't cause a false "Trace hashes don't match", but 
  two tracing players must run the same build. The host's "Log desync 
  traces" server setting (on by default) must be on as well. "Trigger 
  desync" records one trace regardless of the setting. Checked in game Oct 
  19 (Desync-06..08 under ~/.rw-mp-test/client/MpDesyncs): traces and the 
  JIT list present on both sides, hashes equal for equal traces. Two 
  lessons: (1) dynamic methods without a declaring type other than Harmony's 
  _PatchN replacements must be dropped (InvokeStub_<Type>.<Method> appears 
  on one client and not the other, since CoreCLR emits it for a reflection 
  target only after some invocations; InvokeMessageMethod_* is the engine's); 
  (2) the trace budget is per map and world, not per tick, or the first map 
  uses the 200 and the others never get traced. The traces also show that 
  the random state checks compare only the iteration count (state >> 32): 
  the old "Trigger desync" bumped the seed, which only the trace hashes 
  noticed; it now bumps the iteration count.
  - JittedMethods: the Mono profiler callback is replaced on .NET by an 
  EventListener on the runtime's Microsoft-Windows-DotNETRuntime 
  MethodLoadVerbose events (JIT keyword 0x10), started only with slow tracing 
  on (at startup or when the setting is switched on). Kept because with 
  tiered compilation off a method is compiled on its first call, so the list 
  names the code paths that ran for the first time before a desync. Events 
  arrive on the listener's thread, so tick stamps can be a few frames late 
  and the calling method is unknown. Untested in game.
  - DisableOmitFramePointer (Mono JIT option): not applied.
  - Native.MethodNameFromAddr / MethodNameNormalizedFromAddr return null, 
  GetMethodAggressiveInlining returns false, CctorRan throws 
  PlatformNotSupportedException (it has no callers).
  - Debug UI: "Dump addr info table" only logs that it's Mono only; the sync 
  debug panel and ingame debug text show no stack trace hash table stats.
  - ArbiterWindowFix (reparents Unity's batch mode window on Windows) is skipped; 
  the arbiter itself is task 9.
  - SimpleProfiler is only available on Windows when simple_profiler.dll is 
  loaded (GetModuleHandle is kernel32).
  - The MpUI password field can't set TextEditor.m_RevealCursor (UnityStubs has 
  no such field).
- MultiplayerLoader now loads AssembliesCustom with Assembly.LoadFrom, like 
1.7's ModAssemblyHandler, so a running game keeps the dlls mapped. Installing 
over a running game must replace files (delete, then copy), not overwrite them 
in place.
- Tiered compilation must be off (DOTNET_TieredCompilation=0) or Harmony 
patches get dropped mid-session (see Facts). Friends need the launch option 
too until there's an in-mod fix. Candidate in-mod fix: clear 
MethodDesc.m_bFlags2 bit 0x20 (IsEligibleForTieredCompilation) on each 
original before Harmony compiles it (prefix on 
HarmonyLib.PatchFunctions.UpdateWrapper); only helps methods not yet 
compiled, so startup-compiled targets (UIRootOnGUI, LongEventsUpdate, Log) 
would still need tiering off. Needs its own in-game test with tiering on.
- Test saves: a save from a modded 1.6 game loaded with -mod= loses those 
mods and logs game errors (research, apparel, jobs) that have nothing to do 
with Multiplayer. Test with a new colony or a vanilla save.
- ScribeUtil.SupplyMissingThingRefs and LogSharedCrossRefsSummary (task 6) 
stay in as diagnostics: a "references Thing_... which isn't in the shared 
cross refs" warning or a nonzero "spawned things missing" count means the 
shared directory lost entries again.
- Arbiter (task 9): not started on 1.7, HostUtil hosts without it.
- Steam rejoin (Oct 21): after a desync the client's next connection sent a 
join packet while the server still listed its old connection, and 
SteamP2PNetManager logged "Received a join packet: True for player ..." 
and dropped it, every retry, until the old connection timed out. A join 
on a different channel from a listed Steam user (each client connection 
picks a random channel) now disconnects the old connection and joins.
- Per-process hashes (task 7): PawnBio def info, desync log info hashes, 
ModFile.GetHashCode and StackTrace.Hash now use GenText.StableStringHash. 
The remaining GetHashCode calls in the client are local-only (window ids, 
tooltip ids, dictionary keys, menu animation seed).
- Multifaction "create faction" (FactionCreator) deep-saves pawns generated in 
the interface, which carry negative ids. ScribeUtil.ResolveAllCrossReferences 
must call SharedCrossRefs.ReassignNegativeIdsInTemp before dropping temp 
keys (it replaced the CrossRefHandler.Clear path that 
CrossRefHandler_Clear_Patch hooked). Missed in the first version: pawns 
with ids like -3 reached the simulation and WorkGiver_Fish crashed on 
CardinalDirections[(i + thingIDNumber) % 4].
- "X did not have an ideo set; assigning fallback ideo" appears once per pawn 
when a client creates a faction: FactionCreator deep-loads the UI-generated 
pawns before their ideo exists. Pre-existing upstream behaviour, harmless.
- A client that is catching up (simulating many ticks per frame) shows downed 
or resting pawns rolling fast: the PawnDownedWiggler runs per tick. Cosmetic, 
same on 1.6.
- Stability notes for the port: docs/desync-risks-1.7.md (checked facts, 
watch items, untested cross-OS float math).
- Found in the Oct 19 long-run logs (~/.rw-mp-test/*/config/.../Logs/Player.log, 
replays in ~/.rw-mp-test/host/MpReplays):
  - Desync reports said "Log file not found:/tmp/rimworld_log": LogGenerator 
  used Unity's log paths. On 1.7 it now reads Application.consoleLogPath 
  (UnityStubs: <log data root>/Logs/Player.log). Fixed.
  - Server NRE "Exception ticking the server" in PacketWriter.BindRemaining 
  when rejecting a duplicate connection (UsernameAlreadyOnline): the 
  ServerDisconnectPacket had no data. BindRemaining now writes nothing for 
  null. The duplicate connection itself came from the client's -connect= 
  retry; the second attempt joined fine.
  - "Inconsistent player <host> map. Last known map: -1, however received 
  command with transition: 0 -> 1" (server, once): VTRSync only reports the 
  viewed map on change, so the server learns the host's first map late. 
  Upstream behaviour, harmless (commands still reach the host).
  - "Tried to double-add designation" on both sides: the client designated 
  the same wall twice before the first command executed. Upstream behaviour, 
  the second designate throws and is skipped on every client alike.
  - "Could not resolve reference to object with loadID Battle_N" ×8 on the 
  client while loading the rejoin save, and "Thing-needing designation (Hunt 
  target=null)" on both sides: vanilla load-time cleanup of dead references; 
  deterministic.
  - The repeated desync (14:44, then right after each rejoin) is on the 
  client's own map, which the host doesn't view. Map.MapUpdate is identical 
  in 1.6 and 1.7 and the map held nothing unusual (3 colonists, dormant 
  mechs, corpses, wild animals), so no 1.7-specific lead; needs traces.
- Caravans couldn't enter any settlement (Oct 19 Steam sessions with a 
friend). First the right-click on the tile did nothing: 
SyncActions.SyncAction2_Postfix looked up __originalMethod in a 
Dictionary<MethodBase, ISyncAction> and threw KeyNotFoundException for 
Settlement.GetFloatMenuOptions, taking the whole OnGUI pass down (c41586d 
made a miss log once instead). Then the menu opened unsynced, so a 
client's "enter colony" ran on that client alone, desynced it, and the 
rejoin put the caravan back, in a loop. Cause, reproduced Oct 20 with a 
headless -quicktest run that hosted programmatically and called the menu: 
upstream's HashCode.Combine<int x2..8> prefixes returned a deterministic 
hash only while Multiplayer.Client was set. RuntimeMethodInfo.GetHashCode 
and ValueTuple.GetHashCode go through HashCode.Combine on this runtime, so 
the SyncActions table (filled at startup) still held all 5 keys after 
hosting but no lookup could find them. The same flip broke the ritual hook 
table (tuple keys) and any other MethodInfo- or tuple-keyed table spanning 
a session start or end, Harmony's own patch state included. Fix: the global 
prefixes are gone; SeedHashCombinePatch transpiles the four seed call sites 
(Map.NextGenSeed, Gravship.ConstantRandSeed, Building_Trap.KnowsOfTrap, 
LayoutWorker.FillAllRooms) to the same deterministic functions, logging an 
error if a call site no longer has the call. MethodIdentityComparer stays 
(hash from metadata tokens only). Verified headless after the fix: tuple and 
MethodInfo hashes unchanged across hosting, the lookup hits, and the menus 
of the player's colony and a foreign settlement go through 
SyncAction.DoSync. Checked in game Oct 20 16:41-18:42 (Steam, host and 
Windows friend, both on 1659f15 and 1.7.4984): caravans entered colonies 
and a quest site, no SyncActions miss on either side, and the desync 
reports list the active Harmony patches again (they said "No patches have 
been reported" before: Harmony's patch state is a MethodBase-keyed table 
too). One desync in 1h50 (18:31, "Wrong random state on map 1", the 
friend's side, no traces because slow tracing was off), cause unknown. 
The site entry showed a second asymmetry: MapGenFactionPatch looked the 
arriving caravan up with PlayerControlledCaravanAt, which compares with 
Faction.OfPlayer, and caravan map gen runs in a long event outside the 
tick, so only the owner's game found the caravan and the host generated 
the site under its own faction ("Couldn't set the faction context for map 
gen" on the host only). It now takes any player-faction caravan on the 
tile. 
Note the Steam launch no longer passes -mod= (no "Running with command line 
mod list" line), so the saved mod list applies: Prepare Carefully (1.6) and 
Replace Stuff Continued (1.7) are active in those sessions, and the friend 
joined with "Mod list diff: WrongOrder" and "Configs match: False".
- Variable tick rate (game feature since 1.6): Thing.DoTick runs TickInterval 
(needs, health, job tracker interval, mind state) every UpdateRateTicks ticks, 
and vanilla takes that rate from the camera (1 to 5 in view, 15 off screen 
or on another map). Upstream's VTRSyncPatch prefixes 
GenTicks.GetCameraUpdateRate to return a synced per-map rate instead. Its 
only caller is the 6-byte Thing.UpdateRateTicks getter, which on .NET can 
inline the callee and skip the prefix, so each client ticked pawns at its 
own camera's rate. Oct 19 Desync-10 traces: on map 0 the client's copy of 
the host colonist Human253 entered a cell at tick 11126 (three Rand calls in 
Pawn_FilthTracker.Notify_EnteredNewCell) and the host's didn't, with every 
earlier trace identical; every genuine desync that day was on the map the 
other player wasn't viewing, minutes after the second map appeared, which is 
exactly when one player's pawns leave the other's view. Fix: prefixes on the 
virtual getters Thing.UpdateRateTicks and Pawn.UpdateRateTicks (DoTick 
reaches them through callvirt, which the JIT can't inline; Pawn's override 
calls base non-virtually, hence its own patch). Hit counters 
"GenTicks.GetCameraUpdateRate synced", "Thing.UpdateRateTicks synced" and 
"Pawn.UpdateRateTicks synced" show which fire. Confirmed Oct 19 18:04: 
GetCameraUpdateRate 0 on host and client, the getters in the millions. 
Upstream's patch is dead on .NET; this will be the first thing to tell them.
- PawnStateTrace (slow tracing only): each map tick adds a desync log item 
whose text is a digest of every spawned pawn (position, tickDelta, pather 
cell and cost, job and toil, stance, age ticks, food/rest/joy/mood, each 
hediff with stage and severity) and whose hash joins the trace hash list. A 
state-only divergence then shows as "Trace hashes don't match" with the two 
digests as the first differing trace in local_traces.txt and 
host_traces.txt; diff them for the pawn and field. Costs a ~7 KB string per 
map tick while on. Both players need slow tracing on, as for the traces. 
The digest runs in the map tick's finally block and the trace recorder runs 
inside Rand calls, so an exception from either cut simulation code short on 
the tracing player's side only (a skipped map random state entry reads as 
"Wrong random state on map N"). Both now catch, log "desync tracing failed 
in ... and is off until restart" once, and stop tracing. Found Oct 21: the 
Windows friend desynced twice within 30 s of each join as soon as he 
turned slow tracing on, while the host had it off (so no trace comparison 
ran); his log, which would name the exception, hasn't been read yet.
- Multifaction storyteller (Oct 19 fixes, in-game check pending):
  - Quest raids (QuestPart_ThreatsGenerator, "raids for N days" quests) fired 
  once per player faction because every faction's storyteller ticks every map 
  and the quest list is shared; the same deterministic interval seed made 
  both land at once, so one colony saw a doubled raid. The filter is now in 
  StorytellerFactionTick (mod code, runs for sure). The old 
  QuestPartsListForReadingPatch stays but patches a one-line getter the .NET 
  JIT inlines, so don't rely on it; if the mod ever stops replacing 
  StorytellerTick, move the filter again.
  - Days since settling were one shared TickManager clock, so a faction 
  created on day 60 skipped Cassandra's 0.70 to 1.00 ramp, the 30-day 
  adaptation grace and every incident's earliestDay (infestations, mech 
  clusters, late raid types). Each FactionWorldData now carries settleTick, 
  swapped into TickManager.lastSettleTicksInt by 
  MultiplayerWorldComp.SetFaction. Saves from before the field get the 
  shared value (-1 marker), so existing factions keep their old ramp; only 
  factions created after this change start fresh.
  - World.PlayerWealthForStoryteller (quest points) still adds the gear of 
  every player faction's caravans (Caravan.PlayerWealthForStoryteller counts 
  equipment regardless of faction). Maps are already per faction through 
  IsPlayerHome. Small, left alone.
- Custom ideoligions for new factions (Page_ConfigureIdeo_Multifaction): the 
draft is kept out of IdeoManager and the player faction; only its details 
pane is drawn (IdeoUIUtility.DoIdeoDetails, not the ideo list) and it's added 
to the manager's list only while a window's contents draw 
(DraftIdeoVisibleToUi on Window.InnerWindowOnGUI) so the editor's dialogs 
find it. The first version hooked WindowStack.WindowStackOnGUI, which was 
useless: GUI.Window queues the contents and the GUI dispatcher runs them 
after WindowStackOnGUI returns (GUI.RunPendingWindow), so the draft wasn't 
in the list when the player clicked its row, the game's SelectOrMakeNewIdeo 
added it for real and made it the spectator faction's primary, and the 
editor showed "Delete custom" and a read-only pane (Oct 19 screenshot). The 
list is gone for the same reason: its "Delete custom" button targets the 
player faction's primary, which on a client is another faction's live ideo. 
The finished ideo travels in 
IdeologyData.CustomIdeo in the game's external-ideo format 
(GameDataSaveLoader.isSavingOrLoadingExternalIdeo), gzipped and base64-encoded 
in pieces of 30000 chars because synced strings are capped at 32767 chars 
(ByteReader) and an ideo is ~35 KB of XML (found in the first in-game check: 
"String too long (35285>32767)" on every client, no faction created, the 
player dropped back to the world map). It gets new ids inside the 
CreateFaction command. Loading a saved ideo into a fluid 
draft rejects one with too many or disallowed memes instead of offering to 
strip them like the game does. If the editor misbehaves in game, check that 
Page_ConfigureIdeo.PostOpen is still skipped (Window.PostOpen is called 
through a non-virtual AccessTools.MethodDelegate), that the precept tooltip 
ends with the green "Click to edit" (edit mode on), and that nothing else 
reads IdeoManager during a window's OnGUI expecting only real ideos.
- Precept choices in the ideo editor follow the game's rules: an alternative 
is greyed out with "Requires meme: X" when the draft lacks that meme 
(Corpses: Don't care needs one), and a fluid ideo gets exactly one meme at 
creation (IdeoFoundation.MemeCountRangeFluidAbsolute). Nothing in the mod 
restricts precepts; the editor is the game's with editMode GameStart.
- Host quit with Steam P2P on: the game shuts Steam down while the server 
thread still ticks, so SteamP2PNetManager.Tick threw "Steamworks is not 
initialized" ~2000 times in the last second (Oct 19 15:44 log). Tick now 
returns when SteamManager.Initialized is false. The two run_two.sh 
instances get Steam P2P because the Steam client is running on this PC.
- Synced exposables load under ProgramState.MapInitializing 
(ScribeUtil.ReadExposable swaps it in while the game is Playing). Found in 
the Oct 19 multifaction check: starting pawn "Daz" (an addiction hediff) 
logged "Error while determining if Daz should have Need Food", "SaveableFromNode 
exception" and "Pawn Daz had some null needs after loading" on host and 
client. Need_Food.MaxLevel reads the MaxNutrition stat only when Playing; 
the stat walks the hediffs, and Hediff_Addiction.Need dereferences 
hediff.pawn (set in ResolvingCrossRefs) and pawn.needs (set after the 
tracker loads). A save load never hits it because the state is 
MapInitializing. Same code on 1.6, so upstream has the bug for any synced 
pawn with an addiction.
- IsoRim built in (Oct 26, IsoRim Waylith 3fff2ec). Mapped by a five-agent 
read of both codebases before merging. What changed and why:
  - Loading: its own IsoRim.dll from the IsoRim load folder (see Build and 
  install, IsoRim). Two Mod classes now share the rwmt.Multiplayer pack 
  (MultiplayerLoader.Multiplayer and IsoRimMod, both under Options, Mod 
  settings); their constructor and static-constructor order is undefined 
  (PLINQ), so nothing may depend on it. IsoRim's settings file is now 
  Mod_Multiplayer-1.7_IsoRimMod.xml; install_local.sh copies the newest 
  Mod_*_IsoRimMod.xml over once, natively and in the Proton prefix. 
  Multiplayer's config sync ignores rwmt.multiplayer, so IsoRim settings are 
  per player and never synced, and the join check (Assemblies/ and Defs/ of 
  every load folder) makes both players run the same IsoRim build.
  - A standalone IsoRim (Peren.IsoRim17, connor.isorim) is incompatible 
  (About.xml). While one is active the IsoRim load folder is skipped, so the 
  unfixed standalone one runs instead: MultiplayerStatic logs an error and 
  install_local.sh warns. On Oct 26 peren.isorim17 was removed from both the 
  native and the Proton ModsConfig (backups in 
  ~/Documents/RimWorld-save-backup-2026-10-26/*/ModsConfig.before-isorim-merge.xml).
  - Desync fixes (the 3D view is a per-player toggle, so IsoRim may not 
  change the simulation): CompThrownFleckEmitter.Emit is now in 
  Multiplayer's Rand push/pop list (IsoRim replaces it with more Rand draws 
  on smoke and toxic spewers while the view is on; MP's prefix at 801 runs 
  before IsoRim's, its finalizer pops after). MP: guards in IsoRim: 
  DraftedSouth keeps the game's south turn in a session (the 3D view draws 
  the kept facing instead, see Drafted facing below); Attach.DrawPos_Postfix 
  and Api20's skip prefix do nothing while simulating; Projectiles.Rear, 
  CollapseDebris.Snapshot and ShipFreeze's section rebuild run inside 
  Rand.PushState/PopState; the binder's turn button refuses in a session; 
  IsoRim's dev actions (category "IsoRim") are local in DebugSync.
  - UX: the 3D view now survives Multiplayer's reloads (hosting, joins, 
  resyncs; View3D.NewGame keeps Active while a session goes on, and 
  RootUpdate_Postfix resets it at the main menu when no session exists, so 
  a new game, join or replay starts as before). Other 
  factions' blueprints get no 3D ghost (MpBridge.HidesBlueprint, the rule of 
  Factions/Blueprints.cs). Other players' selection brackets keep their 
  colour in 3D (IsoRim's bracket prefix takes the material colour). Remote 
  cursors, drag boxes and pings use MapScreen (skips points behind a 
  perspective camera, drag box as a projected ground rectangle, ping size 
  per location); the top-down view still draws the drag box with 
  Widgets.DrawBox. Widgets.DrawLine tints with its colour argument on top of 
  GUI.color under UnityStubs (found in review: the box came out at alpha 
  0.04), so MapScreen draws lines with a white GUI.color.
  - Not handled: the slowest player's frame time paces the whole session 
  (MultiplayerServer, clamped to 4x), so a slow 3D player slows everyone. 
  Add-ons that gate on ModsConfig.IsActive("Peren.IsoRim17") (IsoRimZ, packs 
  from IsoRim's model binder) see IsoRim as missing. The Workshop build's 
  pre-compressed Assets.zip isn't in the git source, so textures load from 
  loose PNGs. MP's patches and IsoRim's 2174-model startup run in the same 
  unordered static-constructor phase: rerun "Patch hit counts" and the 
  small-target inlining audit with IsoRim present.
  - Drafted facing in a session (Oct 26, Source/IsoRim/MpFacing.cs): the 
  game's south turn still runs for everyone; MpFacing remembers per 
  thingIDNumber the rotation just before it (DraftedSouth -> BeforeSouth, 
  recorded whether or not this player's 3D view is on, so tick code never 
  branches on the view) and IsoRim's drawing reads MpFacing.Drawn(pawn) 
  instead of pawn.Rotation (Animals.Advance for every 3D figure, 
  PreRenderResults_Prefix for sprite cards and flat sprites, the carried-item 
  offset, first person, silhouettes, attachments, mech turret and weapon 
  tips). An entry ends when the rotation tracker turns the pawn: the six 
  Thing.set_Rotation calls in Pawn_RotationTracker.FaceAdjacentCell (4), 
  FaceCell and Face are transpiled to MpFacing.GameTurned (set, then forget; 
  an error is logged if the count isn't 6 after a game update), on DeSpawn, 
  and at the main menu. Direct south writes outside the tracker (a few comps 
  and job drivers) aren't seen, and game state copied from the rotation stays 
  south: a corpse of a pawn killed while idle and drafted, a jump pack 
  flight, breath puffs, the terror halo. Another mod that sets the sprite 
  facing to pawn.Rotation in its own PreRenderResults prefix is treated as 
  the game's facing. FaceCell (69 bytes) and Face (61) are small enough to be 
  inlined into a caller compiled before IsoRim patches them at startup; then 
  an entry would outlive a real turn (visual only).
  - Other players in 3D (Oct 26, Source/IsoRim/MpView.cs, bound by name from 
  Source/Client/UI/IsoRimView.cs so a standalone IsoRim falls back to the 
  flat drawing): a 3D sender sends the point its pointer shows (the nearest 
  pawn, or the top of the thing under it, Interaction.PickUnderPointer) 
  instead of the ground behind it, except while box-selecting; the packet is 
  unchanged (x, z), so flat players now see that cursor or ping on the wall 
  or building cell. Each 3D receiver puts icons, names and pings on top of 
  what its own view draws there (IsoCellMap.TopOf with its own wall height 
  and cut-away, stacked floors through Floors.ColumnFloor, bridge decks) and 
  draws a ring under each cursor and the drag boxes in the world from a 
  MapInterface.MapInterfaceUpdate postfix (DrawPlayerMarkers3D; bright where 
  seen, faint through walls and roofs; IsoRim's own drag-box look). A 3D 
  player's drag box was a screen rectangle in their view, so the ground 
  rectangle between its corners is approximate. Left click on a player in 
  the chat window's list jumps to their cursor (same faction only; switches 
  maps; the 3D camera centres the surface point and leaves first person), and 
  the jump-to-ping key does the same in 3D. The marker materials (BlendDef at 
  queue 3160, OverlayDef at 3650) haven't been seen on screen yet.
  - 1.7.4990 (Oct 26): Waylith moved TexturePixelData from Waylith.Rendering to 
  Waylith.Graphics.Textures. The IsoRim.dll built against 4985 (and the 
  Workshop IsoRim, byte-identical to upstream's) then fails to JIT 
  LiveTexture.Next and SpritePixels.LevelOf. LiveTexture is how IsoRim updates 
  its per-map GPU textures in place every frame: the cell texture the vertex 
  shader cuts walls with (Cells.cs b channel, ShaderCut, IsoRim.vert.glsl 
  CutAway), the glow map (_IsoSky, lamp light on 3D faces) and the roof clip. 
  The first upload of each worked (it doesn't call Next), every later one threw 
  past Next's own catch (a JIT-time TypeLoadException), so those textures froze 
  on the first 3D frame: walls never folded near the pointer or colonists, 3D 
  lighting stopped following day, night and lamps, while doors still dropped 
  (Doors.Draw lowers them on the CPU from WallBoxes.CellCut). Log: one "[IsoRim] 
  Handing the map's cell data to the shaders failed" (ErrorOnce). Fixes: the 
  three references now name Waylith.Graphics.Textures (LiveTexture.cs:9, 
  SpritePixels.cs:266/285); Upload calls Next (now NoInlining) inside a 
  try/catch, so a later engine move falls back to plain Texture2D.Apply with 
  one "Texture update in place turned off" warning instead of freezing (types 
  used by Upload itself, GpuTextureAllocation in the ring field, would still 
  fail the whole class); IsoRim's "Hooks missing" summary is a Log.Warning even 
  without dev mode (ModLog.Warn is dev-only, so a dropped hook was invisible in 
  a normal Player.log; the per-hook lines stay dev-only). Subtree edits marked 
  "Port". Audited for 4990 (four agents plus adversarial verify): all 588 
  IsoRim reflection lookups resolve, its 153 hooks install and every 
  transpiler matches; the Waylith texture calls IsoRim makes keep their 
  meaning (parameter order, row order, formats, lifetimes). Not covered by 
  RwDiff: scripts/mp_targets.py reads only Source/Client and 
  Source/MultiplayerLoader, so IsoRim's transpilers and name lookups are 
  checked only by a whole-assembly compare after a game update. Every player 
  needs 1.7.4990 (the join check compares the full version) and a build from 
  after this fix, with the Workshop IsoRim off. Two engine-side races noted by 
  the audit, not seen in game: a ring image swapped in while the render thread 
  records the map pass can show one stale frame, and SpritePixels releasing 
  reloaded CPU levels while the render thread uploads them (on-demand texture 
  residency) could leave a texture short of mip levels.
  - To check in game (two players, one with 3D on and one off, both on the 
  same build): a smoke or toxic spewer on a map (dev mode can spawn one), 
  idle drafted pawns (walk one east and stop: the 3D player sees it face 
  east, the flat player south; aim north, then stop: north in 3D), a roof 
  collapse, a gravship landing, a join with 3D on (stays on, kept facings 
  too), the other player's cursor on a wall top, drag box, ping and 
  selection in 3D, clicking the other player in the chat list (also from 
  first person and from another map), and the "Avg Frame" of the 3D player 
  in the sync debug panel.
