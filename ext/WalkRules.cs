// Game-free rules of the client walker (no Unity types, so tests/ links this file). The numbers are game facts measured or decompiled
// by other harnesses and listed with their source; the decisions (stall ladder, door gate, user override, pursuit) are pure functions.
using System;
using System.Collections.Generic;

namespace HubnerExt
{
    internal static class WalkFacts
    {
        public const float CapsuleRadius = 0.49f, CapsuleHeight = 1.85f;     // the player's character controller
        public const float StepUp = 0.45f;                                    // lips up to this are walked over (an assumption taken from another harness: nobody has measured the controller's real step limit)
        public const float SlideDeg = 38f;                                    // a walker slips on terrain steeper than this unless running and moving
        public const float RunClimbMaxDeg = 60f;                              // measured: banks up to 57.5 degrees are run up; over this it is a wall
        public const float HopMin = 0.40f, HopMax = 1.2f;                     // a jump clears a top this high (apex 2.21 m measured on flat ground; 1.2 m is a planning figure with margin, and it falls on a slope)
        public const float StandReach = 2.0f;                                 // the goal counts as reached only within this height of it: a stand a storey away is not the goal
        public const float StallSeconds = 2.0f;                               // no progress this long = a stall
        public const float ProgressMetres = 0.2f;                             // progress = the route ahead shrank this much and the feet moved this much
        public const int GiveUpStallsAtOneSpot = 4;
        public const float SameSpotRadius = 1.5f;
        public const int GiveUpStallsTotal = 5;
        public const float DoorAheadMetres = 2.8f;                            // only doors the line crosses this far ahead are opened
        public const float DoorLateralMetres = 1.2f;
        public const float DoorPressHoldSeconds = 2.0f;                       // the same door is not pressed again within this
        public const float TeleportJumpMetres = 30f;                          // a position jump this large is a teleport: the walk is over
        public const float RunStartMetres = 20f, RunMinStamina = 0.5f;        // run when this far from the end and this fresh
    }

    internal enum StallAction { Hop, RunUp, Rest, SideStep, Reroute, GiveUp }

    /// <summary>What to do about a stall, in order: hop only when a probe measured a hoppable top, run up a steep bank, rest for stamina, step aside, re-route, give up.</summary>
    internal static class StallLadder
    {
        /// <param name="riseAhead">measured height of the top ahead in metres, NaN when the probe found none</param>
        /// <param name="slopeDeg">slope of the ground under the feet in degrees</param>
        public static StallAction Pick(int stallsAtSpot, int stallsTotal, float riseAhead, float slopeDeg, bool routeGoesUp, bool inWater, bool hazardAhead, float staminaFrac, bool running, int sideSteps, int reroutes)
        {
            return Pick(stallsAtSpot, stallsTotal, riseAhead, slopeDeg, routeGoesUp, inWater, hazardAhead, staminaFrac, running, sideSteps, reroutes, true);
        }

        /// <param name="regenerates">false while the body cannot get stamina back (an encumbered player regenerates none): resting would wait for ever, so the walk gives up</param>
        public static StallAction Pick(int stallsAtSpot, int stallsTotal, float riseAhead, float slopeDeg, bool routeGoesUp, bool inWater, bool hazardAhead, float staminaFrac, bool running, int sideSteps, int reroutes, bool regenerates)
        {
            if (stallsAtSpot >= WalkFacts.GiveUpStallsAtOneSpot || stallsTotal >= WalkFacts.GiveUpStallsTotal) return StallAction.GiveUp;
            if (!inWater && !hazardAhead && !float.IsNaN(riseAhead) && riseAhead >= WalkFacts.HopMin && riseAhead <= WalkFacts.HopMax && staminaFrac > 0.15f) return StallAction.Hop;
            if (routeGoesUp && slopeDeg > WalkFacts.SlideDeg && slopeDeg <= WalkFacts.RunClimbMaxDeg && staminaFrac > WalkFacts.RunMinStamina && !running) return StallAction.RunUp;
            if (staminaFrac < 0.3f) return regenerates ? StallAction.Rest : StallAction.GiveUp;
            if (sideSteps < 2) return StallAction.SideStep;
            if (reroutes < 2) return StallAction.Reroute;
            return StallAction.GiveUp;
        }

