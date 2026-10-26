using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only keys for the see-through while building (BuildSee; the setting itself: set=buildSeeThrough,0|1):
    //   bsee=coverage,inner,outer[,px]  the see-through's coverage at the pointer, its radii (cells) and dither cell (pixels)
    //                                (overrides the settings seeStrength, seeRadius; bsee=off: the settings again)
    //   designator=def|mine|off      the game's build designator of that def (terrain or thing), its Mine tool, or none
    //   bsptr=x,z|off                the cut-away's pointer (cutptr) at that map point AND the real pointer over it every
    //                                frame, so the designator's ghost follows it (U<z>: floor 2)
    public static partial class Probe
    {
        private static bool bsFollow;

        private static bool BuildSeeKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "bsee":
                    if (a[0] == "off")
                    {
                        BuildSee.CoverageOverride = BuildSee.InnerOverride = BuildSee.OuterOverride = -1f;   // the settings again
                        return true;
                    }
                    BuildSee.CoverageOverride = F(a[0]);
                    BuildSee.InnerOverride = F(a[1]);
                    BuildSee.OuterOverride = F(a[2]);
                    if (a.Length > 3)
                    {
                        BuildSee.DitherPx = F(a[3]);
                    }
                    return true;
                case "designator":
                {
                    DesignatorManager dm = Find.DesignatorManager;
                    if (a[0] == "off")
                    {
                        dm.Deselect();
                        return true;
                    }
                    if (a[0] == "mine")
                    {
                        dm.Select(new Designator_Mine());   // the game's Mine tool (MineXray)
                        return true;
                    }
                    BuildableDef def = (BuildableDef)DefDatabase<TerrainDef>.GetNamedSilentFail(a[0]) ?? DefDatabase<ThingDef>.GetNamed(a[0]);
                    Designator_Build d = BuildCopyCommandUtility.FindAllowedDesignator(def);
                    if (d == null)
                    {
                        Log.Warning("[IsoRim probe] designator: none for " + a[0]);
                        return true;
                    }
                    dm.Select(d);
                    Log.Message("[IsoRim probe] designator " + d.Label + " in hand");
                    return true;
                }
                case "bsptr":
                    if (a[0] == "off")
                    {
                        HasCutPointer = false;
                        bsFollow = false;
                        fakeMouse = null;
                        return true;
                    }
                    HookMouse();
                    HasCutPointer = true;
                    CutPointer = new Vector3(F(a[0]), 0f, F(a[1]));
                    bsFollow = true;
                    return true;
            }
            return false;
        }

        // Every frame (CutTick): the real pointer over the cut-away's pointer, where the camera is now.
        private static void BuildSeeTick()
        {
            if (bsFollow && HasCutPointer && View3D.Ready)
            {
                fakeMouse = ScreenOf(CutPointer);
            }
        }
    }
}
