using HarmonyLib;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only key for Api.DrawOnCard (CardOverlay.cs), drawn as another mod would: from MapComponentUpdate, every frame.
    //   cardapi=1   on every person drawn as a card: a red square left of the middle lit like the card (LikeCard) and a
    //               yellow one right of it at full brightness (Unlit); logs how many cards took them (again when the zoom crosses the game's atlas threshold).
    public static partial class Probe
    {
        private static bool cardApiOn;
        private static Material cardApiRed, cardApiGlow;
        private static int cardApiLogged, cardApiZoom = -1;

        private static bool CardApiKey(string key, string[] a)
        {
            if (key != "cardapi")
            {
                return false;
            }
            if (!cardApiOn && a[0] == "1")
            {
                new Harmony("peren.isorim.probe.cardapi").Patch(AccessTools.Method(typeof(MapComponentUtility), nameof(MapComponentUtility.MapComponentUpdate)),
                    postfix: new HarmonyMethod(typeof(Probe), nameof(CardApi_Postfix)));
                cardApiRed = MaterialPool.MatFrom(BaseContent.WhiteTex, ShaderDatabase.Transparent, new Color(1f, 0f, 0f, 1f));
                cardApiGlow = MaterialPool.MatFrom(BaseContent.WhiteTex, ShaderDatabase.Transparent, new Color(1f, 0.9f, 0.1f, 1f));
            }
            cardApiOn = a[0] == "1";
            return true;
        }

        public static void CardApi_Postfix(Map map)
        {
            if (!cardApiOn || map != Find.CurrentMap)
            {
                return;
            }
            int cards = 0, others = 0;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (!p.RaceProps.Humanlike)
                {
                    continue;
                }
                Vector3 at = p.DrawPos + new Vector3(0f, 0.1f, 0.15f);   // over the chest, a little above the sprite's layers
                Vector3 s = new Vector3(0.3f, 1f, 0.3f);
                bool lit = Api.DrawOnCard(p, MeshPool.plane10, Matrix4x4.TRS(at + new Vector3(-0.2f, 0f, 0f), Quaternion.identity, s), cardApiRed, CardLight.LikeCard);
                bool glow = Api.DrawOnCard(p, MeshPool.plane10, Matrix4x4.TRS(at + new Vector3(0.2f, 0f, 0f), Quaternion.identity, s), cardApiGlow, CardLight.Unlit);
                if (lit && glow)
                {
                    cards++;
                }
                else
                {
                    others++;
                }
            }
            int zoom = Find.CameraDriver.ZoomRootSize > 18f ? 1 : 0;
            if (cardApiLogged < 6 && cards + others > 0 && zoom != cardApiZoom)
            {
                cardApiLogged++;
                cardApiZoom = zoom;
                Log.Message("[IsoRim probe] cardapi frame " + Time.frameCount + ": drawn on " + cards + " cards, " + others + " people not cards (zoom root "
                    + Find.CameraDriver.ZoomRootSize.ToString("0.0") + ")");
            }
        }
    }
}
