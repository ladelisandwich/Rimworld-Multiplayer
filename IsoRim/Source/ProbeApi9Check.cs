using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only binding checks for the ap9 fixtures (ProbeApi9.cs):
    //   ap9check=label,N   after N frames: per fixture pawn, whether its item is drawn with its binding (hat, belt gear and
    //                      its live flames, held shield: shown on how many of its frames), with PASS/FAIL lines. Expected:
    //                      bound hats on every pawn the game shows headgear on; the torch belt as gear with flames; a
    //                      shield shown on every frame for drafted pawns with a knife (standing or walking), never for the
    //                      lying, the undrafted and the one with a two-handed rifle (VE Framework does not draw it then).
    public static partial class Probe
    {
        private static bool ap9CheckPatched;
        private static int ap9CheckFrames = -1, ap9Frames;
        private static string ap9CheckLabel;
        private static readonly Dictionary<int, int> ap9Asked = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> ap9Shown = new Dictionary<int, int>();

        private static bool Api9CheckKey(string key, string[] a, Map map)
        {
            if (key != "ap9check")
            {
                return false;
            }
            if (!ap9CheckPatched)
            {
                ap9CheckPatched = true;
                new Harmony("peren.isorim.probe.ap9check").Patch(AccessTools.Method(typeof(ApparelModels), nameof(ApparelModels.ShieldShown)),
                    postfix: new HarmonyMethod(typeof(Probe), nameof(Api9ShieldShown_Postfix)));
            }
            ap9CheckLabel = a[0];
            ap9CheckFrames = I(a[1]);
            ap9Frames = 0;
            ap9Asked.Clear();
            ap9Shown.Clear();
            return true;
        }

        public static void Api9ShieldShown_Postfix(Pawn pawn, bool __result)
        {
            if (ap9CheckFrames < 0 || pawn == null)
            {
                return;
            }
            ap9Asked[pawn.thingIDNumber] = (ap9Asked.TryGetValue(pawn.thingIDNumber, out int n) ? n : 0) + 1;
            if (__result)
            {
                ap9Shown[pawn.thingIDNumber] = (ap9Shown.TryGetValue(pawn.thingIDNumber, out int s) ? s : 0) + 1;
            }
        }

        private static void Api9CheckUpdate(Map map)
        {
            if (ap9CheckFrames < 0)
            {
                return;
            }
            if (++ap9Frames < ap9CheckFrames)
            {
                return;
            }
            ap9CheckFrames = -1;
            string L = "[IsoRim probe] ap9check " + ap9CheckLabel + ": ";
            int pass = 0, fail = 0;
            Log.Message(L + "frames " + ap9Frames + ", Api.Version " + Api.Version + ", bindings " + ApparelModels.Count);
            foreach (Pawn p in ap9Spawned.OrderBy(x => ap9Roles[x.thingIDNumber]))
            {
                string role = ap9Roles[p.thingIDNumber];
                People.Look look = People.LastLook(p);
                List<string> fails = new List<string>();
                Apparel item = p.apparel != null ? p.apparel.WornApparel.FirstOrDefault(x => x.def.defName.StartsWith("VFEM2_")) : null;
                ApparelModels.Binding b = item != null ? ApparelModels.For(item.def) : null;
                string build = People.BuildOf(p.story.bodyType);
                Model bound = b != null ? b.ModelFor(build) : null;
                int asked = ap9Asked.TryGetValue(p.thingIDNumber, out int na) ? na : 0;
                int shown = ap9Shown.TryGetValue(p.thingIDNumber, out int ns) ? ns : 0;
                string state = "item " + (item != null ? item.def.defName + " (" + (item.Stuff != null ? item.Stuff.defName : "no stuff") + ", colour " + item.DrawColor.ToString("0.00") + ")" : "-") + ", binding " + (b != null ? b.slot.ToString() : "none") + ", model " + (bound != null)
                    + ", drafted " + p.Drafted + ", downed " + p.Downed + ", look " + (look != null ? ApparelModels.Report(look) : "none") + ", shield asked " + asked + " shown " + shown;
                if (look == null)
                {
                    fails.Add("not drawn");
                }
                else if (b == null || bound == null)
                {
                    fails.Add("no binding or model");
                }
                else
                {
                    switch (b.slot)
                    {
                        case ApparelModels.Slot.Head:
                            if (look.hat != bound)
                            {
                                fails.Add("hat not the bound model");
                            }
                            break;
                        case ApparelModels.Slot.Belt:
                            if (!look.gear.Contains(bound))
                            {
                                fails.Add("belt not in the gear");
                            }
                            if (ApparelModels.FlamesOf(bound) != null && !look.flames)
                            {
                                fails.Add("flames not on");
                            }
                            break;
                        case ApparelModels.Slot.Shield:
                        {
                            if (look.shield != bound)
                            {
                                fails.Add("shield not the bound model");
                            }
                            bool expect = p.Drafted && !p.Downed && !role.Contains("rifle");
                            if (expect && (asked == 0 || shown < asked * 0.95f))
                            {
                                fails.Add("shield not shown");
                            }
                            if (!expect && shown > 0)
                            {
                                fails.Add("shield shown");
                            }
                            break;
                        }
                    }
                }
                if (fails.Count == 0)
                {
                    pass++;
                }
                else
                {
                    fail++;
                }
                Log.Message(L + role + " " + p.LabelShort + ": " + state + " -> " + (fails.Count == 0 ? "PASS" : "FAIL (" + string.Join(", ", fails) + ")"));
            }
            Log.Message(L + "total " + pass + " PASS, " + fail + " FAIL");
        }
    }
}
