using System.Collections.Generic;
using System.Linq;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Common
{
    public class ServerPlayingState(ConnectionBase conn) : MpConnectionState(conn)
    {
        [PacketHandler(Packets.Client_WorldReady)]
        public void HandleWorldReady(ByteReader data)
        {
            Player.UpdateStatus(PlayerStatus.Playing);
        }

        [PacketHandler(Packets.Client_RequestRejoin)]
        public void HandleRejoin(ByteReader data)
        {
            connection.ChangeState(ConnectionStateEnum.ServerLoading);
            Player.ResetTimeVotes();
        }

        [TypedPacketHandler]
        public void HandleDesynced(ClientDesyncedPacket packet) =>
            Server.playerManager.OnDesync(Player, packet.tick, packet.diffAt);

        [TypedPacketHandler]
        public void HandleTraces(ClientTracesPacket packet)
        {
            if (!Player.IsHost) return;
            Server.GetPlayer(packet.playerId)?.SendPacket(ServerTracesPacket.Transfer(packet.rawTraces, packet.rawJittedMethods));
        }

        [TypedPacketHandler]
        public void HandleClientCommand(ClientCommandPacket packet)
        {
            int? mapToResync = null;

            if (packet.type == CommandType.PlayerCount)
            {
                // The server's record of the player's map is the previous map that gets counted, not the one the
                // client claims. The counts are saved in the game (Oct 27), so every client already counts this
                // player where the server says it is: after a rejoin (same ServerPlayer, same currentMapId) or a
                // report the client repeats, trusting the client's previous map would count the player twice.
                // Every change of currentMapId sends exactly one PlayerCount, so the counts equal the number of
                // players on each map at that command's place in the order.
                ByteReader reader = new ByteReader(packet.data);
                var claimedPrevMapId = reader.ReadInt32();
                var newMapId = reader.ReadInt32();

                // The reset value is the server's alone (StandalonePersistence.LoadInto): kept as the player's map, it
                // would go out as the previous map of its next report and zero every count on every client
                if (newMapId == ScheduledCommand.PlayerCountResetAll)
                {
                    ServerLog.Error($"Player {Player.Username} reported the player count reset value as its map, ignored");
                    return;
                }

                var prevMapId = Player.currentMapId;
                if (claimedPrevMapId != prevMapId)
                    ServerLog.Detail($"Player {Player.Username} reported map transition {claimedPrevMapId} -> {newMapId}, " +
                                     $"server's last known map is {prevMapId}");
                Player.hasReportedCurrentMap = true;

                // A repeat report (a rejoin onto the map the player was on): nothing changes
                if (prevMapId == newMapId)
                    return;

                Player.currentMapId = newMapId;
                packet.data = ByteWriter.GetBytes(prevMapId, newMapId);

                if (Server.CanUseStandaloneMapStreaming(newMapId))
                    mapToResync = newMapId;
            }

            // todo check if map id is valid for the player

            Server.commands.Send(packet.type, Player.FactionId, packet.mapId, packet.data, Player);

            if (mapToResync is int currentMapId)
                Server.SendMapResponse(Player, currentMapId);
        }

        public const int MaxChatMsgLength = 128;

        [TypedPacketHandler]
        public void HandleChat(ClientChatPacket packet)
        {
            Player.helpOnlyUsableCommands = packet.helpOnlyUsableCommands;

            string msg = packet.msg;
            msg = msg.Trim();

            if (msg.Length == 0) return;

            if (msg.Length > MaxChatMsgLength)
                msg = msg[..MaxChatMsgLength];

            if (msg[0] == '/')
            {
                var cmd = msg[1..];
                Server.HandleChatCommand(Player, cmd);
            }
            else
            {
                Server.SendChat($"{connection.username}: {msg}");
            }
        }

        [PacketHandler(Packets.Client_WorldDataUpload, allowFragmented: true)]
        public void HandleWorldDataUpload(ByteReader data)
        {
            // On standalone, accept from any playing client; otherwise only host/arbiter
            if (!Server.IsStandaloneServer && (Server.ArbiterPlaying ? !Player.IsArbiter : !Player.IsHost))
                return;

            ServerLog.Detail($"Got world upload {data.Left}");

            Server.worldData.mapData = new Dictionary<int, byte[]>();

            int maps = data.ReadInt32();
            for (int i = 0; i < maps; i++)
            {
                int mapId = data.ReadInt32();
                Server.worldData.mapData[mapId] = data.ReadPrefixedBytes();
            }

            Server.worldData.savedGame = data.ReadPrefixedBytes();
            Server.worldData.sessionData = data.ReadPrefixedBytes();

            if (Server.worldData.CreatingJoinPoint)
                Server.worldData.EndJoinPointCreation();
        }

        [TypedPacketHandler]
        public void HandleStandaloneWorldSnapshot(ClientStandaloneWorldSnapshotPacket packet)
        {
            if (!Server.IsStandaloneServer)
                return;

            if (!Player.IsPlaying)
                return;

            var accepted = Server.worldData.TryAcceptStandaloneWorldSnapshot(Player, packet.tick,
                packet.worldData, packet.sessionData, packet.sha256Hash);

            if (accepted)
            {
                ServerLog.Detail(
                    $"Accepted standalone world snapshot tick={packet.tick} from {Player.Username}");
            }
            else
            {
                ServerLog.Detail(
                    $"Rejected standalone world snapshot tick={packet.tick} from {Player.Username}");
            }
        }

        [TypedPacketHandler]
        public void HandleStandaloneMapSnapshot(ClientStandaloneMapSnapshotPacket packet)
        {
            if (!Server.IsStandaloneServer)
                return;

            if (!Player.IsPlaying)
                return;

            var accepted = Server.worldData.TryAcceptStandaloneMapSnapshot(Player, packet.mapId, packet.tick,
                packet.mapData, packet.sha256Hash);

            if (accepted)
            {
                ServerLog.Detail(
                    $"Accepted standalone map snapshot map={packet.mapId} tick={packet.tick} from {Player.Username}");
            }
            else
            {
                ServerLog.Detail(
                    $"Rejected standalone map snapshot map={packet.mapId} tick={packet.tick} from {Player.Username}");
            }
        }

        [TypedPacketHandler]
        public void HandleCursor(ClientCursorPacket clientPacket)
        {
            if (Player.lastCursorTick == Server.NetTimer) return; // policy
            Player.lastCursorTick = Server.NetTimer;

            var serverPacket = new ServerCursorPacket(Player.id, clientPacket);
            Server.SendToIngame(serverPacket, reliable: false, excluding: Player);
        }

        [TypedPacketHandler]
        public void HandleSelected(ClientSelectedPacket packet) =>
            Server.SendToPlaying(new ServerSelectedPacket(Player.id, packet), excluding: Player);

        [TypedPacketHandler]
        public void HandlePing(ClientPingLocPacket packet) =>
            Server.SendToPlaying(new ServerPingLocPacket(Player.id, packet));

        [TypedPacketHandler]
        public void HandleClientKeepAlive(ClientKeepAlivePacket packet)
        {
            Player.ticksBehind = packet.ticksBehind;
            Player.ticksBehindReceivedAt = Server.gameTimer;
            Player.simulating = packet.simulating;
            Player.keepAliveAt = Server.NetTimer;

            if (Player.IsHost)
                Server.workTicks = packet.workTicks;

            var idMatched = Player.keepAliveId == packet.id;
            connection.OnKeepAliveArrived(idMatched);
            if (idMatched) Player.keepAliveId++;
        }

        [TypedPacketHandler]
        public void HandleDesyncCheck(ClientSyncInfoPacket packet)
        {
            var arbiter = Server.ArbiterPlaying;
            if (arbiter ? !Player.IsArbiter : !Player.IsHost) return; // policy

            // Keep at most 10 sync infos
            Server.worldData.syncInfos.Add(packet.rawSyncOpinion);
            if (Server.worldData.syncInfos.Count > 10)
                Server.worldData.syncInfos.RemoveAt(0);

            foreach (var p in Server.PlayingPlayers.Where(p => !p.IsArbiter && (arbiter || !p.IsHost)))
                p.conn.SendFragmented(new ServerSyncInfoPacket { rawSyncOpinion = packet.rawSyncOpinion }.Serialize());
        }

        [TypedPacketHandler]
        public void HandleFreeze(ClientFreezePacket packet)
        {
            Player.frozen = packet.freeze;

            if (!packet.freeze)
                Player.unfrozenAt = Server.NetTimer;
        }

        [TypedPacketHandler]
        public void HandleAutosaving(ClientAutosavingPacket packet)
        {
            var forceJoinPoint = packet.reason == JoinPointRequestReason.Save;

            ServerLog.Detail(
                $"Received Client_Autosaving from {Player.Username}, standalone={Server.IsStandaloneServer}, " +
                $"isHost={Player.IsHost}, reason={packet.reason}, force={forceJoinPoint}");

            // On standalone, any playing client can trigger a join point (always, regardless of settings)
            // On hosted, only the host can trigger and only if the Autosave flag is set
            if (Server.IsStandaloneServer ||
                (Player.IsHost && Server.settings.autoJoinPoint.HasFlag(AutoJoinPointFlags.Autosave)))
                Server.worldData.TryStartJoinPointCreation(forceJoinPoint, sourcePlayer: Player);
        }

        [TypedPacketHandler]
        public void HandleDebug(ClientDebugPacket _)
        {
            if (!Server.commands.CanUseDevMode(Player))
                return;

            Server.worldData.mapCmds.Clear();
            Server.gameTimer = Server.startingTimer;

            Server.SendToPlaying(new ServerDebugPacket());
        }

        [TypedPacketHandler]
        public void HandleSetFaction(ClientSetFactionPacket packet)
        {
            // todo restrict handling

            int playerId = packet.playerId;
            int factionId = packet.factionId;

            var player = Server.GetPlayer(playerId);
            if (player == null) return;
            if (player.FactionId == factionId) return;

            player.FactionId = factionId;
            Server.SendToPlaying(new ServerSetFactionPacket(playerId, factionId));
        }

        [TypedPacketHandler]
        public void HandleFrameTime(ClientFrameTimePacket packet) => Player.frameTime = packet.frameTime;
    }
}
