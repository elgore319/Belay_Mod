using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>Material slots shared by every avatar mesh.</summary>
    public static class Slot
    {
        public const int Jacket = 0;
        public const int JacketTrim = 1;
        public const int Pants = 2;
        public const int Webbing = 3;
        public const int Metal = 4;
        public const int Pack = 5;
        public const int Glove = 6;
        public const int Boot = 7;
        public const int Visor = 8;
        public const int Lamp = 9;
        public const int Buff = 10;
        public const int Helmet = 11;
        public const int Rope = 12;
        public const int Count = 13;
    }

    /// <summary>
    /// The look of a Belay climber: a hooded shell jacket in the player's color, a hard-shell helmet with
    /// a headlamp and goggles, a sit harness with gear loops, a pack with a rope coil, gloves and boots.
    /// All original geometry, built from code (no imported assets).
    ///
    /// Units are meters. Each Build* method fills one part in its own local space, described above it.
    /// </summary>
    public static class AvatarDesign
    {
        // Skeleton proportions, measured from the soles of the feet.
        public const float PelvisHeight = 0.95f;
        public const float ShoulderHeight = 0.48f;   // above the pelvis
        public const float ShoulderWidth = 0.205f;   // half width
        public const float NeckHeight = 0.56f;       // above the pelvis
        public const float HipWidth = 0.095f;        // half width
        public const float UpperArm = 0.29f;
        public const float Forearm = 0.27f;
        public const float Thigh = 0.45f;
        public const float Shin = 0.43f;
        public const float AnkleHeight = 0.08f;
        public const float HarnessLoopDrop = 0.06f;  // belay loop sits just under the pelvis origin

        public static Color[] Colors(Color player)
        {
            Color.RGBToHSV(player, out float h, out float s, out float v);
            Color jacket = Color.HSVToRGB(h, Mathf.Clamp01(s * 0.85f), Mathf.Clamp01(v * 0.82f));
            Color trim = Color.HSVToRGB(h, Mathf.Clamp01(s * 0.7f), Mathf.Clamp01(v * 0.45f));
            Color helmet = Color.HSVToRGB(h, Mathf.Clamp01(s * 0.9f), Mathf.Clamp01(0.35f + v * 0.65f));
            var colors = new Color[Slot.Count];
            colors[Slot.Jacket] = jacket;
            colors[Slot.JacketTrim] = trim;
            colors[Slot.Pants] = new Color(0.17f, 0.18f, 0.2f);
            colors[Slot.Webbing] = new Color(0.08f, 0.08f, 0.09f);
            colors[Slot.Metal] = new Color(0.62f, 0.64f, 0.66f);
            colors[Slot.Pack] = new Color(0.24f, 0.26f, 0.2f);
            colors[Slot.Glove] = new Color(0.47f, 0.34f, 0.22f);
            colors[Slot.Boot] = new Color(0.2f, 0.14f, 0.1f);
            colors[Slot.Visor] = new Color(0.05f, 0.06f, 0.08f);
            colors[Slot.Lamp] = new Color(1f, 0.93f, 0.75f);
            colors[Slot.Buff] = new Color(0.12f, 0.12f, 0.13f);
            colors[Slot.Helmet] = helmet;
            colors[Slot.Rope] = Color.Lerp(player, new Color(0.9f, 0.85f, 0.7f), 0.55f);
            return colors;
        }

        /// <summary>Torso, harness and pack. Origin at the pelvis, +y up, +z forward.</summary>
        public static void BuildTorso(MeshKit k)
        {
            // Hips and seat of the pants.
            k.Use(Slot.Pants).Block(new Vector3(0, -0.11f, 0), 0.17f, 0.3f, 0.19f, 0.33f, 0.2f);

            // Jacket: waist, ribs, chest, with a slight forward lean of the chest.
            k.Use(Slot.Jacket);
            k.Block(new Vector3(0, 0.03f, 0), 0.2f, 0.33f, 0.2f, 0.36f, 0.22f, 0.005f);
            k.Block(new Vector3(0, 0.23f, 0.005f), 0.2f, 0.36f, 0.22f, 0.42f, 0.235f, 0.01f);
            k.Block(new Vector3(0, 0.43f, 0.015f), 0.09f, 0.42f, 0.235f, 0.3f, 0.2f, -0.005f);
            // Shoulder pads.
            k.Ellipsoid(new Vector3(-ShoulderWidth, ShoulderHeight, 0.0f), new Vector3(0.07f, 0.065f, 0.08f), 3, 6);
            k.Ellipsoid(new Vector3(ShoulderWidth, ShoulderHeight, 0.0f), new Vector3(0.07f, 0.065f, 0.08f), 3, 6);
            // Hood rolled into the collar at the back.
            k.Use(Slot.JacketTrim);
            k.Block(new Vector3(0, 0.5f, -0.02f), 0.09f, 0.24f, 0.18f, 0.2f, 0.15f, -0.01f);
            k.Ellipsoid(new Vector3(0, 0.55f, -0.08f), new Vector3(0.11f, 0.06f, 0.05f), 3, 6);
            // Zip, chest pocket and hem.
            k.Box(new Vector3(0, 0.27f, 0.118f), new Vector3(0.025f, 0.46f, 0.012f));
            k.Box(new Vector3(0.09f, 0.34f, 0.124f), new Vector3(0.11f, 0.09f, 0.012f));
            k.Block(new Vector3(0, 0.02f, 0), 0.035f, 0.345f, 0.21f, 0.345f, 0.21f);
            // Elastic cuffs of the jacket hem at the side seams.
            k.Box(new Vector3(-0.185f, 0.2f, 0), new Vector3(0.012f, 0.3f, 0.06f));
            k.Box(new Vector3(0.185f, 0.2f, 0), new Vector3(0.012f, 0.3f, 0.06f));

            // Harness: waist belt, buckle, belay loop, gear loops with carabiners.
            k.Use(Slot.Webbing);
            k.Block(new Vector3(0, -0.04f, 0), 0.06f, 0.35f, 0.225f, 0.35f, 0.225f);
            k.Block(new Vector3(0, -0.13f, 0.09f), 0.1f, 0.05f, 0.04f, 0.06f, 0.04f, 0.015f);
            k.Use(Slot.Metal);
            k.Box(new Vector3(0, -0.01f, 0.116f), new Vector3(0.07f, 0.035f, 0.012f));
            k.Use(Slot.Rope);
            k.Torus(new Vector3(0, -HarnessLoopDrop - 0.02f, 0.125f), Vector3.right, 0.035f, 0.009f, 8);
            k.Use(Slot.Metal);
            for (int side = -1; side <= 1; side += 2)
            {
                for (int i = 0; i < 3; i++)
                {
                    float x = side * (0.12f + i * 0.025f);
                    float z = 0.07f - i * 0.06f;
                    k.Torus(new Vector3(x, -0.1f, z), new Vector3(side, 0, 0.4f), 0.022f, 0.005f, 6, 3);
                }
            }

            // Chalk bag on the back of the belt.
            k.Use(Slot.JacketTrim).Tube(new Vector3(0, -0.13f, -0.15f), new Vector3(0, -0.03f, -0.155f), 0.045f, 0.05f, 7);
            k.Use(Slot.Buff).Ellipsoid(new Vector3(0, -0.03f, -0.155f), new Vector3(0.05f, 0.015f, 0.05f), 2, 7, true);

            // Backpack with lid, side straps and a coiled rope strapped to the top.
            k.Use(Slot.Pack);
            k.Block(new Vector3(0, 0.08f, -0.165f), 0.36f, 0.29f, 0.12f, 0.27f, 0.11f, -0.01f);
            k.Block(new Vector3(0, 0.43f, -0.17f), 0.05f, 0.29f, 0.13f, 0.25f, 0.1f, -0.01f);
            k.Box(new Vector3(0, 0.2f, -0.235f), new Vector3(0.18f, 0.13f, 0.03f));
            k.Use(Slot.Webbing);
            k.Box(new Vector3(-0.1f, 0.25f, -0.228f), new Vector3(0.025f, 0.34f, 0.012f));
            k.Box(new Vector3(0.1f, 0.25f, -0.228f), new Vector3(0.025f, 0.34f, 0.012f));
            // Shoulder straps over the front of the chest, with a sternum strap.
            k.Box(new Vector3(-0.11f, 0.33f, 0.122f), new Vector3(0.04f, 0.26f, 0.012f));
            k.Box(new Vector3(0.11f, 0.33f, 0.122f), new Vector3(0.04f, 0.26f, 0.012f));
            k.Box(new Vector3(0, 0.37f, 0.126f), new Vector3(0.22f, 0.018f, 0.01f));
            k.Use(Slot.Rope);
            for (int i = 0; i < 3; i++)
            {
                k.Torus(new Vector3(0, 0.49f + i * 0.012f, -0.17f), Vector3.up, 0.1f - i * 0.006f, 0.016f, 10);
            }
        }

        /// <summary>Head, helmet, goggles and headlamp. Origin at the base of the neck, +z is where they look.</summary>
        public static void BuildHead(MeshKit k)
        {
            // Neck gaiter and a face covered by a balaclava.
            k.Use(Slot.Buff);
            k.Tube(new Vector3(0, -0.02f, 0), new Vector3(0, 0.08f, 0.01f), 0.065f, 0.06f, 7);
            k.Block(new Vector3(0, 0.06f, 0.015f), 0.13f, 0.16f, 0.18f, 0.15f, 0.17f, 0.005f);
            k.Block(new Vector3(0, 0.07f, 0.09f), 0.06f, 0.1f, 0.04f, 0.12f, 0.03f, 0.01f); // jaw/mouth guard

            // Goggles: strap all the way round and a wide dark lens.
            k.Use(Slot.Webbing).Block(new Vector3(0, 0.15f, 0.015f), 0.03f, 0.175f, 0.195f, 0.175f, 0.195f);
            k.Use(Slot.Visor).Block(new Vector3(0, 0.135f, 0.1f), 0.06f, 0.16f, 0.035f, 0.15f, 0.03f, 0.008f);
            k.Use(Slot.JacketTrim).Block(new Vector3(0, 0.128f, 0.098f), 0.074f, 0.172f, 0.025f, 0.162f, 0.025f, 0.008f);

            // Hard-shell helmet: faceted dome, rim, ridge and vents.
            k.Use(Slot.Helmet);
            k.Ellipsoid(new Vector3(0, 0.19f, 0.01f), new Vector3(0.118f, 0.1f, 0.135f), 4, 10, true);
            k.Torus(new Vector3(0, 0.19f, 0.01f), Vector3.up, 0.124f, 0.012f, 10, 4);
            k.Use(Slot.Webbing);
            // Chin straps.
            k.Box(new Vector3(-0.085f, 0.12f, 0.03f), new Vector3(0.01f, 0.12f, 0.015f));
            k.Box(new Vector3(0.085f, 0.12f, 0.03f), new Vector3(0.01f, 0.12f, 0.015f));

            // Headlamp on the front of the helmet.
            k.Use(Slot.Webbing).Box(new Vector3(0, 0.215f, 0.135f), new Vector3(0.07f, 0.045f, 0.035f));
            k.Use(Slot.Lamp).Tube(new Vector3(0, 0.215f, 0.15f), new Vector3(0, 0.215f, 0.158f), 0.017f, 0.017f, 8);
        }

        /// <summary>Upper arm sleeve, along +z from shoulder (0) to elbow (1). Scale z by the bone length.</summary>
        public static void BuildUpperArm(MeshKit k)
        {
            k.Use(Slot.Jacket).Tube(new Vector3(0, 0, -0.02f), new Vector3(0, 0, 1.03f), 0.058f, 0.047f, 7);
            k.Use(Slot.JacketTrim).Tube(new Vector3(0, 0, 0.45f), new Vector3(0, 0, 0.52f), 0.06f, 0.059f, 7, false);
        }

        /// <summary>Forearm sleeve, along +z from elbow (0) to wrist (1).</summary>
        public static void BuildForearm(MeshKit k)
        {
            k.Use(Slot.Jacket).Tube(new Vector3(0, 0, -0.03f), new Vector3(0, 0, 0.9f), 0.048f, 0.04f, 7);
            k.Use(Slot.JacketTrim).Tube(new Vector3(0, 0, 0.84f), new Vector3(0, 0, 1.01f), 0.045f, 0.043f, 7);
        }

        /// <summary>Thigh, along +z from hip (0) to knee (1), with the harness leg loop.</summary>
        public static void BuildThigh(MeshKit k)
        {
            k.Use(Slot.Pants).Tube(new Vector3(0, 0, -0.04f), new Vector3(0, 0, 1.02f), 0.078f, 0.06f, 7);
            k.Use(Slot.Webbing).Tube(new Vector3(0, 0, 0.12f), new Vector3(0, 0, 0.2f), 0.082f, 0.08f, 7, false);
        }

        /// <summary>Shin, along +z from knee (0) to ankle (1), with a knee patch.</summary>
        public static void BuildShin(MeshKit k)
        {
            k.Use(Slot.Pants).Tube(new Vector3(0, 0, -0.03f), new Vector3(0, 0, 0.95f), 0.06f, 0.048f, 7);
            k.Use(Slot.JacketTrim).Tube(new Vector3(0, 0, 0.0f), new Vector3(0, 0, 0.16f), 0.064f, 0.062f, 7, false);
        }

        /// <summary>Approach boot. Origin at the ankle, +z toward the toes, +y up.</summary>
        public static void BuildBoot(MeshKit k)
        {
            k.Use(Slot.Boot);
            k.Block(new Vector3(0, -AnkleHeight + 0.02f, 0.035f), 0.07f, 0.1f, 0.26f, 0.095f, 0.2f, 0.01f);
            k.Block(new Vector3(0, -0.02f, -0.005f), 0.08f, 0.09f, 0.11f, 0.085f, 0.1f);
            k.Use(Slot.Webbing).Block(new Vector3(0, -AnkleHeight, 0.035f), 0.022f, 0.105f, 0.275f, 0.105f, 0.27f);
            k.Use(Slot.Metal).Box(new Vector3(0, 0.0f, 0.06f), new Vector3(0.05f, 0.012f, 0.05f));
        }

        /// <summary>A gloved fist gripping a hold. Origin at the wrist, +z toward the knuckles, +y the back of the hand.</summary>
        public static void BuildFist(MeshKit k, bool left)
        {
            float thumbSide = left ? 1f : -1f;
            k.Use(Slot.Glove);
            k.Block(new Vector3(0, -0.04f, 0.0f), 0.08f, 0.085f, 0.09f, 0.09f, 0.1f, 0.01f);
            k.Block(new Vector3(0, -0.035f, 0.08f), 0.065f, 0.09f, 0.05f, 0.09f, 0.04f, 0.0f);
            k.Tube(new Vector3(thumbSide * 0.045f, -0.02f, 0.02f), new Vector3(thumbSide * 0.03f, -0.03f, 0.08f), 0.017f, 0.014f, 5);
            k.Use(Slot.JacketTrim).Tube(new Vector3(0, 0, -0.02f), new Vector3(0, 0, 0.015f), 0.05f, 0.05f, 7);
        }

        /// <summary>An open gloved hand reaching for a hold. Same space as <see cref="BuildFist"/>.</summary>
        public static void BuildOpenHand(MeshKit k, bool left)
        {
            float thumbSide = left ? 1f : -1f;
            k.Use(Slot.Glove);
            k.Block(new Vector3(0, -0.015f, 0.0f), 0.03f, 0.085f, 0.09f, 0.09f, 0.1f, 0.0f);
            for (int i = 0; i < 4; i++)
            {
                float x = (i - 1.5f) * 0.021f * -thumbSide;
                float reach = 0.085f + (i == 1 || i == 2 ? 0.012f : 0f);
                k.Tube(new Vector3(x, 0f, 0.09f), new Vector3(x, -0.01f, 0.09f + reach), 0.011f, 0.009f, 5);
            }
            k.Tube(new Vector3(thumbSide * 0.045f, -0.005f, 0.03f), new Vector3(thumbSide * 0.075f, -0.01f, 0.085f), 0.014f, 0.011f, 5);
            k.Use(Slot.JacketTrim).Tube(new Vector3(0, 0, -0.02f), new Vector3(0, 0, 0.015f), 0.05f, 0.05f, 7);
        }
    }
}
