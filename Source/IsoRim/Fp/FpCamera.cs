using UnityEngine;

namespace IsoRim.Fp
{
    // The play-mode camera (fp-play-spec 7.1): one yaw and one pitch the mouse turns directly, read by IsoRim's
    // FirstPerson.Yaw, Pitch and Eye through Ports 11-13 while playing. The yaw is the body heading (the movement keys
    // walk relative to it); IsoRim's own eased walking heading and look offsets are left alone and come back unchanged on
    // leaving. Local to this player, never synced, never read by simulation code.
    public static class FpCamera
    {
        public const float MinPitch = -80f;   // looking up
        public const float MaxPitch = 80f;    // looking down
        public const float StartPitch = 8f;   // as IsoRim's first person starts

        private static float yaw;
        private static float pitch = StartPitch;

        // Degrees clockwise from north, 0..360.
        public static float Yaw
        {
            get { return yaw; }
        }

        // Degrees below the horizon, clamped -80..80.
        public static float Pitch
        {
            get { return pitch; }
        }

        // M6: lead + bob; zero before.
        public static Vector3 EyeOffset
        {
            get { return Vector3.zero; }
        }

        // Play starts facing the way the colonist is drawn (MpFacing), looking a little down, as IsoRim's first person does.
        public static void Begin(float yaw)
        {
            FpCamera.yaw = Mathf.Repeat(yaw, 360f);
            pitch = StartPitch;
        }

        // Degrees turned (right is +yaw) and tilted (down is +pitch). No time factor: mouse counts are already distances.
        public static void Look(float dYaw, float dPitch)
        {
            if (float.IsNaN(dYaw) || float.IsInfinity(dYaw) || float.IsNaN(dPitch) || float.IsInfinity(dPitch))
            {
                return;
            }
            yaw = Mathf.Repeat(yaw + dYaw, 360f);
            pitch = Mathf.Clamp(pitch + dPitch, MinPitch, MaxPitch);
        }
    }
}