        /// <summary>Progress: the remaining route shrank AND the feet moved. Pressing against a wall moves neither; a sliding body moves the feet but not the route.</summary>
        public static bool Progressed(float routeShrank, float feetMoved) { return routeShrank >= WalkFacts.ProgressMetres && feetMoved >= WalkFacts.ProgressMetres; }
    }

    internal enum DoorDecision { Press, SkipLocked, SkipNotReady, SkipOffRoute, SkipHeld, SkipUnneeded }

    /// <summary>Which doors the walker may press. Door.Interact returns true even when nothing opened (ward-denied, key missing), so "Interact returned true" is no evidence of an open door.</summary>
    internal static class DoorRules
    {
        public static bool Locked(bool wardDenies, bool needsKey, bool haveKey) { return wardDenies || (needsKey && !haveKey); }

        /// <summary>Is the door within the lateral band of the walking line and no farther ahead than <see cref="WalkFacts.DoorAheadMetres" />?</summary>
        public static bool OnLineAhead(double px, double pz, double dx, double dz, double doorX, double doorZ)
        {
            var len = Math.Sqrt(dx * dx + dz * dz); if (len < 1e-6) return false;
            dx /= len; dz /= len;
            var rx = doorX - px; var rz = doorZ - pz;
            var along = rx * dx + rz * dz;
            var side = Math.Abs(rx * -dz + rz * dx);
            return along >= 0 && along <= WalkFacts.DoorAheadMetres && side <= WalkFacts.DoorLateralMetres;
        }

        public static DoorDecision Decide(bool locked, bool leafAtRest, bool onRouteAhead, bool open, double secondsSinceLastPress)
        {
            if (locked) return DoorDecision.SkipLocked;
            if (!onRouteAhead) return DoorDecision.SkipOffRoute;
            if (open) return DoorDecision.SkipUnneeded;
            if (!leafAtRest) return DoorDecision.SkipNotReady;
            if (secondsSinceLastPress < WalkFacts.DoorPressHoldSeconds) return DoorDecision.SkipHeld;
            return DoorDecision.Press;
        }
    }

    /// <summary>
    ///     Who has the controls. The walker writes input only while the human has not touched anything for <see cref="ResumeSeconds" />; while the human drives, the walker's clocks
    ///     (stuck, timeout) must not run: <see cref="Yielded" /> returns the seconds to add to them.
    /// </summary>
    internal sealed class UserOverride
    {
        public const double ResumeSeconds = 1.5;
        private double _lastTouch = double.NegativeInfinity;
        private double _lastCheck = double.NaN;

        public void Touch(double now) { _lastTouch = now; }
        public bool Active(double now) { return now - _lastTouch < ResumeSeconds; }

        /// <summary>Call once per walker tick. While the user is active it returns the time since the previous call (to be added to every walker clock), else 0.</summary>
        public double Yielded(double now)
        {
            var dt = double.IsNaN(_lastCheck) ? 0 : Math.Max(0, now - _lastCheck);
            _lastCheck = now;
            return Active(now) ? dt : 0;
        }
    }

    internal enum ArriveState { Far, Arrived, WrongStorey }

    internal static class Ledge
    {
        /// <summary>
        ///     Height of a top ahead above the ground the feet stand on. A top measured from the feet includes the slope itself: on a plain 30 degree bank the ground one metre ahead is 0.58 m higher,
        ///     which is not a ledge. <paramref name="slopeRisePerMetre" /> is the ground's rise per metre along the walking direction.
        /// </summary>
        public static double Rise(double topAboveFeet, double distanceAhead, double slopeRisePerMetre) { return topAboveFeet - distanceAhead * Math.Max(0, slopeRisePerMetre); }

        /// <summary>Rise per metre along a direction on ground with this normal (positive uphill).</summary>
        public static double SlopeAlong(double nx, double ny, double nz, double dirX, double dirZ)
        {
            if (ny < 1e-6) return double.PositiveInfinity;
            var len = Math.Sqrt(dirX * dirX + dirZ * dirZ); if (len < 1e-9) return 0;
            return -(nx * dirX + nz * dirZ) / len / ny;
        }

        /// <summary>The side to step to when blocked: away from a hazard (a portal trigger, a lit fire) first, then toward the side with more room. +1 left, -1 right.</summary>
        public static int SideStepSide(double clearLeft, double clearRight, bool hazardLeft, bool hazardRight)
        {
            if (hazardLeft != hazardRight) return hazardLeft ? -1 : 1;
            return clearLeft >= clearRight ? 1 : -1;
        }
    }

