using RimWorld;
using Verse;

namespace IsoRim.Fp
{
    // First-person play's own key bindings (Source/IsoRim/Defs/IsoRimFp_Keys.xml). Read polled (JustPressed, IsDown),
    // never patched: KeyBindingDef.IsDown and JustPressed are tiny and shared by the whole game.
    [DefOf]
    public static class FpKeyDefOf
    {
        public static KeyBindingDef R3D_FpSkillCheck;   // Space: the skill check, only while a check shows
        public static KeyBindingDef R3D_FpFreeCursor;   // Alt held: the cursor is free for gizmos, tabs and time controls

        static FpKeyDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(FpKeyDefOf));
        }
    }
}
