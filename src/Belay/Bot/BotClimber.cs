using System;
using System.Collections.Generic;
using Belay.Players;
using Steamworks;
using UnityEngine;

namespace Belay.Bot
{
    /// <summary>How a climbing bot paces itself.</summary>
    public enum BotStyle
    {
        /// <summary>Slower, stays further back, rests on ledges early, only makes short jumps.</summary>
        Careful,
        /// <summary>The default.</summary>
        Steady,
        /// <summary>Fast, stays close, takes longer jumps and rests late.</summary>
        Bold,
    }

    /// <summary>
    /// A bot's own body. It walks, jumps, falls and climbs with gravity and the level's collision, grabs the
    /// same holds you can (anything tagged Handhold), and each hand has grip stamina that drains while it
    /// hangs and comes back when it rests, like yours. It is told where to go (TestBot picks the goal) and
    /// works out how to get there itself.
    /// </summary>
    public class BotClimber
    {
        private const float Radius = RemoteBody.Radius;
        private static float HalfHeight => RemoteBody.Height * 0.5f;
        private const float Skin = 0.03f;
        private const float Gravity = 16f;
        private const float ReachRange = 1.45f;
        private const float MaxSpan = 1.7f;
        private const float ShoulderHeight = 0.45f;
        private const float HangBelow = 0.62f;
        private const float WallGap = 0.36f;
        private const float HandMoveTime = 0.26f;
        public const float GripMax = 10f;

        public class Hand
        {
            public bool Holding;
            public bool Moving;
            public Transform Hold;
            public Vector3 LocalPoint;
            public Vector3 LocalNormal;
            public float Grip = GripMax;
            public float MoveT;
            public Vector3 MoveFrom;
            /// <summary>Where the hand is drawn (world space of this PC).</summary>
            public Vector3 Shown;

            public bool HasHold => Hold != null && Hold.gameObject.activeInHierarchy;
            public Vector3 Point => Hold.TransformPoint(LocalPoint);
            public Vector3 Normal
            {
                get
                {
                    Vector3 n = Hold.TransformDirection(LocalNormal);
                    return n.sqrMagnitude > 0.0001f ? n.normalized : Vector3.back;
                }
            }

            public void Release()
            {
                Holding = false;
                Moving = false;
                Hold = null;
            }
        }

        private struct HoldHit
        {
            public Transform Transform;
            public Vector3 Point;
            public Vector3 Normal;
        }

        public readonly SteamId Self;
        /// <summary>Capsule center, in shared space.</summary>
        public Vector3 Shared;
        public Vector3 Velocity;
        public float Yaw;
        public float Pitch;
        public readonly Hand[] Hands = { new Hand(), new Hand() };

        public bool Grounded { get; private set; }
        public bool Mantling => mantleT >= 0f;
        public bool Hanging => Hands[0].Holding || Hands[1].Holding;
        public bool Falling => !Grounded && !Hanging && !Mantling && !Grabbed;
        public float GripFraction => Mathf.Min(Hands[0].Grip, Hands[1].Grip) / GripMax;

        /// <summary>Raised for fall damage (amount, cause) and for deadly falls.</summary>
        public Action<float, string> Hurt;
        public Action<string> Kill;

        /// <summary>
        /// No hold within reach on the way: the bot may make one (a piton, a thrown rebar). Gets the
        /// shoulder, the goal and the way into the wall (world space); returns true if it made one.
        /// </summary>
        public Func<Vector3, Vector3, Vector3, bool> MakeHold;
        private float makeHoldCooldown;

        /// <summary>A barnacle's tongue has it: it's hauled up to this point and can't climb.</summary>
        public Transform GrabbedBy;
        public bool Grabbed => GrabbedBy != null;

        private float fallStartY;
        private float airTime;
        private float reachTimer;
        private float catchCooldown;
        private float mantleT = -1f;
        private Vector3 mantleFrom;
        private Vector3 mantleTo;
        private Vector3 lastWallNormal = Vector3.zero;

        public BotClimber(SteamId self)
        {
            Self = self;
        }

        /// <summary>Put the body somewhere, standing or about to fall, with fresh hands.</summary>
        public void Place(Vector3 shared, float yaw)
        {
            Shared = shared;
            Velocity = Vector3.zero;
            Yaw = yaw;
            Pitch = 0f;
            mantleT = -1f;
            GrabbedBy = null;
            airTime = 0f;
            fallStartY = WorldSpace.ToLocal(shared).y;
            lastWallNormal = Vector3.zero;
            foreach (Hand h in Hands)
            {
                h.Release();
                h.Grip = GripMax;
            }
            Vector3 local = WorldSpace.ToLocal(shared);
            Hands[0].Shown = RestHand(local, 0);
            Hands[1].Shown = RestHand(local, 1);
        }

