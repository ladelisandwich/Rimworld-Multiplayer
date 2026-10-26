using Multiplayer.API;
using RimWorld;
using Verse;

namespace IsoRim
{
    // Port-owned glue between IsoRim and the Multiplayer mod it ships in (this file lives outside the IsoRim subtree so
    // upstream pulls never touch it). Upstream IsoRim files call it from one-line guards marked "MP:".
    //
    // Rule for every IsoRim hook that can run inside a game tick, a synced command or a load: it must not branch on the
    // 3D view or on IsoRim settings (both are per player and never synced), must not write game state, and must not
    // draw from Verse.Rand outside Rand.PushState/PopState. Otherwise the players' games diverge.
    //
    // Only Multiplayer's public API (0MultiplayerAPI) is used, never Multiplayer.dll: IsoRim.dll is loaded by the game
    // before MultiplayerLoader adds Multiplayer.dll.
    public static class MpBridge
    {
        // A multiplayer session is running (hosting, connected, or loading/reloading inside one).
        public static bool InSession => MP.enabled && MP.IsInMultiplayer;

        // The code running now is part of the shared simulation in a session: a tick, a synced command, or a load.
        // Multiplayer's InInterface is only true for plain UI and drawing, so everything else counts as simulation.
        public static bool Simulating => InSession && !MP.InInterface;

        // The player's own faction in a session (the local player's faction even while another faction is ticking).
        public static Faction RealPlayerFaction => InSession ? MP.RealPlayerFaction : Faction.OfPlayer;

        // Multiplayer's multifaction rule for blueprints (Source/Client/Factions/Blueprints.cs, BlueprintPrintPatch):
        // only blueprints of no faction or of the player's own faction are shown.
        public static bool HidesBlueprint(Thing t) =>
            InSession && t.def.IsBlueprint && t.Faction != null && t.Faction != MP.RealPlayerFaction;
    }
}
