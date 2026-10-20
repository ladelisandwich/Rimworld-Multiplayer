# What it will take to add bot players to RimWorld Multiplayer 1.7

**Status.** Written Oct 20 2026 for branch `bot-player` of the 1.7 port. A code verification pass checked 147 claims:
92 held, 54 needed corrections and 1 was wrong. It also found 8 major problems. All corrections and problems are
folded in below. Nothing is implemented yet. The next step is M0 (section 11).

Path conventions: paths starting with `Source/`, `scripts/`, `1.7/`, `Languages/` or `CLAUDE.md` are relative to
`/home/ahearn/Documents/Rimworld-Multiplayer-Bot`. `rw-src/` is an ilspycmd decompile of RimWorld 1.7.4982's
`Assembly-CSharp.dll`; it is not in the repo. `Defs/` is `<RimWorld>/Data/Core/Defs`. Line numbers were read on the
`bot-player` branch on Oct 20 and re-checked by the verification pass. Anything marked *(unverified)* still needs a
code read or an in-game check. "Code-derived" means read in code but not yet seen in game.

---------------------------------------------------------------------------------------------------------------------

## 1. Summary

A bot is a **virtual player inside the host's game**. It is a real multiplayer player: a `ServerPlayer` with a new
`PlayerType.Bot`, its own player id, colour, chat name and faction. Its network connection exists only in the host's
memory.

The host adds a bot from Chat → Factions → **Add bot**. One host-only command makes every client build the bot's
faction, colonists and map the same seeded way. The host's in-process server then registers the bot player and binds
it to that faction in one step.

A small rule-based **brain runs only on the host**, between frames, for at most ~1.5 ms per frame. It places zones,
blueprints and bills, sets work priorities and research, equips weapons and drafts colonists in fights. It acts
through the same synced methods and designators a human click uses, sent on the bot's own connection. The server
stamps them with the bot's id and faction, and every client runs them at the same tick. Vanilla pawn AI does the
actual work.

**Cheats** are a few bot-only commands: emergency food, medicine, stabilizing a bleeding colonist and a replacement
colonist. Each one re-checks its trigger and cooldown inside the command, and everyone sees a message for it. Raids on
bots are also softer (points ×0.75 or ×0.6).

Five small deterministic rules run on every client, keyed on the saved bot roster. Bot letters don't pause the game.
Bot map speed follows the humans. Growth moments of bot children resolve themselves. Raids on bots are scaled. A
bot-state digest makes a divergence visible.

**Cost:** about 28-37 working days to an MVP, plus about 15-20 two-instance test rounds that you run. It needs a
protocol bump (56 → 57). It works in hosted games only, not on the standalone server. Each bot adds a whole colony
map that every machine simulates. That CPU cost is unmeasured; M2 measures it.

**What blocks it:** nothing blocks a game with only the host and bots. Such a game is a single simulation and cannot
desync. Games with other humans wait on the open unviewed-map desync (CLAUDE.md task 8, Desync-11), because a bot
colony is always a map that nobody views. Fixing that desync is unbounded work and may fail the first time.

---------------------------------------------------------------------------------------------------------------------

## 2. How the mod works today (only what the bot relies on)

**Lockstep and commands.** Every client simulates the whole game. A client sends `ClientCommandPacket(type, mapId,
data)`, which carries no player id and no faction (`Source/Common/Networking/Packet/CommandPacket.cs:37-50`). The
server adds both from the `ServerPlayer` bound to the connection that sent the packet
(`Source/Common/Networking/State/ServerPlayingState.cs:55`: `Server.commands.Send(packet.type, Player.FactionId,
packet.mapId, packet.data, Player)`). `CommandHandler.Send` (`Source/Common/CommandHandler.cs:18-68`) does three
things:

- It applies policy when `sourcePlayer != null`. DebugTools and debug-only sync ids need `CanUseDevMode` (`:23-27`),
  which is `debugMode && (devModeScope == Everyone || IsHost)` (`:88-93`). Host-only sync ids need `IsHost`
  (`:29-31`). Time commands follow `TimeControl.HostOnly`.
- It stamps `server.gameTimer`, stores the command in `worldData.mapCmds` (join-point data, `:50-51`) and broadcasts
  it to every playing player (`:64`).
- It is the only place where the command's player and faction come from. Nothing logs the stamped command today.

**Sync interception.** A synced game method called while `Multiplayer.ShouldSync` is serialized and sent instead of
being run. `ShouldSync` is `InInterface && !dontSync`, and `InInterface` requires: a client, not ticking, not
executing commands, not reloading, `ProgramState.Playing`, and no long event (`Source/Client/Multiplayer.cs:66-74`).
Otherwise the original body runs locally (`Source/Client/Syncing/Handler/SyncMethod.cs:74-77`). Every sync handler
ends in `SyncHandler.SendSyncCommand` → `Multiplayer.Client.SendCommand` (`Source/Client/Syncing/Handler/SyncHandler.cs:20-24`,
skipped in GhostMode). Designators are synced by prefixes on every `Designator` subtype, the base class included
(`Source/Client/MultiplayerStatic.cs:299-320`). The prefix stamps `Find.CurrentMap` (`Source/Client/Patches/Designators.cs:32,54,77`),
serializes UI state such as the selected zone, the static selected area and the install target (`:108-138`), and sends
through its own private `SendSyncCommand` (`:92-96`, the send at `:95`).

Other senders always use the host connection: time controls (`Source/Client/AsyncTime/TimeControlUI.cs:242, 480,
482`), PlayerCount for VTR (`Source/Client/Patches/VTRSyncPatch.cs:115`) and DebugTools
(`Source/Client/Debug/DebugSync.cs:163`, which also writes `session.playerId` into its payload). PlayerCount is sent
from the `Game.CurrentMap` setter patch (MapSwitchPatch, `VTRSyncPatch.cs:126-151`) and from a postfix on every read
of `WorldRendererUtility.CurrentWorldRenderMode` (`:166-205`).

SyncFields: a direct `SyncField.DoSync(target, value, index)` sends at once while `inGameLoop || ShouldSync`
(`Source/Client/Syncing/Handler/SyncField.cs:35-64`). A field with `bufferChanges` written through the Watch path is
only cached (`Source/Client/Syncing/SyncFieldUtil.cs:63-79`) and is sent at least 200 ms later by
`SyncFieldUtil.UpdateSync` (`Source/Client/OnMainThread.cs:37`; `SyncFieldUtil.cs:83-108`).

`SyncUtil.WriteContext` writes host UI state into some payloads: the Shift state for `QueueOrder_Down` handlers
(`Source/Client/Syncing/SyncUtil.cs:122-123`), `UI.MouseCell()` for `MapMouseCell` handlers and the world selection
for `WorldSelected` handlers (`:110-120`).

**Execution context of a map command.** `AsyncTimeComp.ExecuteCmd` (`Source/Client/AsyncTime/AsyncTimeComp.cs:234-329`):

- It sets `Current.Game.currentMapIndex` directly (`:244`), so the `Game.CurrentMap` setter patch is not triggered.
- It sets `TickPatch.currentExecutingCmdIssuedBySelf = cmd.IsIssuedBySelf() && !Simulating` (`:247`) and
  `currentExecutingCmdType` (`:248`).
- It pushes the map's faction, then the command's faction with `force: true` (`:250-251`). That swaps in the
  faction's designation, area, zone and plan managers, haul destination manager, haul, filth and mergeable listers and
  resource counter (`Source/Client/Comp/Map/MultiplayerMapComp.cs:124-139`). It also swaps the faction's research,
  drug, apparel and food policies (not reading policies), play settings, history, storyteller, story watcher and
  settle tick (`Source/Client/Comp/World/MultiplayerWorldComp.cs:155-182`).
- It clears the selector (`:255-256`) and applies the issuer's `PlayerData` dev and god mode (`:262`).
- Inside a Sync command, `SyncUtil.HandleCmd` sets `KeyIsDownPatch.shouldQueue` from the payload
  (`Source/Client/Syncing/SyncUtil.cs:77-81`), so `TryTakeOrderedJob` never reads a local key.

**Execution context of a global command.** In multifaction, `AsyncWorldTimeComp.PreContext` force-pushes the
spectator faction and sets every map's `MpComp` to it (`Source/Client/AsyncTime/AsyncWorldTimeComp.cs:144-156`).
`ExecuteCmd` (`:171-275`) then pushes the command's faction without force and with no map (`:181`). So
`Faction.OfPlayer` and the world data are the command faction's, but every map's managers are the spectator's.

**Long events from commands.** A long event queued from a command is forced synchronous
(`Source/Client/Patches/LongEvents.cs:69-84`) and seeded with a Rand drawn in the command
(`Source/Client/Patches/Seeds.cs:95-111`). It does not run inside the command. `LongEventHandler.LongEventsUpdate`
runs it in a later `Root.Update` (`rw-src/Verse/Root.cs:122`; `rw-src/Verse/LongEventHandler.cs:266-300, 407-443`).
By then `ExecuteCmd` has popped its faction push and restored local dev and god mode. No further command or tick runs
first (`Source/Client/Patches/TickPatch.cs:42, 118-123, 186-191`). Inside the event, `ExecutingCmds`, `Ticking` and
`InInterface` are false, and ids are real (`Source/Client/Patches/UniqueIds.cs:18-19`). `Faction.OfPlayer`, the world
data, every map's managers and `Find.CurrentMap` are each client's own. The human path captures
`currentExecutingCmdIssuedBySelf` before queuing for this reason (`Source/Client/Factions/FactionCreator.cs:83`).

**Issuer-only effects.** While a command runs, every client except the issuer cancels sounds (`PlayOneShot`), tutor
dialogs, `TryHideWorld`, motes and flecks, targeting and non-historical messages
(`Source/Client/Patches/Feedback.cs:18-101`). Camera jumps (`TryJump`, `TryJumpAndSelect`, `TrySelect`) are cancelled
only while simulating (`:163-173`), so a camera jump in a command body moves every client's camera. Historical
messages are never cancelled, and `Messages.Message` defaults to `historical: true` (`rw-src/Verse/Messages.cs:31-55`).
A message reaches `Find.Archive` and takes a message id only if this client doesn't already show an identical live
message (`:126-147`). That live list is client-local and expires in real time.

**Players.** `PlayerManager.OnConnected` allocates the id and adds the player to `Players` at once
(`Source/Common/PlayerManager.cs:53-63`). `OnJoin` gives a multifaction joiner the spectator faction, a colour, a
"has joined." chat line and a player-list Add (`:136-157`). Id 0, a standalone primary, or multifaction off get the
host faction instead (`:142-144`). `MakeHost` = `OnJoin` + `ChangeState(ServerPlaying)` + `SendPlayerList` +
`SendInitDataCommand` + `UpdateStatus(Playing)` (`:176-184`). It runs on the main thread before the server thread
starts (`Source/Client/Networking/HostUtil.cs:37-48`). `SendInitDataCommand` sends `CanUseDevMode(player)`
(`PlayerManager.cs:159-166`). `InitPlayerData` creates `playerData[id]` on every client
(`Source/Client/AsyncTime/AsyncWorldTimeComp.cs:225-230`). The host itself is an in-memory `LocalConnection` pair
(`Source/Client/Networking/NetworkingInMemory.cs:8-55`) registered this way (`HostUtil.cs:195-208`).

A new `ServerPlayer` has status `Simulating` (`Source/Common/ServerPlayer.cs:12`). Only `Client_WorldReady`
(`ServerPlayingState.cs:10-13`) and `MakeHost` (`PlayerManager.cs:183`) set `Playing`. Username checks (3-15
characters, `MultiplayerServer.UsernamePattern`, not online) run only in
`Source/Common/Networking/State/ServerJoiningState.cs:76-96`. On clients, a PlayerList Add is skipped when an entry
with the same id or username exists (`Source/Client/Networking/State/ClientPlayingState.cs:33`), and `HandleSetFaction`
dereferences `GetPlayerInfo` without a null check (`:215`). `PlayerType` is `{Normal, Steam, Arbiter}`, a byte on
the wire (`ServerPlayer.cs:143-148`), and `Protocol = 56` (`Source/Common/Version.cs:9`). The server's
`HandleSetFaction` accepts any player id from anyone (`ServerPlayingState.cs:246-260`, "todo restrict handling"). In
the Factions window only the host can drag names (`Source/Client/Factions/FactionsWindow.cs:79`), but anyone can
press `>` to switch themselves (`:45-46`).

**Server pacing.** The server advances `gameTimer` only when three things hold: it isn't frozen, some `PlayingPlayer`
of any status is under 40 ticks behind, and no status-Playing player is over 90 ticks behind
(`Source/Common/MultiplayerServer.cs:122-135`). Each tick it waits `serverTimePerTick × (1 + slowdown)`, where
slowdown = min(max reported `ticksBehind` of status-Playing players / 60, 2) (`:137-142`). `serverTimePerTick` is the
largest status-Playing `frameTime`, clamped to 16.67-66.67 ms (`:194-200`). `PlayingIngamePlayers` is
`PlayingPlayers` with `status == Playing` (`:45`). `ExtrapolatedTicksBehind = ticksBehind + (gameTimer -
ticksBehindReceivedAt)` (`ServerPlayer.cs:20`), and only keep-alives set `ticksBehindReceivedAt`
(`ServerPlayingState.cs:182`).

**Multifaction.** `FactionCreator.CreateFaction` (`Source/Client/Factions/FactionCreator.cs:42-97`) runs on every
client inside a long event. Its exception handler is `GameAndMapInitExceptionHandlers.ErrorWhileGeneratingMap`
(`:96`), which goes to the main menu (`rw-src/Verse/GameAndMapInitExceptionHandlers.cs:19-24`). Only the issuer then
switches to the new faction, moves its camera and sends `ClientSetFactionPacket(own id)` (`:83-95`). Every player
faction's storyteller ticks on every map (`Source/Client/Factions/FactionRepeater.cs:15-40`). Its incidents go through
`StorytellerFactionTick.Tick` before `TryFire` (`Source/Client/Factions/StorytellerFactionTick.cs:15-25`). Every
faction's storyteller also generates quests, and other players' quests are shown unless `hideOtherPlayersQuests` is on
(default false, `Source/Client/Settings/MpSettings.cs:39`). Other factions' letters are removed from each client's
stack (`Source/Client/Factions/MultifactionPatches.cs:636-645`). Letters can still pause the map (async time) or
everything (sync time) through `ReceiveLetterPause.PauseOnLetter` (`Source/Client/AsyncTime/AsyncTimePatches.cs:174-215`).
`LetterStack.ReceiveLetter` reaches that hook only when the pause mode allows it (`rw-src/Verse/LetterStack.cs:70-73`).
The host setting `pauseOnLetter` defaults to AnyThreat (`Source/Common/ServerSettings.cs:30`).