        public void ReleaseAll()
        {
            if (Hanging)
            {
                fallStartY = WorldSpace.ToLocal(Shared).y;
            }
            Hands[0].Release();
            Hands[1].Release();
            catchCooldown = 0.6f;
        }

        /// <summary>A hit from a teammate: a hard one tears it off the wall.</summary>
        public void Knock(Vector3 velocity)
        {
            if (Hanging && velocity.magnitude > 7f)
            {
                ReleaseAll();
            }
            if (!Hanging)
            {
                Grounded = false;
                Velocity += velocity;
                mantleT = -1f;
            }
        }

        // ---------------------------------------------------------------- Step

        /// <summary>Move one frame toward a goal (world space of this PC). Returns false if nothing moved.</summary>
        public void Step(float dt, Vector3 goal, BotStyle style, bool idle)
        {
            Vector3 pos = WorldSpace.ToLocal(Shared);
            Depenetrate(ref pos);
            UpdateGrip(dt);
            foreach (Hand h in Hands)
            {
                if ((h.Holding || h.Moving) && !h.HasHold)
                {
                    h.Release();
                }
            }
            catchCooldown -= dt;

            makeHoldCooldown -= dt;
            if (Grabbed)
            {
                StepGrabbed(dt, ref pos);
            }
            else if (Mantling)
            {
                StepMantle(dt, ref pos);
            }
            else if (Hanging)
            {
                StepHanging(dt, ref pos, goal, style, idle);
            }
            else
            {
                bool below = GroundBelow(pos, 0.12f, out RaycastHit ground);
                Grounded = Velocity.y <= 0.5f && below && ground.normal.y > 0.6f;
                if (Grounded)
                {
                    pos.y = Mathf.Max(pos.y, ground.point.y + HalfHeight + Skin);
                    if (airTime > 0f)
                    {
                        Land(pos);
                    }
                    Velocity = Vector3.zero;
                    airTime = 0f;
                    fallStartY = pos.y;
                    StepGround(dt, ref pos, goal, style, idle);
                }
                else
                {
                    StepAir(dt, ref pos, goal);
                }
            }

            UpdateHands(dt, pos);
            Shared = WorldSpace.ToShared(pos);
        }

        private void UpdateGrip(float dt)
        {
            int hanging = (Hands[0].Holding ? 1 : 0) + (Hands[1].Holding ? 1 : 0);
            foreach (Hand h in Hands)
            {
                if (h.Holding)
                {
                    h.Grip -= (hanging == 2 ? 0.5f : 1f) * dt;
                    if (h.Grip <= 0f)
                    {
                        h.Grip = 0f;
                        h.Release();
                        if (!Hanging)
                        {
                            fallStartY = WorldSpace.ToLocal(Shared).y;
                            catchCooldown = 1.5f;
                        }
                    }
                }
                else
                {
                    h.Grip = Mathf.Min(GripMax, h.Grip + (Grounded ? 3f : 1.2f) * dt);
                }
            }
        }

        private void StepGround(float dt, ref Vector3 pos, Vector3 goal, BotStyle style, bool idle)
        {
            Vector3 to = goal - pos;
            Vector3 flat = new Vector3(to.x, 0f, to.z);
            float distance = flat.magnitude;
            if (idle || distance < 0.35f)
            {
                if (!idle && to.y > 1.1f)
                {
                    TryStartClimb(pos, goal, Facing());
                }
                return;
            }
            Vector3 dir = flat / distance;
            Yaw = Mathf.MoveTowardsAngle(Yaw, Quaternion.LookRotation(dir).eulerAngles.y, 360f * dt);
            Pitch = Mathf.MoveTowardsAngle(Pitch, 10f, 90f * dt);

            if (!GroundAhead(pos, dir, 0.55f, out float drop))
            {
                bool goalBelow = to.y < -1.2f;
                if (goalBelow && drop < 7f)
                {
                    // Step off toward a goal below.
                }
                else if (TryJump(pos, dir, goal, style))
                {
                    return;
                }
                else
                {
                    // Edge: don't walk off. Holds may lead on from here.
                    TryStartClimb(pos, goal, dir);
                    return;
                }
            }

            float speed = (style == BotStyle.Careful ? 2.3f : style == BotStyle.Bold ? 3.8f : 3f) * Mathf.Clamp01(distance + 0.3f);
            Vector3 want = dir * speed * dt;
            Vector3 moved = MoveSlide(ref pos, want);
            if (moved.magnitude < want.magnitude * 0.35f)
            {
                if (TryStepUp(ref pos, dir))
                {
                    return;
                }
                if (to.y > -0.5f && TryStartClimb(pos, goal, dir))
                {
                    return;
                }
                if (to.y > 0.2f && to.y < 1.4f)
                {
                    // A ledge about waist high: hop up onto it.
                    Velocity = dir * 2.2f + Vector3.up * 6.2f;
                    Grounded = false;
                    airTime = 0.001f;
                    return;
                }
            }
            SnapDown(ref pos);
        }

