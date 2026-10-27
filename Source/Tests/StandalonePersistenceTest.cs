using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Multiplayer.Common;

namespace Tests;

[TestFixture]
public class StandalonePersistenceTest
{
    private string tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"mp-standalone-persistence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        MultiplayerServer.instance = null;

        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, true);
    }

    [Test]
    public void WriteJoinPoint_PersistsCommandsAndTickForReload()
    {
        var server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings())
        {
            IsStandaloneServer = true,
            persistence = new StandalonePersistence(tempDir),
        };

        server.worldData.savedGame = [1, 2, 3];
        server.worldData.sessionData = [4, 5, 6];
        server.worldData.mapData[7] = [7, 8, 9];

        var worldCmd = ScheduledCommand.Serialize(new ScheduledCommand(CommandType.Sync, 1234, 1, ScheduledCommand.Global, 5, [10]));
        var mapCmd = ScheduledCommand.Serialize(new ScheduledCommand(CommandType.Designator, 1234, 1, 7, 5, [11]));
        server.worldData.mapCmds[ScheduledCommand.Global] = [worldCmd];
        server.worldData.mapCmds[7] = [mapCmd];

        server.persistence.WriteJoinPoint(server.worldData, 1234);

        var reloadedServer = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings())
        {
            IsStandaloneServer = true,
            persistence = new StandalonePersistence(tempDir),
        };

        var info = reloadedServer.persistence.LoadInto(reloadedServer);

        Assert.That(info, Is.Null);
        Assert.That(reloadedServer.gameTimer, Is.EqualTo(1234));
        Assert.That(reloadedServer.startingTimer, Is.EqualTo(1234));
        Assert.That(reloadedServer.worldData.savedGame, Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(reloadedServer.worldData.sessionData, Is.EqualTo(new byte[] { 4, 5, 6 }));
        Assert.That(reloadedServer.worldData.mapData[7], Is.EqualTo(new byte[] { 7, 8, 9 }));
        // The persisted world command, then the player count reset LoadInto adds (LoadInto_EndsWorldCommandsWithPlayerCountReset)
        Assert.That(reloadedServer.worldData.mapCmds[ScheduledCommand.Global], Has.Count.EqualTo(2));
        Assert.That(reloadedServer.worldData.mapCmds[7], Has.Count.EqualTo(1));

        var reloadedWorldCmd = ScheduledCommand.Deserialize(new ByteReader(reloadedServer.worldData.mapCmds[ScheduledCommand.Global][0]));
        var reloadedMapCmd = ScheduledCommand.Deserialize(new ByteReader(reloadedServer.worldData.mapCmds[7][0]));

        Assert.That(reloadedWorldCmd.ticks, Is.EqualTo(1234));
        Assert.That(reloadedMapCmd.ticks, Is.EqualTo(1234));
        Assert.That(reloadedServer.worldData.standaloneWorldSnapshot.tick, Is.EqualTo(1234));
        Assert.That(reloadedServer.worldData.standaloneMapSnapshots[7].tick, Is.EqualTo(1234));
    }

    [Test]
    public void LoadInto_FallsBackToLatestReplaySectionTick()
    {
        var persistence = new StandalonePersistence(tempDir);
        persistence.EnsureDirectories();

        File.WriteAllBytes(Path.Combine(tempDir, "Saved", "world.dat"), [1]);
        File.WriteAllBytes(Path.Combine(tempDir, "Saved", "session.dat"), []);
        File.WriteAllBytes(Path.Combine(tempDir, "Saved", "world_cmds.dat"), ScheduledCommand.SerializeCmds(new List<ScheduledCommand>()));
        File.WriteAllBytes(Path.Combine(tempDir, "Saved", "info.xml"), ReplayInfo.Write(new ReplayInfo
        {
            sections = new List<ReplaySection>
            {
                new(100, 100),
                new(999, 999),
            }
        }));

        var server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings())
        {
            IsStandaloneServer = true,
            persistence = persistence,
        };

        persistence.LoadInto(server);

        Assert.That(server.gameTimer, Is.EqualTo(999));
        Assert.That(server.startingTimer, Is.EqualTo(999));
    }

    // The loaded game carries the player counts of the session that saved it, which this server's fresh player
    // records can never take back down, so LoadInto zeroes them with a command after the loaded ones (Oct 27)
    [Test]
    public void LoadInto_EndsWorldCommandsWithPlayerCountReset()
    {
        var server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings())
        {
            IsStandaloneServer = true,
            persistence = new StandalonePersistence(tempDir),
        };

        server.worldData.savedGame = [1, 2, 3];
        server.worldData.sessionData = [];
        server.worldData.mapData[3] = [7, 8, 9];

        // A report of the old session after its last join point
        var oldReport = ScheduledCommand.Serialize(new ScheduledCommand(CommandType.PlayerCount, 1200, ScheduledCommand.NoFaction,
            ScheduledCommand.Global, 5, ByteWriter.GetBytes(-1, 3)));
        server.worldData.mapCmds[ScheduledCommand.Global] = [oldReport];
        server.persistence.WriteJoinPoint(server.worldData, 1234);

        var reloadedServer = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings())
        {
            IsStandaloneServer = true,
            persistence = new StandalonePersistence(tempDir),
        };

        reloadedServer.persistence.LoadInto(reloadedServer);

        var worldCmds = reloadedServer.worldData.mapCmds[ScheduledCommand.Global];
        Assert.That(worldCmds, Has.Count.EqualTo(2));
        Assert.That(worldCmds[0], Is.EqualTo(oldReport));
        AssertPlayerCountReset(worldCmds[1], 1234);
        Assert.That(reloadedServer.worldData.mapCmds[3], Is.Empty);
    }

    [Test]
    public void LoadInto_AfterSeedingFromSaveZip_EndsWorldCommandsWithPlayerCountReset()
    {
        var zipPath = Path.Combine(tempDir, "save.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "world/000_save", [1, 2, 3]);
            WriteEntry(zip, "world/000_cmds", ScheduledCommand.SerializeCmds([
                new ScheduledCommand(CommandType.PlayerCount, 400, ScheduledCommand.NoFaction, ScheduledCommand.Global, 0,
                    ByteWriter.GetBytes(-1, 0))
            ]));
            WriteEntry(zip, "maps/000_0_save", [4, 5, 6]);
            WriteEntry(zip, "maps/000_0_cmds", ScheduledCommand.SerializeCmds([]));
            WriteEntry(zip, "info", ReplayInfo.Write(new ReplayInfo { sections = [new(100, 500)] }));
        }

        var persistence = new StandalonePersistence(tempDir);
        persistence.SeedFromSaveZip(zipPath);

        var server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings())
        {
            IsStandaloneServer = true,
            persistence = persistence,
        };

        persistence.LoadInto(server);

        var worldCmds = server.worldData.mapCmds[ScheduledCommand.Global];
        Assert.That(worldCmds, Has.Count.EqualTo(2));
        Assert.That(ScheduledCommand.Deserialize(new ByteReader(worldCmds[0])).ticks, Is.EqualTo(400));
        AssertPlayerCountReset(worldCmds[1], 500);
    }

    private static void AssertPlayerCountReset(byte[] raw, int tick)
    {
        var cmd = ScheduledCommand.Deserialize(new ByteReader(raw));
        Assert.That(cmd.type, Is.EqualTo(CommandType.PlayerCount));
        Assert.That(cmd.ticks, Is.EqualTo(tick));
        Assert.That(cmd.mapId, Is.EqualTo(ScheduledCommand.Global));
        Assert.That(cmd.factionId, Is.EqualTo(ScheduledCommand.NoFaction));
        Assert.That(cmd.playerId, Is.EqualTo(ScheduledCommand.NoPlayer));

        var data = new ByteReader(cmd.data);
        Assert.That(data.ReadInt32(), Is.EqualTo(ScheduledCommand.PlayerCountResetAll));
        Assert.That(data.ReadInt32(), Is.EqualTo(ScheduledCommand.PlayerCountResetAll));
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(bytes, 0, bytes.Length);
    }
}