**Time.** Each map has its own `DesiredTimeSpeed`, a plain field read by its getter
(`Source/Client/AsyncTime/AsyncTimeComp.cs:53-57`). In async time new maps start Paused
(`Source/Client/Patches/MapSetup.cs:57-67`). With async time off they take the current global speed (`:71-72`). The
world runs at the fastest unpaused map (`AsyncWorldTimeComp.cs:47-56`). A pausing session forces the map to rate 0
(`AsyncTimeComp.cs:21-29`). A map nobody views ticks interval work every 15 ticks (VTR, `AsyncTimeComp.cs:98-99`).

**Desync detection.** Every 30 Timer ticks the host (or the arbiter) sends its opinion, but only when it isn't
simulating. Clients compare, in order: the FP round mode, the sequence of map ids, per-map Rand iteration counts, the
world, commands, then trace hashes (`Source/Client/Desyncs/ClientSyncOpinion.cs:33-60`;
`Source/Client/ConstantTicker.cs:112-133`). `TryAddMapRandomState(map, state)` appends `(uint)(state >> 32)`
to the map's list (`Source/Client/Desyncs/SyncCoordinator.cs:185-190`), and random states are compared even while
simulating. Without an arbiter the host never compares; it only sends (`ConstantTicker.cs:122-124`;
`ServerPlayingState.cs:198-206`), so only a client can detect a desync. With an arbiter playing, the host gets the
arbiter's opinion and compares too. In a game with only the host and bots nothing compares, but nothing can diverge
either: there is one simulation.

**Join points.** Every join point (a player joins, autosave, desync, `/joinpoint`) saves and reloads the whole game
on every client, the host included (`Source/Client/Saving/SaveLoad.cs:35-116`, `SaveAndReloadCore`). Command queues
survive, but every game object is replaced.

---------------------------------------------------------------------------------------------------------------------

## 3. Recommended architecture

### 3.1 Components

| Component | Where | Role |
|---|---|---|
| `BotServerConnection`, `BotClientConnection` | `Source/Common/Networking/BotConnections.cs` (new, ~40 lines) | In-memory pair. It copies the pattern of `LocalConnection` (`NetworkingInMemory.cs:8-55`), not the class: `LocalConnection`'s server half would deliver every broadcast to a stateless client half, which throws "No handler for packet" (`Source/Common/Networking/ConnectionBase.cs:175-180`). The client half takes the `MultiplayerServer` in its constructor, because Common can't see the Client static `Multiplayer.LocalServer` (`Source/Client/Multiplayer.cs:39`). It is never `ChangeState`d. It keeps the default `State` `ClientJoining` (enum value 0, `ConnectionBase.cs:15`), and `Send` refuses only `Disconnected` (`:55`), so it can send. Its `SendRaw` is `server.Enqueue(() => { if (s.State == ServerPlaying) s.serverPlayer.HandleReceive(new ByteReader(raw), reliable); })`, which reuses the log-and-disconnect wrapper (`ServerPlayer.cs:55-66`). The server half sets `username` in its constructor. It overrides `Send(Packets, byte[], bool)` (`ConnectionBase.cs:53`), `SendRaw` and `OnClose` to drop everything. It is in `PlayingPlayers`, so the server does send to it; dropping is what keeps a command from being scheduled twice. No Verse calls; errors go to `ServerLog`. |
| `BotServerApi` | `Source/Common/BotServerApi.cs` (new) | Server thread only, no Verse calls. `RegisterBot(server, serverHalf, name, factionId)` registers and binds the bot in one queued action (3.4). `SetBotFaction` moves a registered bot (the M3 spike uses it). `RemoveBot` = `p.Disconnect(MpDisconnectReason.Kick)`. |
| Server policy | `Source/Common` (changed) | See 3.4. |
| `Multiplayer.CommandConnection` + `BotCommandScope` | `Source/Client/Multiplayer.cs`, `Source/Client/Bots/BotCommandScope.cs` | `CommandConnection => BotCommandScope.Current?.Connection ?? Client`, used at the two send points only (`SyncHandler.cs:23`, `Designators.cs:95`). Only Sync and Designator commands are rerouted. GhostMode (a dev checkbox, `Multiplayer.cs:57`) mutes bot sends like host sends; that is accepted. |
| `BotInterfaceContext` | `Source/Client/Bots/BotInterfaceContext.cs` | The "bot view", see 3.3. Only `BotManager` opens it. |
| `BotManager` (+ `BotRuntime`) | `Source/Client/Bots/` | Host-only loop called at the end of `OnMainThread.Update` (`Source/Client/OnMainThread.cs:20-63`). Gates, reload epoch, reconciliation of the roster with live bot players, runtime states (section 8), round-robin brain stepping inside the scopes, budget. |
| Brain | `Source/Client/Bots/Brain/*` | Rules (section 6). Reads game state in the bot view. Writes only through synced calls. |
| `BotFactory.CreateBotFaction` | `Source/Client/Bots/BotFactory.cs` | Host-only synced global command. It queues a seeded long event that builds the faction, pawns and map with its own context and failure handler (section 5). |
| `BotRoster` / `BotRecord` | `Source/Client/Bots/`, saved in `MultiplayerGameComp.ExposeData` | The save-bound list of bots (section 8). Values only. Changed only inside commands and the creation long event. |
| `BotCheats`, `BotRosterEdits` | `Source/Client/Bots/` | Bot-owned synced methods, registered `.SetHostOnly()` and sent on the bot's connection (section 7). |
| `SetBotSettings`, `Remove` | `Source/Client/Bots/` | Host-sent synced methods, registered `.SetHostOnly()`, sent on the host's connection (section 8). |
| `BotGuard`, `BotActionScope` | `Source/Client/Bots/` | Guard on every bot synced body. Neutralizes the host context for host-sent bot commands (3.5). |
| Passive rules | Mod code in `AsyncTimeComp`, `AsyncTimePatches`, `GrowthMomentSession`, `StorytellerFactionTick` | Letter-pause exemption, time governor, growth moments, raid scaling, bot digest (3.6). |
| Pure helpers | `Source/Common/Bots/GridPlanner.cs`, `BotCheatPolicy.cs` | Base layout and cheat math on plain ints. Unit-tested without the game. |

### 3.2 Data flow: one decision to execution on every client

```
HOST PROCESS, main thread, OnMainThread.Update (interface phase)        HOST PROCESS, server thread
 BotManager.Update
   gates (6.1); reload epoch check; read CurrentWorldRenderMode once
   try { enter BotInterfaceContext(bot, map) + BotCommandScope(bot)        MultiplayerServer.Run
     currentMapIndex=bot map (field write), TimeSnapshot(bot map),           queue.RunQueue
     PushFaction(bot, force), selector cleared, godMode off                    |
     think.MoveNext(): e.g. new Designator_Build(Wall).DesignateMultiCell     v
         |  Designator prefix (unchanged) serializes, map = bot map        closure from BotClientConnection:
         v                                                                  server half is ServerPlaying ->
     DesignatorPatches.SendSyncCommand                                      serverPlayer.HandleReceive
         -> Multiplayer.CommandConnection = bot's BotClientConnection --->  ServerPlayingState.HandleClientCommand
   } finally { leave both scopes }                                          (PlayerType.Bot, status Simulating)
                                                                            commands.Send(type, FactionId=bot faction,
                                                                                          mapId, data, Player=bot)
                                                                            ScheduledCommand{tick T, playerId=bot,
                                                                                            factionId=bot faction}
                                                                            SendToPlaying (bot server halves drop it)
                                                                                 |
     <---------------------- ServerCommandPacket to every client, host included -+
 EVERY CLIENT at Timer == T: AsyncTimeComp.ExecuteCmd
   currentMapIndex = bot map; IsIssuedBySelf false everywhere (nobody is the bot)
   PushFaction(cmd faction = bot, force) -> bot's zone/area/designation managers, research, play settings
   playerData[botId].SetContext() -> devMode=false, godMode=false on every client
   HandleDesignator -> Designator_Build.DesignateMultiCell -> blueprints of the bot faction
```

### 3.3 The bot view (`BotInterfaceContext`)

Rather than edit every read of UI state in the sync code, the brain runs inside a scope that sets those states the way
`ExecuteCmd` sets them for a command. It does this in the interface and executes nothing locally.

Only `BotManager` opens `BotInterfaceContext` and `BotCommandScope`, around each `MoveNext()` of a think, inside
`try/finally`. Rule code never opens them. A scope opened inside a rule's iterator would stay open across a
`yield return`, through the host's own OnGUI and the next frame's input. The host's own clicks would then be
serialized against the bot map and sent as the bot. (The 1.7 engine dispatches input before `Update` and draws IMGUI
after it. This is code-derived from a decompile of the engine assemblies, which are not in `rw-src/`.) A debug
assertion checks that the scope depth is 0 at the end of `BotManager.Update`, and the two send points check it too
whenever `BotManager` isn't stepping.

Entering the scope:

0. Before entering, read `WorldRendererUtility.CurrentWorldRenderMode` once. A pending world-view transition then
   sends its host PlayerCount outside the scope (`VTRSyncPatch.cs:166-205`). Inside the scope it would name the bot
   map as the host's map, and every client's VTR counts would change.
1. `prevIndex = Current.Game.currentMapIndex; Current.Game.currentMapIndex = (sbyte)map.Index;` This is a field
   write, like `ExecuteCmd:244`, so no PlayerCount command and no VTR change.
2. `prevTime = TimeSnapshot.GetAndSetFromMap(map)` (`Source/Client/AsyncTime/SetMapTime.cs:229-244`), so tick-keyed
   caches read the bot map's time.
3. `map.PushFaction(botFaction, force: true)` (`Source/Client/Factions/FactionExtensions.cs:13-20`).
4. Save `Find.Selector.selected` and replace it with a new list. Save `Designator_AreaAllowed.selectedArea` and
   `DebugSettings.godMode`, then set god mode to false.
5. `BotCommandScope.Current = bot`. While a scope is set, `SyncUtil.WriteContext` writes `false` for the QueueOrder
   bit (`SyncUtil.cs:122-123`), and `DesignateThing` skips `FleckMaker.ThrowMetaPuffs` (`Designators.cs:87`).
6. `Dispose` reverses everything in `finally`, in reverse order.

Rules for brain code inside the scope, enforced by review and by the dev tripwire (section 9):

- Read-only game access. Mutate only through synced methods, direct SyncField writes and designators, and only while
  `Multiplayer.ShouldSync`.
- Write SyncFields only with a direct `((SyncField)f).DoSync(target, value, index)`. `Sync.Field` returns a
  `SyncField` (`Source/Client/Syncing/Sync.cs:41-46`), so the cast holds. Never use `MP.WatchBegin`/`Watch`/`WatchEnd`:
  buffered fields (`Source/Client/Syncing/Game/SyncFields.cs:108-243`) are sent later by `SyncFieldUtil.UpdateSync`,
  outside the scope, on the host connection with the host's faction. Zone targets then resolve in the host's zone
  manager and come back null (`Source/Client/Syncing/Dict/SyncDictRimWorld.cs:1150`).
- Never call code that goes through the `Game.CurrentMap` setter (MapSwitchPatch would send a host PlayerCount), the
  time controls or `DebugSync`. Those always go out on the host connection.
- Never use handlers with `SyncContext.MapMouseCell` or `WorldSelected` (`SyncUtil.cs:110-120`). They would carry the
  host's mouse cell or world selection, as the carry-to-biosculpter and capture-entity float-menu delegates do
  (`Source/Client/Syncing/Game/SyncDelegates.cs:21-23`). The catalogue in 6.4 uses none.
- No `Messages.Message` from the interface: historical messages go into `Find.Archive`, which is game state. Use the
  bot chat line or the log instead.
- Never create `ILoadReferenceable` objects except `Bill` and `Job`. Their negative interface ids are replaced inside
  the command (`Source/Client/Syncing/Game/SyncMethods.cs:675-703`).
- No `LongEventHandler` calls.
- Plan only when `!map.regionDirtyer.AnyDirty` (`rw-src/Verse/RegionDirtyer.cs:13`). Region and room reads rebuild
  dirty regions on the host only, and region/room ids come from static counters.
- Don't call lazy getters that write state. `Zone_Growing.PlantDefToGrow` assigns `plantDefToGrow` on its first read
  (`rw-src/RimWorld/Zone_Growing.cs:24-41`). Read the field instead; null means the default.
- Store `thingIDNumber`, `loadID`, `map.uniqueID` and cells in anything that outlives a think, because a join point
  replaces every object. A think's `IEnumerator` locals and the expectation predicates do hold objects. So `BotManager`
  keeps a reload epoch: the `Current.Game` reference, or a counter bumped in `SaveLoad.SaveAndReloadCore`. When it
  changes, `BotManager` throws away the current think and every pending predicate, and re-resolves the bot's map and
  faction by id.
- Only order spawned pawns that are on the bot map. Pawns inside containers or carried don't serialize reliably
  (Thing holder rules, `Source/Client/Syncing/Dict/SyncDictRimWorld.cs:788-866`).
- Before ordering Equip, AttackMelee or Goto, check `pawn.CanReserveAndReach(target, ...)` in the bot view. An ordered
  job whose reservation fails logs a warning on every client and does nothing
  (`rw-src/Verse.AI/Pawn_JobTracker.cs:921-946`).

### 3.4 Identity, attribution and server policy

Brain actions, cheats and the bot's own roster edits (anchor, plan, stage, Defeated) all go on the bot's connection.
They are stamped `playerId = bot` and `factionId = bot faction`. Host UI actions on bots go on the host connection:
create, settings and remove. Pause and resume are host-local and send nothing (section 8, Bot states).

**Registration.** `BotServerApi.RegisterBot(server, serverHalf, name, factionId)` runs as one queued action on the
server thread:

1. Refuse and log unless the name is 3-15 characters, matches `MultiplayerServer.UsernamePattern`, is not already in
   `Players` (`GetPlayer(name) == null`) and is not `hostUsername`. A bot bypasses the checks in
   `ServerJoiningState.cs:76-96`. A name over 15 characters makes every PlayerInfo serialization throw
   (`Source/Common/Networking/Packet/PlayerListPacket.cs:96`, `IPacket.cs:303-306`). A name equal to the host's makes
   the bot `IsHost` (`ServerPlayer.cs:43`). A duplicate is skipped by every client's PlayerList Add
   (`ClientPlayingState.cs:33`), and the SetFaction for its id then throws at `ClientPlayingState.cs:215`, which stops
   multiplayer on a remote client.
2. `serverHalf.username = name` (before `OnConnected`, which logs it); `p = OnConnected(serverHalf)`;
   `p.type = PlayerType.Bot` (before `OnJoin`, whose PlayerList Add carries the type, `ServerPlayer.cs:87-107`).
3. `OnJoin(p)`: the "has joined." line, a colour, the player-list Add, and under multifaction the spectator faction.
4. `serverHalf.ChangeState(ServerPlaying)`.
5. `server.commands.Send(CommandType.InitPlayerData, ScheduledCommand.NoFaction, ScheduledCommand.Global,
   ByteWriter.GetBytes(p.id, false))`, sent directly. `SendInitDataCommand` would send `CanUseDevMode(bot)`, which is
   true under `DevModeScope.Everyone`. The command itself is required. Without `playerData[botId]`,
   `AsyncTimeComp.cs:262` applies each client's own god mode, and a wall designated under god mode is placed instantly
   on one side only (`rw-src/RimWorld/Designator_Build.cs:474`).
