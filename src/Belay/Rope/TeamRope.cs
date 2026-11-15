using Belay.Net;
using Belay.Players;
using Steamworks;
using UnityEngine;

namespace Belay.Rope
{
    /// <summary>
    /// One rope between two roped teammates, simulated as a chain of points the way the game's own swinging
    /// ropes are (ENV_Rope: verlet points, gravity, drag, length constraints), but tied to a harness at each
    /// end. The points fall, drape, catch on ledges and corners, and slide over them. The rope is drawn as
    /// one smooth tube and every segment is a handhold you can grab to climb it or swing on it.
    ///
    /// Each PC simulates every rope on its own; only the players' positions are networked.
    /// </summary>
    public class TeamRope : MonoBehaviour
    {
        /// <summary>Rest distance between simulated points (meters).</summary>
        private const float NodeSpacing = 0.4f;
        private const float Radius = 0.017f;
        /// <summary>How far points are kept off surfaces.</summary>
        private const float SurfaceGap = 0.03f;
        private const float Gravity = 9.81f;
        /// <summary>Velocity kept per step (air drag).</summary>
        private const float Damping = 0.992f;
        /// <summary>Share of sliding speed lost per step while touching a surface.</summary>
        private const float Friction = 0.35f;
        private const int Iterations = 24;
        /// <summary>A held point weighs this much more than a bare rope point (it's carrying a climber).</summary>
        private const float HeldInverseMass = 0.04f;
        /// <summary>Segments next to your own harness can't be grabbed, so looking down while climbing grabs the wall.</summary>
        private const float NoGrabNearHarness = 1.2f;

        // The game's swinging ropes: swingSpeed 4 * 2.5 as push, swingDrag 20 on the hands.
        private const float SwingAccel = 9f;
        private const float SwingHandDrag = 20f;
        /// <summary>ENT_Player velocity units per m/s (it moves vel * 175 m per second), and AddForce(v) adds v / 10.</summary>
        private const float VelToMetersPerSecond = 175f;
        private const float ForceToVel = 0.1f;

        private const int Sides = 6;
        private const int Subdivisions = 3;
        /// <summary>Rope length covered by one repeat of the texture.</summary>
        private const float TextureMeters = 0.32f;

        public SteamId EndA { get; private set; }
        public SteamId EndB { get; private set; }
        public float Length { get; private set; }
        public bool Live { get; private set; }

        /// <summary>True while the local player is swinging on this rope (sprint held, like the game's ropes).</summary>
        public bool Swinging { get; private set; }

        /// <summary>The point the local player is hanging from, or -1.</summary>
        public int HeldNode { get; private set; } = -1;

        private int count;
        private float spacing;
        private Vector3[] pos, prev, safe, drawFrom, renderPoints, drawPoints;
        private float[] invMass;
        private bool initialized;
        private float lastOffset;
        private Vector3 lastA, lastB;
        private Vector3 heldVelocity;
        private readonly int[] heldPivots = new int[2];

        private RopeHold[] holds;
        private ENT_Player holdsIgnoreFor;
        private bool grabbable;

        private Mesh mesh;
        private MeshRenderer meshRenderer;
        private Vector3[] vertices, normals;
        private Vector2[] uvs;
        private int ringCount;

        public static TeamRope Create(SteamId a, SteamId b, float length)
        {
            var go = new GameObject("Belay_Rope");
            DontDestroyOnLoad(go);
            var rope = go.AddComponent<TeamRope>();
            rope.EndA = a;
            rope.EndB = b;
            rope.Build(length);
            return rope;
        }

        /// <summary>
        /// A rope that hangs from a fixed point (a plunger cup) instead of joining two players: the top is
        /// pinned to the anchor, the bottom hangs free. Anyone can climb it.
        /// </summary>
        public static TeamRope CreateHanging(Vector3 anchorShared, float length)
        {
            var go = new GameObject("Belay_HangingRope");
            DontDestroyOnLoad(go);
            var rope = go.AddComponent<TeamRope>();
            rope.hanging = true;
            rope.AnchorShared = anchorShared;
            rope.Build(length);
            return rope;
        }

        private bool hanging;

