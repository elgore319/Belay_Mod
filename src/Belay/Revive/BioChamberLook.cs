using Belay.Players.Avatar;
using UnityEngine;

namespace Belay.Revive
{
    /// <summary>
    /// The revive biochamber that stands in each terminal room: a tall glass pod on a heavy steel plinth,
    /// fed by hoses from the wall behind it, with feeding lines hanging inside over a pool of growth medium and an
    /// amber lamp ring under its cap. A teammate revived at the terminal is regrown in it and steps out
    /// when the front of the glass swings open.
    ///
    /// Everything about the look lives in this file. Pure math (no Unity object calls), so
    /// tools/avatar-preview can render it without the game.
    /// </summary>
    public static class BioChamberLook
    {
        public const int SteelSlot = 0;
        public const int DarkSlot = 1;
        public const int HoseSlot = 2;
        public const int GrowthSlot = 3;
        public const int HazardSlot = 4;
        public const int GlowSlot = 5;
        public const int GlassSlot = 6;
        public const int SlotCount = 7;

        /// <summary>Radius of the glass, in meters.</summary>
        public const float GlassRadius = 0.56f;

        /// <summary>Height of the chamber floor (where the revived player stands), in meters.</summary>
        public const float FloorHeight = 0.3f;

        /// <summary>Top of the glass, in meters.</summary>
        public const float GlassTop = 2.85f;

        /// <summary>Overall height from the floor to the top of the hoses, in meters.</summary>
        public const float Height = 3.6f;

        /// <summary>Distance from the middle of the chamber to the wall behind it, in meters.</summary>
        public const float WallBack = 0.82f;

        /// <summary>How far round the front of the glass swings when fully open, in degrees.</summary>
        public const float OpenAngle = 140f;

        public const float TileSize = 0.5f;

        public static PixelTextures.Recipe[] Recipes()
        {
            var r = new PixelTextures.Recipe[SlotCount];
            r[SteelSlot] = new PixelTextures.Recipe
            {
                Base = new Color(0.36f, 0.38f, 0.37f),
                Grime = new Color(0.17f, 0.15f, 0.12f),
                Speck = new Color(0.55f, 0.56f, 0.54f),
                GrimeAmount = 0.4f,
                SpeckAmount = 0.07f,
                Contrast = 0.45f,
            };
            r[DarkSlot] = new PixelTextures.Recipe
            {
                Base = new Color(0.16f, 0.16f, 0.16f),
                Grime = new Color(0.3f, 0.17f, 0.09f), // rust
                Speck = new Color(0.32f, 0.3f, 0.28f),
                GrimeAmount = 0.3f,
                SpeckAmount = 0.08f,
                Contrast = 0.5f,
                Stripes = true, // grating
            };
            r[HoseSlot] = new PixelTextures.Recipe
            {
                Base = new Color(0.12f, 0.11f, 0.1f),
                Grime = new Color(0.07f, 0.06f, 0.05f),
                Speck = new Color(0.22f, 0.2f, 0.18f),
                GrimeAmount = 0.3f,
                SpeckAmount = 0.05f,
                Contrast = 0.4f,
                Stripes = true, // ribbed rubber
            };
            r[GrowthSlot] = new PixelTextures.Recipe
            {
                Base = new Color(0.55f, 0.24f, 0.2f),
                Grime = new Color(0.32f, 0.36f, 0.14f), // sickly medium
                Speck = new Color(0.8f, 0.55f, 0.45f),
                GrimeAmount = 0.45f,
                SpeckAmount = 0.1f,
                Contrast = 0.55f,
            };
            r[HazardSlot] = new PixelTextures.Recipe
            {
                Base = new Color(0.78f, 0.6f, 0.12f),
                Grime = new Color(0.1f, 0.09f, 0.08f),
                Speck = new Color(0.35f, 0.3f, 0.15f),
                GrimeAmount = 0.5f,
                SpeckAmount = 0.12f,
                Contrast = 0.4f,
                Stripes = true,
            };
            r[GlowSlot] = new PixelTextures.Recipe
            {
                Base = new Color(1f, 0.68f, 0.25f),
                Flat = true,
            };
            r[GlassSlot] = new PixelTextures.Recipe
            {
                Base = new Color(0.62f, 0.78f, 0.7f),
                Grime = new Color(0.45f, 0.5f, 0.4f),
                Speck = new Color(0.85f, 0.92f, 0.88f),
                GrimeAmount = 0.25f,
                SpeckAmount = 0.05f,
                Contrast = 0.25f,
            };
            return r;
        }

