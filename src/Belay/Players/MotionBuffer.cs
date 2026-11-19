using System.Collections.Generic;
using Belay.Net;
using UnityEngine;

namespace Belay.Players
{
    /// <summary>
    /// Plays a teammate's snapshots back smoothly: drawn a short self-adjusting delay in the past on a curve
    /// through nearby snapshots (head turns too). If packets stop, the body coasts briefly instead of freezing.
    /// </summary>
    public class MotionBuffer
    {
        /// <summary>One snapshot as drawn on this PC, with its world positions resolved this frame.</summary>
        public struct Frame
        {
            public Vector3 Body;
            public Vector3 LeftHand;
            public Vector3 RightHand;
            public float Yaw;
            public float Pitch;
            /// <summary>Movement of the body relative to whatever it stands on, in meters per second.</summary>
            public Vector3 Velocity;
            /// <summary>The snapshot whose flags (grips, crouch, held items...) apply now.</summary>
            public PlayerState State;
        }

        private struct Snapshot
        {
            public PlayerState State;
            public float Time;
        }

        private const int Capacity = 32;
        /// <summary>Furthest the body coasts past the newest snapshot before it waits.</summary>
        private const float MaxExtrapolate = 0.15f;
        /// <summary>A jump this big between snapshots is a teleport (revive, dev teleport), not movement.</summary>
        private const float TeleportDistance = 8f;

        private readonly List<Snapshot> snapshots = new List<Snapshot>(Capacity);
        private float interval = 0.05f;
        private float jitter;
        private float playback;
        private bool started;

        /// <summary>How far behind the newest snapshot the body is drawn.</summary>
        public float Delay => Mathf.Clamp(interval * 1.3f + jitter * 2.5f + 0.01f, 0.04f, 0.3f);

        public void Add(PlayerState state, float now)
        {
            float time = now;
            if (snapshots.Count > 0)
            {
                float last = snapshots[snapshots.Count - 1].Time;
                float gap = now - last;
                if (gap > 0.002f && gap < 0.5f)
                {
                    jitter = Mathf.Lerp(jitter, Mathf.Abs(gap - interval), 0.1f);
                    interval = Mathf.Lerp(interval, gap, 0.1f);
                }
                // Packets that land in the same frame still get spread out a little.
                time = Mathf.Max(now, last + interval * 0.25f);
                if (gap > 1f)
                {
                    // Back from a stall: don't play the stale history.
                    snapshots.Clear();
                    started = false;
                }
            }
            if (snapshots.Count == Capacity)
            {
                snapshots.RemoveAt(0);
            }
            snapshots.Add(new Snapshot { State = state, Time = time });
        }

        /// <summary>Drops the history so the next frame draws the newest snapshot as it is.</summary>
        public void Reset()
        {
            if (snapshots.Count > 1)
            {
                snapshots.RemoveRange(0, snapshots.Count - 1);
            }
            started = false;
        }

        /// <summary>Advances the playback clock by <paramref name="dt"/> and samples the body at it.</summary>
        public Frame Sample(float now, float dt)
        {
            float target = now - Delay;
            if (!started || Mathf.Abs(target - playback) > 0.5f)
            {
                playback = target;
                started = true;
            }
            else
            {
                // Run the clock a touch fast or slow to settle on the target delay without a visible jump.
                float error = target - playback;
                playback += dt * (1f + Mathf.Clamp(error * 4f, -0.15f, 0.15f));
            }

            // Old snapshots are only needed as the curve's lead-in.
            while (snapshots.Count > 3 && snapshots[1].Time < playback - 0.05f)
            {
                snapshots.RemoveAt(0);
            }

            int count = snapshots.Count;
            Snapshot newest = snapshots[count - 1];
            if (count == 1 || playback >= newest.Time)
            {
                return Extrapolate(count, playback - newest.Time);
            }
            if (playback <= snapshots[0].Time)
            {
                return Resolve(snapshots[0].State, Vector3.zero);
            }

            int b = 1;
            while (snapshots[b].Time < playback)
            {
                b++;
            }
            int a = b - 1;
            Snapshot sa = snapshots[a], sb = snapshots[b];
            float span = Mathf.Max(sb.Time - sa.Time, 1e-4f);
            float u = Mathf.Clamp01((playback - sa.Time) / span);

            Vector3 p1 = Body(sa.State), p2 = Body(sb.State);
            if ((p2 - p1).sqrMagnitude > TeleportDistance * TeleportDistance)
            {
                // A teleport: be there at once instead of flying across the map.
                Frame jump = Resolve(sb.State, Vector3.zero);
                return jump;
            }
            Vector3 segment = (p2 - p1) / span;
            Vector3 m1 = Tangent(a, p1, p2, span, segment);
            Vector3 m2 = Tangent(b, p1, p2, span, segment);
            Vector3 body = Hermite(p1, p2, m1 * span, m2 * span, u);
            Vector3 velocity = HermiteSlope(p1, p2, m1 * span, m2 * span, u) / span;

            PlayerState flags = u < 0.5f ? sa.State : sb.State;
            return new Frame
            {
                Body = body,
                LeftHand = Vector3.Lerp(Hand(sa.State, 0), Hand(sb.State, 0), u),
                RightHand = Vector3.Lerp(Hand(sa.State, 1), Hand(sb.State, 1), u),
                Yaw = Mathf.LerpAngle(sa.State.Yaw, sb.State.Yaw, u),
                Pitch = Mathf.LerpAngle(sa.State.Pitch, sb.State.Pitch, u),
                Velocity = velocity,
                State = flags,
            };
        }