        /// <summary>For a hanging rope: its fixed top, in shared (offset-free) space.</summary>
        public Vector3 AnchorShared { get; private set; }

        public bool Hanging => hanging;

        public bool Joins(SteamId a, SteamId b) => EndA == a && EndB == b;

        public bool AttachedTo(SteamId id) => EndA == id || EndB == id;

        // ------------------------------------------------------------------ setup

        private void Build(float length)
        {
            Length = length;
            int segments = Mathf.Clamp(Mathf.CeilToInt(length / NodeSpacing), 4, 80);
            spacing = length / segments;
            count = segments + 1;
            pos = new Vector3[count];
            prev = new Vector3[count];
            safe = new Vector3[count];
            drawFrom = new Vector3[count];
            invMass = new float[count];
            initialized = false;

            if (holds != null)
            {
                foreach (RopeHold old in holds)
                {
                    if (old != null)
                    {
                        old.ForceDropHands();
                        Destroy(old.gameObject);
                    }
                }
            }
            holds = new RopeHold[segments];
            int layer = HoldLayer;
            for (int i = 0; i < segments; i++)
            {
                holds[i] = RopeHold.Create(this, i, layer);
            }
            holdsIgnoreFor = null;

            ringCount = segments * Subdivisions + 1;
            renderPoints = new Vector3[ringCount];
            BuildMesh();
        }

        public void SetLength(float length)
        {
            if (Mathf.Abs(length - Length) > 0.01f)
            {
                Build(length);
            }
        }

        /// <summary>
        /// A layer the hands' grab casts see. "Interactable No Collide" is what the game's ropes use while you
        /// swing, so the rope never shoves the player capsule around.
        /// </summary>
        private static int HoldLayer
        {
            get
            {
                ENT_Player player = ENT_Player.playerObject;
                int mask = player != null ? player.interactMask.value : 0;
                foreach (string name in new[] { "Interactable No Collide", "Interactable" })
                {
                    int layer = LayerMask.NameToLayer(name);
                    if (layer >= 0 && (mask & (1 << layer)) != 0)
                    {
                        return layer;
                    }
                }
                return PlayerContact.BodyLayer;
            }
        }

        private void BuildMesh()
        {
            if (mesh == null)
            {
                var child = new GameObject("Belay_RopeMesh");
                child.transform.SetParent(transform, false);
                child.AddComponent<MeshFilter>().sharedMesh = mesh = new Mesh { name = "Belay_Rope" };
                mesh.MarkDynamic();
                meshRenderer = child.AddComponent<MeshRenderer>();
                meshRenderer.sharedMaterial = RopeLook.Material;
            }
            int ring = Sides + 1;
            vertices = new Vector3[ringCount * ring];
            normals = new Vector3[vertices.Length];
            uvs = new Vector2[vertices.Length];
            var triangles = new int[(ringCount - 1) * Sides * 6];
            int t = 0;
            for (int r = 0; r < ringCount - 1; r++)
            {
                for (int s = 0; s < Sides; s++)
                {
                    int a = r * ring + s, b = a + 1, c = a + ring, d = c + 1;
                    triangles[t++] = a; triangles[t++] = c; triangles[t++] = b;
                    triangles[t++] = b; triangles[t++] = c; triangles[t++] = d;
                }
            }
            mesh.Clear();
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = triangles;
        }

        // ------------------------------------------------------------------ ends

        private static Vector3 LocalHarness(ENT_Player player) => player.transform.position + Vector3.down * 0.4f;

        private static bool TryEnd(SteamId id, out Vector3 point)
        {
            point = default;
            if (id == NetSession.LocalId)
            {
                ENT_Player player = ENT_Player.playerObject;
                if (player == null || player.dead)
                {
                    return false;
                }
                point = LocalHarness(player);
                return true;
            }
            RemotePlayer remote = Tether.Instance != null ? Tether.Instance.LivePlayer(id) : null;
            if (remote == null)
            {
                return false;
            }
            point = remote.RopeAnchor;
            return true;
        }