6. `p.FactionId = factionId` and `SendToPlaying(new ServerSetFactionPacket(p.id, factionId))`, right behind the Add, so
   no client sees the SetFaction first.
7. No `UpdateStatus(Playing)` and no `SendPlayerList` (the bot's connection drops it anyway).

The body is in `try/catch`. On failure it calls `p.Disconnect(...)`, which sends the PlayerList Remove if the player
had joined (`PlayerManager.cs:65-107`). Without it, `ActionQueue` would only log and leave a half-registered player
counted in `Players` (`Source/Common/ActionQueue.cs:23-31`). The Client-side closure that enqueues `RegisterBot`
posts the result back with `OnMainThread.Enqueue`, because Common can't see `OnMainThread`. This server-thread
sequence is code-derived; R0 is its first in-game check.

**Status stays Simulating.** The bot keeps its default status `Simulating` and never gets `UpdateStatus(Playing)`.
`PlayingIngamePlayers` filters `status == Playing` (`MultiplayerServer.cs:45`), so the >90 pause, the slowdown and
`serverTimePerTick` never see a bot. That matters. A bot never sends keep-alives and keeps `ticksBehindReceivedAt = 0`,
so with status Playing its `ExtrapolatedTicksBehind` would equal `gameTimer`. Then `:123` would pause the game at once
and for good, because `gameTimer` stops and the value never drops. Simulating is visible in two places. The chat list
dims it (`Source/Client/Windows/ChatWindow.cs:191`); section 9 replaces that with a "(bot)" tag. `/players` prints
`[{player.status}]` (`PlayersCommand.cs:21`) and `/whois` prints `Status: {player.status}` (`WhoisCommand.cs:11`), so
both show "Simulating" for every bot. That is accepted: M1's "bot" role is printed next to it.

Server changes, all in `Source/Common` and unit-testable:

| Change | Where | M | Why |
|---|---|---|---|
| `PlayerType.Bot = 3` appended after `Arbiter` (no renumbering), `IsBot` | `ServerPlayer.cs:143-148`, near `:44` | M0 | identity on the wire (`ServerPlayerListPacket.PlayerInfo.type`) |
| one `ServerLog.Log` line per bot command: type, playerId, factionId, mapId | `CommandHandler.Send`, when `sourcePlayer is { IsBot: true }` | M0 | `ServerLog.Detail` would not reach Player.log: it prints only with `detailEnabled` (`Source/Common/Util/ServerLog.cs:20-24`), and hosted games route only info and error to `Debug.Log` (`MultiplayerStatic.cs:65-66`) |
| `CanUseDevMode` returns false for `IsBot` | `CommandHandler.cs:88-93` | M1 | no dev mode, DebugTools or debug-only sync ids for bots in any scope; unit test both scopes |
| `hostOnly` sync ids pass for `IsHost \|\| IsBot` | `CommandHandler.cs:29-31` | M1 | bot-owned cheats and roster edits are host-only (humans can't send them) but come from the bot. Only code in the host process can drive a bot connection, and `PlayerType.Bot` is set only by server code |
| `PacingPlayers => PlayingIngamePlayers.Where(p => !p.IsBot)` for the >90 pause | `MultiplayerServer.cs:123` | M1 | defence in depth only, for a bot that someday gets status Playing. `:125`, `:139` and `:194` need nothing: a bot's `ticksBehind` and `frameTime` stay 0 and can't raise a max, and a bot never sends keep-alives, so its `ExtrapolatedTicksBehind` equals `gameTimer` and can't satisfy the `<40` advance at `:125` on its own once `gameTimer` is past 40. `PlayingIngamePlayers` is used only at the three pacing sites and by `SendToIngame` (`:229`, the cursor relay, `ServerPlayingState.cs:167`); a separate property keeps the change explicit |
| maxPlayers and the standalone-primary rule ignore bots | `PlayerManager.cs:39-41, 139-140` | M1 | bots don't take human slots |
| no sync-info relay to bots | `ServerPlayingState.cs:205` | M1 | saves building fragmented packets every 30 ticks |
| `HandleSetFaction`: refuse any bot as the moved player, from anyone. A non-host may move only itself, and not into a faction a bot holds (`Server.PlayingPlayers.Any(p => p.IsBot && p.FactionId == factionId)`) | `ServerPlayingState.cs:246-260` | M1 | closes the "todo restrict handling" hole. Bots are moved only by server code. Only the host can put a human on a bot faction (take-over, section 8) |
| `PlayerCommandUtility.GetRole` returns "bot" for `IsBot`; "-" instead of ticks behind for bots | `ChatCommands/BuiltIn/PlayerCommandUtility.cs:5-11`, `PlayersCommand.cs:21`, `WhoisCommand.cs:16` | M1 | display. Existing tests (`ChatCommandManagerTest.cs:206-244`) stay valid |
| `Protocol = 57` | `Version.cs:9` | M1 | the wire format doesn't change (`ByteReader.ReadEnum` doesn't validate, `ByteReader.cs:139-145`). But every client must run the bot sync handlers and passive rules, so mixed builds must be refused at the handshake (`ServerJoiningState.cs:45`). No Verify snapshot depends on the value |

Client defensive fix (M1): make `Session.GetPlayerInfo(playerId)` null-safe in `ClientPlayingState.HandleSetFaction`
(`ClientPlayingState.cs:215`).

With `InitPlayerData(botId, false)` and `CanUseDevMode` false for bots, every bot command runs with `Prefs.devMode`
and god mode off on every client (`AsyncTimeComp.cs:262`; `AsyncWorldTimeComp.cs:185`), instead of each client's own
values.

Mods: MultiplayerAPI 0.6.0's `IPlayerInfo` has no type (`Source/Client/MultiplayerAPIBridge.cs:188-195`), so mods
using it can't tell bots apart. Only code that reads `Multiplayer.Client.PlayerInfo.type` sees `PlayerType.Bot`.
Accepted for the MVP.

### 3.5 Guards for bot synced methods

- `BotGuard.FromBot(faction, map)`: the body runs only if all of these hold:
  - `Multiplayer.ExecutingCmds && TickPatch.currentExecutingCmdType == CommandType.Sync` (set at
    `AsyncTimeComp.cs:248` and `AsyncWorldTimeComp.cs:178`)
  - the executing command's stamped `factionId == faction.loadID`. `ExecuteCmd` records it next to
    `currentExecutingCmdType` (new). `Faction.OfPlayer == faction` alone is not enough: on the bot's own map, a stamp
    that doesn't resolve is a no-op push (`FactionContext.cs:13-16`), so PreContext's push of `map.ParentFaction`
    stays (`AsyncTimeComp.cs:182-186`)
  - the roster has an Active record for `faction`
  - `map == null || map.ParentFaction == faction`

  A call made while `!ShouldSync` would otherwise run the body locally on the host only. The guard turns that into a
  logged no-op instead of a desync.
- `BotGuard.FromHost()` + `BotActionScope`, for host-sent bot commands (create, settings, remove): save and clear
  `TickPatch.currentExecutingCmdIssuedBySelf`, `Prefs.data.devMode` and `DebugSettings.godMode` for the body, and
  restore them in `finally`. Without this the host would play sounds or open tutor dialogs for the command alone
  (`Feedback.cs:18-33`), and the host's god mode would leak in. Neither guard can be used inside the creation long
  event, which runs outside the command (section 5).

Rules for every bot synced method body:

- A method that touches map state takes the bot `Map` (or a map `Thing`) as an argument, so it runs as a map command
  with the forced bot push (the `Map` serializer makes it one, `SyncDictRimWorld.cs:927-929`). In a global command
  every map's managers are the spectator's (section 2). Zones, areas, designations and listers would then be the
  spectator's, while forbiddance follows the bot. Roster-only edits may stay global.
- Never read `session.players` or `MpSettings`. Both are per-client. `session.players` is updated by packet handlers
  outside lockstep (`ClientPlayingState.cs:27-60`).
- Never jump the camera: it would move every client's camera (section 2, Issuer-only effects).
- Put the bot's name and the new `cheatsUsed` count into every historical message. Then no client can already show an
  identical live message, so every client archives it with the same id (`Messages.cs:126-147`).

### 3.6 Passive deterministic rules (simulation code on every client, keyed on the saved roster)

Every hook starts with `if (BotRoster.Empty) <original behaviour>`, so a game without bots behaves exactly as today.
`BotRoster.IsBotMap` and `IsBotFaction` count every record whose map or faction still exists, whatever its saved
state. A removed bot whose colony was kept therefore keeps running under these rules (section 8).

1. **Letter-pause exemption.** `ReceiveLetterPause`'s transpiler replaces `TickManager.Pause` with `PauseOnLetter`.
   Change it to also insert `ldarg.1` (the `Letter`, `rw-src/Verse/LetterStack.cs:56`) and call
   `PauseOnLetter(TickManager, Letter)`. The transpiler checks it made exactly one replacement and logs an error
   otherwise. Return early when any of these holds:
   - `BotRoster.IsBotMap(Multiplayer.MapContext)`. This catches raid letters. They arrive in the bot map's tick or
     under `MapContextIncidentExecute` (`Source/Client/AsyncTime/StorytellerPatches.cs:106-127`).
   - `BotRoster.IsBotFaction(Faction.OfPlayer)`. This catches letters from bot global commands
     (`AsyncWorldTimeComp.cs:181`) and from the bot storyteller ticking inside other maps' ticks
     (`FactionRepeater.cs:157-169`).
   - `Multiplayer.MapContext == null && BotRoster.IsBotMap(let.lookTargets?.PrimaryTarget.Map)`. This catches
     delayed letters delivered by `LetterStackTick` in the world tick (`rw-src/Verse/TickManager.cs:467`), where
     `Faction.OfPlayer` is the spectator. Without the `MapContext` test it would also catch a human's own letter that
     points at a bot map.

   With the default AnyThreat, every ThreatBig or ThreatSmall letter would otherwise pause the bot map until someone
   sends a time command for it (async time), or pause everything (sync time). In async time a world-tick letter only
   sets the world's raw speed (`AsyncTimePatches.cs:207-208`). That matters for sync time and for the governor's
   fallback.
2. **Time governor** (borrowed, section 4; replaces all speed commands). In async time, the
   `AsyncTimeComp.DesiredTimeSpeed` getter on a bot map returns the maximum raw speed over non-bot maps, or the
   world's raw speed if there are none, capped by the bot's `speedCap` setting. Both comps get
   `internal TimeSpeed RawTimeSpeed => timeSpeedInt` (the fields are private, `AsyncWorldTimeComp.cs:22`,
   `AsyncTimeComp.cs:83`), and the governor reads only those. Calling the world's `DesiredTimeSpeed` getter would
   recurse: it reads every map's getter (`AsyncWorldTimeComp.cs:50-54`), so with no human map the two would call each
   other until the stack overflows. The setter is unchanged. Every input is a synced field, so every client computes
   the same value.

   Bot maps run while any non-bot map's raw speed is unpaused. That includes offline players' maps, temporary and
   site maps, and human maps held by a pausing session (raw speed ignores sessions, `AsyncTimeComp.cs:25-29`). They
   stop when every non-bot map's raw speed is Paused: PauseAll from pauseOnJoin (`ServerJoiningState.cs:29-30`) or
   pauseOnDesync (`PlayerManager.cs:115-116`), or the LowestWins ResetGlobal vote (`CommandHandler.cs:70-85`). Their
   own letter pauses and votes can't stall them. With async time off the governor does nothing.
3. **Growth moments.** A `ChoiceLetter_GrowthMoment`
   for a bot child would add a map `GrowthMomentSession` on every client
   (`Source/Client/Persistent/GrowthMomentSession.cs:80-95, 156-161`) and pause the bot map at the letter's timeout
   until someone chooses (`:33-35`). In that letter patch, when the pawn belongs to a bot faction, skip
   `TryAddSession` and make the choice in the tick: the first passions and the first trait (or none) through
   `letter.MakeChoices` (`rw-src/RimWorld/ChoiceLetter_GrowthMoment.cs:138`) *(unverified)*. The alternative is to
   drop the letter for bot pawns.

   No session type is exempt from pausing a bot map. Caravan forming, transporter loading, ritual, psychic ritual,
   map portal and gravship sessions pause on purpose, because their dialogs hold transferables or pawn lists that go
   stale while the map ticks (`CaravanFormingSession.cs:198`, `TransporterLoadingSession.cs:124`,
   `RitualSession.cs:89`, `PsychicRitualSession.cs:103`, `MapPortalSession.cs:41`, `GravshipTravelSession.cs:24`). The
   brain opens none of them, so a bot map pauses for one only if a human opens it there. A blanket exemption would
   also make an unresolved growth moment open its window on every bot-map tick (`GrowthMomentSession.cs:62-66`).
4. **Raid scaling.** In `StorytellerFactionTick.Tick`, just before `storyteller.TryFire(incident)` (`:24`), apply this
   when `BotRoster.IsBotFaction(Faction.OfPlayer)`, the category is `ThreatBig` or `ThreatSmall`, and
   `parms.points > 0`:

   `parms.points = Mathf.Clamp(points × threatScale, StorytellerUtility.GlobalPointsMin(), StorytellerUtility.GlobalPointsMax)`

   `GlobalPointsMin()` is the seeded floor (35 to `MinThreatPointsCeiling`) that `DefaultThreatPointsNow` uses. It
   pushes and pops Rand, so the map's Rand doesn't advance (`rw-src/RimWorld/StorytellerUtility.cs:12, 96-101, 187`;
   `rw-src/Verse/Rand.cs:241-247`). `IncidentWorker_RaidEnemy` keeps preset points
   (`rw-src/RimWorld/IncidentWorker_RaidEnemy.cs:83-88`).

   The hook covers storyteller-comp incidents and `QuestPart_ThreatsGenerator` raids (`Storyteller.cs:247-268`;
   `QuestPart_ThreatsGenerator.cs:17-24`). It misses incidentQueue items (`IncidentQueue.cs:68` calls `TryFire`
   directly) and quest parts that call `IncidentWorker.TryExecute` directly (`QuestPart_Incident.cs:68, 75`;
   `QuestPart_RandomRaid.cs:107`; `QuestPart_SurpriseReinforcement.cs:47`). A Harmony prefix on `Storyteller.TryFire`
   keyed on `parms.target is Map m && BotRoster.IsBotMap(m)` would also cover the queue. It is optional in M5.
5. **Bot digest** (borrowed, section 4). In `AsyncTimeComp.Tick`, inside the `try` after `CacheNothingHappening()`,
   every 60 map ticks on a bot map, compute an order-independent digest of bot state:
   - colonist count and the sum of their `thingIDNumber`s
   - the sum of ids and the count of the bot faction's buildings, blueprints and frames
   - the bot's zone count and total zone cells
   - the sum of bill `loadID`s
   - the current research project's `shortHash`
   - the roster record's state, cheat counters and cooldowns (only commands write these, section 8)

   Push it with `TryAddMapRandomState(map.uniqueID, (ulong)(uint)digest << 32)`. A state-only divergence on a bot map
   then shows up as "Wrong random state on map N" at the next digest, once that 30-tick opinion window closes and the
   host's opinion reaches a client. That is up to ~90 Timer ticks at 1x speed, plus latency. Today such a divergence
   only shows once it changes a Rand count. Nothing is detected while the bot map is paused, and only a client
   detects it.

   The message is the same as for a Rand-count mismatch (`ClientSyncOpinion.cs:46-47`), and the trace diff points
   nowhere because no Rand call differs. So every 10th digest also writes a `[BotDigest]` line with the components to
   the local log, on the host and on clients. A readable `TryAddInfoForDesyncLog` entry is added only when
   `ManagedStackTracing.Enabled && DeferredStackTracing.ShouldAddStackTraceForDesyncLog()`, the same gate as
   PawnStateTrace (`Source/Client/Desyncs/PawnStateTrace.cs:17`). Without that gate, trace lists would differ between a
   tracing and a non-tracing client.

### 3.7 Multiple bots

Each bot has one roster record, one faction, one home map, one connection pair and one `ServerPlayer`. Bots share no
state. Bot factions are Neutral to every player faction (`FactionCreator.cs:266-268`). `BotManager` runs at most
one bot's brain step per frame, round robin.

The host UI enforces a cap from host-local `MpSettings` (default 2) before it sends the create command. Inside the
command every client checks only the hard maximum of 4, or a cap carried in `BotCreationData`. It never reads
`MpSettings`, which is per-client (`MpSettings.cs:11`). The cap is low because each bot is a full colony map on every
machine.

---------------------------------------------------------------------------------------------------------------------

## 4. Why not the alternatives

**Deterministic in-tick agent** (brain inside the bot map's tick on every client, no commands). Its strengths are
real: state survives everything by construction, it has no latency, it works on standalone, and decisions replay. But
every line of brain code, and every game helper it calls, becomes simulation code on every client, so one per-client
read desyncs everyone. Its own audit already missed one: `Pawn_JobTracker.TryTakeOrderedJob` reads
`KeyBindingDefOf.QueueOrder.IsDownEvent` (`rw-src/Verse.AI/Pawn_JobTracker.cs:904`). `KeyIsDownPatch.shouldQueue` is
set only inside `SyncUtil.HandleCmd` (`SyncUtil.cs:77-81`), so during a tick the key state is read from the client's
own input. A host holding Shift while a bot rallies would queue the order on the host only.

It also costs brain CPU on every client, loses designators (placement re-implemented with lower-level APIs), and makes
every brain tweak a simulation change. **Borrowed:** the time governor, the bot digest in map random states, the
persisted stage and plan, the in-tick deterministic inbox for joiner letters (v2), the Common grid planner with golden
tests, and the IL lint idea.

**Host brain + host-only synced actions, no virtual player.** This was the closest runner-up: no server or protocol
change, smallest spike. It lost on identity:

- Every bot command is stamped as the host's player and faction, so logs, `/players` and desync reports can't tell
  them apart.
- Every action must re-push the bot faction and clear the issuer flag, dev mode and god mode on shared statics.
- About 15 bespoke wrappers re-implement what designators already do.
- Faction-swapped references deserialize under the host's managers.

**Borrowed:** `BotGuard` and `BotActionScope` (for host-sent commands), the rule "cells and ids, never Zone/Area
objects, in host-sent payloads", settlement naming with `SettlementNameGenerator`, restoring `InitData`/`Scenario`
after creation, the region-dirty planning gate, the ack counter idea (here: the roster's `actionSeq`), and the pure
`BotLayout` tests.

**Headless RimWorld process per bot.** The brain would be isolated, and every human code path would work unchanged.
It lost on several counts:

- It is unproven on 1.7: a headless instance has only reached the main menu (0x0 resolution error).
- Each bot costs a full extra world simulation plus an unthrottled loop on a core (`RunHeadlessLoop` has no frame
  throttle), and about 10 s of boot plus a catch-up per (re)join.
- Every bot desync triggers `PauseAll` and a forced join point for everyone (`Source/Common/PlayerManager.cs:109-121`,
  defaults `ServerSettings.cs:26,32`).
- It reuses `FactionCreator.CreateFaction` with its per-client reads.
- It needs process supervision and mod/config parity.

**Borrowed:** the `PacingPlayers` property (now defence in depth only), giving bots a chat voice (`ClientChatPacket`
on the bot connection), persona usernames, reusing the existing `DoBeginResearch` sync, `PlaySettings.autoRebuild`
through the existing `SyncAutoRebuild` field, the `HandleSetFaction` restriction, and (optionally) a third instance as
an extra desync detector in soak tests.

**A server-stamped "on behalf of bot" packet** would also work. It is rejected because it re-implements the player
list, PlayerData, `/kick`, chat and policy, which a real `ServerPlayer` gets for free.

---------------------------------------------------------------------------------------------------------------------

## 5. Add-bot flow

Preconditions: hosted game (`Multiplayer.LocalServer != null`, `!session.ConnectedToStandaloneServer`), multifaction
on (`Multiplayer.GameComp.multifaction`, chosen in the host window), and fewer bots than the host-local cap. Async time
is strongly recommended; the dialog warns when it is off.

| # | Who | Step | APIs reused / added |
|---|---|---|---|
| 1 | host UI | Chat → Factions (`Source/Client/Windows/ChatWindow.cs:98-103`) → "Add bot" | new button in `Source/Client/Factions/FactionsWindow.cs` |
| 2 | host UI | `Dialog_AddBot`: persona username (3-15 chars, `UsernamePattern` `^[a-zA-Z0-9_]+$`, unique among online names and the roster, not the host's name, and not a substring of a human's name, `Source/Common/ChatCommands/ChatCommandArgumentReader.cs:69-78`), faction name (unique like `FactionSidebar.cs:477-502`), colour (first palette colour no player faction uses), cheat level Off/Light/Normal, colonists (scenario default via `FactionCreator.GetStartingPawnsCountForScenario`), ideo preset (random `IdeoPresetDef` when Ideology is active and not classic), tile "Random". The `MpSettings` cap is checked here only | `TileFinder.RandomSettlementTileFor(Find.WorldGrid.Surface, Faction.OfPlayer, true, t => far from player settlements)` + `TileFinder.IsValidTileForNewSettlement` (`rw-src/RimWorld.Planet/TileFinder.cs:26, 65`). Interface Rand never touches the simulation |
| 3 | host | `BotFactory.CreateBotFaction(BotCreationData)` | new; `SyncMethod.Register(...).SetHostOnly()`; `BotCreationData : ISyncSimple`; global command on the host connection |
| 4 | every client, tick T, in the command | `BotGuard.FromHost` + `BotActionScope`. Re-validate: multifaction, the hard maximum (or the cap in `BotCreationData`), username unique in the roster, faction name unique among factions, tile valid. Never `session.players` or `MpSettings`, which differ per client. On failure a historical message and return. `rosterId = GameComp.nextBotId++`; queue the long event with BotFactory's own exception handler | `AsyncWorldTimeComp.ExecuteCmd`. Never `ErrorWhileGeneratingMap` (`FactionCreator.cs:96`): it sends that client to the main menu, and on the host that stops the server for everyone |
| 5 | every client, long event (later frame, outside the command) | Save `Scenario`, `InitData`, `Prefs` dev mode, `godMode`, `currentMapIndex`, and a copy of `StartingPawnUtility.StartingAndOptionalPawnGenerationRequests` (private static, `rw-src/Verse/StartingPawnUtility.cs:28`, reached through the publicizer), all before anything can throw. Then `bool pushed = false; try {`. Set dev and god mode false, `currentMapIndex = -1` by field write (no current map, as in a vanilla new game) *(unverified: `Game.AddMap` leaves `currentMapIndex` alone, but other generation code may read `Find.CurrentMap`; M4 checks it)*, and `Current.Game.Scenario = ScenarioDefOf.Crashlanded.scenario` | The event has no command context (section 2), so it sets everything itself. The scenario removes the per-client `Find.Scenario` difference (`FactionSidebar.cs:447`). The request list is cleared by `ClearAllStartingPawns` (`:242`); without the copy a human with the multifaction pawn page open on any client would lose its slots, and Biotech's `PrepForMapGen` would refill it with bot requests (`GameInitData.cs:132-139`) |
| 6 | every client | Ideo: preset → `GenerateIdeo(preset, structure, forFaction: scenario.playerFaction.factionDef)` (new explicit-def overload), with styles `new List<StyleCategoryDef>{ null }`. Classic or no Ideology → primary ideo of the first non-spectator, non-temporary player faction. `NewFactionWithIdeo(name, color, def, ideoOverride)`; `Find.IdeoManager.Add` only for a freshly generated ideo. Then rebuild the bot's storyteller: `factionData[bot.loadID].storyteller = new Storyteller(src.def, src.difficultyDef, <copy of src.difficulty>)`, where `src` is the first non-spectator, non-temporary player faction's `FactionWorldData`, or values carried in `BotCreationData` | `FactionCreator.cs:219-286` made `internal` plus an explicit-ideo parameter. This avoids the reads of `Faction.OfPlayer` (`:234, :244`), `Find.Scenario` (`:279`), `Find.FactionManager.OfPlayer.def` (`:282`), and the fourth one: `FactionWorldData.New` copies each client's own `Find.Storyteller` (`:258` → `Source/Client/Comp/World/FactionWorldData.cs:83`). A null style list throws in `ApplySelectedStylesToIdeo` (`rw-src/RimWorld/Page_ChooseIdeoPreset.cs:577-584`). A duplicate `Add` logs an error (`IdeoManager.cs:83-95`). The method's own relation loops (`:253-254, :266-268`) also see a temporary faction that exists on one client only while its multifaction pawn page is open (`Page_ConfigureStartingPawns_Multifaction.cs:46-52`); `FactionManager.Remove` cleans those relations when the page closes (`FactionManager.cs:107-145`). That is pre-existing and shared with the human path |
| 7 | every client | `FactionExtensions.PushFaction(null, f, force: true)`, then `pushed = true`, then `map.MpComp().SetFaction(f)` on every existing map, mirroring `AsyncWorldTimeComp.PreContext` (`:144-156`). Then the bot ideo's default drug policies: for each precept with `defaultDrugPolicyOverride`, `Current.Game.drugPolicyDatabase.MakePolicyDefault(...)` | `NewFactionWithIdeo` already added `f`'s `FactionMapData` to every map (`FactionCreator.cs:262-264`), so `SetFaction` finds it. Unlike `MpScope.PushFaction` (`Source/Client/Util/MpScope.cs:21-24`) this swaps world data too, so research, policies and the storyteller are the bot's. Drug policies as `ScenPart_ConfigPage_ConfigureStartingPawnsBase.cs:32-41`, without its `customXenotypeDatabase.Clear()` (global state) |
| 8 | every client | `Current.Game.InitData = new GameInitData { startingPawnCount = n, gameToLoad = FactionCreator.preventSpecialCalculationPathInGenTicksTicksAbs, startingAndOptionalPawns = new(), startingPossessions = new() }`. Up to 20 tries of `ClearAllStartingPawns()` + `AddNewPawn()` × n, until `WorkTypeRequirementsSatisfied()`. Set each pawn's ideo. Hash the pawns (`thingIDNumber`, `StableStringHash` of names, skill levels, trait `Def.shortHash`; never `GetHashCode`) and fold it as `TryAddCommandRandomState(((ulong)(uint)hash) << 32)` | `StartingPawnUtility.cs:46-57, 228-243, 447-487`; vanilla loop `ScenPart_ConfigPage_ConfigureStartingPawns.cs:64-77`; vanilla generates in Playing too (`Dialog_ChooseNewWanderers.cs:62-69`). `TryAddCommandRandomState` keeps only the high 32 bits (`SyncCoordinator.cs:162-167`). `DefaultStartingPawnRequest` takes `Faction.OfPlayer` (the pushed bot). `TutorSystem.TutorialMode` and `ChildrenAllowed` read the bot's storyteller, which is deterministic after step 6. The hash makes a generation divergence fail loudly at once |
| 9 | every client | `map = FactionCreator.GenerateNewMap(tile, scenario, false)` (internal), with a new optional step that names the settlement before `Find.WorldObjects.Add`, as vanilla does (`rw-src/RimWorld/ScenPart_PlayerFaction.cs:58-65`): `SettlementNameGenerator.GenerateSettlementName(settlement, f.def.playerInitialSettlementNameMaker)` (`rw-src/RimWorld.Planet/SettlementNameGenerator.cs:10`). Attack-cache notifications as `FactionCreator.cs:76-78`, skipping temporary factions. `FactionCreator.InitNewGame(scenario)` under the full push. Fold a map hash (thing count, sum of `thingIDNumber`s) the same way | `GenerateNewMap` creates the settlement internally and returns only the map (`FactionCreator.cs:115-157`, settlement at `:123-126`). The name comes out "Colony N" (`NamerInitialSettlementColony`); the bot's name would be more distinctive. Starting things arrive by drop pod (`MultifactionPatches.cs:700-730`) and stay forbidden for every faction (`Forbiddables.cs:106`); setup rule S2b unforbids them |
| 10 | every client | Add `BotRecord{ id, factionLoadId, homeMapId, username, persona, settings, state = Active, createdTick }`; historical message `MpBotFounded` with the bot's name. `} finally {` if `pushed`, pop the push and set every map's `MpComp` back to the client's own faction; then, always, restore `currentMapIndex`, `InitData`, `Scenario`, the request list, dev and god mode `}` | roster in `MultiplayerGameComp`. `FactionContext.Pop` has no guard (`Source/Client/Factions/FactionContext.cs:25-31`). Without the flag, an exception in step 6 would make the `finally` pop a push that never happened: on an empty stack that throws, loses the original exception and skips the other restores; otherwise it pops another caller's entry |
| 11 | host only, next frames | `BotManager` reconciliation: an Active or Defeated record with no runtime → runtime Registering, create the connection pair, then `LocalServer.Enqueue(() => { var p = BotServerApi.RegisterBot(server, serverHalf, name, factionLoadId); OnMainThread.Enqueue(...) })`, with the closure written in Client code. The runtime goes Active once the host's `session.players` holds the bot with the record's faction id, or Suspended if `RegisterBot` refused | server thread, one action (3.4): validate, `OnConnected`, `OnJoin` ("Ada_bot has joined.", player list Add), `ServerPlaying`, `InitPlayerData(id, false)`, faction set and `ServerSetFactionPacket`. The bot faction exists before any player is bound to it |

**Failure path.** The long event gets BotFactory's own exception handler. It logs the exception, posts
`MpBotCreateFailed` and removes a half-made faction or settlement. The `finally` in steps 5-10 has already restored
the context. An exception on only some clients is a desync anyway; the pawn and map hashes make it visible at once.
The exact cleanup calls are an M4 design item (section 13).

The same reconciliation handles a fresh Add, loading a save with bots, re-hosting after a restart, and Resume after a
kick.

**What the host must skip, and does skip because `BotFactory` contains none of it:**

- the issuer block of `FactionCreator.CreateFaction` (`:83-95`): `Current.Game.CurrentMap = newMap`,
  `ChangeRealPlayerFaction`, `InitLocalVisuals`/`CameraJumper`, `PortraitsCache.Clear`, and
  `ClientSetFactionPacket(session.playerId, …)`, which would move the host into the bot faction;
- the sidebar's permanent `Scenario`/`InitData` replacement (`FactionSidebar.cs:445-454`);
- any write through the `Game.CurrentMap` setter.

**Fallback if in-command pawn generation diverges (step 8):** generate the pawns on the host with the UI pattern of
`Page_ConfigureStartingPawns_Multifaction` (`Source/Client/Factions/Page_ConfigureStartingPawns_Multifaction.cs:11-84`)
and ship them with the existing `FactionCreator.SendPawn` under a synthetic negative key. Changes needed:

- Expose `FactionCreator`'s private `pawnStore` (`FactionCreator.cs:18, 27-31`) to `BotFactory` through an internal
  accessor.
- On the host, set `Current.Game.InitData = new GameInitData { startedFromEntry = true, gameToLoad = ... }` and
  `Scenario = Crashlanded` before `FactionGenerator.NewGeneratedFaction`, which reads
  `Find.GameInitData.startedFromEntry` (`rw-src/RimWorld/FactionGenerator.cs:142`). Restore both afterwards.
- Generate under `ProgramState.Entry` with `InitData.playerFaction` = the temporary faction, by calling
  `StartingPawnUtility` directly (Clear + `AddNewPawn` loop). The page's own generation is
  `_scenario.PostIdeoChosen()` (`:81-84`), which is not used.
- Remove the temporary faction, and the ideo it adds to the host's `IdeoManager` only (`FactionIdeosTracker.cs:196-199`),
  in the same frame.
- Restore `ProgramState.Playing` before calling `SendPawn`, so it syncs (as `FactionSidebar.cs:370-401` does).
- In the long event, re-set ideo and faction, as step 8 and `PrepForMapGen` already do. Negative ids are reassigned on
  read (`SharedCrossRefs.cs:32-44`).

`SendPawn` has no host-only guard, so any client could write to the synthetic key. This costs about 1 day.

**Separate finding, not part of this work unless you want it:** the human path has four per-client reads in
`FactionCreator`.

1. The ideo fallback reads `Faction.OfPlayer` (`:234, :244`). In the long event that is each client's own faction:
   the spectator on the issuer (`SidebarPatch.cs:19`), whose ideo is generated separately (`HostUtil.cs:162-167`).
2. `GenerateIdeo` reads `Find.Scenario` (`:279`, per-client via `FactionSidebar.cs:447`) and
   `Find.FactionManager.OfPlayer.def` (`:282`).
3. `FactionWorldData.New` copies `Find.Storyteller` from the local faction (`:258` → `FactionWorldData.cs:83`).
   Storyteller changes are host-only sync fields applied in the host faction's context (`SyncFields.cs:211-213,
   249-253`), so factions' storytellers can differ.
4. `InitNewGame` runs under `MpScope.PushFaction`, which only sets `ofPlayer`. So
   `ResearchUtility.ApplyPlayerStartingResearch` (`rw-src/ResearchUtility.cs:6-50`),
   `ScenPart_StartingResearch.PostGameStart` and `Notify_MonolithLevelChanged` (`FactionCreator.cs:161-172`) write into
   each client's own faction's research manager (the spectator's on the issuer). The new faction's manager gets
   nothing.

These are code-derived and *(unverified in game)*. Fixing them is a small separate commit; the storyteller fix is the
same as step 6.

---------------------------------------------------------------------------------------------------------------------

## 6. The brain

### 6.1 Where and when it runs

`BotManager.Update()` is called at the end of `OnMainThread.Update`. That is mod code, not a new Harmony patch. A step
runs only if all of these hold:

- `Multiplayer.LocalServer != null`, `!Multiplayer.arbiterInstance`, `!Multiplayer.IsReplay`
- `session.client.State == ClientPlaying` and `!session.desynced`
- `Multiplayer.ShouldSync`
- `!TickPatch.Simulating`, `!TickPatch.Frozen` and `TickPatch.ShouldHandle` (`Source/Client/Patches/TickPatch.cs:42-44`)
- `LongEventHandler.eventQueue.Count == 0`
- the reload epoch is unchanged (otherwise the think and expectations are dropped first, 3.3)
- the bot's home map exists
- the record's state is Active and the runtime state is Active (section 8, Bot states). The runtime leaves Active when
  a human is on the bot faction (TakenOver) and when the bot's `session.players` entry is gone or its faction id no
  longer matches the record (Suspended)

The brain keeps running while the host window is unfocused (`Application.runInBackground`,
`Source/Client/Session/MultiplayerSession.cs:125`).

### 6.2 Scheduling and budget

- Round robin: at most one bot steps per frame. A bot "thinks" when its map's `mapTicks >= nextThink`. That is every
  250 map ticks in peace and every 60 under threat, plus whenever a pending expectation resolves.
- A think is an `IEnumerator` of rule evaluations resumed across frames. `BotManager` opens the bot view and the
  command scope around each `MoveNext()` and closes them in `finally` (3.3). Each resume runs under a host-local
  `Stopwatch` budget of 1.5 ms. Wall clock is fine here because nothing host-local enters the simulation. The host's
  frame time becomes everyone's `serverTimePerTick` (`MultiplayerServer.cs:194`), so the budget protects every
  player. Heavy searches (base site, tree list) yield every few hundred cells.
- Rate limits: at most 5 commands per think and 10 per real second per bot. Threat orders bypass the per-think cap.
- Expectations: every action records `(signature, sentAtTimer, predicate)`, e.g. "blueprint, frame or building of def
  X at cell C".
  - Latency: a command is stamped with the server's `gameTimer` when it arrives, and clients deliberately run 3-7
    Timer ticks behind `tickUntil` (`TickPatch.cs:75-81`). So a bot command executes at least 3-7 frames after it was
    sent, plus one server loop (~33 ms).
  - A predicate that isn't observed within 300 `TickPatch.Timer` ticks is logged `[BotAudit] expired`, and the rule
    backs off ×10. This also catches commands that server policy silently dropped (`CommandHandler.cs:20-38`).

### 6.3 State: mostly stateless

Rules are idempotent and re-derived from the world each think ("no stockpile → make one"). Three things are persisted
in the `BotRecord`, through bot-sent `BotRosterEdits` that run as commands on every client: the base **anchor**, the
**stage**, and the **plan**, a list of `{def, cell, rot, stuff}`. A re-hosted bot continues the same base instead of
re-deriving a different one.

Host-only memory holds cursors, pending expectations, the last 50 actions for the debug window, per-rule fault timers
and the reload epoch. `BotManager.Reset()` clears it from `Multiplayer.StopMultiplayer`
(`Source/Client/Multiplayer.cs:219`). An epoch change drops the current think and the expectations.

### 6.4 Action catalogue (all inside the bot view; existing sync unless marked new)

| Action | Call | Sync path |
|---|---|---|
| Work priority | `pawn.workSettings.SetPriority(w, 3)` after `!pawn.WorkTypeIsDisabled(w)` | SyncMethod (`SyncMethods.cs:39`) |
| Stockpile / growing zone | `new Designator_ZoneAddStockpile_Resources()` / `new Designator_ZoneAdd_Growing()` `.DesignateMultiCell(cells)`. First filter the cells with the same designator's `CanDesignateCell(c).Accepted` in the bot view, because the zone designator re-checks nothing at execution. With an empty selector it makes a new zone and never merges with an adjacent one, unless the drag overlaps exactly one zone of the same type, which is then extended. With the bot's zone as `selected[0]`, only cells 4-connected to it (directly or through other dragged cells) extend it; disconnected groups become new zones, and cells already in any zone are skipped (`rw-src/RimWorld/Designator_ZoneAdd.cs:103-190`) | Designator prefix + selected-zone extra (`Designators.cs:45-69, 122-123`; read side `AsyncTimeComp.cs:367-372`) |
| Trees / harvest | `Designator_PlantsHarvestWood` / `Designator_PlantsHarvest` `.DesignateMultiCell` | base `Designator.DesignateMultiCell` re-checks `CanDesignateCell` per cell at execution (`rw-src/Verse/Designator.cs:261-281`) |
| Build | `var d = new Designator_Build(def); d.SetStuffDef(stuff); d.placingRot = rot; d.DesignateMultiCell(cells)` | prefix; serializer writes def/rot/stuff/precept (`SyncDictRimWorld.cs:650-668`). With null stuff, the default stuff is picked at execution from the command map's resource counter (`rw-src/RimWorld/Designator_Build.cs:34-65`) |
| Unforbid | `Designator_Unforbid.DesignateMultiCell` | prefix. Forbid state is per faction: a thing spawned under another faction's context is forbidden for the bot, and everything that comes out of a drop pod is forbidden for every faction (`Source/Client/Factions/Forbiddables.cs:98-111`, the ActiveDropPod skip at `:106`) |
| Bills | `var b = (Bill_Production)recipe.MakeNewBill(); b.repeatMode = …; b.targetCount = …; giver.BillStack.AddBill(b)` | SyncMethod with the bill exposed (`SyncMethods.cs:47`), so fields set before the call travel with it |
| Research | `new MainTabWindow_Research().DoBeginResearch(proj)` (private, via the publicizer) | existing non-debug sync with a transformed target (`SyncMethods.cs:57-58`). The global command pushes the bot faction, so it reaches the bot's `ResearchManager` (multifaction; checked in code). Inside the command it plays the ResearchStart sound on every camera and, without a bench, posts a historical "MessageResearchMenuWithoutBench" on every client (`rw-src/RimWorld/MainTabWindow_Research.cs:693-702`). S12 calls it only once the bench exists; the sound is cosmetic. `SetCurrentProject` itself is debug-only (`:65`) and would be dropped. Fallback: a bot-owned `SetResearch` without the UI side effects |
| Auto rebuild | `((SyncField)SyncFields.SyncAutoRebuild).DoSync(null, true)` once | global SyncField (null target type, `SyncField.cs:46-61`) resolved at execution through `Verse.Current/Game/playSettings` (`:80-104`; `SyncFields.cs:122`). Under the global command's faction push that is the bot's `PlaySettings` (multifaction; checked in code) |
| Fight back | `((SyncField)SyncFields.SyncHostilityResponse).DoSync(pawn, HostilityResponseMode.Attack)` | SyncField (`SyncFields.cs:20`; `DoSync` at `SyncField.cs:35-64`) |
| Draft / move / attack / equip | `pawn.drafter.Drafted = b`; `pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto / AttackMelee / AttackStatic / Equip, target), JobTag.Misc)`, after a `CanReserveAndReach` check (3.3). An Equip job may set `job.ignoreForbidden = true`, which `Job.ExposeData` saves (`rw-src/Verse.AI/Job.cs:515`) | SyncMethods (`SyncMethods.cs:25, 40`); the job is exposed and the QueueOrder bit is forced false in bot scope. `JobDriver_Equip` fails on a forbidden target unless `ignoreForbidden` is set (`rw-src/Verse.AI/JobDriver_Equip.cs:70-76`) |
| Beds | `bed.Medical = true`; `comp.TryAssignPawn(p)` (optional; colonists claim beds themselves) | `SyncMethods.cs:82-104` |
| Map dialogs | `dialog.Click(ver, opt)` for `map.MpComp().mapDialogs` | SyncMethod (`Source/Client/Persistent/PersistentDialogs.cs:152-157`) |
| Crop choice (v2) | new bot-owned `BotRosterEdits.SetPlant(Faction, Zone_Growing, ThingDef)` | `SetPlantDefToGrow` is only synced through the gizmo lambda (`SyncDelegates.cs:38`) |
| Chat | `botClientConnection.Send(ClientChatPacket.Create(text))`, never starting with '/', at most 1 line per in-game hour per bot | server broadcasts "Ada_bot: text" (`ServerPlayingState.cs:61-85`) |

### 6.5 MVP behaviour

Every peaceful think starts with this rule, from the first think on:

- **P4 Clutter:** things in `map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)` that are
  `t.IsForbidden(botFaction)`, not in storage, and in the bot's home area → unforbid. `listerHaulables` can't be used:
  it excludes forbidden things (`rw-src/RimWorld/ListerHaulables.cs:194-199`).

Setup rules, idempotent, evaluated in order each peaceful think until all hold:

| # | Rule | Condition → action |
|---|---|---|
| S0 | Anchor | no saved anchor → spiral from the colonists' centroid for a 7x7 room rect whose cells pass `GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall, c, Rot4.North, map, false, null, null, stuff)`: not fogged, no water, no thick roof, with room for a 6x6 stockpile and an 8x8 field nearby. The pure search lives in `Source/Common/Bots/GridPlanner.cs`. Persist anchor + plan |
| S1 | Settings | once: auto rebuild on; `hostilityResponse = Attack` for colonists capable of violence |
| S2 | Work coverage | a work type in {Firefighter, Doctor, Cooking, Construction, Growing, PlantCutting, Hauling, Cleaning, Research} with no colonist at priority > 0 → the best non-disabled colonist by `skills.AverageOfRelevantSkillsFor(w)` gets 3 |
| S2b | Start items | items near the start spot (the colonists' centroid, or the anchor once saved) that are forbidden for the bot → `Designator_Unforbid.DesignateMultiCell`. Crashlanded's possessions arrive by drop pod with `forbid: true` (`rw-src/RimWorld/ScenPart_PlayerPawnsArriveMethod.cs:105`), and MP keeps drop-pod contents forbidden for every faction (`Forbiddables.cs:106`). Runs before S3, so weapons, meals and medicine are usable from day 1 |
| S3 | Weapons | an unarmed, violence-capable colonist and a weapon on the bot's map that it can reserve and reach → Equip job (unforbidden by S2b, or with `job.ignoreForbidden = true`). Crashlanded drops a bolt-action rifle, revolver and knife (`Defs/Scenarios/Scenarios_Classic.xml:96-107`) |
| S4 | Stockpile | no `Zone_Stockpile` (bot's zone manager) → 6x6 next to the room door, cells passing the stockpile designator's `CanDesignateCell(c).Accepted`. That covers `IsZoneableCell` plus the impassable-terrain check (`Designator_ZoneAddStockpile.cs:40-58`) and the other-zone check (`Designator_ZoneAdd.cs:66-77`) |
| S5 | Field | no `Zone_Growing` → 8x8 whose cells pass `ThingDefOf.Plant_Potato.CanEverPlantAt(c, map, canWipePlantsExceptTree: true)` (`rw-src/RimWorld/PlantUtility.cs:216`) and the growing designator's `CanDesignateCell`. The default `false` rejects any plant, grass included (`PlantUtility.cs:160`, `ThingDef.cs:1374-1377`); vanilla farm code passes `true` (`SymbolResolver_WorkSite_Farming.cs:59`). Trees still block. Fertility ≥ 0.7 is already enforced, because the potato's `fertilityMin` is 0.7 (`Defs/ThingDefs_Plants/Plants_Bases.xml:23`, `PlantUtility.cs:85`) |
| S6 | Wood | stockpiled wood below the plan's need and < 6 trees designated → harvest the nearest mature trees within 30 cells, not in another faction's home area |
| S7 | Shelter | plan walls/door missing (no building, blueprint or frame of the bot) → `Designator_Build(Wall)` on the perimeter, `Door` on the door cell. The roof comes on its own: an enclosed room gets a build-roof area under the walls' faction (`Source/Client/Factions/AutoRoofFactionPatch.cs:12-39`) |
| S8 | Sleep | beds + spots < colonists → `SleepingSpot` first (WorkToBuild 0, placed instantly), later wooden `Bed` |
| S9 | Cooking | no Campfire → build it; once built: bill `CookMealSimple`, TargetCount = 2 × colonists (Campfire recipes: `Defs/ThingDefs_Buildings/Buildings_Temperature.xml:105-115`) |
| S10 | Butchering | no `ButcherSpot` → build next to the stockpile; bill `ButcherCorpseFlesh` Forever |
| S11 | Table | `Table1x2c` + 2 `Stool` in the room |
| S12 | Research | `SimpleResearchBench` missing → build it; bench exists and no project → cheapest `CanStartNow` from a preferred list looked up with `GetNamedSilentFail` (defNames *(unverified)*), else the cheapest available |

Periodic rules (each peaceful think, after setup):

- **P1 Food:** `daysOfFood = resourceCounter.TotalHumanEdibleNutrition / (colonists × 1.6)`. This counts stored food
  only (`rw-src/RimWorld/ResourceCounter.cs:128-143`), so it reads 0 until a stockpile exists and the starting meals
  are unforbidden and hauled. Below 3: raise the cooking target and designate berry harvests. Below 0.75 with no raw
  food: the cheat trigger (section 7), armed only once S4 holds.
- **P2 Rebuild:** plan cells missing both building and blueprint/frame get their blueprint placed again. Auto rebuild
  handles destroyed buildings.
- **P3 Expand:** colonists > sleeping places → more beds.
- **P5 Dialogs:** answer `mapDialogs` with the closing option (resolveTree, no link; prefer reject).
- **P6 Bills hygiene:** re-add missing cooking/butcher bills.

Threat rules (checked every think):

- **T1 Detect:** `GenHostility.AnyHostileActiveThreatTo(map, botFaction)`, with a threat within 45 cells of the anchor
  or of a colonist.
- **T2 Rally:** draft violence-capable, non-downed colonists and send them to distinct cells inside the room near the
  door. Pick each cell with `RCellFinder.BestOrderedGotoDestNear(root, pawn, c => !pickedThisThink.Contains(c))`
  (`rw-src/RimWorld/RCellFinder.cs:75`). Destination reservations exist only once a Goto has executed
  (`JobDriver_Goto.cs:13`), so without the validator every pawn would get the same cell. It returns
  `searcher.Position` on failure.
- **T3 Counter:** if hostiles in view ≤ half of our able fighters, or all of them are downed or fleeing, attack the
  nearest.
- **T4 Fall back:** health below 40% or bleeding → undraft (the patient AI takes the pawn to bed).
- **T5 Stand down:** no threat for 600 ticks → undraft everyone.

**What vanilla AI does on its own** once the inputs exist (`rw-src/RimWorld/JobGiver_Work.cs:49-130`, Humanlike think
tree): hauling to stockpiles, sowing and harvesting zones, chopping designated trees, building same-faction blueprints,
bills with ingredients, research at a bench with a project, eating, sleeping and bed claiming, tending and feeding
patients, firefighting and cleaning in the home area, roofing enclosed rooms, home area growth (`autoHomeArea`), joy,
and recruiting prisoners once a mode is set.

**What needs the brain:** zones, blueprints, designations, bills, the research project, work coverage, drafting and
attacks, equipping weapons, capturing, accepting joiners, answering dialogs, and unforbidding stray items. Humanlikes
never pick up a new weapon on their own; they only re-equip their own weapon dropped while downed
(`Defs/ThinkTreeDefs/Humanlike.xml:297-300`, `JobGiver_PickupDroppedWeapon`).

### 6.6 v2 list

- Joiner acceptance through a small in-tick deterministic inbox. `BotRecord.inbox` takes `ChoiceLetter_AcceptJoiner`
  ids whose map is a bot map. It needs its own hook on letter arrival. The letter-pause hook can't see these letters:
  `LetterStack.ReceiveLetter` calls `Pause` only when `AutomaticPauseMode >= let.def.pauseMode` (`LetterStack.cs:70-73`),
  so non-pausing letters never reach it, and with pauseOnLetter = Never nothing does. The next bot-map tick runs the
  accept option, which sends the signal and removes the letter (`rw-src/RimWorld/ChoiceLetter_AcceptJoiner.cs:45-55`).
  The accept path still needs a per-client-read audit *(unverified)*.
- Hunting.
- Prisoners: capture a downed raider (`JobDefOf.Capture` + prisoner bed), then `SetExclusiveInteraction(AttemptRecruit)`.
- A second room, stone blocks, a freezer and power.
- Crop variety through the bot-owned `SetPlant`.
- An adaptive speed cap: the host lowers `speedCap` through `SetBotSettings` when `serverTimePerTick` or human ticks
  behind rise, with hysteresis.
- "Bot-controlled" label on the time controls of bot maps.
- Social chat lines ("raid incoming", "we held").
- Opt-in rival bots.

Trade, caravans, rituals and transporter loading need issuer-side dialogs that no client owns for a bot. They are not
planned.

---------------------------------------------------------------------------------------------------------------------

## 7. Limited cheats

Cheats are bot-owned static methods in `BotCheats`, registered with `SyncMethod.Register(...).SetHostOnly()`. The
brain sends them inside the bot scope, so they go out on the bot's connection. The server accepts them because
`IsBot` passes the host-only check (3.4). Humans' copies are dropped. Every signature is `(Faction faction, Map map, …)`:
the `Map` makes it a map command on the bot map with the forced bot push (3.5), and the `Faction` travels by loadID.

Each call runs this cycle inside the command, on every client:

1. Check `BotGuard.FromBot(faction, map)`. This also proves the command was stamped with the bot's faction.
2. The roster's cheat level must allow this kind.
3. The cooldown must have expired: `rec.lastCheat[kind]` vs `Find.TickManager.TicksGame`, which is the bot map's
   `mapTicks` inside a map command.
4. Re-check the trigger on simulation state.
5. Apply the effect. `ExecuteCmd` has pushed the bot faction, so items placed inside the command are added to the
   bot's unforbidden set when they spawn. Place them with `GenPlace.TryPlaceThing`, or with `DropThingsNear(...,
   canInstaDropDuringInit: true, forbid: false)`, which then places them at once with `GenPlace`
   (`rw-src/RimWorld/DropPodUtility.cs:27, 69-76`). Real drop pods can't be used. Their contents spawn later inside
   the pod's tick (`rw-src/RimWorld/ActiveTransporter.cs:53-62, 83-120`), and MP keeps them forbidden for every
   faction whatever the forbid flag (`Forbiddables.cs:106`).
6. `rec.lastCheat[kind] = now`, `rec.cheatsUsed++`, `rec.actionSeq++`.
7. A historical `Messages.Message` on every client, with the bot's name and the new `cheatsUsed` count in the text
   (3.5).

The brain posts a bot chat line after it sees `cheatsUsed` go up, so a refused request is never announced. Duplicates
and races resolve the same way on every client. Rand comes from the command's map stream, and ids are real.

| Cheat | Brain trigger (host) | In-command gate | Effect | Cooldown | Level |
|---|---|---|---|---|---|
| Emergency rations | `daysOfFood < 0.75` and no raw food for 1 day; armed once S4 holds | stored nutrition < colonists × 1.6 | `MealSurvivalPack` × 4 × colonists, placed in the command (step 5) at a bot stockpile cell or the anchor | 5 days | Light |
| Medicine | a colonist needs tending and no medicine is stockpiled | same | `MedicineHerbal` × 6, placed the same way | 7 days | Light |
| Stabilize | `BleedRateTotal > 0.1` and no capable conscious doctor | still bleeding | `TendUtility.DoTend(null, patient, null)` up to 3× while tendable (`rw-src/RimWorld/TendUtility.cs:26`) | 1 day per pawn | Light |
| Reinforcement | fewer than 2 free colonists for a full day | count again | 1 colonist: `PawnGenerator.GeneratePawn(new PawnGenerationRequest(faction.def.basicMemberKind, faction, …))`, spawned at `RCellFinder.TryFindRandomPawnEntryCell`, `workSettings.EnableAndInitializeIfNotAlreadyInitialized()` | 10 days, max 3 | Normal |
| Rescue after wipe | 0 colonists alive | same | 2 colonists + rations | 15 days, max 3 per bot | Normal |
| Softer raids | continuous (3.6 rule 4) | n/a | threat points × `threatScale` | n/a | Off 1.0, Light 0.75, Normal 0.6 |

Visibility: there is a historical message on every screen, a bot chat line, a per-bot "cheats used" counter in the
Factions window, and `[BotSummary]` log lines.

After the cap, or with cheats Off, a wiped bot goes Defeated. The brain sends `BotRosterEdits.SetDefeated` on its
connection, and the body re-checks 0 colonists and no rescue left. The brain stops, and the Factions window offers
Remove (keep colony or abandon). GameEnder is disabled in MP (`Source/Client/Patches/Patches.cs:19-24`), so a wipe
never ends the game. The cheat level changes only through the host-sent `SetBotSettings` (M8).

Per-faction `Difficulty.threatScale` is not used. Before the first reload every faction storyteller shares one
`Difficulty` object (`FactionWorldData.cs:83`), so a change would hit everyone. After any join point each storyteller
loads its own `Difficulty`, rebuilt from the def (`rw-src/RimWorld/Storyteller.cs:166-173`), so a per-faction value
would be lost on reload for non-custom difficulties.

---------------------------------------------------------------------------------------------------------------------

## 8. Persistence and sessions

| What | Where | Survives |
|---|---|---|
| Roster: `List<BotRecord> bots`, `int nextBotId`. `BotRecord : IExposable { id, factionLoadId, homeMapId, username, persona, settings{cheatLevel, threatScale, speedCap}, state (Active/Defeated/Removed), anchor, stage, plan, lastCheat, cheatsUsed, actionSeq, createdTick, schemaVersion }` | `MultiplayerGameComp.ExposeData` (`Source/Client/Comp/Game/MultiplayerGameComp.cs:28-41`), `Scribe_Collections.Look(ref bots, "bots", LookMode.Deep); bots ??= new()`; scribed under `mpGameComp` (`Source/Client/Saving/SavingPatches.cs:15-35`) | saves, autosaves, join points, rejoins, replays, host restart |
| Bot faction, map, pawns, zones, bills | the game | everything |
| Bot `ServerPlayer` and player id | server memory | nothing; re-created by reconciliation with a new id |
| `playerData[botId]` | session data | reloads; a fresh `InitPlayerData` after re-hosting; stale ids are inert |
| Brain memory and runtime state | host RAM | nothing; rules re-derive from the world and the persisted plan |

`BotRecord` holds values only: ints, defs, cells and strings. `mpGameComp` is scribed only in LoadingVars and Saving
(`SavingPatches.cs:24-26`), so `Scribe_References` or `LookMode.Reference` to a Faction, Map or Zone would never
resolve.

`FactionWorldData` is not a safe home for a bot flag, because the current faction's entry is rebuilt with
`FromCurrent` on load (`MultiplayerWorldComp.cs:102-131`). The roster is only changed inside commands or the creation
long event. A dev-mode check in the setters logs any other write.

**Bot states.** There are two separate state machines.

The saved state lives in `BotRecord.state`. It is part of the digest and changes only through named commands:

| Saved state | Meaning | Set by |
|---|---|---|
| Active | the bot plays | the creation long event (M4) |
| Defeated | 0 colonists and no rescue left. The brain stops; the player stays registered, so the faction tile still shows the bot | bot-sent `BotRosterEdits.SetDefeated` (M8) |
| Removed | the brain stops and the player is unregistered. "Keep colony": the faction and map stay, and the passive rules keep applying to them, so the map is not frozen at the Paused speed it was created with (`MapSetup.cs:57-67`). "Abandon": the settlement is abandoned too | host-sent `Remove` (M9) |

Settings (cheat level, `threatScale`, `speedCap`) change only through the host-sent `SetBotSettings` (M8).

The runtime state lives in host-only `BotRuntime`. It is never saved and never part of the digest:

| Runtime state | Meaning | Leaves for |
|---|---|---|
| Registering | reconciliation found an Active or Defeated record with no runtime and queued `RegisterBot` | Active once the bot is in `session.players` with the record's faction id; Suspended if `RegisterBot` refused the name |
| Active | the brain may step (only while the saved state is Active) | any state below |
| PausedByHost | the host pressed Pause in the Factions window (host-local, sends nothing) | Active on Resume |
| Suspended | the bot's player is gone or wrong: a kick (the runtime sees the PlayerList Remove for its id in `session.players`, `ClientPlayingState.cs:44-51`), a refused name, or a faction id that no longer matches the record | Registering when the host presses Resume; never automatic. Resume queues one server-thread action. If the bot's own player (the runtime's player id) is still registered, as in the faction-mismatch case, it first calls `BotServerApi.RemoveBot`, because `RegisterBot` refuses a name already in `Players` (3.4). Then it calls `RegisterBot` |
| TakenOver | a human is on the bot faction (only the host can arrange this, 3.4) | Active when they leave |

Reconciliation also unregisters the player of a Removed record (`BotServerApi.RemoveBot`).

- **Join points** reload every object on every client, the host included. The brain skips frames while the join-point
  long event runs, and the reload epoch then drops the current think and expectations (3.3). In-flight bot commands
  survive because queues are carried (`SaveLoad.cs:39-56`).
- **Rejoin** (client): the client downloads the join point plus every command since, bot commands included. They are
  ordinary stored `ScheduledCommand`s. The full player list, bots included, comes with it.
- **Host quits:** the hosted server stops and every player, bots included, is dropped. `BotManager.Reset()` clears
  host memory. Whoever hosts the save next gets the bots back through reconciliation. A bot name that now collides
  (the new host's name, or a human who joined first) leaves that runtime Suspended with the reason. There is no host
  migration.
- **Standalone server:** unsupported for driving bots. It has no game process and no host (`hostUsername` is never set
  there), and no client owns the server object. A bot save hosted there still loads. The bot colonies run orderless
  under vanilla AI. The passive rules (letter exemption, governor, growth moments, raid scaling, digest) keep working
  because they are in-tick code keyed on the saved roster. "Add bot" is hidden.
- **Convert to singleplayer:** bots become ordinary player factions with no brain.

---------------------------------------------------------------------------------------------------------------------

## 9. UI, settings, strings

- **Factions window** (`Source/Client/Factions/FactionsWindow.cs`):
  - An "Add bot" header button for the host when allowed, with a tooltip explaining why it is disabled otherwise.
  - Per bot faction: a "[Bot]" tag, the saved state, cheat level, cheats used and colonists. The host also sees the
    runtime state and the current brain phase.
  - Host-only buttons: Pause/Resume brain (host-local, sends nothing), Resume after Suspended, Settings (host-sent
    `SetBotSettings`), Remove (keep colony / abandon; host-sent `Remove`), and View. View is a local camera jump on the
    host only; while the host views the map, its VTR drops to 1 for everyone.
  - Bot usernames can't be dragged (`:79` gets `&& p.type != PlayerType.Bot`; `p` is the client `PlayerInfo`, which
    has `type` but no `IsBot`, `Source/Client/Session/PlayerInfo.cs:21`). Dragging is host-only already
    (`LocalServer != null`), and the server refuses moving a bot anyway (3.4). The `>` switch button (`:45-46`) is
    hidden on bot factions for non-hosts, and the server refuses a non-host switching into a faction a bot holds.
  - Bot factions are left out of the sidebar's Join list (`Source/Client/Factions/FactionSidebar.cs:64`).
- **Chat player list** (`Source/Client/Windows/ChatWindow.cs:132-157`): bots show as `Ada_bot (bot)` in the normal
  colour instead of the dimmed Simulating colour (`entryLabelColor` at `:156`, `GetColor` at `:187-195`), with no ping
  and a tooltip naming the bot's faction.
- **Debug** (category "Multiplayer local", not synced, `Source/Client/Debug/DebugActions.cs:27-29`):
  - "Bots: debug window": per bot the roster, runtime state, last 40 actions with sent/observed/expired, the step ms
    average and maximum, and tripwire hits.
  - "Bots: think now", "Bots: pause/resume brain", "Bots: force cheat <kind>" (still goes through the synced path),
    "Bots: dump digest".
  - "Bots: raid as the bot storyteller" (optional, M7): sends a bot-owned synced method on the bot's connection. Its
    body fires a raid on the bot map under the bot's faction push, so the raid faction is picked hostile to the bot
    (`rw-src/RimWorld/IncidentWorker_RaidEnemy.cs:16`).
  - A dev-mode **mutation tripwire**: before and after each brain step, fingerprint the bot map and the host map
    (thing count, designation and zone counts, `UniqueIDsManager` next ids, `Faction.OfPlayer`, `currentMapIndex`,
    the bot's growing zones' `plantDefToGrow`, and the size of the `SyncFieldUtil` buffer). Any change is logged as
    `[BotAudit] MUTATION in rule X`. A new buffer entry means a Watch-path SyncField write.
- **Logs:** fixed prefixes `[Bot]` (register, faction set, each action, cheat requested/applied, threat start/end,
  exceptions), `[BotAudit]`, `[BotSummary]` and `[BotDigest]`. `[BotSummary]` is one line per bot per in-game day from
  synced values only, so host and client lines must be identical. Server-side bot command lines come from
  `ServerLog.Log` and land in the host's Player.log with the `MpServerLog:` prefix.
- **Settings:** host-local `MpSettings`: bot cap (default 2), brain budget (1.5 ms), verbose `[Bot]` logging. M9
  builds the settings UI. There is no `ServerSettings` field in the MVP; per-bot settings live in the roster.
  Optional v2: a `/bots` chat command registered at host time with `MultiplayerServer.RegisterChatCommand`
  (`Source/Common/MultiplayerServer.cs:275-276`), which hops to the main thread with `OnMainThread.Enqueue`.
- **Strings** (built in M4, with the first UI): `Languages/` is the upstream `rwmt/Multiplayer-Locale` submodule, so
  new keys go in a fork-local `1.7/Languages/English/Keyed/MultiplayerBot.xml`. The game loads it because
  `LoadFolders.xml` lists `1.7`, and a missing key renders as the raw key (`rw-src/Verse/Translator.cs:53-65`).
  `scripts/common.sh` `stage_mod` copies only `1.7/Assemblies` and `1.7/AssembliesCustom` (`:30`) and must also copy
  `1.7/Languages`. Keys: `MpBotAdd`, `MpBotAddDesc`, `MpBotAddDisabledReason`, `MpBotFounded`, `MpBotCreateFailed`,
  `MpBotRemoved`, `MpBotDefeated`, `MpBotCheatFood`, `MpBotCheatMedicine`, `MpBotCheatStabilize`,
  `MpBotCheatReinforce`, `MpBotState{Active,Defeated,Removed}`,
  `MpBotRuntime{Registering,PausedByHost,Suspended,TakenOver}`, `MpBotCheatLevel{Off,Light,Normal}`.

---------------------------------------------------------------------------------------------------------------------

## 10. Edge cases and risks

| Risk | Impact | Mitigation |
|---|---|---|
| Unviewed-map desyncs that aren't bot-specific (Desync-11: a hediff divergence on the map the other player didn't view, CLAUDE.md Progress, tasks 8 and 10) | every bot map is unviewed; in games with other humans, desyncs get blamed on bots | M2 baseline with a map nobody views and slow tracing. Games with only the host and bots can't desync. The digest catches a state divergence within ~90 Timer ticks plus latency |
| A bot with status Playing | `ExtrapolatedTicksBehind = gameTimer`, so `MultiplayerServer.cs:123` pauses the game at once and for good | the bot stays Simulating; M1 adds a `!IsBot` filter at `:123` and a unit test |
| Bot gets dev mode under `DevModeScope.Everyone` | every bot command runs with dev mode on; the bot's debug-only commands pass | `InitPlayerData(id, false)` sent directly (M0); `CanUseDevMode` false for bots (M1) |
| Duplicate, invalid or host-equal bot name | a remote client throws at `ClientPlayingState.cs:215`; serialization throws above 15 chars; a host-named bot is `IsHost` | `RegisterBot` validates on the server thread; the runtime goes Suspended with the reason |
| Failure inside `RegisterBot` | a half-registered player counted in `Players` | `try/catch` with `p.Disconnect(...)` |
| Brain mutates state in the interface (host-only write) | divergence that only clients can detect | read-only rule, `BotGuard`, dev tripwire, digest, expectation audit |
| A scope left open across a `yield` | the host's own clicks go out as the bot | only `BotManager` opens scopes, around `MoveNext` in `try/finally`; depth assertion at the send points |
| Stale game objects after a join point | orders on replaced objects | reload epoch (3.3) |
| Buffered SyncField written by the brain | sent later on the host connection with the host's faction; zone targets null | direct `DoSync` only; tripwire watches the buffer |
| Host UI context leaking into bot commands: a world-view PlayerCount, `MapMouseCell`, `WorldSelected` | the host recorded on the bot map; the host's cell or selection used | render-mode pre-read before the scope; no such handlers in brain code |
| Interface reads with side effects (region rebuild, caches, lazy getters such as `Zone_Growing.PlantDefToGrow`) on the host | host-only state drift | plan only when `!map.regionDirtyer.AnyDirty`; no reachability scans right after own commands; read fields; stat reads are already interface-safe (`Source/Client/Patches/Determinism.cs:442-506`) |
| The creation long event runs outside the command with per-client context | desync at bot creation | steps 5-10 set every piece of context themselves; pawn and map hashes |
| The creation long event throws | with the human path's handler, the host would go to the main menu and stop the server | own handler, `try/finally`, `MpBotCreateFailed`, cleanup |
| Bot storyteller copied from each client's own | threats, `ChildrenAllowed` and research speed differ per client | rebuilt from a deterministic source in step 6 |
| In-command starting-pawn generation not identical on clients (Biotech xenotypes, scenario reads) | desync at bot creation | explicit scenario, full faction push, deterministic storyteller, pawn hash; fallback to the UI `SendPawn` path (+1 day) |
| A hidden `Find.CurrentMap`/selector read on a sync path the bot view doesn't cover | action goes to the wrong map or carries the wrong selection | M3 test on the client's own map (not viewed by the host); expectation audit flags misses |
| Host-sent bot commands run issuer-only effects on the host | sounds/dialogs on the host only; Rand in issuer-only branches would desync (`Seeds.cs:238-266`) | `BotActionScope` clears the issuer flag and dev/god mode; cheats go on the bot connection, where nobody is the issuer |
| Camera jump or repeated message text inside a bot command | every client's camera moves; Archive and message ids differ per client | body rules in 3.5 |
| Letter delivered outside the bot tick still pauses | bot map or everyone paused | the letter-aware exemption (3.6 rule 1) |
| PauseAll / LowestWins reset pauses bot maps | bots stall until someone acts | the governor derives bot speed from the humans' maps; no time commands at all |
| Growth moment for a bot child | bot map paused until someone chooses | 3.6 rule 3 |
| Raid scaling misses incidentQueue items and direct `TryExecute` quest parts | occasional full-strength raid on a bot | accepted for MVP; optional `Storyteller.TryFire` prefix keyed on a bot target map |
| Drop-pod contents forbidden for every faction (`Forbiddables.cs:106`) | starting items and any pod delivery unusable by the bot | S2b and P4 unforbid; cheats place items inside the command |
| Bot storytellers add quests to the shared quest list (`hideOtherPlayersQuests` defaults to false, `MpSettings.cs:39`) | humans see quests offered to bots and can accept them | open question: filter bot-faction quests from other players' Quests tab when the roster isn't empty, or document the toggle |
| Bot removal leaks (FactionWorldData/FactionMapData, its storyteller still ticking via `FactionRepeater`, `mapComps`/`asyncTimeComps` entries after Abandon) | small permanent tick cost and memory per removed bot | MVP keeps the hidden faction; v2: skip abandoned roster factions in `FactionRepeater.Template` (deterministic) and remove map comps on map deinit (a general mod bug fix) |
| CPU: each bot map simulated on every machine | the slowest machine sets the pace for everyone | cap 2; per-map tick-cost probe in M2; `speedCap` setting; adaptive cap in v2 |
| Protocol change | mixed builds can't join | bump 56 → 57 in M1 |
| Human views a bot map | `ForceShowDialogs` shows bot dialogs to them; their speed buttons are ignored by the governor | the brain resolves dialogs quickly (P5); the `ver` check makes a human click harmless; v2 "bot-controlled" label |
| Human joins a bot faction | two drivers | only the host can arrange it (server rule, 3.4); the runtime goes TakenOver and resumes when they leave |
| `/kick Ada_bot` | bot player gone | the runtime sees the PlayerList Remove and goes Suspended (no auto re-register); Resume in the Factions window |
| Host's Shift key held during a bot order | bot orders would queue | the QueueOrder bit is forced false in bot scope |
| Thing serialization of carried/contained pawns | null target → command silently ignored | the brain orders only spawned pawns on the bot map |
| Ordered job whose reservation fails | a warning on every client, nothing happens | `CanReserveAndReach` before sending |
| Bots appear in the player list, `/players`, API `GetPlayers` | other mods may treat bots as humans | a `(bot)` tag and the "bot" role; `PlayerInfo.type == Bot` for code that reads `Multiplayer.Client.PlayerInfo`. MultiplayerAPI 0.6.0's `IPlayerInfo` has no type (accepted) |
| Joiner/quest letters for bots are stripped from every stack (`MultifactionPatches.cs:636-645`) | bots can't grow except by cheat | v2 in-tick inbox with its own letter hook (6.6) |
| Non-async time | one global speed; a bot letter could pause everyone | the letter exemption and the growth-moment rule apply in both modes; the governor does nothing; the dialog warns; recommend async time |

---------------------------------------------------------------------------------------------------------------------

## 11. Milestones

Sizes are working days of implementation. "Round" = an in-game test you run (section 12).

Every milestone's exit also includes two things. First, CLAUDE.md Progress and Known gaps are updated. Second, every
new Harmony patch has a `PatchHitCounter` name (`Source/Client/Patches/PatchHitCounter.cs`) and was seen firing in
game in the "Patch hit counts" dump, because the .NET JIT can inline small targets so a patch silently never runs.

| M | Goal | Steps and files | Size | Exit criteria |
|---|---|---|---|---|
| **M0** | Thin end-to-end spike you can see: a second in-process player whose orders execute on both instances | Common: `ServerPlayer.cs` (`Bot` after `Arbiter` at `:143-148`, `IsBot` near `:44`); `Networking/BotConnections.cs` (new, 3.1); `BotServerApi.cs` (new; `RegisterBot` as in 3.4, including the faction set; no `UpdateStatus(Playing)`); one `ServerLog.Log` line in `CommandHandler.Send` when `sourcePlayer is { IsBot: true }`. Nothing in `MultiplayerServer.cs`: the bot stays Simulating. Client: `Multiplayer.CommandConnection`; `Bots/BotCommandScope.cs`; `SyncHandler.cs:23` and `Designators.cs:95` send on `CommandConnection`; `Bots/BotSpikeDebug.cs` with three `[DebugAction(MpDebugActions.MultiplayerLocalCategory)]` actions whose static spike state is cleared when `LocalServer` changes: "Bots spike: register Bot_T on my faction" and "Bots spike: Bot_T drafts the selected pawn" (Action type), and "Bots spike: Bot_T places a wall blueprint" (ToolMap). Each sets `BotCommandScope` around its one call in `try/finally`; M3 converts all three to `BotManager`'s stepping helper, and from then on only `BotManager` opens scopes. Not ToolMapForPawns: `DebugActionNodeEnter.Postfix` wraps it as a synced debug command whatever the category (`DebugSync.cs:282`). Not in M0: Protocol 57, the "bot" role, any GhostMode change, pacing filters | 1-1.5 | Round R0: "Bot_T has joined." in chat on both; Bot_T in both player lists (dimmed until M9); the pawn drafts and the blueprint appears on both screens; the game never pauses by itself; 10 min with no desync; the host log has one `MpServerLog:` line per Bot_T command with Bot_T's id and the host faction's id |
| **M1** | Server hardening + unit tests | Protocol 57; `CanUseDevMode` false for bots; `IsHost \|\| IsBot` host-only policy; `PacingPlayers` filter at `:123`; maxPlayers/standalone-primary exclusions; no sync-info relay to bots; `HandleSetFaction` rules; "bot" role and "-" ticks behind in `/players` and `/whois`; null-safe client `HandleSetFaction`; extract `ShouldAdvance()` / `ComputeTimePerTick()` from `MultiplayerServer.Run`/`TickNet`. `Source/Tests/BotPlayerTest.cs`: set `MultiplayerServer.instance` (`ServerPlayer.Server` needs it, `ServerPlayer.cs:46`); call `StartInitData().SetResult(new ServerInitData(...))` with the bot ids in `HostOnlySyncCmds` before any Sync policy test (`InitData` starts null, `MultiplayerServer.cs:92`); drive the server half with `server.queue.RunQueue`. Cases: register (name rules, duplicate refused, host name refused, failure cleanup), a stale bot forced to `status = PlayerStatus.Playing` (`server.gameTimer = 10000` with the default `ticksBehindReceivedAt = 0`, or `ticksBehind = 10000`; `ExtrapolatedTicksBehind` has no setter) next to a host with `ticksBehindReceivedAt = gameTimer` (really 0 behind) → `ShouldAdvance()` true. With the default status Simulating the bot is not in `PlayingIngamePlayers` (`MultiplayerServer.cs:45`), so that case would pass without the filter; a second case keeps the default status and checks that registration alone never pauses the server. maxPlayers, stamping through `BotClientConnection`, dev mode in both scopes, policy, set-faction rules, the drop-everything server half, disconnect cleanup, `/kick`. `PacketTest`: a `PlayerType.Bot` sample. Hand-check the regenerated `Source/Tests/packet-serializations/ServerPlayerListPacket.verified.txt` (the Bot byte is 03), because `AutoVerify` accepts every snapshot change locally (`PacketTest.cs:297-298`) | 2-2.5 | `dotnet test Source/Tests -c Release` green; the snapshot diff checked by hand |
| **M2** | Unviewed-map baseline + cost probe. Decoupled: gates only games with other humans | dev-only per-map tick-time probe ("Multiplayer local > Log tick cost per map", a `Stopwatch` around `TickTickable`, display only); no bot code | 0.5 + round R1 | R1: 60 min, slow tracing, three colony maps (the host's, the client's, and one nobody views), no desync. If it desyncs, the fix is task 8/10 work: unbounded, and likely to need more than one attempt. M3-M9 go ahead meanwhile, because a game with only the host and bots is a single simulation and cannot desync |
| **M3** | Bot view + full routing | `BotInterfaceContext` (3.3); `BotManager`'s stepping helper, which opens both scopes around a `MoveNext` in `try/finally` (every spike action, the three M0 ones included, is converted to step through it); scope-depth assertion at the send points; render-mode pre-read; QueueOrder bit; `ThrowMetaPuffs` skip; region-dirty gate; dev tripwire with the SyncField buffer check. Spike actions extended: "Bots spike: move Bot_T to the client's faction" (`SetBotFaction`; Bot_T is never registered twice) and "Bots spike: Bot_T acts on its faction map" (stockpile + wall blueprint + bill + priority on a map the host isn't viewing) | 1.5-2.5 | Round R2: the client sees the stockpile, walls and bill on its own map; the host's camera/selection unchanged; no `[BotAudit] MUTATION`; no desync |
| **M4** | Bot faction creation + roster + reconciliation + strings | `BotFactory.cs`: the command (steps 1-4) and the long event (steps 5-10) in `try/finally` with its own exception handler; the PreContext mirror on every map; `currentMapIndex` and dev/god mode set and restored; the `pushed` flag; the storyteller rebuilt; the styles list; the request-list snapshot; the settlement named inside `GenerateNewMap`; temporary factions filtered; default drug policies; pawn and map hashes with the `<< 32` shift. `FactionCreator` helpers `internal` + explicit ideo/def overloads. `BotRecord`/`BotRoster` (values only). `BotGuard`/`BotActionScope`. `Dialog_AddBot` with validation. Factions window button and tiles, and the View button (host-local camera jump). Sidebar Join filter. `BotManager` reconciliation with the runtime states Registering, Active and Suspended, and kick detection. `BotManager.Reset` in `StopMultiplayer`. `1.7/Languages/English/Keyed/MultiplayerBot.xml` and the `scripts/common.sh:30` staging of `1.7/Languages` | 5-7 | Round R3: Add bot → a new faction tile with Ada_bot under it and a named settlement on both instances; the host's faction/camera unchanged by the creation; after the host views and unpauses the bot map, 3 colonists on both; no raw string keys on screen; no desync for 15 min |
| **M5** | Passive rules + digest | letter-aware `PauseOnLetter` transpiler change (check 3 only when `MapContext == null`); the governor with `RawTimeSpeed` accessors; the growth-moment rule; raid scaling with `GlobalPointsMin()`/`GlobalPointsMax` (optional `TryFire` prefix); `BotDigest` in `AsyncTimeComp.Tick` with `[BotDigest]` lines; `[BotSummary]` lines | 2.5-3.5 | Round R4: a Pirates raid on the bot map pauses nobody; after a client (re)joins (PauseAll) the bot map resumes when the host unpauses; `[BotSummary]` lines identical on both logs |
| **M6** | Brain framework + setup rules | `BotManager` stepping, budget, expectations and the reload epoch; `BotActions` with `CanDesignateCell` filtering, the zone-extension rules, reservation checks and the `plantDefToGrow` field read; `Brain/SetupRules.cs` (S0-S12 with S2b) and P4 from the first think; `Source/Common/Bots/GridPlanner.cs` + `Source/Tests/GridPlannerTest.cs` (golden layouts, deterministic order); persisted anchor/stage/plan via bot-sent `BotRosterEdits` | 6-8 | Round R5: 5 in-game days at speed 3: starting items unforbidden on day 1; stockpile, field, chopped trees, walled room with door and roof, beds, campfire with meal bill, research bench with a project, weapons equipped; few `[BotAudit] expired` lines; max step ms ≤ budget |
| **M7** | Periodic + threat rules | `Brain/PeriodicRules.cs`, `Brain/ThreatRules.cs` (T2 with the validator), dialog answering; optional "Bots: raid as the bot storyteller" | 2.5-3.5 | Round R6: a Pirates raid (or the bot-storyteller raid) → draft, rally inside, fight, undraft after; no desync |
| **M8** | Cheats + bot settings | `BotCheats.cs` (host-only, bot connection, items placed in the command), `Source/Common/Bots/BotCheatPolicy.cs` + tests, triggers, messages with name and count, bot chat lines, counters, `BotRosterEdits.SetDefeated`, host-sent `SetBotSettings` (cheat level, `threatScale`, `speedCap`) and its dialog | 2-2.5 | Round R7: destroy the bot's food with dev tools → one rations delivery, the message on both screens, one chat line, the counter goes up; a second request within the cooldown does nothing on both |
| **M9** | Lifecycle, UI polish, soak | host-sent `Remove` (keep colony / abandon), PausedByHost and Resume, TakenOver, debug window, chat tag and colour, `MpSettings` settings UI (cap, budget, verbose logging) | 3-4 | Rounds R8 (save/quit/re-host/rejoin/kick/remove) and R9 (2 bots, 60 min, slow tracing): no desync, no errors, frame time noted |

**MVP total:** the sizes add up to 26-35.5 days. With about two days for fixes between rounds, the MVP is about 28-37
working days. It also needs about 15-20 test rounds of your time: R0-R9 assume every round passes the first time,
and R1 and R9 take 60 minutes each with slow tracing. The total excludes any M2 desync fix. That work is unbounded and
may fail the first time: CLAUDE.md task 8 still records 4 own-map desyncs in 65 minutes, and Desync-11 appeared after
the VTR fix.

**v2** (6.6 + removal hygiene + queued-incident scaling + adaptive speed cap): about +8-12 days.

**Optional:** an offline replay determinism check (run a recorded bot session twice through the existing `-replay=`
batch runner, `Source/Client/MultiplayerStatic.cs:158-222`, and diff a per-map digest dump). It depends on headless
1.7 being able to load a map *(unverified)*.

The riskiest assumptions are front-loaded: identity and routing in M0, in-command pawn generation in M4. The
unviewed-map baseline (M2) runs alongside and gates only games with other humans.

---------------------------------------------------------------------------------------------------------------------

## 12. In-game test protocol

**One-time setup per round**

1. Wait until I say the build is installed (I run `./scripts/install_local.sh`), or run it yourself.
2. Run `./scripts/run_two.sh`. In the host instance, start a new small Crashlanded colony, then Esc → Multiplayer →
   Host. In the host window:
   - Gameplay tab: **Multifaction ON** and **Async time ON**.
   - **Dev mode ON**, with its scope button on **Host only**. A release build leaves Dev mode off
     (`Source/Client/Windows/HostWindow.cs:71-75`), and the server then drops every synced debug action
     (`CommandHandler.cs:23-27`). "Host only" keeps synced debug actions to the host. Bots never get dev mode in
     either scope (`InitPlayerData(id, false)` from M0, `CanUseDevMode` from M1).
   - Leave the arbiter off, and host.
3. Press Enter in the terminal; the client auto-joins `127.0.0.1:30502`.
4. Turn Dev mode on in both instances (Options). On the client only "Multiplayer local" actions take effect; the
   server drops its synced debug actions under "Host only".
5. For rounds R1 and R9 only: in both instances, Mod settings → Multiplayer → "Desync tracing: slow (.NET)".

**Raids on a bot map.** A dev raid the host sends is stamped with the host's faction, so its raider faction is picked
hostile to the host (`rw-src/RimWorld/IncidentWorker_RaidEnemy.cs:16`), not necessarily to the bot. Pick Pirates,
which are hostile to everyone, or use "Bots: raid as the bot storyteller" once M7 adds it.

Logs (I read these myself; you only tell me the round is done and what you saw on screen):

- Host: `~/.rw-mp-test/host/config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Logs/Player.log`. Server lines
  in it start with `MpServerLog:`.
- Client: `~/.rw-mp-test/client/config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Logs/Player.log`.
- Desync reports: `~/.rw-mp-test/client/MpDesyncs/`. Replays: `~/.rw-mp-test/host/MpReplays/`.

What I grep for: `[Bot]`, `[BotAudit]`, `[BotSummary]`, `[BotDigest]`, `MpServerLog: Simulation paused`, `Exception`,
`Error`, `Wrong random state`, `Trace hashes`, and `Patch hit counts`. Before quitting each round, run Debug actions →
Multiplayer → "Patch hit counts" on the host. It is a synced debug action, so it runs and logs on both instances
(code-derived, `DebugSync.cs:270-300`).

**Rounds** (budget about 15-20 in total, counting repeats)

- **R0 (M0):** Host: Debug actions → Multiplayer local → "Bots spike: register Bot_T on my faction". Open the chat
  window on both instances. Select a colonist on the host and run "Bots spike: Bot_T drafts the selected pawn". Then
  run "Bots spike: Bot_T places a wall blueprint" and click an empty cell. Play 10 minutes at speed 2.
  Report: Bot_T in both player lists (yes/no), drafted and blueprint visible on the client (yes/no), the game ever
  paused by itself (yes/no), any desync window.
- **R1 (M2):** The client creates its own faction (sidebar) and stays on its map; the host stays on its map. A third
  colony map must exist that neither player views. For example, the client creates two factions from the sidebar,
  one after the other, and then switches back to the first with the Factions window's `>` button; the second
  faction's map is then left with nobody viewing it. Reaching the sidebar a second time may need a switch to the
  spectator faction first *(unverified)*. Run 60 minutes at speed 3, then run "Log
  tick cost per map" on both.
  Report: any desync window and the time it appeared.
- **R2 (M3):** As R1, without the third map. On the host run "Bots spike: register Bot_T on my faction" (once per
  session), then "Bots spike: move Bot_T to the client's faction" and "Bots spike: Bot_T acts on its faction map".
  The host must not open the client's map.
  Report: does the client see a stockpile, walls and a bill appear; did the host's camera or selection jump.
- **R3 (M4):** The client stays spectator. Host: Chat → Factions → Add bot → defaults → Create. Check that the host
  is still on its own faction and camera. Then the host opens the bot map with Factions → View and unpauses it with
  its own time controls. With the default time control (EveryoneControls) they send a `MapTimeSpeed` command for that
  map (`TimeControlUI.cs:480-482`). Until M5's governor nothing else unpauses a bot map: in async time a new map
  starts Paused (`MapSetup.cs:57-67`), and Crashlanded's colonists and items arrive in drop pods that open 110 ticks
  after landing (`rw-src/RimWorld/ScenPart_PlayerPawnsArriveMethod.cs:105`). View is host-only, so the client checks
  that the settlement appears on its world map.
  Report: time from click to the settlement on the host's world map, the faction tile with Ada_bot on both, the
  settlement on the client's world map, the host still on its own faction/camera after Create, colonists out of their
  pods after the unpause, any raw string key on screen, any desync in 15 minutes.
- **R4 (M5):** Host: Factions → View the bot map, then a dev raid there with the faction set to Pirates. Separately,
  restart the client so it rejoins (pause on join).
  Report: did any instance pause from the raid; after the rejoin, did the bot map resume when the host unpaused.
- **R5 (M6):** Play 5 in-game days at speed 3 on the host's own map; once per in-game day open the bot map with View
  and take a screenshot.
  Report: the screenshots, and whether colonists ever stood idle for a long time.
- **R6 (M7):** A Pirates raid on the bot map (or "Bots: raid as the bot storyteller"); watch it with View.
  Report: drafted, gathered, fought, undrafted after (yes/no for each), desync.
- **R7 (M8):** With cheat level Light, destroy the bot's meals and raw food with dev tools and wait an in-game hour.
  Repeat the destroy once more right after.
  Report: one rations delivery and one message on both, one chat line, nothing the second time.
- **R8 (M9):** Save, quit both, host the save again, client joins. Then the client rejoins (dev rejoin, or the host
  types `/resync <client name>`). Then the host types `/kick Ada_bot`, checks the bot shows Suspended, and presses
  Resume. Then Factions → Remove bot (abandon).
  Report: the bot resumed the same base, no second base, the kick and Resume worked, removal clean on both.
- **R9 (M9):** Two bots, slow tracing on both, 60 minutes at speed 3.
  Report: desyncs (time), the "Avg frame time" from the chat player tooltip with 0, 1 and 2 bots.

---------------------------------------------------------------------------------------------------------------------

## 13. Open questions

1. Is the unviewed-map desync (Desync-11) still there after the VTR getter fix? M2 answers this. It gates only games
   with other humans.
2. Does in-command starting-pawn generation produce identical pawns on every client (Biotech xenotypes, possessions)?
   M4's pawn hash answers it. Does `PreferredNamesPatch` (`Source/Client/EarlyPatches/SettingsPatches.cs:59-69`) fire on
   .NET, given CLAUDE.md's warning about inlined `Prefs` getters? The fallback is the UI `SendPawn` path.
3. Should the four per-client reads in the human `FactionCreator` path (ideo fallback, `GenerateIdeo`, the copied
   storyteller, the starting research target) be fixed now as a separate commit? They are code-derived and
   *(unverified)* in game.
4. What does one bot map cost per tick on this PC and on a friend's? This decides the default cap of 2 or 3.
5. Is constructing `new MainTabWindow_Research()` in the interface free of side effects? It is used as the
   `DoBeginResearch` target. *(unverified)*. Fallback: a bot-owned `SetResearch`.
6. Should bot quests be hidden from humans? Options: filter bot-faction quests out of other players' Quests tab when
   the roster isn't empty, or leave it to the `hideOtherPlayersQuests` toggle (`MpSettings.cs:39`).
7. Growth moments for bot children: choose in the tick through `MakeChoices` *(unverified)*, or drop the letter?
8. What exactly does BotFactory's failure handler remove (faction, world object, map), so that every client that
   threw ends in the same state? An M4 design item.
9. Is standalone-server support needed at all? This plan says no: bots stay orderless there.
10. Should bots be allowed in non-async-time games (warn) or blocked?
11. Should the bot cap count against `maxPlayers` for humans? This plan says no.
12. The preferred research defNames for S12 *(unverified)*.
13. Persona naming: a fixed list ("Ada_bot", "Bo_bot", …) or generated names?
