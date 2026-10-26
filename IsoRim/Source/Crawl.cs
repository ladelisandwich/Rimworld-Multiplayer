using Verse;

namespace IsoRim
{
    // A downed person crawling (1.7), as the game draws it, for the 3D figure (People.Draw).
    //
    // Whether they crawl is the game's own test, Pawn.Crawling: downed, able to crawl (awake, hands, age), on a job done
    // crawling (JobDef.isCrawlingIfDowned: going to a bed, fleeing; not Wait_Downed) and not in bed. It shows them:
    //   - facing the way they go (pawn.Rotation, not the lying facing), seen from behind going north, and going south their
    //     back with the face turned up (PawnRenderTree flipHead): face down;
    //   - the body turned by PawnRenderUtility.CrawlingBodyAngle: going east 75 degrees, not 90, so the side picture has the
    //     head end raised 15 degrees: the chest propped up;
    //   - the head turned back by PawnRenderUtility.CrawlingHeadAngle (east -50): looking ahead;
    //   - moving in lurches, three to a cell (PawnTweener.TweenedPosRoot), which is in the drawn position.
    // The figure lies face down, head first the way it goes (turned to its way as a walker is), chest and head raised by
    // those side-view angles. When the crawl stops (no longer Crawling) it lies as any downed pawn; in bed as in bed.
    public static class Crawl
    {
        // Degrees the chest is raised off the ground: the game's side picture turned short of lying flat.
        public static float ChestLift
        {
            get { return Rot4.East.AsAngle - PawnRenderUtility.CrawlingBodyAngle(Rot4.East); }
        }

        // Degrees the head is tipped back from the body: the game's side picture's head turn.
        public static float HeadLift
        {
            get { return -PawnRenderUtility.CrawlingHeadAngle(Rot4.East); }
        }
    }
}