        private void StepAir(float dt, ref Vector3 pos, Vector3 goal)
        {
            airTime += dt;
            if (airTime > 9f)
            {
                Kill?.Invoke("fell into the void");
                return;
            }
            Velocity.y = Mathf.Max(Velocity.y - Gravity * dt, -45f);
            // A little air control, like yours.
            Vector3 flat = goal - pos;
            flat.y = 0f;
            if (flat.sqrMagnitude > 0.04f)
            {
                Vector3 horizontal = new Vector3(Velocity.x, 0f, Velocity.z);
                horizontal = Vector3.MoveTowards(horizontal, flat.normalized * Mathf.Max(horizontal.magnitude, 1.5f), 2.5f * dt);
                Velocity.x = horizontal.x;
                Velocity.z = horizontal.z;
            }
            Vector3 before = pos;
            MoveSlide(ref pos, Velocity * dt, true);
            Vector3 actual = (pos - before) / Mathf.Max(dt, 0.0001f);
            // Lose speed into whatever it hit.
            if (Velocity.y < 0f && actual.y > Velocity.y * 0.5f)
            {
                Velocity.y = Mathf.Max(Velocity.y, actual.y);
            }
            Velocity.x = Mathf.Lerp(Velocity.x, actual.x, 0.5f);
            Velocity.z = Mathf.Lerp(Velocity.z, actual.z, 0.5f);

            // Grab on while falling, like you can.
            if (Velocity.y < -2.5f && catchCooldown <= 0f && Hands[0].Grip > 1.5f && Hands[1].Grip > 1.5f)
            {
                Vector3 shoulder = pos + Vector3.up * ShoulderHeight;
                if (FindHold(shoulder, shoulder + Vector3.up, Facing(), 0.9f, null, out HoldHit hold))
                {
                    Attach(Hands[0], hold);
                    if (FindHold(shoulder, shoulder + Vector3.up, -hold.Normal, 0.9f, p => Vector3.Distance(p, hold.Point) > 0.2f && Vector3.Distance(p, hold.Point) < MaxSpan, out HoldHit second))
                    {
                        Attach(Hands[1], second);
                    }
                    float fell = fallStartY - pos.y;
                    Velocity = Vector3.zero;
                    airTime = 0f;
                    fallStartY = pos.y;
                    if (fell > 9f)
                    {
                        // Catching a long fall costs grip, like yours.
                        Hands[0].Grip = Mathf.Max(1f, Hands[0].Grip - fell * 0.2f);
                        Hands[1].Grip = Mathf.Max(1f, Hands[1].Grip - fell * 0.2f);
                    }
                }
            }
        }

        private void Land(Vector3 pos)
        {
            float fell = fallStartY - pos.y;
            if (fell > 22f)
            {
                Kill?.Invoke("a fall");
            }
            else if (fell > 7f)
            {
                Hurt?.Invoke((fell - 7f) * 0.6f, "a fall");
            }
        }