        /// <summary>
        /// Everything except the swinging front of the glass. Standing upright (y up), its front facing +z,
        /// the middle of the plinth's bottom at the origin.
        /// </summary>
        public static void BuildBody(MeshKit k)
        {
            k.TileSize = TileSize;
            const int sides = 8;

            // Plinth: a heavy octagon of steel with a dark grated top and a hazard-striped step out front.
            k.Use(SteelSlot);
            k.Loft(new[]
            {
                new Vector4(0f, 0.74f, 0.74f, 0f),
                new Vector4(0.14f, 0.74f, 0.74f, 0f),
                new Vector4(0.2f, 0.66f, 0.66f, 0f),
                new Vector4(FloorHeight - 0.02f, 0.64f, 0.64f, 0f),
            }, sides);
            k.Use(DarkSlot);
            k.Loft(new[]
            {
                new Vector4(FloorHeight - 0.02f, GlassRadius - 0.02f, GlassRadius - 0.02f, 0f),
                new Vector4(FloorHeight, GlassRadius - 0.02f, GlassRadius - 0.02f, 0f),
            }, sides * 2);
            k.Use(HazardSlot);
            k.Block(new Vector3(0, 0, 0.66f), 0.14f, 0.7f, 0.34f, 0.7f, 0.3f);

            // A shallow pool of growth medium on the floor, and a heap of it at the back.
            k.Use(GrowthSlot);
            k.Loft(new[]
            {
                new Vector4(FloorHeight, 0.5f, 0.5f, 0f),
                new Vector4(FloorHeight + 0.025f, 0.46f, 0.46f, 0f),
            }, 14);
            k.Ellipsoid(new Vector3(0.1f, FloorHeight, -0.32f), new Vector3(0.22f, 0.09f, 0.16f), 3, 9, dome: true);

            // Back half of the glass, fixed.
            k.Use(GlassSlot);
            GlassArc(k, 90f, 270f);

            // Steel ribs up the back and sides, and the frame the door closes against.
            k.Use(SteelSlot);
            foreach (float deg in new[] { 90f, 150f, 210f, 270f })
            {
                Vector3 dir = Dir(deg);
                k.Tube(dir * (GlassRadius + 0.03f) + Vector3.up * FloorHeight, dir * (GlassRadius + 0.03f) + Vector3.up * GlassTop, 0.035f, 0.035f, 6);
            }
            // A middle band round the back.
            for (int i = 0; i < 6; i++)
            {
                float a0 = 90f + i * 30f, a1 = a0 + 30f;
                k.Tube(Dir(a0) * (GlassRadius + 0.035f) + Vector3.up * 1.4f, Dir(a1) * (GlassRadius + 0.035f) + Vector3.up * 1.4f, 0.03f, 0.03f, 5);
            }

            // Cap: a steel collar and a low dome over the glass.
            k.Loft(new[]
            {
                new Vector4(GlassTop - 0.04f, 0.66f, 0.66f, 0f),
                new Vector4(GlassTop + 0.2f, 0.66f, 0.66f, 0f),
                new Vector4(GlassTop + 0.24f, 0.6f, 0.6f, 0f),
            }, sides);
            k.Use(DarkSlot);
            k.Ellipsoid(new Vector3(0, GlassTop + 0.24f, 0), new Vector3(0.56f, 0.17f, 0.56f), 3, sides * 2, dome: true);

            // Amber lamp ring under the cap, lighting the inside.
            k.Use(GlowSlot);
            k.Torus(new Vector3(0, GlassTop - 0.07f, 0), Vector3.up, GlassRadius - 0.07f, 0.025f, 16, 4);

            // Thick supply hoses from the dome, bending back and running straight into the wall behind.
            k.Use(HoseSlot);
            float wall = -WallBack - 0.06f; // a little into the wall, so no gap shows
            for (int i = -1; i <= 1; i++)
            {
                float x = i * 0.24f;
                float z0 = -0.05f - Mathf.Abs(i) * 0.05f;
                float y = GlassTop + 0.48f + (i == 0 ? 0.1f : 0f);
                Vector3 a = new Vector3(x, GlassTop + 0.3f, z0);
                Vector3 b = new Vector3(x, y - 0.08f, z0 - 0.04f);
                Vector3 c = new Vector3(x, y, z0 - 0.2f);
                Vector3 d = new Vector3(x, y, wall);
                k.Tube(a, b, 0.07f, 0.07f, 8);
                k.Tube(b, c, 0.07f, 0.07f, 8);
                k.Tube(c, d, 0.07f, 0.07f, 8);
            }
            // Two feed pipes out of the back of the plinth into the wall.
            for (int i = -1; i <= 1; i += 2)
            {
                k.Tube(new Vector3(i * 0.28f, 0.17f, -0.6f), new Vector3(i * 0.28f, 0.17f, wall), 0.05f, 0.05f, 8);
            }
            // Steel couplings where the hoses meet the dome, and flanges where everything meets the wall.
            k.Use(SteelSlot);
            for (int i = -1; i <= 1; i++)
            {
                Vector3 a = new Vector3(i * 0.24f, GlassTop + 0.27f, -0.05f - Mathf.Abs(i) * 0.05f);
                k.Tube(a - Vector3.up * 0.06f, a + Vector3.up * 0.08f, 0.095f, 0.095f, 8);
                float y = GlassTop + 0.48f + (i == 0 ? 0.1f : 0f);
                k.Tube(new Vector3(i * 0.24f, y, -WallBack + 0.08f), new Vector3(i * 0.24f, y, -WallBack), 0.1f, 0.1f, 8);
            }
            for (int i = -1; i <= 1; i += 2)
            {
                k.Tube(new Vector3(i * 0.28f, 0.17f, -WallBack + 0.06f), new Vector3(i * 0.28f, 0.17f, -WallBack), 0.08f, 0.08f, 8);
            }
            // A bolted plate on the wall behind the top hoses.
            k.Box(new Vector3(0, GlassTop + 0.53f, -WallBack + 0.015f), new Vector3(0.85f, 0.4f, 0.03f));

            // Feeding lines hanging inside, ending in fleshy nozzles at chest height.
            for (int i = 0; i < 5; i++)
            {
                float deg = 110f + i * 35f;
                Vector3 top = Dir(deg) * 0.36f + Vector3.up * (GlassTop - 0.02f);
                Vector3 mid = Dir(deg + 8f) * 0.3f + Vector3.up * (2.0f - (i % 2) * 0.12f);
                Vector3 end = Dir(deg + 4f) * 0.24f + Vector3.up * (1.62f - (i % 3) * 0.1f);
                k.Use(HoseSlot);
                k.Tube(top, mid, 0.018f, 0.016f, 5);
                k.Tube(mid, end, 0.016f, 0.014f, 5);
                k.Use(GrowthSlot);
                k.Ellipsoid(end, new Vector3(0.03f, 0.045f, 0.03f), 3, 6);
            }

            // Control box on the right: a small amber screen and two status lamps.
            k.Use(SteelSlot);
            k.Block(new Vector3(0.74f, 0.75f, 0.08f), 0.55f, 0.16f, 0.3f, 0.16f, 0.26f);
            k.Tube(new Vector3(0.68f, 0.75f, 0.08f), new Vector3(0.6f, 0.2f, 0.08f), 0.035f, 0.035f, 6);
            k.Use(GlowSlot);
            k.Box(new Vector3(0.82f, 1.12f, 0.08f), new Vector3(0.012f, 0.12f, 0.18f));
            k.Box(new Vector3(0.82f, 0.88f, 0.02f), new Vector3(0.015f, 0.035f, 0.035f));
            k.Use(GrowthSlot);
            k.Box(new Vector3(0.82f, 0.88f, 0.13f), new Vector3(0.015f, 0.035f, 0.035f));
        }