        /// <summary>Where an end is pinned. A hanging rope's bottom end is free, so it has none.</summary>
        private bool TryEndAt(int end, out Vector3 point)
        {
            if (hanging)
            {
                point = end == 0 ? WorldSpace.ToLocal(AnchorShared) : default;
                return end == 0 && WorldSpace.Ready;
            }
            return TryEnd(end == 0 ? EndA : EndB, out point);
        }

        private bool IsLocal(int end) => !hanging && (end == 0 ? EndA : EndB) == NetSession.LocalId;

        // ------------------------------------------------------------------ simulation

        /// <summary>Runs one physics step. Returns false (and hides the rope) when an end is missing.</summary>
        public bool Step(float dt, bool visible)
        {
            bool freeEnd = hanging;
            Vector3 b = default;
            if (!TryEndAt(0, out Vector3 a) || !freeEnd && !TryEndAt(1, out b))
            {
                SetLive(false, false);
                return false;
            }
            if (freeEnd)
            {
                b = initialized ? pos[count - 1] : a + Vector3.down * Length;
            }
            SetLive(true, visible);

            float offset = WorldSpace.Offset;
            if (initialized && !Mathf.Approximately(offset, lastOffset))
            {
                Shift(Vector3.up * (offset - lastOffset));
            }
            lastOffset = offset;
            if (!initialized || (a - lastA).sqrMagnitude > 64f || !freeEnd && (b - lastB).sqrMagnitude > 64f)
            {
                Reset(a, b);
            }
            lastA = a;
            lastB = b;
            System.Array.Copy(pos, drawFrom, count);

            ENT_Player player = ENT_Player.playerObject;
            int held = FindHeldNode(player);
            if (held != HeldNode && held >= 0 && HeldNode < 0)
            {
                // First grab: the rope takes the climber's momentum at the grab point.
                Vector3 v = player.GetVelocity() * VelToMetersPerSecond;
                prev[held] = pos[held] - v * dt;
            }
            HeldNode = held;

            for (int i = 0; i < count; i++)
            {
                invMass[i] = i == held ? HeldInverseMass : 1f;
            }
            invMass[0] = 0f;
            invMass[count - 1] = freeEnd ? 1f : 0f;
            int moving = freeEnd ? count : count - 1;

            Vector3 swing = held >= 0 ? SwingInput(player) : Vector3.zero;
            float g = Gravity * dt * dt;
            for (int i = 1; i < moving; i++)
            {
                Vector3 velocity = (pos[i] - prev[i]) * Damping;
                prev[i] = pos[i];
                pos[i] += velocity + Vector3.down * g;
                if (i == held)
                {
                    pos[i] += swing * (dt * dt);
                }
            }
            pos[0] = a;
            if (!freeEnd)
            {
                pos[count - 1] = b;
            }

            if (held > 0 && held < count - 1)
            {
                // Which point the climber hangs from on each side: the far end, or the ledge in between.
                // Our own harness doesn't count: the rope between it and our hands is slack.
                for (int end = 0; end < 2; end++)
                {
                    heldPivots[end] = IsLocal(end) || freeEnd && end == 1 ? -1 : PivotIndex(held, pos[held], end == 0 ? 0 : count - 1);
                }
            }
            for (int it = 0; it < Iterations; it++)
            {
                if ((it & 1) == 0)
                {
                    for (int j = 0; j < count - 1; j++) Constrain(j);
                }
                else
                {
                    for (int j = count - 2; j >= 0; j--) Constrain(j);
                }
                if (held > 0 && held < count - 1)
                {
                    HoldUp(held, heldPivots);
                }
                if (it == Iterations / 2 || it == Iterations - 1)
                {
                    Collide();
                }
            }

            if (held >= 0)
            {
                heldVelocity = (pos[held] - prev[held]) / dt;
            }
            PlaceHolds(player);
            return true;
        }