        private void StepHanging(float dt, ref Vector3 pos, Vector3 goal, BotStyle style, bool idle)
        {
            Vector3 anchor = Vector3.zero;
            Vector3 normal = Vector3.zero;
            int count = 0;
            foreach (Hand h in Hands)
            {
                if (h.Holding)
                {
                    anchor += h.Point;
                    normal += h.Normal;
                    count++;
                }
            }
            anchor /= count;
            // Only the wall's facing matters here; a hold on top of a ledge points up, so fall back to the
            // way it's facing.
            normal.y = 0f;
            normal = normal.sqrMagnitude > 0.04f ? normal.normalized : lastWallNormal.sqrMagnitude > 0.5f ? lastWallNormal : -Facing();
            lastWallNormal = normal;
            Grounded = false;
            Velocity = Vector3.zero;
            airTime = 0f;
            fallStartY = pos.y;

            Vector3 bodyTarget = anchor - Vector3.up * HangBelow + normal * WallGap;
            float speed = style == BotStyle.Careful ? 1.7f : style == BotStyle.Bold ? 2.8f : 2.2f;
            MoveSlide(ref pos, Vector3.MoveTowards(pos, bodyTarget, speed * dt) - pos);
            Vector3 into = -normal;
            {
                Yaw = Mathf.MoveTowardsAngle(Yaw, Quaternion.LookRotation(into).eulerAngles.y, 270f * dt);
            }
            Pitch = Mathf.MoveTowardsAngle(Pitch, goal.y > pos.y + 0.5f ? -35f : 25f, 120f * dt);

            float rest = style == BotStyle.Careful ? 0.5f : style == BotStyle.Bold ? 0.2f : 0.35f;
            bool tired = GripFraction < rest;
            Vector3 to = goal - pos;

            // Goal well below and ground close under it: let go.
            if (!idle && to.y < -1.5f && GroundBelow(pos, 3f, out _))
            {
                ReleaseAll();
                return;
            }
            // Pull up onto a ledge when the way goes up, when idle, or to rest.
            if ((idle || tired || to.y > -0.6f) && TryMantle(pos, anchor, normal))
            {
                return;
            }
            if (idle)
            {
                return;
            }

            reachTimer -= dt;
            if (reachTimer > 0f || Hands[0].Moving || Hands[1].Moving)
            {
                return;
            }
            reachTimer = style == BotStyle.Careful ? 0.45f : style == BotStyle.Bold ? 0.18f : 0.3f;
            Reach(pos, goal, into);
        }

        private void Reach(Vector3 pos, Vector3 bodyGoal, Vector3 into)
        {
            // Hands go where the shoulders will be when the body gets there.
            Vector3 goal = bodyGoal + Vector3.up * 0.55f;
            Vector3 shoulder = pos + Vector3.up * ShoulderHeight;
            // Move the hand that's furthest from where it's going first (or a free hand).
            int first = Hands[0].Holding && Hands[1].Holding
                ? (Vector3.Distance(Hands[0].Point, goal) >= Vector3.Distance(Hands[1].Point, goal) ? 0 : 1)
                : Hands[0].Holding ? 1 : 0;
            for (int k = 0; k < 2; k++)
            {
                int i = k == 0 ? first : 1 - first;
                Hand hand = Hands[i];
                Hand other = Hands[1 - i];
                // Never let go with the last hand that's holding on.
                if (hand.Holding && !other.Holding)
                {
                    continue;
                }
                float current = hand.Holding ? Vector3.Distance(hand.Point, goal) : float.MaxValue;
                Vector3 otherPoint = other.Holding ? other.Point : shoulder;
                bool Accept(Vector3 p)
                {
                    float span = Vector3.Distance(p, otherPoint);
                    if (other.Holding && (span > MaxSpan || span < 0.15f))
                    {
                        return false;
                    }
                    // Left hand stays left of the right one (roughly), so arms don't cross.
                    Vector3 right = Vector3.Cross(Vector3.up, into).normalized;
                    float side = Vector3.Dot(p - otherPoint, right);
                    if (other.Holding && (i == 0 ? side > 0.45f : side < -0.45f))
                    {
                        return false;
                    }
                    return !hand.Holding || Vector3.Distance(p, goal) < current - 0.2f;
                }
                if (FindHold(shoulder, goal, into, ReachRange, Accept, out HoldHit hold))
                {
                    StartMove(hand, hold);
                    return;
                }
            }
            TryMakeHold(shoulder, goal, into);
        }

        private void TryMakeHold(Vector3 shoulder, Vector3 goal, Vector3 into)
        {
            if (MakeHold == null || makeHoldCooldown > 0f || Vector3.Distance(shoulder, goal) < 1.2f)
            {
                return;
            }
            makeHoldCooldown = 2.5f;
            if (MakeHold(shoulder, goal, into))
            {
                // The next reach finds it.
                reachTimer = 0.4f;
            }
        }

