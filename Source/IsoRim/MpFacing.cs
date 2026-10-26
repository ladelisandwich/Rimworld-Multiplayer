using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace IsoRim
{
    // Port-owned (outside the subtree, like MpBridge). In a multiplayer session the game's turn of an idle drafted pawn
    // south stays (Patches.DraftedSouth: the rotation is game state, the 3D view is each player's own), so the 3D view
    // draws the facing it had just before that turn instead, as single-player IsoRim shows it. Draw-only: never read by
    // the simulation, never saved, no Rand. Keyed by thingIDNumber, so it survives Multiplayer's reloads (joins, resyncs).
    public static class MpFacing
    {
        private static readonly ConcurrentDictionary<int, Rot4> kept = new ConcurrentDictionary<int, Rot4>();
        private static readonly MethodInfo setRotation = AccessTools.PropertySetter(typeof(Thing), nameof(Thing.Rotation));
        private static int replaced;

        // Patches.DraftedSouth in a session, just before the game's pawn.Rotation = Rot4.South (in the tick, every player).
        internal static void BeforeSouth(Pawn pawn)
        {
            if (pawn == null)
            {
                return;
            }
            Rot4 now = pawn.Rotation;
            // Not south: turned since the last south step (by the tracker this tick, or by a job directly). South: the
            // game's own south from the last step (entry kept), or a real turn south by the tracker (entry already gone).
            if (now != Rot4.South)
            {
                kept[pawn.thingIDNumber] = now;
            }
        }

        // Replaces Thing.set_Rotation in Pawn_RotationTracker.FaceAdjacentCell, FaceCell and Face: the game turned the
        // pawn (walking, aiming, its job), so whatever was kept for it is over. Runs in every player's tick.
        public static void GameTurned(Thing pawn, Rot4 rot)
        {
            pawn.Rotation = rot;   // exactly the game's write, first
            kept.TryRemove(pawn.thingIDNumber, out _);
        }

        internal static void Forget(Thing t)
        {
            if (t is Pawn)
            {
                kept.TryRemove(t.thingIDNumber, out _);
            }
        }

        // At the main menu (no game, no session): ids from a later game must not match.
        internal static void ForgetAll()
        {
            if (!kept.IsEmpty)
            {
                kept.Clear();
            }
        }

        // The facing to draw a pawn with: the game's own, except for a pawn the game turned south in a session while
        // this player's 3D view is on. Thread-safe (the game's ParallelPreDraw workers); never writes.
        public static Rot4 Drawn(Pawn pawn)
        {
            Rot4 game = pawn.Rotation;
            if (game != Rot4.South || !View3D.Active || !MpBridge.InSession)
            {
                return game;
            }
            return kept.TryGetValue(pawn.thingIDNumber, out Rot4 k) ? k : game;
        }

        // As Drawn for a pawn, a thing's own rotation otherwise.
        public static Rot4 Drawn(Thing t)
        {
            return t is Pawn pawn ? Drawn(pawn) : t.Rotation;
        }

        // From Patches.ApplyAll, with IsoRim's Harmony instance, at startup before any map, and before UpdateRotation is
        // patched: its replacement is compiled then and calls FaceCell and Face, small enough to be inlined.
        internal static void Apply(Harmony harmony)
        {
            replaced = 0;
            foreach (string name in new[] { "FaceAdjacentCell", "FaceCell", "Face" })
            {
                // A failure here must not stop the rest of Patches.ApplyAll (IsoRimMod logs one error and gives up)
                try
                {
                    harmony.Patch(AccessTools.Method(typeof(Pawn_RotationTracker), name),
                        transpiler: new HarmonyMethod(typeof(MpFacing), nameof(Turned_Transpiler)));
                }
                catch (Exception e)
                {
                    Log.Error("[IsoRim] MP drafted facing: could not patch Pawn_RotationTracker." + name + ": " + e);
                }
            }
            if (replaced != 6)
            {
                Log.Error("[IsoRim] MP drafted facing: replaced " + replaced + " rotation writes in Pawn_RotationTracker (expected 6); " +
                          "a drafted pawn may be drawn facing the way it was kept after the game turned it.");
            }
        }

        public static IEnumerable<CodeInstruction> Turned_Transpiler(IEnumerable<CodeInstruction> code)
        {
            MethodInfo turned = AccessTools.Method(typeof(MpFacing), nameof(GameTurned));
            foreach (CodeInstruction ci in code)
            {
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand as MethodInfo == setRotation)
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = turned;
                    replaced++;
                }
                yield return ci;
            }
        }
    }
}
