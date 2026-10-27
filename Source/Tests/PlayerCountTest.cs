using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

// The server counts a player where its own record says the player is (ServerPlayer.currentMapId), not where the
// client claims it came from: the counts are saved in the game since Oct 27, so a rejoiner or a repeated report must
// not be counted twice. Every forwarded PlayerCount lands in worldData.mapCmds[Global].
[TestFixture]
public class PlayerCountTest
{
    private MultiplayerServer server = null!;
    private int nextPlayerId;

    [SetUp]
    public void SetUp()
    {
        server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings
        {
            gameName = "Test",
            direct = false,
            lan = false
        });
        nextPlayerId = 1;
    }

    [TearDown]
    public void TearDown()
    {
        MultiplayerServer.instance = null;
    }

    private (ServerPlayer player, RecordingConnection conn) AddJoinedPlayer(string username, int currentMapId, bool hasReportedCurrentMap)
    {
        var conn = new RecordingConnection(username);
        var player = new ServerPlayer(nextPlayerId++, conn)
        {
            currentMapId = currentMapId,
            hasReportedCurrentMap = hasReportedCurrentMap,
            hasJoined = true,
            status = PlayerStatus.Playing,
        };
        conn.serverPlayer = player;
        conn.ChangeState(ConnectionStateEnum.ServerPlaying);
        server.playerManager.Players.Add(player);
        return (player, conn);
    }

    private static void Report(ServerPlayer player, int claimedPrevMapId, int newMapId)
    {
        player.conn.GetState<ServerPlayingState>()!.HandleClientCommand(new ClientCommandPacket(
            CommandType.PlayerCount,
            ScheduledCommand.Global,
            ByteWriter.GetBytes(claimedPrevMapId, newMapId)
        ));
    }

    // Rejoins and disconnects also reset the player's time votes with a TimeSpeedVote command; only PlayerCount matters here
    private List<ScheduledCommand> GlobalCommands() =>
        server.worldData.mapCmds.TryGetValue(ScheduledCommand.Global, out var cmds)
            ? cmds.Select(raw => ScheduledCommand.Deserialize(new ByteReader(raw)))
                .Where(cmd => cmd.type == CommandType.PlayerCount).ToList()
            : [];

    private static (int prev, int next) MapIds(ScheduledCommand cmd)
    {
        var reader = new ByteReader(cmd.data);
        return (reader.ReadInt32(), reader.ReadInt32());
    }

    [Test]
    public void FirstReportFromNoMap_IsForwarded()
    {
        var (player, _) = AddJoinedPlayer("player", -1, hasReportedCurrentMap: false);

        Report(player, -1, 5);

        var cmds = GlobalCommands();
        Assert.That(cmds, Has.Count.EqualTo(1));
        Assert.That(cmds[0].type, Is.EqualTo(CommandType.PlayerCount));
        Assert.That(cmds[0].playerId, Is.EqualTo(player.id));
        Assert.That(MapIds(cmds[0]), Is.EqualTo((-1, 5)));
        Assert.That(player.currentMapId, Is.EqualTo(5));
        Assert.That(player.hasReportedCurrentMap, Is.True);
    }

    [Test]
    public void RepeatReport_IsDropped()
    {
        var (player, _) = AddJoinedPlayer("player", -1, hasReportedCurrentMap: false);
        Report(player, -1, 5);

        // Once with the right previous map, once with a stale one (a client that lost track): neither counts again
        Report(player, 5, 5);
        Report(player, -1, 5);

        Assert.That(GlobalCommands(), Has.Count.EqualTo(1));
        Assert.That(player.currentMapId, Is.EqualTo(5));
    }

    [Test]
    public void Move_IsForwardedWithServersPreviousMap()
    {
        var (player, _) = AddJoinedPlayer("player", 5, hasReportedCurrentMap: true);

        // The client claims it came from map 3; the server knows it was on 5, which is where it's counted
        Report(player, 3, 7);

        var cmds = GlobalCommands();
        Assert.That(cmds, Has.Count.EqualTo(1));
        Assert.That(MapIds(cmds[0]), Is.EqualTo((5, 7)));
        Assert.That(player.currentMapId, Is.EqualTo(7));
    }

    [Test]
    public void WorldMapTrip_IsForwardedBothWays()
    {
        var (player, _) = AddJoinedPlayer("player", 5, hasReportedCurrentMap: true);

        Report(player, 5, -2);
        Report(player, -2, 5);

        var cmds = GlobalCommands();
        Assert.That(cmds.Select(MapIds), Is.EqualTo(new[] { (5, -2), (-2, 5) }));
        Assert.That(player.currentMapId, Is.EqualTo(5));
    }

    [Test]
    public void RejoinOntoSameMap_IsDropped()
    {
        server.worldData.savedGame = [];
        server.worldData.sessionData = [];
        var (player, conn) = AddJoinedPlayer("player", 5, hasReportedCurrentMap: true);

        // Rejoin keeps the ServerPlayer: through ServerLoading (world data sent) back to ServerPlaying
        conn.GetState<ServerPlayingState>()!.HandleRejoin(new ByteReader([]));
        Assert.That(conn.SentPackets, Does.Contain(Packets.Server_WorldData));
        Assert.That(conn.State, Is.EqualTo(ConnectionStateEnum.ServerPlaying));
        Assert.That(player.currentMapId, Is.EqualTo(5));

        // The game the rejoiner loaded already counts it on map 5
        Report(player, -1, 5);

        Assert.That(GlobalCommands(), Is.Empty);
        Assert.That(player.currentMapId, Is.EqualTo(5));
    }

    [Test]
    public void RejoinOntoOtherMap_IsANormalMove()
    {
        server.worldData.savedGame = [];
        server.worldData.sessionData = [];
        var (player, conn) = AddJoinedPlayer("player", 5, hasReportedCurrentMap: true);

        conn.GetState<ServerPlayingState>()!.HandleRejoin(new ByteReader([]));
        Report(player, -1, 7);

        var cmds = GlobalCommands();
        Assert.That(cmds, Has.Count.EqualTo(1));
        Assert.That(MapIds(cmds[0]), Is.EqualTo((5, 7)));
        Assert.That(player.currentMapId, Is.EqualTo(7));
    }

    [Test]
    public void Disconnect_SendsMapToNoMap()
    {
        var (player, conn) = AddJoinedPlayer("player", -1, hasReportedCurrentMap: false);
        Report(player, -1, 5);

        server.playerManager.SetDisconnected(conn, MpDisconnectReason.ClientLeft);

        var cmds = GlobalCommands();
        Assert.That(cmds, Has.Count.EqualTo(2));
        Assert.That(cmds[1].type, Is.EqualTo(CommandType.PlayerCount));
        Assert.That(MapIds(cmds[1]), Is.EqualTo((5, -1)));
        Assert.That(server.playerManager.Players, Does.Not.Contain(player));
    }

    // The client's poll reports no map (-1) when it views neither a map nor the world, for example during the
    // gravship cutscene; the player is then counted nowhere, so its disconnect must not take it off a map again
    [Test]
    public void ReportToNoMap_IsForwarded_ThenDisconnectSendsNothing()
    {
        var (player, conn) = AddJoinedPlayer("player", -1, hasReportedCurrentMap: false);
        Report(player, -1, 5);

        Report(player, 5, -1);

        var cmds = GlobalCommands();
        Assert.That(cmds.Select(MapIds), Is.EqualTo(new[] { (-1, 5), (5, -1) }));
        Assert.That(player.currentMapId, Is.EqualTo(-1));

        server.playerManager.SetDisconnected(conn, MpDisconnectReason.ClientLeft);

        Assert.That(GlobalCommands(), Has.Count.EqualTo(2));
    }

    [Test]
    public void FirstReportOntoWorldMap_IsForwarded_ThenDisconnectLeavesTheWorld()
    {
        var (player, conn) = AddJoinedPlayer("player", -1, hasReportedCurrentMap: false);

        Report(player, -1, -2);

        var cmds = GlobalCommands();
        Assert.That(cmds, Has.Count.EqualTo(1));
        Assert.That(MapIds(cmds[0]), Is.EqualTo((-1, -2)));
        Assert.That(player.currentMapId, Is.EqualTo(-2));

        server.playerManager.SetDisconnected(conn, MpDisconnectReason.ClientLeft);

        cmds = GlobalCommands();
        Assert.That(cmds, Has.Count.EqualTo(2));
        Assert.That(MapIds(cmds[1]), Is.EqualTo((-2, -1)));
    }

    [Test]
    public void ReportToNoMap_WhileOnNoMap_IsDropped()
    {
        var (player, _) = AddJoinedPlayer("player", -1, hasReportedCurrentMap: true);

        // The client claims map 7, but the server never counted the player there
        Report(player, 7, -1);

        Assert.That(GlobalCommands(), Is.Empty);
        Assert.That(player.currentMapId, Is.EqualTo(-1));
    }

    // Kept as the player's map, the reset value would be forwarded as the previous map of the next report, which
    // every client executes as "zero every count"
    [Test]
    public void ReportOfResetValue_IsDropped()
    {
        var (player, _) = AddJoinedPlayer("player", 5, hasReportedCurrentMap: true);

        Report(player, 5, ScheduledCommand.PlayerCountResetAll);
        Report(player, ScheduledCommand.PlayerCountResetAll, 7);

        var cmds = GlobalCommands();
        Assert.That(cmds, Has.Count.EqualTo(1));
        Assert.That(MapIds(cmds[0]), Is.EqualTo((5, 7)));
        Assert.That(player.currentMapId, Is.EqualTo(7));
    }
}