        private void StepGrabbed(float dt, ref Vector3 pos)
        {
            if (Hanging)
            {
                Hands[0].Release();
                Hands[1].Release();
            }
            mantleT = -1f;
            Grounded = false;
            Velocity = Vector3.zero;
            airTime = 0f;
            fallStartY = pos.y;
            Vector3 mouth = GrabbedBy.position - Vector3.up * (HalfHeight + 0.2f);
            pos = Vector3.MoveTowards(pos, mouth, 2.2f * dt);
            Pitch = Mathf.MoveTowardsAngle(Pitch, -60f, 180f * dt);
        }

        private void StepMantle(float dt, ref Vector3 pos)
        {
            mantleT += dt / 0.55f;
            float t = Mathf.Clamp01(mantleT);
            // Up first, then in over the edge.
            Vector3 up = new Vector3(mantleFrom.x, mantleTo.y, mantleFrom.z);
            pos = t < 0.6f ? Vector3.Lerp(mantleFrom, up, t / 0.6f) : Vector3.Lerp(up, mantleTo, (t - 0.6f) / 0.4f);
            Pitch = Mathf.MoveTowardsAngle(Pitch, 30f, 120f * dt);
            if (t >= 1f)
            {
                mantleT = -1f;
                Grounded = true;
                fallStartY = pos.y;
            }
        }

        // ---------------------------------------------------------------- Moves

        private bool TryStartClimb(Vector3 pos, Vector3 goal, Vector3 into)
        {
            if (Hands[0].Moving || Hands[1].Moving || Hands[0].Grip < 2f || Hands[1].Grip < 2f)
            {
                return false;
            }
            reachTimer -= Time.deltaTime;
            if (reachTimer > 0f)
            {
                return false;
            }
            reachTimer = 0.3f;
            into.y = 0f;
            if (into.sqrMagnitude < 0.01f)
            {
                into = Facing();
            }
            into.Normalize();
            Vector3 shoulder = pos + Vector3.up * ShoulderHeight;
            float feet = pos.y - HalfHeight;
            if (FindHold(shoulder, goal, into, ReachRange, p => p.y > feet + 1.5f, out HoldHit hold))
            {
                StartMove(Hands[0], hold);
                return true;
            }
            if (goal.y > feet + 2.2f)
            {
                TryMakeHold(shoulder, goal, into);
            }
            return false;
        }

        private bool TryMantle(Vector3 pos, Vector3 anchor, Vector3 normal)
        {
            Vector3 inward = -new Vector3(normal.x, 0f, normal.z).normalized;
            if (inward.sqrMagnitude < 0.01f)
            {
                return false;
            }
            Vector3 probe = anchor + Vector3.up * (HalfHeight + 0.6f) + inward * 0.5f;
            if (!CastDown(probe, HalfHeight + 1.2f, out RaycastHit ground) || ground.normal.y < 0.7f
                || Mathf.Abs(ground.point.y - anchor.y) > 0.55f)
            {
                return false;
            }
            Vector3 stand = ground.point + Vector3.up * (HalfHeight + Skin * 2f);
            if (!Fits(stand) || !Fits(new Vector3(pos.x, stand.y, pos.z)))
            {
                return false;
            }
            mantleFrom = pos;
            mantleTo = stand;
            mantleT = 0f;
            Hands[0].Release();
            Hands[1].Release();
            return true;
        }

        private bool TryStepUp(ref Vector3 pos, Vector3 dir)
        {
            const float Step = 0.5f;
            Vector3 raised = pos + Vector3.up * Step;
            if (!Fits(raised))
            {
                return false;
            }
            Vector3 ahead = raised + dir * 0.35f;
            if (!Fits(ahead) || !CastDown(ahead, Step + HalfHeight + 0.1f, out RaycastHit ground) || ground.normal.y < 0.6f)
            {
                return false;
            }
            pos = new Vector3(ahead.x, ground.point.y + HalfHeight + Skin, ahead.z);
            return true;
        }