        /// <summary>
        /// The front of the glass with its steel edges, closed (open = 0) or swung round the back of the
        /// pod (open = 1). Same space as <see cref="BuildBody"/>.
        /// </summary>
        public static void BuildDoor(MeshKit k, float open)
        {
            k.TileSize = TileSize;
            float turn = Mathf.Clamp01(open) * OpenAngle;
            // The door's radius is a touch larger so it slides round outside the fixed back half.
            float from = -90f + turn, to = 90f + turn;
            k.Use(GlassSlot);
            GlassArc(k, from, to, GlassRadius + 0.04f);
            k.Use(SteelSlot);
            foreach (float deg in new[] { from, to })
            {
                Vector3 dir = Dir(deg);
                k.Tube(dir * (GlassRadius + 0.05f) + Vector3.up * FloorHeight, dir * (GlassRadius + 0.05f) + Vector3.up * GlassTop, 0.03f, 0.03f, 6);
            }
            // A handle bar on the door.
            Vector3 h = Dir(from + 40f) * (GlassRadius + 0.1f);
            k.Tube(h + Vector3.up * 0.95f, h + Vector3.up * 1.35f, 0.018f, 0.018f, 5);
        }

        /// <summary>Direction round the pod: 0 degrees faces front (+z), 90 the right (+x).</summary>
        private static Vector3 Dir(float degrees)
        {
            float a = degrees * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a));
        }

        private static void GlassArc(MeshKit k, float fromDeg, float toDeg, float radius = GlassRadius)
        {
            int steps = Mathf.Max(2, Mathf.CeilToInt((toDeg - fromDeg) / 15f));
            Vector3 center = new Vector3(0, (FloorHeight + GlassTop) * 0.5f, 0);
            for (int i = 0; i < steps; i++)
            {
                Vector3 d0 = Dir(Mathf.Lerp(fromDeg, toDeg, (float)i / steps));
                Vector3 d1 = Dir(Mathf.Lerp(fromDeg, toDeg, (float)(i + 1) / steps));
                Vector3 a = d0 * radius + Vector3.up * FloorHeight;
                Vector3 b = d1 * radius + Vector3.up * FloorHeight;
                Vector3 c = d1 * radius + Vector3.up * GlassTop;
                Vector3 e = d0 * radius + Vector3.up * GlassTop;
                k.Tri(a, b, c, center, p => new Vector3(p.x, 0, p.z).normalized);
                k.Tri(a, c, e, center, p => new Vector3(p.x, 0, p.z).normalized);
            }
        }
    }
}
