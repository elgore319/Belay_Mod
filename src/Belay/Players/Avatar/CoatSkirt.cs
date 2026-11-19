using System.Collections.Generic;
using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>
    /// A coat that hangs below the waist (the lab coat, the pier slicker, the expedition parka), simulated
    /// as panels hanging from the waist all the way round: each panel's hem swings with the body, settles
    /// back under gravity, and is pushed out of the way by the thighs and shins, so a stride or a high step
    /// lifts the coat instead of the leg going through it. Works in the torso's design space (pelvis at the
    /// origin, +y up, +z forward); the coat's mesh is bent to follow the panels.
    /// </summary>
    public sealed class CoatSkirt
    {
        public const int Columns = 16;

        /// <summary>Where the coat hangs from, just above the pelvis.</summary>
        public const float Waist = 0.06f;

        /// <summary>How thick the legs are (AvatarDesign's thigh and shin), in design units.</summary>
        public const float ThighRadius = 0.085f, ShinRadius = 0.066f;

        /// <summary>A leg, as a capsule in the torso's design space.</summary>
        public struct Capsule
        {
            public Vector3 A, B;
            public float Radius;
        }

        private readonly float hem;
        private readonly Vector3[] top = new Vector3[Columns];
        private readonly Vector3[] rest = new Vector3[Columns];
        private readonly float[] length = new float[Columns];
        private readonly Vector3[] offset = new Vector3[Columns];
        private readonly Vector3[] velocity = new Vector3[Columns];
        private readonly Vector3[] scratch = new Vector3[Columns];
        private readonly Vector3[] outwards = new Vector3[Columns];

        public CoatSkirt(float hem, float flare, float back)
        {
            this.hem = hem;
            for (int i = 0; i < Columns; i++)
            {
                float a = Angle(i);
                top[i] = new Vector3(Mathf.Sin(a) * 0.178f, Waist, Mathf.Cos(a) * 0.118f);
                rest[i] = new Vector3(Mathf.Sin(a) * (0.21f + flare), hem, Mathf.Cos(a) * (0.15f + flare * 0.7f) - 0.015f - back);
                length[i] = (rest[i] - top[i]).magnitude;
                outwards[i] = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            }
        }

        private static float Angle(int column) => (column + 0.5f) * Mathf.PI * 2f / Columns;

        /// <summary>Puts the coat back at rest (after a teleport or a new outfit).</summary>
        public void Reset()
        {
            for (int i = 0; i < Columns; i++)
            {
                offset[i] = Vector3.zero;
                velocity[i] = Vector3.zero;
            }
        }

        /// <summary>
        /// One step. <paramref name="down"/> is the direction of gravity and <paramref name="acceleration"/>
        /// the pelvis's acceleration (m/s², gravity not included), both in the torso's space.
        /// </summary>
        public void Step(float dt, Vector3 down, Vector3 acceleration, IList<Capsule> legs)
        {
            dt = Mathf.Clamp(dt, 0f, 0.05f);
            if (dt <= 0f)
            {
                Collide(legs, false);
                return;
            }
            const float stiffness = 70f, damping = 9f;
            // The coat hangs straight down: tilting the body swings the hem back under the waist, a bit
            // (it's heavy cloth, not a curtain).
            Vector3 sag = down.normalized - Vector3.down;
            for (int i = 0; i < Columns; i++)
            {
                Vector3 target = sag * (length[i] * 0.55f);
                Vector3 force = (target - offset[i]) * stiffness - velocity[i] * damping - acceleration * 0.6f;
                velocity[i] += force * dt;
                offset[i] += velocity[i] * dt;
            }
            Collide(legs, true);
        }

        private void Collide(IList<Capsule> legs, bool keepVelocity)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                for (int i = 0; i < Columns; i++)
                {
                    Vector3 p = rest[i] + offset[i];
                    foreach (Capsule leg in legs)
                    {
                        // Where the panel (waist to hem) comes nearest the leg.
                        Closest(top[i], p, leg.A, leg.B, out float s, out Vector3 onPanel, out Vector3 onLeg);
                        Vector3 away = onPanel - onLeg;
                        float d = away.magnitude;
                        float clear = leg.Radius + 0.012f; // the cloth's own thickness
                        // The coat stays on the outside of the leg: a leg that has gone through the panel
                        // (a knee swung up through the front) pushes it out past itself, not back in.
                        Vector3 outward = outwards[i];
                        float side = d > 1e-4f ? Vector3.Dot(away / d, outward) : -1f;
                        float need;
                        Vector3 n;
                        if (side >= 0f)
                        {
                            if (d >= clear)
                            {
                                continue;
                            }
                            n = away / d;
                            need = clear - d;
                        }
                        else
                        {
                            // Through it: out along the outward side of the leg, all the way past it.
                            n = d > 1e-4f ? -away / d : outward;
                            if (Vector3.Dot(n, outward) < 0f)
                            {
                                n = outward;
                            }
                            need = clear + d;
                            if (d > clear * 3f)
                            {
                                continue; // the leg's behind this panel, nowhere near it
                            }
                        }
                        if (s < 0.02f)
                        {
                            continue;
                        }
                        // Moving the hem by x moves this point by x*s, so push the hem enough to clear here.
                        p += n * (need / Mathf.Max(s, 0.25f));
                        if (keepVelocity)
                        {
                            float into = Vector3.Dot(velocity[i], n);
                            if (into < 0f)
                            {
                                velocity[i] -= n * into;
                            }
                        }
                    }
                    // Cloth doesn't stretch: the hem stays its length from the waist (so it lifts as it's pushed).
                    p = top[i] + (p - top[i]).normalized * length[i];
                    offset[i] = p - rest[i];
                }
                // Neighbouring panels are one piece of cloth: smooth the hem round.
                for (int i = 0; i < Columns; i++)
                {
                    scratch[i] = (offset[(i + Columns - 1) % Columns] + offset[(i + 1) % Columns]) * 0.5f;
                }
                for (int i = 0; i < Columns; i++)
                {
                    offset[i] = Vector3.Lerp(offset[i], scratch[i], 0.25f);
                }
            }
        }

        /// <summary>
        /// Bends the coat's mesh (built at rest) to the panels: everything below the waist moves with the
        /// panel it hangs on, more the further down it is. Vertices above the waist stay put.
        /// </summary>
        public void Deform(IList<Vector3> vertices, Vector3[] output)
        {
            float drop = Mathf.Max(0.01f, Waist - hem);
            for (int v = 0; v < vertices.Count; v++)
            {
                Vector3 p = vertices[v];
                if (p.y >= Waist)
                {
                    output[v] = p;
                    continue;
                }
                float f = Mathf.Clamp01((Waist - p.y) / drop);
                float a = Mathf.Atan2(p.x, p.z);
                if (a < 0f)
                {
                    a += Mathf.PI * 2f;
                }
                float c = a / (Mathf.PI * 2f) * Columns - 0.5f;
                int i0 = Mathf.FloorToInt(c);
                float frac = c - i0;
                int i = ((i0 % Columns) + Columns) % Columns;
                int j = (i + 1) % Columns;
                Vector3 move = Vector3.Lerp(offset[i], offset[j], frac);
                output[v] = p + move * f;
            }
        }

        /// <summary>Closest points between segments pq and ab; <paramref name="s"/> is how far along pq.</summary>
        private static void Closest(Vector3 p, Vector3 q, Vector3 a, Vector3 b, out float s, out Vector3 onPq, out Vector3 onAb)
        {
            Vector3 d1 = q - p, d2 = b - a, r = p - a;
            float aa = Vector3.Dot(d1, d1), ee = Vector3.Dot(d2, d2), ff = Vector3.Dot(d2, r);
            float t;
            if (aa <= 1e-8f && ee <= 1e-8f)
            {
                s = 0f;
                t = 0f;
            }
            else if (aa <= 1e-8f)
            {
                s = 0f;
                t = Mathf.Clamp01(ff / ee);
            }
            else
            {
                float cc = Vector3.Dot(d1, r);
                if (ee <= 1e-8f)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-cc / aa);
                }
                else
                {
                    float bb = Vector3.Dot(d1, d2);
                    float denom = aa * ee - bb * bb;
                    s = denom > 1e-8f ? Mathf.Clamp01((bb * ff - cc * ee) / denom) : 0f;
                    t = (bb * s + ff) / ee;
                    if (t < 0f)
                    {
                        t = 0f;
                        s = Mathf.Clamp01(-cc / aa);
                    }
                    else if (t > 1f)
                    {
                        t = 1f;
                        s = Mathf.Clamp01((bb - cc) / aa);
                    }
                }
            }
            onPq = p + d1 * s;
            onAb = a + d2 * t;
        }
    }
}