    internal static class Pursuit
    {
        /// <summary>
        ///     The goal is reached when the feet are within <paramref name="radius" /> flat of it AND within <paramref name="height" /> of its height. A stand five metres below a bed is not the bed;
        ///     without the height test the walker arrived "under the floor" and spun in place. A NaN dy (the goal came without a y) or a height of 0 skips the height test.
        /// </summary>
        public static ArriveState Arrival(double flat, double dy, double radius, double height)
        {
            if (flat >= radius) return ArriveState.Far;
            if (height <= 0 || double.IsNaN(dy)) return ArriveState.Arrived;
            return Math.Abs(dy) <= height ? ArriveState.Arrived : ArriveState.WrongStorey;
        }

        /// <summary>How far ahead on the line the body aims: more at speed, never a raw waypoint. clamp(0.9 + 0.3 v, 1.0, 2.4) metres.</summary>
        public static double LookAhead(double speed) { return Math.Max(1.0, Math.Min(2.4, 0.9 + 0.3 * speed)); }

        /// <summary>
        ///     The point to steer at: the feet are projected on the polyline (progress only moves forward) and the point <paramref name="lookAhead" /> metres further along it is returned.
        ///     <paramref name="seg" /> and <paramref name="t" /> carry the monotone progress between calls.
        /// </summary>
        public static void Aim(IList<double[]> pts, ref int seg, ref double t, double x, double z, double lookAhead, out double ax, out double az)
        {
            ax = pts[pts.Count - 1][0]; az = pts[pts.Count - 1][1];
            if (pts.Count < 2) return;
            // project onto the current and following segments, keep the furthest progress that is still near the feet
            for (var s = seg; s < pts.Count - 1 && s <= seg + 2; s++)
            {
                var a = pts[s]; var b = pts[s + 1];
                var vx = b[0] - a[0]; var vz = b[1] - a[1]; var l2 = vx * vx + vz * vz; if (l2 < 1e-9) continue;
                var u = Math.Max(0, Math.Min(1, ((x - a[0]) * vx + (z - a[1]) * vz) / l2));
                if (s == seg && u < t) u = t;
                if (s > seg && u <= 0.0) break;                         // not on the next segment yet
                var px = a[0] + vx * u - x; var pz = a[1] + vz * u - z;
                if (px * px + pz * pz > 9.0 && s > seg) break;          // the next segment is more than 3 m away: the feet have not got there
                seg = s; t = u;
            }
            // walk lookAhead metres along the line from (seg, t)
            var remaining = lookAhead; var cs = seg; var ct = t;
            while (cs < pts.Count - 1)
            {
                var a = pts[cs]; var b = pts[cs + 1];
                var vx = b[0] - a[0]; var vz = b[1] - a[1]; var len = Math.Sqrt(vx * vx + vz * vz);
                var left = len * (1 - ct);
                if (remaining <= left && len > 1e-9) { var u = ct + remaining / len; ax = a[0] + vx * u; az = a[1] + vz * u; return; }
                remaining -= left; cs++; ct = 0;
            }
        }
    }

    internal static class TeleportGuard
    {
        /// <summary>A position jump of 30 m or more in one tick is a teleport (portal, respawn): the walk is over.</summary>
        public static bool Jumped(double dx, double dy, double dz) { return Math.Sqrt(dx * dx + dy * dy + dz * dz) >= WalkFacts.TeleportJumpMetres; }
    }

    internal static class RunRules
    {
        /// <summary>Run when the end is far and stamina is fresh, hold it down to 8 m left, drop it under half stamina; always run a steep bank the route climbs.</summary>
        public static bool WantRun(bool wasRunning, double metresLeft, double staminaFrac, double slopeDeg, bool routeGoesUp, bool userAskedRun)
        {
            if (slopeDeg > WalkFacts.SlideDeg && slopeDeg <= WalkFacts.RunClimbMaxDeg && routeGoesUp && staminaFrac > WalkFacts.RunMinStamina) return true;
            if (staminaFrac < WalkFacts.RunMinStamina) return false;
            if (wasRunning) return metresLeft > 8;
            return userAskedRun || metresLeft > WalkFacts.RunStartMetres;
        }
    }
}
