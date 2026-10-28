using System;
using System.Text;
using HarmonyLib;
using Verse;

namespace Multiplayer.Client
{
    // Connects the built-in IsoRim's first-person play (IsoRim.Fp.FpMpHooks, Source/IsoRim/Fp/FpIds.cs) to the session.
    // IsoRim gets three getters: the player id the server stamped on the running command (who sent a control command;
    // a client can't claim another player's id), this player's id, and whether play is off for this player (ghost mode,
    // a replay, the arbiter, or its commands not registered). Multiplayer registers IsoRim's synced commands, forwards the
    // session events that end control (a player leaving, a new session) and adds first-person state to PawnStateTrace's
    // digest. Bound by name at startup like IsoRimView: IsoRim.dll is its
    // own assembly loaded by the game, and a standalone IsoRim has no FpMpHooks. Unbound, every call here does nothing
    // and IsoRim keeps first-person play off in a session (FpIds.PlayOff).
    [StaticConstructorOnStartup]
    public static class IsoRimFp
    {
        private const string HooksType = "IsoRim.Fp.FpMpHooks";

        private static Action<int> onPlayerLeft;
        private static Action onSessionReset;
        private static Func<Pawn, string> describe;

        // IsoRim.dll's synced commands are registered (RegisterSyncMethods). Until then, or if that failed, play is off in a
        // session: a command that isn't synced would run on the sender's machine alone.
        private static bool syncRegistered;

        static IsoRimFp()
        {
            Bind();
        }

        // Registers IsoRim.dll's [SyncMethod]s (the first-person commands). Called by MultiplayerStatic right before
        // Sync.PostInitHandlers numbers the handlers, after every static constructor has run: a handler's sync id is its
        // index in that list, and static constructors on startup run in an unordered (parallel query) order, so registering
        // from one of them could number IsoRim's commands differently on two machines whenever another mod also registers
        // from a static constructor. Here they always come after everything registered earlier, in the assembly's order.
        public static void RegisterSyncMethods()
        {
            var type = GenTypes.GetTypeInAnyAssembly(HooksType, "IsoRim.Fp");
            if (type == null) return;

            try
            {
                Sync.RegisterAllAttributes(type.Assembly);
                syncRegistered = true;
            }
            catch (Exception e)
            {
                Log.Error($"Multiplayer: couldn't register IsoRim's first-person commands, so first-person play stays off " +
                          $"in multiplayer games: {e}");
            }
        }

        private static void Bind()
        {
            var type = GenTypes.GetTypeInAnyAssembly(HooksType, "IsoRim.Fp");
            if (type == null) return;

            try
            {
                var install = AccessTools.DeclaredMethod(type, "Install") ??
                              throw new MissingMethodException(type.FullName, "Install");
                var left = AccessTools.MethodDelegate<Action<int>>(AccessTools.DeclaredMethod(type, "OnPlayerLeft"));
                var reset = AccessTools.MethodDelegate<Action>(AccessTools.DeclaredMethod(type, "OnSessionReset"));
                var desc = AccessTools.MethodDelegate<Func<Pawn, string>>(AccessTools.DeclaredMethod(type, "Describe"));

                // Last, so IsoRim is only told it's bound once every forwarder here exists
                install.Invoke(null, new object[]
                {
                    (Func<int>)(() => TickPatch.currentExecutingCmdPlayerId),
                    (Func<int>)(() => Multiplayer.session?.playerId ?? -1),
                    (Func<bool>)(() => !syncRegistered || Multiplayer.GhostMode || Multiplayer.IsReplay || Multiplayer.arbiterInstance)
                });

                onPlayerLeft = left;
                onSessionReset = reset;
                describe = desc;
            }
            catch (Exception e)
            {
                onPlayerLeft = null;
                onSessionReset = null;
                describe = null;
                Log.Error($"Multiplayer: couldn't bind IsoRim's first-person play (IsoRim.Fp.FpMpHooks), so it stays " +
                          $"off in multiplayer games: {e}");
            }
        }

        // A player left the session (the server's PlayerResetGlobal time vote): every client calls this in the world
        // command at the same tick. Data only. Caught here so a failure can't skip the rest of the vote on this client.
        public static void PlayerLeft(int playerId)
        {
            if (onPlayerLeft == null) return;

            try
            {
                onPlayerLeft(playerId);
            }
            catch (Exception e)
            {
                Log.Error($"Multiplayer: releasing the first-person pawns of player {playerId} failed: {e}");
            }
        }

        // A new session (hosting) or the server's PlayerCountResetAll: nobody controls a pawn. Data only.
        public static void ResetSession()
        {
            if (onSessionReset == null) return;

            try
            {
                onSessionReset();
            }
            catch (Exception e)
            {
                Log.Error($"Multiplayer: resetting first-person control failed: {e}");
            }
        }

        // Appends " <first-person state>" to the pawn's digest when the pawn is involved. Synced state only, so equal
        // on every client; an exception reaches PawnStateTrace, which turns tracing off and says why.
        public static void Describe(Pawn pawn, StringBuilder b)
        {
            if (describe == null) return;

            var s = describe(pawn);
            if (!s.NullOrEmpty())
                b.Append(' ').Append(s);
        }
    }
}