        private void Reset(Vector3 a, Vector3 b)
        {
            float distance = Vector3.Distance(a, b);
            float sag = Mathf.Max(0f, Length - distance) * 0.35f;
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)(count - 1);
                Vector3 p = Vector3.Lerp(a, b, t) + Vector3.down * (sag * 4f * t * (1f - t));
                pos[i] = prev[i] = safe[i] = drawFrom[i] = p;
            }
            initialized = true;
        }

        private void Shift(Vector3 delta)
        {
            for (int i = 0; i < count; i++)
            {
                pos[i] += delta;
                prev[i] += delta;
                safe[i] += delta;
                drawFrom[i] += delta;
            }
        }

        /// <summary>A rope only pulls: points may bunch up but never sit farther apart than the spacing.</summary>
        private void Constrain(int j)
        {
            float w0 = invMass[j], w1 = invMass[j + 1];
            float w = w0 + w1;
            if (w <= 0f)
            {
                return;
            }
            Vector3 delta = pos[j + 1] - pos[j];
            float d = delta.magnitude;
            if (d <= spacing || d < 1e-6f)
            {
                return;
            }
            Vector3 fix = delta * ((d - spacing) / d / w);
            pos[j] += fix * w0;
            pos[j + 1] -= fix * w1;
        }

        /// <summary>
        /// A climber hanging on the rope is far heavier than the rope, so the chain alone would stretch under
        /// them. Hold the grabbed point within its share of the rope from each far end (or from the edge the
        /// rope runs over), like a pendulum: it can swing, but not fall farther.
        /// </summary>
        private void HoldUp(int held, int[] pivots)
        {
            for (int end = 0; end < 2; end++)
            {
                int pivotIndex = pivots[end];
                if (pivotIndex < 0)
                {
                    continue; // our own harness: the rope between it and our hands is just slack
                }
                Vector3 pivot = pos[pivotIndex];
                float available = Mathf.Max(0.3f, Mathf.Abs(pivotIndex - held) * spacing);
                Vector3 off = pos[held] - pivot;
                float d = off.magnitude;
                if (d > available && d > 1e-4f)
                {
                    Vector3 dir = off / d;
                    pos[held] = pivot + dir * available;
                    Vector3 v = pos[held] - prev[held];
                    float outward = Vector3.Dot(v, dir);
                    if (outward > 0f)
                    {
                        prev[held] += dir * outward;
                    }
                }
            }
        }

        private void Collide()
        {
            int moving = hanging ? count : count - 1;
            for (int i = 1; i < moving; i++)
            {
                Vector3 move = pos[i] - safe[i];
                float d = move.magnitude;
                if (d > 1e-5f && WorldProbe.Cast(safe[i], move, d + SurfaceGap, out RaycastHit hit))
                {
                    pos[i] = hit.point + hit.normal * SurfaceGap;
                    prev[i] = Vector3.Lerp(prev[i], pos[i], Friction);
                }
                safe[i] = pos[i];
            }
        }

        /// <summary>
        /// Walking along the rope from <paramref name="from"/> toward an end, the last point still in a
        /// straight line of sight from <paramref name="fromPos"/>: the end itself on a clear line, or the
        /// ledge the rope bends over.
        /// </summary>
        public int PivotIndex(int from, Vector3 fromPos, int endIndex)
        {
            int step = endIndex > from ? 1 : -1;
            if (!WorldProbe.Blocked(fromPos, pos[endIndex]))
            {
                return endIndex;
            }
            int last = from;
            for (int k = from + step; k != endIndex; k += step)
            {
                if (WorldProbe.Blocked(fromPos, pos[k]))
                {
                    break;
                }
                last = k;
            }
            return last == from ? from + step : last;
        }

        /// <summary>For the local player's end of this rope: the point the rope pulls them toward.</summary>
        public void LocalPivot(Vector3 me, out Vector3 pivot, out float available, out bool wrapped)
        {
            bool localIsA = EndA == NetSession.LocalId;
            int from = localIsA ? 0 : count - 1;
            int far = localIsA ? count - 1 : 0;
            int index = PivotIndex(from, me, far);
            pivot = pos[index];
            wrapped = index != far;
            // Past a ledge, only the rope between us and it counts; on a clear line, all of it.
            available = wrapped ? Mathf.Max(0.3f, Mathf.Abs(index - from) * spacing) : Length;
        }

        // ------------------------------------------------------------------ hands on the rope

        private int FindHeldNode(ENT_Player player)
        {
            if (player == null)
            {
                return -1;
            }
            int best = -1;
            float bestY = float.MinValue;
            foreach (ENT_Player.Hand hand in player.hands)
            {
                if (hand == null || !hand.IsHolding() || !(hand.currentInteract is RopeHold hold) || hold.Rope != this)
                {
                    continue;
                }
                Vector3 grip = hand.GetHoldWorldPosition();
                int node = (grip - pos[hold.Segment]).sqrMagnitude < (grip - pos[hold.Segment + 1]).sqrMagnitude
                    ? hold.Segment : hold.Segment + 1;
                // Two hands on the rope: the higher one carries the weight.
                if (grip.y > bestY)
                {
                    bestY = grip.y;
                    best = node;
                }
            }
            if (best == 0 || best == count - 1)
            {
                best = Mathf.Clamp(best, 1, count - 2);
            }
            return best;
        }

        /// <summary>The game's rope swing: sprint (or the "directional swing" setting) turns move keys into a push.</summary>
        private Vector3 SwingInput(ENT_Player player)
        {
            bool swing;
            try
            {
                bool sprint = InputManager.GetButton("Sprint").Pressed;
                swing = sprint != SettingsManager.settings.inverseRopeSwing;
                if (SettingsManager.settings.directionalSwing)
                {
                    swing = Mathf.Abs(Vector3.Dot(player.cam.transform.forward, Vector3.up)) < 0.5f;
                }
            }
            catch
            {
                swing = false;
            }
            Swinging = swing;
            foreach (ENT_Player.Hand hand in player.hands)
            {
                if (hand != null && hand.currentInteract is RopeHold hold && hold.Rope == this)
                {
                    hand.dragMult = swing ? SwingHandDrag : hold.dragMult;
                }
            }
            if (!swing)
            {
                return Vector3.zero;
            }
            Vector2 move = InputManager.GetVector("Move").vector;
            Vector3 push = player.transform.forward * move.y + player.transform.right * move.x;
            push.y = 0f;
            return push.sqrMagnitude > 1f ? push.normalized * SwingAccel : push * SwingAccel;
        }

        internal void OnGrab(RopeHold hold, ENT_Player.Hand hand)
        {
        }

        internal void OnRelease(ENT_Player player, bool jumped)
        {
            if (jumped && player != null && HeldNode >= 0)
            {
                // Let go with a jump: keep the swing's momentum.
                player.AddForce(heldVelocity / VelToMetersPerSecond / ForceToVel);
            }
            Swinging = false;
        }

        /// <summary>Whether a point (a teammate's hand) is on this rope.</summary>
        public bool Touches(Vector3 point, float within)
        {
            if (!Live || !initialized)
            {
                return false;
            }
            float sq = within * within;
            for (int i = 0; i < count - 1; i++)
            {
                Vector3 a = pos[i], ab = pos[i + 1] - a;
                float t = ab.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(point - a, ab) / ab.sqrMagnitude) : 0f;
                if ((a + ab * t - point).sqrMagnitude <= sq)
                {
                    return true;
                }
            }
            return false;
        }

        private void PlaceHolds(ENT_Player player)
        {
            if (player != null && holdsIgnoreFor != player && player.cCon != null)
            {
                holdsIgnoreFor = player;
                foreach (RopeHold hold in holds)
                {
                    Physics.IgnoreCollision(player.cCon, hold.Collider, true);
                }
            }
            bool localA = IsLocal(0), localB = IsLocal(1);
            for (int i = 0; i < holds.Length; i++)
            {
                RopeHold hold = holds[i];
                Vector3 p0 = pos[i], p1 = pos[i + 1];
                Vector3 axis = p1 - p0;
                float d = axis.magnitude;
                hold.transform.position = (p0 + p1) * 0.5f;
                if (d > 1e-4f)
                {
                    hold.transform.rotation = Quaternion.LookRotation(axis / d, Mathf.Abs(axis.y) > 0.99f * d ? Vector3.forward : Vector3.up);
                }
                hold.Collider.height = Mathf.Max(d, 0.05f) + RopeHold.GripRadius * 2f;

                float fromA = (i + 0.5f) * spacing, fromB = Length - fromA;
                bool nearOwnHarness = localA && fromA < NoGrabNearHarness || localB && fromB < NoGrabNearHarness;
                bool on = grabbable && !nearOwnHarness;
                if (hold.active != on)
                {
                    if (!on)
                    {
                        hold.ForceDropHands();
                    }
                    hold.active = on;
                    hold.Collider.enabled = on;
                }
            }
        }

        private void SetLive(bool live, bool visible)
        {
            Live = live;
            grabbable = live && visible;
            if (meshRenderer != null)
            {
                meshRenderer.enabled = live && visible;
            }
            if (!live)
            {
                HeldNode = -1;
                Swinging = false;
                if (holds != null)
                {
                    foreach (RopeHold hold in holds)
                    {
                        if (hold != null && hold.active)
                        {
                            hold.ForceDropHands();
                            hold.active = false;
                            hold.Collider.enabled = false;
                        }
                    }
                }
            }
        }

        public void Hide()
        {
            SetLive(false, false);
        }

        private void OnDestroy()
        {
            if (holds != null)
            {
                foreach (RopeHold hold in holds)
                {
                    if (hold != null)
                    {
                        hold.ForceDropHands();
                    }
                }
            }
            if (mesh != null)
            {
                Destroy(mesh);
            }
        }

        // ------------------------------------------------------------------ drawing

        private void LateUpdate()
        {
            if (!Live || !initialized || meshRenderer == null || !meshRenderer.enabled)
            {
                return;
            }
            // Blend between the last two physics steps so the rope moves smoothly at any frame rate, and pin
            // the ends to where the harnesses are drawn this frame.
            float alpha = Time.fixedDeltaTime > 0f ? Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime) : 1f;
            if (drawPoints == null || drawPoints.Length != count)
            {
                drawPoints = new Vector3[count];
            }
            Vector3[] points = drawPoints;
            for (int i = 0; i < count; i++)
            {
                points[i] = Vector3.LerpUnclamped(drawFrom[i], pos[i], alpha);
            }
            if (TryEndAt(0, out Vector3 a)) points[0] = a;
            if (TryEndAt(1, out Vector3 b)) points[count - 1] = b;

            for (int i = 0; i < count - 1; i++)
            {
                Vector3 p0 = points[Mathf.Max(i - 1, 0)], p1 = points[i], p2 = points[i + 1], p3 = points[Mathf.Min(i + 2, count - 1)];
                for (int s = 0; s < Subdivisions; s++)
                {
                    renderPoints[i * Subdivisions + s] = CatmullRom(p0, p1, p2, p3, s / (float)Subdivisions);
                }
            }
            renderPoints[ringCount - 1] = points[count - 1];

            int ring = Sides + 1;
            Vector3 normal = Vector3.zero;
            float v = 0f;
            for (int r = 0; r < ringCount; r++)
            {
                Vector3 tangent = r < ringCount - 1 ? renderPoints[r + 1] - renderPoints[r] : renderPoints[r] - renderPoints[r - 1];
                if (tangent.sqrMagnitude < 1e-10f)
                {
                    tangent = Vector3.down;
                }
                tangent.Normalize();
                // Parallel transport keeps the tube from twisting as it bends.
                normal = r == 0 || normal.sqrMagnitude < 1e-6f
                    ? Vector3.Cross(tangent, Mathf.Abs(tangent.y) < 0.9f ? Vector3.up : Vector3.right).normalized
                    : Vector3.ProjectOnPlane(normal, tangent).normalized;
                if (normal.sqrMagnitude < 1e-6f)
                {
                    normal = Vector3.Cross(tangent, Vector3.right).normalized;
                }
                Vector3 binormal = Vector3.Cross(tangent, normal);
                if (r > 0)
                {
                    v += Vector3.Distance(renderPoints[r], renderPoints[r - 1]) / TextureMeters;
                }
                for (int s = 0; s <= Sides; s++)
                {
                    float angle = s / (float)Sides * Mathf.PI * 2f;
                    Vector3 n = normal * Mathf.Cos(angle) + binormal * Mathf.Sin(angle);
                    int k = r * ring + s;
                    vertices[k] = renderPoints[r] + n * Radius;
                    normals[k] = n;
                    uvs[k] = new Vector2(s / (float)Sides, v);
                }
            }
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.RecalculateBounds();
        }

        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3);
        }
    }
}
