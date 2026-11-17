using System.Collections.Generic;
using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>
    /// Our own 3D models for items whose world model isn't what the player holds. The rapier lies on the
    /// ground as a folded artifact and only becomes a sword in the hand (a 2D sprite in first person), so
    /// teammates get a 3D sword drawn after that sprite: a long needle blade, a dark molten cup guard
    /// with drips running back over the hand, and a wrapped oxblood grip.
    ///
    /// Models are built in <see cref="HeldGrip"/> template space, in meters: the fist closes at the
    /// origin, +X runs toward the business end and +Z is the way the guard's cup faces out.
    /// </summary>
    public static class ItemModels
    {
        public const int MetalSlot = 0;
        public const int GripSlot = 1;
        public const int SlotCount = 2;

        public static PixelTextures.Recipe[] RapierRecipes()
        {
            var r = new PixelTextures.Recipe[SlotCount];
            r[MetalSlot] = new PixelTextures.Recipe
            {
                Base = new Color(0.34f, 0.33f, 0.36f),
                Grime = new Color(0.12f, 0.1f, 0.12f),
                Speck = new Color(0.8f, 0.8f, 0.84f),
                GrimeAmount = 0.35f,
                SpeckAmount = 0.08f,
                Contrast = 0.6f,
            };
            r[GripSlot] = new PixelTextures.Recipe
            {
                Base = new Color(0.33f, 0.12f, 0.11f),
                Grime = new Color(0.16f, 0.06f, 0.06f),
                Speck = new Color(0.55f, 0.3f, 0.26f),
                GrimeAmount = 0.3f,
                SpeckAmount = 0.05f,
                Contrast = 0.5f,
                Stripes = true,
            };
            return r;
        }

        /// <summary>Whether we draw this item ourselves instead of copying its world model.</summary>
        public static bool HasModel(string prefabName)
        {
            return prefabName != null && prefabName.ToLowerInvariant().Contains("rapier");
        }

        public static void BuildRapier(MeshKit k)
        {
            k.TileSize = 0.12f;

            // Grip: wrapped, a little swelled in the middle, with a ferrule at each end.
            k.Use(GripSlot);
            k.Tube(new Vector3(-0.075f, 0, 0), new Vector3(0f, 0, 0), 0.0125f, 0.0145f, 8);
            k.Tube(new Vector3(0f, 0, 0), new Vector3(0.07f, 0, 0), 0.0145f, 0.013f, 8);
            k.Use(MetalSlot);
            k.Tube(new Vector3(-0.08f, 0, 0), new Vector3(-0.07f, 0, 0), 0.016f, 0.016f, 8);
            k.Tube(new Vector3(0.068f, 0, 0), new Vector3(0.078f, 0, 0), 0.016f, 0.016f, 8);
            // Pommel.
            k.Ellipsoid(new Vector3(-0.098f, 0, 0), new Vector3(0.022f, 0.02f, 0.02f), 4, 8);

            // Cup guard: a shallow bell opening back toward the hand, sitting forward of the fist.
            Cup(k, new Vector3(0.1f, 0, 0.004f), 0.068f, 0.04f, 10, 14);
            // Short cross bar through the cup.
            k.Tube(new Vector3(0.088f, 0, -0.085f), new Vector3(0.088f, 0, 0.085f), 0.006f, 0.006f, 6);
            k.Ellipsoid(new Vector3(0.088f, 0, -0.09f), new Vector3(0.01f, 0.01f, 0.01f), 3, 6);
            k.Ellipsoid(new Vector3(0.088f, 0, 0.09f), new Vector3(0.01f, 0.01f, 0.01f), 3, 6);

            // Molten drips running off the cup's rim back over the hand, each ending in a bead.
            float[] angles = { 200f, 235f, 265f, 300f, 330f, 20f, 160f };
            float[] lengths = { 0.07f, 0.11f, 0.06f, 0.09f, 0.05f, 0.04f, 0.08f };
            for (int i = 0; i < angles.Length; i++)
            {
                float a = angles[i] * Mathf.Deg2Rad;
                Vector3 radial = new Vector3(0, Mathf.Sin(a), Mathf.Cos(a));
                Vector3 rim = new Vector3(0.066f, 0, 0) + radial * 0.066f;
                // Mostly back toward the pommel and a little down, hugging the hand.
                Vector3 end = rim + new Vector3(-lengths[i], -0.25f * lengths[i], 0) - radial * (0.2f * lengths[i]);
                Vector3 mid = Vector3.Lerp(rim, end, 0.5f) + radial * 0.008f;
                k.Tube(rim, mid, 0.0075f, 0.0055f, 5);
                k.Tube(mid, end, 0.0055f, 0.0035f, 5);
                k.Ellipsoid(end, Vector3.one * (0.0065f + 0.002f * (i % 3)), 3, 6);
            }

            // Blade: a long four-sided needle, slightly flattened, with a short square ricasso.
            k.Tube(new Vector3(0.1f, 0, 0), new Vector3(0.135f, 0, 0), 0.0085f, 0.0085f, 4, twist: Mathf.PI * 0.25f);
            k.Tube(new Vector3(0.135f, 0, 0), new Vector3(0.62f, 0, 0), 0.0075f, 0.0045f, 4, caps: false);
            k.Tube(new Vector3(0.62f, 0, 0), new Vector3(0.96f, 0, 0), 0.0045f, 0.0006f, 4);
        }

        /// <summary>
        /// A bell along +X: closed at the front (<paramref name="front"/>), open toward -X, made double
        /// sided so its inside shows from behind.
        /// </summary>
        private static void Cup(MeshKit k, Vector3 front, float radius, float depth, int rings, int segments)
        {
            var points = new Vector3[rings + 1, segments];
            for (int r = 0; r <= rings; r++)
            {
                float t = (float)r / rings;
                float rr = radius * Mathf.Sin(t * Mathf.PI * 0.5f);
                float x = -depth * (1f - Mathf.Cos(t * Mathf.PI * 0.5f));
                for (int s = 0; s < segments; s++)
                {
                    float a = s * Mathf.PI * 2f / segments;
                    // A slightly wavy, uneven rim, like poured metal.
                    float wobble = 1f + 0.06f * Mathf.Sin(a * 3f + 1.3f) * t;
                    points[r, s] = front + new Vector3(x, Mathf.Sin(a) * rr * wobble, Mathf.Cos(a) * rr * wobble);
                }
            }
            Vector3 outside = front + new Vector3(radius * 2f, 0, 0);
            Vector3 inner = front - new Vector3(radius * 2f, 0, 0);
            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    int n = (s + 1) % segments;
                    // Outer face (normals away from the hand), then the inner face.
                    k.Tri(points[r, s], points[r + 1, s], points[r + 1, n], inner);
                    k.Tri(points[r, s], points[r + 1, n], points[r, n], inner);
                    k.Tri(points[r, s], points[r + 1, n], points[r + 1, s], outside);
                    k.Tri(points[r, s], points[r, n], points[r + 1, n], outside);
                }
            }
        }
    }
}
