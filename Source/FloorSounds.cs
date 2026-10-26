using System;
using HarmonyLib;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace IsoRim
{
    // Sounds on stacked floors: heard where the 3D view shows them. The game plays a world sound at its map cell, but a
    // floor's cells are rows far north of the ground, while the view and the AudioListener (on the main camera) stay over
    // the ground rows: a floor's sounds were 150+ cells away and silent. Every world (not camera) sound source on a floor
    // row is moved to where that floor stands (Floors.RowOffset: down by its rows, up by its height), whether the floor is
    // shown or hidden (the floors above the viewed one are still heard). Plain 3D position, no extra attenuation.
    // Audio positions only, never gameplay. Hooked with the other floor hooks, only when a layout registered.
    //
    // The game's world-sound positions (all in Verse.Sound): a one-shot's source (SampleOneShot.TryMakeAndPlay), a
    // sustainer's root object, parent of its sources (Sustainer.UpdateRootObjectPosition, each update for a thing), the
    // voice ranking of one-shots by camera distance (SampleOneShotManager.CameraDistanceSquaredOf; sustainers rank by
    // their root object), and a sustainer's fade-out start and stop sounds, played at its root object's cell
    // (Sustainer.PlaySoundOnMapOrCamera).
    internal static class FloorSounds
    {
        private static readonly AccessTools.FieldRef<Sustainer, GameObject> rootOf = AccessTools.FieldRefAccess<Sustainer, GameObject>("worldRootObject");
        private static Action<Sustainer> updateRoot;
        private static bool rawRoot;

        internal static void Patch(Harmony h)
        {
            updateRoot = AccessTools.MethodDelegate<Action<Sustainer>>(AccessTools.Method(typeof(Sustainer), "UpdateRootObjectPosition"));
            h.Patch(AccessTools.Method(typeof(SampleOneShot), nameof(SampleOneShot.TryMakeAndPlay)),
                postfix: new HarmonyMethod(typeof(FloorSounds), nameof(OneShot_Postfix)));
            h.Patch(AccessTools.Method(typeof(Sustainer), "UpdateRootObjectPosition"),
                postfix: new HarmonyMethod(typeof(FloorSounds), nameof(Root_Postfix)));
            h.Patch(AccessTools.Method(typeof(Sustainer), "PlaySoundOnMapOrCamera"),
                prefix: new HarmonyMethod(typeof(FloorSounds), nameof(EndSound_Prefix)), postfix: new HarmonyMethod(typeof(FloorSounds), nameof(EndSound_Postfix)));
            h.Patch(AccessTools.Method(typeof(SampleOneShotManager), "CameraDistanceSquaredOf"),
                prefix: new HarmonyMethod(typeof(FloorSounds), nameof(CameraDistance_Prefix)));
        }

        // A world point on a floor row of the current map, where its floor stands; false when nothing moves. In true
        // space (the floor's height in cells, Floors.RowOffset), not drawn space: the listener is the game's own camera
        // transform, above the screen centre at the game's camera height, which the 3D view leaves alone (the squash is
        // only in the view matrix).
        private static bool Moved(Map map, ref Vector3 p)
        {
            if (!Floors.On || map == null || map != Find.CurrentMap)
            {
                return false;
            }
            Vector3 o = Floors.RowOffset(Mathf.FloorToInt(p.z));
            if (o == Vector3.zero)
            {
                return false;
            }
            p += o;
            return true;
        }

        public static void OneShot_Postfix(SubSoundDef def, SoundInfo info, SampleOneShot __result)
        {
            if (!Floors.On || __result == null || def.onCamera || __result.source == null)
            {
                return;
            }
            Transform t = __result.source.gameObject.transform;
            Vector3 p = t.position;
            if (Moved(info.Maker.Map, ref p))
            {
                t.position = p;
            }
        }

        public static void Root_Postfix(Sustainer __instance)
        {
            if (!Floors.On || rawRoot || __instance.info.forcedPlayOnCamera)
            {
                return;
            }
            GameObject root = rootOf(__instance);
            if (root == null)
            {
                return;
            }
            Vector3 p = root.transform.position;
            if (Moved(__instance.info.Maker.Map, ref p))
            {
                root.transform.position = p;
            }
        }

        // A sustainer's fade-out start and stop sounds are played at its root object's cell: the root is put back on the
        // sound's own cell for that call, so they are floor sounds like the others (moved by OneShot_Postfix, with the
        // floor's vacuum), then moved again (a fading sustainer keeps sounding where it is drawn).
        public static void EndSound_Prefix(Sustainer __instance, out bool __state)
        {
            __state = Floors.On && !__instance.info.forcedPlayOnCamera && rootOf(__instance) != null;
            if (__state)
            {
                RootAt(__instance, true);
            }
        }

        public static void EndSound_Postfix(Sustainer __instance, bool __state)
        {
            if (__state && rootOf(__instance) != null)
            {
                RootAt(__instance, false);
            }
        }

        private static void RootAt(Sustainer s, bool raw)
        {
            rawRoot = raw;
            try
            {
                updateRoot(s);
            }
            finally
            {
                rawRoot = false;
            }
        }

        // The voice ranking of one-shots (PrioritizeNearest): a floor sound counts where its floor stands.
        public static bool CameraDistance_Prefix(SoundInfo info, ref float __result)
        {
            if (!Floors.On)
            {
                return true;
            }
            Vector3 p = info.Maker.Cell.ToVector3Shifted();
            if (!Moved(info.Maker.Map, ref p))
            {
                return true;
            }
            __result = (Find.CameraDriver.MapPosition - p.ToIntVec3()).LengthHorizontalSquared;
            return false;
        }
    }
}