        private bool TryJump(Vector3 pos, Vector3 dir, Vector3 goal, BotStyle style)
        {
            float max = style == BotStyle.Careful ? 2.2f : style == BotStyle.Bold ? 3.6f : 3f;
            float feet = pos.y - HalfHeight;
            float bestScore = float.MaxValue;
            Vector3 best = Vector3.zero;
            for (float d = 1.2f; d <= max + 0.01f; d += 0.6f)
            {
                Vector3 over = pos + dir * d + Vector3.up * 1.2f;
                if (!CastDown(over, 4f, out RaycastHit ground) || ground.normal.y < 0.65f)
                {
                    continue;
                }
                float rise = ground.point.y - feet;
                if (rise > 0.9f || rise < -2.5f)
                {
                    continue;
                }
                Vector3 land = ground.point + Vector3.up * (HalfHeight + Skin);
                if (!Fits(land))
                {
                    continue;
                }
                float score = Vector3.Distance(land, goal);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = land;
                }
            }
            if (bestScore == float.MaxValue || bestScore > Vector3.Distance(pos, goal) + 0.5f)
            {
                return false;
            }
            Vector3 delta = best - pos;
            Vector3 flat = new Vector3(delta.x, 0f, delta.z);
            float horizontalSpeed = 4.6f;
            float time = Mathf.Max(0.25f, flat.magnitude / horizontalSpeed);
            float vy = (delta.y + 0.5f * Gravity * time * time) / time;
            Velocity = flat / time + Vector3.up * Mathf.Min(vy, 9f);
            Grounded = false;
            airTime = 0.001f;
            fallStartY = pos.y;
            return true;
        }

        private void StartMove(Hand hand, HoldHit hold)
        {
            hand.MoveFrom = hand.Shown;
            hand.MoveT = 0f;
            hand.Moving = true;
            hand.Holding = false;
            hand.Hold = hold.Transform;
            hand.LocalPoint = hold.Transform.InverseTransformPoint(hold.Point);
            hand.LocalNormal = hold.Transform.InverseTransformDirection(hold.Normal);
        }

        private static void Attach(Hand hand, HoldHit hold)
        {
            hand.Hold = hold.Transform;
            hand.LocalPoint = hold.Transform.InverseTransformPoint(hold.Point);
            hand.LocalNormal = hold.Transform.InverseTransformDirection(hold.Normal);
            hand.Holding = true;
            hand.Moving = false;
            hand.Shown = hold.Point;
        }

