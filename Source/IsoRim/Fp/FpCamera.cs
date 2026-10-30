using System;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim.Fp
{
    // The play-mode camera (fp-play-spec 7.1): one yaw and one pitch the mouse turns directly, read by IsoRim's
    // FirstPerson.Yaw, Pitch and Eye through Ports 11-13 while playing. The yaw is the body heading (the movement keys
    // walk relative to it); IsoRim's own eased walking heading and look offsets are left alone and come back unchanged on
    // leaving. Local to this player, never synced, never read by simulation code.
    //
    // M6 (spec 16): the eye offset (Port 13) carries a small lead and an optional head bob.
    // - Lead: a movement key goes to the simulation as a command, so the colonist starts walking one command round trip
    //   after the key (about 100-200 ms online). Meanwhile the eye moves ahead along the key's heading at the colonist's
    //   own walking speed, at most LeadMax cells and never into a cell it couldn't walk into, and once the colonist walks
    //   that way the lead fades out (LeadTau) while the body catches up. Off with FpSettings.eyeLead.
    // - Head bob: BobAmplitude x FpSettings.headBob cells up and down, one cycle every two cells walked, from the path
    //   follower's progress through each cell (default 0: none).
    public static class FpCamera
    {
        public const float MinPitch = -80f;   // looking up
        public const float MaxPitch = 80f;    // looking down
        public const float StartPitch = 8f;   // as IsoRim's first person starts

        public const float LeadMax = 0.35f;        // cells
        private const float LeadTau = 0.15f;       // seconds: the lead's fade once the colonist walks the key's way
        private const float SameWayDot = 0.7f;     // the colonist's step counts as the key's way within about 45 degrees
        public const float BobAmplitude = 0.012f;  // cells at FpSettings.headBob 1
        private const float BobTau = 0.15f;        // seconds: the bob fades in on starting and out on stopping

        private static float yaw;
        private static float pitch = StartPitch;

        // The eye offset, worked out once a frame (EyeOffset).
        private static int offsetFrame = -1;
        private static double offsetTime = -1;
        private static Vector3 offset;
        private static Vector3 lead;               // map x, 0, map z, in cells
        private static float bobEnvelope;          // 0 standing .. 1 walking
        private static Pawn bobPawn;
        private static IntVec3 bobCell;
        private static int bobSteps;

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

        // Lead (x, z) plus bob (y), in cells; zero while not playing. Read by FirstPerson.Eye (Port 13) every time the eye
        // is placed, so it is worked out on the first read of each frame and kept for the rest of it.
        public static Vector3 EyeOffset
        {
            get
            {
                if (FpPlay.Mode != FpMode.Playing)
                {
                    return Vector3.zero;
                }
                int frame = Time.frameCount;
                if (frame != offsetFrame)
                {
                    offsetFrame = frame;
                    try
                    {
                        UpdateOffset();
                    }
                    catch (Exception e)
                    {
                        offset = Vector3.zero;
                        lead = Vector3.zero;
                        Log.ErrorOnce("[IsoRim] First person: the camera's lead and head bob failed: " + e, 93652451);
                    }
                }
                return offset;
            }
        }

        // The lead alone (harness and HUD).
        internal static Vector3 Lead
        {
            get { return lead; }
        }

        // Play starts facing the way the colonist is drawn (MpFacing), looking a little down, as IsoRim's first person does.
        public static void Begin(float yaw)
        {
            FpCamera.yaw = Mathf.Repeat(yaw, 360f);
            pitch = StartPitch;
            offset = Vector3.zero;
            lead = Vector3.zero;
            offsetTime = -1;
            bobEnvelope = 0f;
            bobPawn = null;
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

        private static void UpdateOffset()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            float dt = offsetTime < 0 ? 0f : Mathf.Clamp((float)(now - offsetTime), 0f, 0.1f);
            offsetTime = now;
            Pawn p = FpPlay.Pawn;
            if (p == null || !p.Spawned || p.Map == null || p.pather == null)
            {
                lead = Vector3.zero;
                bobEnvelope = 0f;
                offset = Vector3.zero;
                return;
            }
            UpdateLead(p, dt);
            offset = new Vector3(lead.x, Bob(p, dt), lead.z);
        }

        // ---------------------------------------------------------------- lead

        private static void UpdateLead(Pawn p, float dt)
        {
            byte d = FpInput.LastSentIntent;
            if (!FpSettings.eyeLead)
            {
                lead = Vector3.zero;
                return;
            }
            if (d <= 15 && WillWalk(p))
            {
                float rad = d * 22.5f * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
                if (!WalkingThatWay(p, dir))
                {
                    float speed = LeadSpeed(p);
                    if (speed <= 0f)
                    {
                        return;   // paused: the eye waits with the colonist
                    }
                    if (FpStep.Choose(p, p.Position, d, 0).IsValid)
                    {
                        Vector3 next = Vector3.MoveTowards(lead, dir * LeadMax, speed * dt);
                        if (WalkableAt(p, next))
                        {
                            lead = next;
                        }
                        return;
                    }
                    // Blocked (a wall, the map edge): the colonist won't walk that way, so neither does the eye.
                }
            }
            lead *= Mathf.Exp(-dt / LeadTau);
            if (lead.sqrMagnitude < 1e-6f)
            {
                lead = Vector3.zero;
            }
        }

        // The colonist acts on a movement intent: in the control job, or on a job the intent ends (FpSync.FpSetIntent's
        // test). On the autopilot (eating, resting, a lord's activity) the intent still goes out but the colonist stays,
        // so the eye mustn't run ahead and wait there.
        private static bool WillWalk(Pawn p)
        {
            if (FpControlJob.InControlJob(p))
            {
                return true;
            }
            return p.jobs != null && FpState.AutopilotKey(p) == null && p.jobs.IsCurrentJobPlayerInterruptible();
        }

        // The colonist's current step goes the key's way.
        private static bool WalkingThatWay(Pawn p, Vector3 dir)
        {
            Pawn_PathFollower pather = p.pather;
            if (!pather.Moving)
            {
                return false;
            }
            IntVec3 next = pather.nextCell;
            IntVec3 at = p.Position;
            Vector3 v = new Vector3(next.x - at.x, 0f, next.z - at.z);
            return v.sqrMagnitude > 0f && Vector3.Dot(v.normalized, dir) > SameWayDot;
        }

        // The colonist's walking speed in cells per second of real time at this game speed (spec 16: 60 / TicksPerMoveCardinal
        // x SpeedFactor), 0 while paused.
        private static float LeadSpeed(Pawn p)
        {
            TickManager tm = Find.TickManager;
            if (tm == null || tm.Paused)
            {
                return 0f;
            }
            float ticks = p.TicksPerMoveCardinal;
            return ticks > 0f ? 60f / ticks * FpMath.SpeedFactor((int)tm.CurTimeSpeed) : 0f;
        }

        private static bool WalkableAt(Pawn p, Vector3 offsetCells)
        {
            Vector3 at = p.DrawPos + offsetCells;
            IntVec3 c = new IntVec3(Mathf.FloorToInt(at.x), 0, Mathf.FloorToInt(at.z));
            return c.InBounds(p.Map) && c.Walkable(p.Map);
        }

        // ---------------------------------------------------------------- head bob

        private static float Bob(Pawn p, float dt)
        {
            if (p != bobPawn)
            {
                bobPawn = p;
                bobCell = p.Position;
                bobSteps = 0;
            }
            if (p.Position != bobCell)
            {
                bobCell = p.Position;
                bobSteps = (bobSteps + 1) & 1023;   // only the parity of the count matters (one cycle per two cells)
            }
            float amount = Mathf.Clamp01(FpSettings.headBob);
            bool moving = p.pather.Moving;
            bobEnvelope = Mathf.Lerp(moving ? 1f : 0f, bobEnvelope, Mathf.Exp(-dt / BobTau));
            if (amount <= 0f || bobEnvelope < 0.001f)
            {
                return 0f;
            }
            float walked = bobSteps + (moving ? Mathf.Clamp01(p.pather.MovePercentage) : 0f);
            return BobAmplitude * amount * bobEnvelope * Mathf.Sin(Mathf.PI * walked);
        }
    }
}