        /// <summary>
        /// Curve slope at snapshot <paramref name="index"/> (Catmull-Rom from neighbours), clamped so sudden stops
        /// don't overshoot into walls.
        /// </summary>
        private Vector3 Tangent(int index, Vector3 p1, Vector3 p2, float span, Vector3 segment)
        {
            int before = index - 1, after = index + 1;
            if (before < 0 || after >= snapshots.Count)
            {
                return segment;
            }
            Vector3 pBefore = Body(snapshots[before].State);
            Vector3 pAfter = Body(snapshots[after].State);
            float time = snapshots[after].Time - snapshots[before].Time;
            if (time < 1e-4f || (pAfter - pBefore).sqrMagnitude > TeleportDistance * TeleportDistance)
            {
                return segment;
            }
            Vector3 slope = (pAfter - pBefore) / time;
            float limit = segment.magnitude * 1.5f + 0.5f;
            return Vector3.ClampMagnitude(slope, limit);
        }

        private Frame Extrapolate(int count, float ahead)
        {
            Snapshot newest = snapshots[count - 1];
            Vector3 velocity = Vector3.zero;
            if (count > 1)
            {
                Snapshot previous = snapshots[count - 2];
                float span = newest.Time - previous.Time;
                if (span > 1e-3f)
                {
                    velocity = (Body(newest.State) - Body(previous.State)) / span;
                    if (velocity.sqrMagnitude > TeleportDistance * TeleportDistance * 100f)
                    {
                        velocity = Vector3.zero;
                    }
                }
            }
            Frame frame = Resolve(newest.State, velocity);
            bool hanging = newest.State.LeftHanging || newest.State.RightHanging;
            float coast = Mathf.Clamp(ahead, 0f, MaxExtrapolate);
            if (!hanging && coast > 0f && velocity.sqrMagnitude > 0.01f)
            {
                Vector3 ahead3 = frame.Body + velocity * coast;
                if (!WorldProbe.Blocked(frame.Body, ahead3))
                {
                    Vector3 shift = ahead3 - frame.Body;
                    frame.Body = ahead3;
                    frame.LeftHand += shift;
                    frame.RightHand += shift;
                }
            }
            if (ahead > MaxExtrapolate)
            {
                // Stalled: let the walk cycle wind down instead of running on the spot.
                frame.Velocity = Vector3.zero;
            }
            return frame;
        }

        private static Frame Resolve(PlayerState state, Vector3 velocity)
        {
            return new Frame
            {
                Body = Body(state),
                LeftHand = Hand(state, 0),
                RightHand = Hand(state, 1),
                Yaw = state.Yaw,
                Pitch = state.Pitch,
                Velocity = velocity,
                State = state,
            };
        }

        /// <summary>
        /// A snapshot's body position on this PC. Elevator riders are placed on our own copy of the elevator.
        /// </summary>
        public static Vector3 Body(PlayerState state)
        {
            Transform platform = PlatformRide.Resolve(state.PlatformId);
            return platform != null ? platform.TransformPoint(state.PlatformPosition) : WorldSpace.ToLocal(state.Position);
        }

        public static Vector3 Hand(PlayerState state, int index)
        {
            Transform platform = PlatformRide.Resolve(state.PlatformId);
            if (platform != null)
            {
                return platform.TransformPoint(index == 0 ? state.PlatformLeftHand : state.PlatformRightHand);
            }
            return WorldSpace.ToLocal(index == 0 ? state.LeftHand : state.RightHand);
        }

        private static Vector3 Hermite(Vector3 p1, Vector3 p2, Vector3 m1, Vector3 m2, float u)
        {
            float u2 = u * u, u3 = u2 * u;
            return (2f * u3 - 3f * u2 + 1f) * p1 + (u3 - 2f * u2 + u) * m1 + (-2f * u3 + 3f * u2) * p2 + (u3 - u2) * m2;
        }

        private static Vector3 HermiteSlope(Vector3 p1, Vector3 p2, Vector3 m1, Vector3 m2, float u)
        {
            float u2 = u * u;
            return (6f * u2 - 6f * u) * p1 + (3f * u2 - 4f * u + 1f) * m1 + (-6f * u2 + 6f * u) * p2 + (3f * u2 - 2f * u) * m2;
        }
    }
}
