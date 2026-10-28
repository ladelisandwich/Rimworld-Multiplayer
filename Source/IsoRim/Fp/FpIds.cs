using System;
using Multiplayer.API;
using Verse;

namespace IsoRim.Fp
{
    // Who is who for first-person play. A controller id is Multiplayer's player id in a session, SinglePlayer outside one.
    // Executing (the player whose command is running) is stamped by the server (TickPatch.currentExecutingCmdPlayerId,
    // read through FpMpHooks), so a client can't claim another player's pawn.
    public static class FpIds
    {
        public const int SinglePlayer = -2;   // controller id outside a session
        public const int None = -1;           // no controller / no command running (in a session)

        // The player whose command runs now. Simulation code (command bodies) uses this, never Local.
        public static int Executing
        {
            get
            {
                if (!MpBridge.InSession)
                {
                    return SinglePlayer;
                }
                return FpMpHooks.Bound ? FpMpHooks.Executing() : None;
            }
        }

        // This player's id. UI only.
        public static int Local
        {
            get
            {
                if (!MpBridge.InSession)
                {
                    return SinglePlayer;
                }
                return FpMpHooks.Bound ? FpMpHooks.Local() : None;
            }
        }

        // First-person play is off for this player: ghost mode, a replay or the arbiter, or a session whose Multiplayer
        // build didn't bind the hooks (a ride-along view still works then).
        public static bool PlayOff
        {
            get { return MpBridge.InSession && (!FpMpHooks.Bound || FpMpHooks.PlayOff()); }
        }

        // A player's name for messages and tags. UI only: "you" for this player, "?" for an id no longer in the session.
        public static string NameOf(int id)
        {
            if (id == Local && id != None)
            {
                return "R3D_Fp_You".Translate();
            }
            if (MpBridge.InSession)
            {
                try
                {
                    foreach (IPlayerInfo p in MP.GetPlayers())
                    {
                        if (p != null && p.Id == id)
                        {
                            return p.Username;
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] First person: reading Multiplayer's player list failed: " + e, 93652001);
                }
            }
            return "?";
        }
    }

    // Bound by Multiplayer.dll (Source/Client/UI/IsoRimFp.cs) by name, through reflection, when both are loaded: IsoRim.dll
    // can't reference Multiplayer.dll (it loads first and must work without it). Before binding, or with a standalone
    // Multiplayer that doesn't know this class, play is off in a session (FpIds.PlayOff).
    public static class FpMpHooks
    {
        private static Func<int> executing;
        private static Func<int> local;
        private static Func<bool> playOff;

        public static bool Bound
        {
            get { return executing != null && local != null && playOff != null; }
        }

        public static void Install(Func<int> executingPlayerId, Func<int> localPlayerId, Func<bool> playOff)
        {
            executing = executingPlayerId;
            local = localPlayerId;
            FpMpHooks.playOff = playOff;
            ModLog.Note("[IsoRim] First person: Multiplayer hooks " + (Bound ? "bound." : "incomplete, play stays off in a session."));
        }

        internal static int Executing()
        {
            return executing();
        }

        internal static int Local()
        {
            return local();
        }

        internal static bool PlayOff()
        {
            return playOff();
        }

        // A player left the session (disconnect, desync, rejoin request): the server's PlayerResetGlobal time vote, run by
        // every client at the same world tick. Data only; each map's own tick releases the pawns.
        public static void OnPlayerLeft(int playerId)
        {
            try
            {
                FpMapComp.ReleaseAllOf(playerId);
            }
            catch (Exception e)
            {
                Log.Error("[IsoRim] First person: releasing the pawns of player " + playerId + " failed: " + e);
            }
        }

        // Every player's control ends (hosting a new session, the server's PlayerCountResetAll). Data only.
        public static void OnSessionReset()
        {
            try
            {
                FpMapComp.ResetAll();
            }
            catch (Exception e)
            {
                Log.Error("[IsoRim] First person: resetting control failed: " + e);
            }
        }

        // The first-person part of Multiplayer's per-tick pawn digest (PawnStateTrace): "" when the pawn isn't involved.
        public static string Describe(Pawn pawn)
        {
            return FpState.Describe(pawn) ?? "";
        }
    }

    // IsoRim.dll's [SyncMethod]s (the first-person commands) aren't registered here: Multiplayer registers them itself at a
    // fixed point after every static constructor, right before it numbers its handlers (IsoRimFp.RegisterSyncMethods, from
    // MultiplayerStatic). A sync id is a handler's index in that list, and static constructors on startup run in
    // GenTypes.AllTypesWithAttribute order, an unordered parallel query: registering from one would number IsoRim's
    // commands differently on two machines whenever another mod also registers from a static constructor.
}