        private void UpdateHands(float dt, Vector3 pos)
        {
            for (int i = 0; i < 2; i++)
            {
                Hand h = Hands[i];
                if (h.Moving && h.HasHold)
                {
                    h.MoveT += dt / HandMoveTime;
                    float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(h.MoveT));
                    h.Shown = Vector3.Lerp(h.MoveFrom, h.Point, t) + Vector3.up * Mathf.Sin(t * Mathf.PI) * 0.12f;
                    if (h.MoveT >= 1f)
                    {
                        h.Moving = false;
                        h.Holding = true;
                    }
                }
                else if (h.Holding)
                {
                    h.Shown = h.Point;
                }
                else
                {
                    Vector3 rest = Falling || Grabbed ? pos + Vector3.up * 0.55f + Side(i) * 0.3f : RestHand(pos, i);
                    h.Shown = Vector3.Lerp(h.Shown, rest, 1f - Mathf.Exp(-12f * dt));
                }
            }
        }

        private Vector3 Facing()
        {
            return Quaternion.Euler(0f, Yaw, 0f) * Vector3.forward;
        }

        private Vector3 Side(int hand)
        {
            return Quaternion.Euler(0f, Yaw, 0f) * (hand == 0 ? Vector3.left : Vector3.right);
        }

        private Vector3 RestHand(Vector3 pos, int hand)
        {
            return pos + Vector3.up * 0.2f + Facing() * 0.4f + Side(hand) * 0.22f;
        }

        // ---------------------------------------------------------------- Physics queries

        private static int bodyMask = -1;
        private static int holdMask = -1;

        /// <summary>What a player's body collides with.</summary>
        private static int BodyMask
        {
            get
            {
                if (bodyMask == -1 && ENT_Player.playerObject != null)
                {
                    int layer = ENT_Player.playerObject.gameObject.layer;
                    int mask = 0;
                    for (int i = 0; i < 32; i++)
                    {
                        if (!Physics.GetIgnoreLayerCollision(layer, i))
                        {
                            mask |= 1 << i;
                        }
                    }
                    bodyMask = mask;
                }
                return bodyMask == -1 ? Physics.DefaultRaycastLayers : bodyMask;
            }
        }

        /// <summary>What a player's hands can reach for.</summary>
        private static int HoldMask
        {
            get
            {
                if (holdMask == -1 && ENT_Player.playerObject != null)
                {
                    holdMask = ENT_Player.playerObject.interactMask.value;
                }
                return holdMask == -1 || holdMask == 0 ? Physics.DefaultRaycastLayers : holdMask;
            }
        }

        private bool IsMe(Collider c)
        {
            RemoteBody body = RemoteBody.Of(c);
            return body != null && body.Owner != null && body.Owner.SteamId == Self;
        }

        private bool Solid(Collider c)
        {
            return c != null && !c.isTrigger && !IsMe(c);
        }

        private static bool IsHold(Collider c)
        {
            if (c == null || RemoteBody.Of(c) != null || c.GetComponentInParent<ENT_Player>() != null)
            {
                return false;
            }
            ObjectTagger tagger = c.GetComponent<ObjectTagger>();
            if (tagger == null || !tagger.HasTag("Handhold"))
            {
                return false;
            }
            CL_Handhold handhold = c.GetComponent<CL_Handhold>();
            return handhold == null || handhold.active;
        }

        private bool CapsuleSweep(Vector3 center, Vector3 dir, float distance, out RaycastHit best)
        {
            float half = HalfHeight - Radius;
            best = default;
            float nearest = float.MaxValue;
            foreach (RaycastHit hit in Physics.CapsuleCastAll(center - Vector3.up * half, center + Vector3.up * half, Radius, dir,
                distance + Skin, BodyMask, QueryTriggerInteraction.Ignore))
            {
                if (!Solid(hit.collider) || hit.distance <= 0f && hit.point == Vector3.zero)
                {
                    continue;
                }
                if (hit.distance < nearest)
                {
                    nearest = hit.distance;
                    best = hit;
                }
            }
            return nearest < float.MaxValue;
        }

        /// <summary>Move with collision, sliding along what it hits. Returns how far it really went.</summary>
        private Vector3 MoveSlide(ref Vector3 pos, Vector3 delta, bool airborne = false)
        {
            Vector3 start = pos;
            for (int i = 0; i < 3; i++)
            {
                float distance = delta.magnitude;
                if (distance < 0.0001f)
                {
                    break;
                }
                Vector3 dir = delta / distance;
                if (!CapsuleSweep(pos, dir, distance, out RaycastHit hit))
                {
                    pos += delta;
                    break;
                }
                float go = Mathf.Max(0f, hit.distance - Skin);
                pos += dir * go;
                delta = Vector3.ProjectOnPlane(delta - dir * go, hit.normal);
                if (airborne && hit.normal.y > 0.6f && Velocity.y < 0f)
                {
                    // Landed: the next step finds the ground.
                    break;
                }
            }
            return pos - start;
        }

        private void SnapDown(ref Vector3 pos)
        {
            if (GroundBelow(pos, 0.45f, out RaycastHit ground) && ground.normal.y > 0.6f)
            {
                pos.y = ground.point.y + HalfHeight + Skin;
            }
        }

        private bool GroundBelow(Vector3 pos, float within, out RaycastHit ground)
        {
            float r = Radius * 0.9f;
            ground = default;
            float nearest = float.MaxValue;
            foreach (RaycastHit hit in Physics.SphereCastAll(pos, r, Vector3.down, HalfHeight - r + within, BodyMask, QueryTriggerInteraction.Ignore))
            {
                if (!Solid(hit.collider) || hit.distance <= 0f && hit.point == Vector3.zero)
                {
                    continue;
                }
                if (hit.distance < nearest)
                {
                    nearest = hit.distance;
                    ground = hit;
                }
            }
            return nearest < float.MaxValue;
        }

        private bool CastDown(Vector3 from, float distance, out RaycastHit ground)
        {
            ground = default;
            float nearest = float.MaxValue;
            foreach (RaycastHit hit in Physics.RaycastAll(from, Vector3.down, distance, BodyMask, QueryTriggerInteraction.Ignore))
            {
                if (Solid(hit.collider) && hit.distance < nearest)
                {
                    nearest = hit.distance;
                    ground = hit;
                }
            }
            return nearest < float.MaxValue;
        }

        /// <summary>Is there floor just ahead (and if not, how far down the next floor is)?</summary>
        private bool GroundAhead(Vector3 pos, Vector3 dir, float ahead, out float drop)
        {
            Vector3 probe = pos + dir * ahead;
            drop = float.MaxValue;
            if (CastDown(probe, HalfHeight + 8f, out RaycastHit ground))
            {
                drop = ground.distance - HalfHeight;
            }
            return drop < 0.9f;
        }

        private static readonly Collider[] overlap = new Collider[32];

        /// <summary>Would the body fit here without touching the level?</summary>
        public bool Fits(Vector3 center)
        {
            float half = HalfHeight - Radius;
            int n = Physics.OverlapCapsuleNonAlloc(center - Vector3.up * half, center + Vector3.up * half, Radius * 0.95f, overlap,
                BodyMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                if (Solid(overlap[i]))
                {
                    return false;
                }
            }
            return true;
        }

        private static CapsuleCollider probe;

        /// <summary>Push the body out of anything it ended up inside (a door shut on it, a spawn too close).</summary>
        private void Depenetrate(ref Vector3 pos)
        {
            float half = HalfHeight - Radius;
            int n = Physics.OverlapCapsuleNonAlloc(pos - Vector3.up * half, pos + Vector3.up * half, Radius * 0.9f, overlap,
                BodyMask, QueryTriggerInteraction.Ignore);
            if (n == 0)
            {
                return;
            }
            if (probe == null)
            {
                var go = new GameObject("Belay_BotProbe");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.transform.position = new Vector3(0f, -100000f, 0f);
                probe = go.AddComponent<CapsuleCollider>();
                probe.radius = Radius;
                probe.height = RemoteBody.Height;
                probe.direction = 1;
                probe.isTrigger = true;
                go.layer = 2; // Ignore Raycast
            }
            for (int i = 0; i < n; i++)
            {
                Collider c = overlap[i];
                if (!Solid(c))
                {
                    continue;
                }
                try
                {
                    if (Physics.ComputePenetration(probe, pos, Quaternion.identity, c, c.transform.position, c.transform.rotation,
                        out Vector3 dir, out float distance) && distance < 1f)
                    {
                        pos += dir * (distance + Skin);
                    }
                }
                catch (Exception)
                {
                    // Some colliders can't be tested this way; the sweeps still keep it out.
                }
            }
        }

        private static readonly float[] ReachUp = { 1.05f, 0.7f, 0.35f, 0f, -0.35f, -0.7f };
        private static readonly float[] ReachSide = { 0f, -0.35f, 0.35f, -0.7f, 0.7f };

        /// <summary>
        /// Look for a hold within reach: rays from the shoulder fanned out over the wall in front, plus
        /// straight at the goal. Picks the one that gets it closest to the goal.
        /// </summary>
        private bool FindHold(Vector3 shoulder, Vector3 goal, Vector3 into, float range, Func<Vector3, bool> accept, out HoldHit best)
        {
            best = default;
            into.y = 0f;
            if (into.sqrMagnitude < 0.0001f)
            {
                into = Facing();
            }
            into.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, into).normalized;
            float bestScore = float.MaxValue;
            HoldHit candidate;
            Vector3 toGoal = goal - shoulder;
            if (toGoal.sqrMagnitude > 0.01f && Ray(shoulder, toGoal.normalized, range, out candidate))
            {
                Consider(candidate, goal, accept, ref best, ref bestScore);
            }
            foreach (float u in ReachUp)
            {
                foreach (float s in ReachSide)
                {
                    Vector3 dir = (into * 0.8f + Vector3.up * u + right * s).normalized;
                    if (Ray(shoulder, dir, range, out candidate))
                    {
                        Consider(candidate, goal, accept, ref best, ref bestScore);
                    }
                }
            }
            return bestScore < float.MaxValue;
        }

        private static void Consider(HoldHit candidate, Vector3 goal, Func<Vector3, bool> accept, ref HoldHit best, ref float bestScore)
        {
            if (accept != null && !accept(candidate.Point))
            {
                return;
            }
            float score = Vector3.Distance(candidate.Point, goal);
            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        private bool Ray(Vector3 from, Vector3 dir, float range, out HoldHit hold)
        {
            hold = default;
            float nearest = float.MaxValue;
            RaycastHit first = default;
            foreach (RaycastHit hit in Physics.RaycastAll(from, dir, range, HoldMask | BodyMask, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider == null || IsMe(hit.collider) || hit.collider.isTrigger)
                {
                    continue;
                }
                if (hit.distance < nearest)
                {
                    nearest = hit.distance;
                    first = hit;
                }
            }
            if (nearest == float.MaxValue || !IsHold(first.collider))
            {
                return false;
            }
            hold = new HoldHit { Transform = first.collider.transform, Point = first.point, Normal = first.normal };
            return true;
        }
    }
}
