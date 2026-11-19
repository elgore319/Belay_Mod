using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>
    /// How a teammate's avatar grips the item in its hand. Pure vector math (no engine calls), so the
    /// avatar preview tool poses items exactly like the game does.
    ///
    /// Template space: the origin is where the fist closes, +X runs along the item from the fist toward
    /// its heavy end (a hammer's head), +Z is the item's second-longest direction (the way a hammer head
    /// points) and +Y = Z x X. In the fist, +X comes out of the thumb side, so with the thumb up a
    /// hammer stands up out of the fist with its head forward.
    /// </summary>
    public static class HeldGrip
    {
        /// <summary>Per-item corrections where the shape alone doesn't say how it's held.</summary>
        public struct Tweak
        {
            /// <summary>Where the fist closes, as a fraction of the length from the light end (negative: automatic).</summary>
            public float GripAt;
            /// <summary>Hold the other end (the shape guessed the heavy end wrong).</summary>
            public bool FlipAxis;
            /// <summary>Turn the item about its own length (degrees), e.g. so a blade's edge faces forward.</summary>
            public float Roll;
            /// <summary>Tilt the item forward out of the fist (degrees), like a hammer held ready.</summary>
            public float Tilt;
            /// <summary>Held flat in the palm (food, notes, small things) instead of gripped like a tool.</summary>
            public bool Palm;
            /// <summary>
            /// A pistol grip, given in the item's own (world model) space: where the hand closes, which way
            /// the barrel points and which way is the top of the gun. Overrides the shape guess.
            /// </summary>
            public bool Pistol;
            public Vector3 PistolGrip, PistolForward, PistolUp;
        }

        /// <summary>
        /// How big the held copy is next to the item lying in the world. The game's world items are
        /// chunky (a hammer is 0.7 m long, a can of beans 0.3 m tall) so they read on the ground; in a
        /// teammate's fist at that size they look like props from a cartoon.
        /// </summary>
        public const float HeldScale = 0.5f;

        /// <summary>Nothing held is longer than this (rebar is over 2 m in the world).</summary>
        public const float MaxHeldLength = 0.9f;

        /// <summary>Scale for the held copy of an item <paramref name="length"/> long (in the world).</summary>
        public static float SizeFor(string prefabName, float length)
        {
            if (ItemModels.HasModel(prefabName))
            {
                return 1f;
            }
            bool tool = IsTool((prefabName ?? "").ToLowerInvariant());
            float scale = tool ? HeldScale : ObjectScale;
            float max = tool ? MaxHeldLength : MaxObjectLength;
            if (length * scale > max)
            {
                scale = max / Mathf.Max(0.01f, length);
            }
            return scale;
        }

        /// <summary>The default tilt: tools lean forward out of the fist instead of standing straight across it.</summary>
        public const float DefaultTilt = 20f;

        public static Tweak TweakFor(string prefabName, float length)
        {
            var t = new Tweak { GripAt = -1f, Tilt = DefaultTilt };
            string n = (prefabName ?? "").ToLowerInvariant();
            if (ItemModels.IsTotem(prefabName))
            {
                // Our own totem model is built with the grip at the origin, standing straight up.
                t.GripAt = 0f;
                t.Tilt = 0f;
            }
            else if (n.Contains("rapier"))
            {
                // Our own sword model is built with the grip at the origin and the blade along +X.
                t.GripAt = 0f;
                t.Tilt = 30f;
            }
            else if (n.Contains("cleaver"))
            {
                // A machete: by the handle, blade up and leaning forward, cutting edge to the front.
                t.GripAt = 0.12f;
                t.Roll = 180f;
                t.Tilt = 35f;
            }
            else if (n.Contains("hammer") || n.Contains("pipewrench"))
            {
                t.GripAt = 0.12f;
            }
            else if (n.Contains("rebar"))
            {
                t.GripAt = 0.3f;
                t.Tilt = 35f;
            }
            else if (n.Contains("piton"))
            {
                // Held by the eye (its heavy end) with the point out past the knuckles, toward the wall.
                t.FlipAxis = true;
                t.GripAt = 0.18f;
                t.Tilt = 80f;
            }
            else if (n.Contains("handgun"))
            {
                // The model lies flat: the barrel runs along -Z and the grip sticks out to -X at the back
                // (+Z) end. The fist closes round the middle of that grip.
                t = Pistol(new Vector3(-0.15f, 0f, 0.21f), Vector3.back, Vector3.right);
            }
            else if (n.Contains("flaregun"))
            {
                // Its grip is raked far back; turn the gun so the grip runs down through the fist, with
                // the fist up under the trigger guard.
                t = Pistol(new Vector3(0f, 0f, 0.07f), new Vector3(0f, 1f, 0.22f).normalized, new Vector3(0f, 0.22f, -1f).normalized);
            }
            else if (length * HeldScale < 0.14f)
            {
                t.Palm = true;
                t.GripAt = 0.5f;
                t.Tilt = 0f;
            }
            else if (!IsLongTool(n))
            {
                // Cans, bottles, cells, gadgets: held round the middle, standing upright in the fist.
                t.GripAt = 0.5f;
                t.Tilt = 0f;
            }
            return t;
        }

        private static bool IsLongTool(string n)
        {
            return n.Contains("hook") || n.Contains("injector") || n.Contains("wrench");
        }

        /// <summary>Gadgets and food aren't held at tool size: nothing but tools is longer than this in the hand.</summary>
        public const float MaxObjectLength = 0.2f;

        /// <summary>Gadgets, guns and food shrink a little more than tools: the world models are chunkier still.</summary>
        public const float ObjectScale = 0.4f;

        private static bool IsTool(string n)
        {
            return n.Contains("hammer") || n.Contains("cleaver") || n.Contains("wrench") || n.Contains("rebar") || n.Contains("piton")
                || n.Contains("rapier") || IsLongTool(n);
        }

        private static Tweak Pistol(Vector3 grip, Vector3 forward, Vector3 up)
        {
            return new Tweak { GripAt = -1f, Pistol = true, PistolGrip = grip, PistolForward = forward, PistolUp = up };
        }

        /// <summary>
        /// Root space to template space for an item whose renderers span <paramref name="min"/>..<paramref name="max"/>
        /// (in the item's root space) with most of their mass around <paramref name="mass"/>.
        /// </summary>
        public static Matrix4x4 Fit(Vector3 min, Vector3 max, Vector3 mass, string prefabName, bool plain = false)
        {
            Vector3 size = max - min;
            Vector3 center = (min + max) * 0.5f;
            int longest = 0;
            for (int i = 1; i < 3; i++)
            {
                if (size[i] > size[longest])
                {
                    longest = i;
                }
            }
            int second = (longest + 1) % 3, third = (longest + 2) % 3;
            if (size[third] > size[second])
            {
                second = third;
            }
            Vector3 axis = Vector3.zero, across = Vector3.zero;
            axis[longest] = 1f;
            across[second] = 1f;
            if (Vector3.Dot(mass - center, axis) < 0f)
            {
                axis = -axis;
            }
            if (Vector3.Dot(mass - center, across) < 0f)
            {
                across = -across;
            }
            float length = size[longest];
            Tweak tweak = plain ? new Tweak { GripAt = -1f } : TweakFor(prefabName, length);
            if (tweak.Pistol)
            {
                // The top of the gun comes out of the thumb side (+X) and the barrel runs along the
                // forearm out past the knuckles (+Z, as the tilt is 0).
                return Basis(tweak.PistolUp, tweak.PistolForward, tweak.PistolGrip);
            }
            if (tweak.FlipAxis)
            {
                axis = -axis;
            }
            float gripAt = tweak.GripAt >= 0f ? tweak.GripAt : (length > 0.25f ? 0.2f : 0.5f);
            Vector3 grip = center + axis * (length * (gripAt - 0.5f));
            if (tweak.Palm)
            {
                // Sits in the palm: centered, not poking out of the fist.
                grip = center;
            }
            return Basis(axis, across, grip);
        }

        private static Matrix4x4 Basis(Vector3 axis, Vector3 across, Vector3 grip)
        {
            Vector3 y = Vector3.Cross(across, axis);
            var m = Matrix4x4.identity;
            m.SetRow(0, new Vector4(axis.x, axis.y, axis.z, -Vector3.Dot(axis, grip)));
            m.SetRow(1, new Vector4(y.x, y.y, y.z, -Vector3.Dot(y, grip)));
            m.SetRow(2, new Vector4(across.x, across.y, across.z, -Vector3.Dot(across, grip)));
            return m;
        }

        /// <summary>
        /// Template space to the fist's local space (the fist is built at avatar scale, so
        /// <paramref name="avatarScale"/> undoes that and the item keeps its real size).
        /// </summary>
        public static Matrix4x4 InFist(bool left, string prefabName, float length, float avatarScale)
        {
            Tweak tweak = TweakFor(prefabName, length);
            // Where the curled fingers close: inside the fist, under the knuckles.
            Vector3 hole = tweak.Palm ? new Vector3(left ? 0.012f : -0.012f, -0.05f, 0.075f) : new Vector3(0f, -0.036f, 0.105f);
            // Out of the thumb side (+X for the left hand, -X for the right), leaning forward (toward the
            // knuckles, +Z) by the tilt, and turned about its own length by the roll.
            float side = left ? 1f : -1f;
            float tilt = tweak.Tilt * Mathf.Deg2Rad;
            Vector3 x = new Vector3(side * Mathf.Cos(tilt), 0f, Mathf.Sin(tilt));
            Vector3 z0 = new Vector3(-side * Mathf.Sin(tilt), 0f, Mathf.Cos(tilt));
            Vector3 y0 = Vector3.Cross(z0, x);
            float roll = tweak.Roll * Mathf.Deg2Rad;
            Vector3 z = z0 * Mathf.Cos(roll) + y0 * Mathf.Sin(roll);
            Vector3 y = Vector3.Cross(z, x);
            float s = SizeFor(prefabName, length) / Mathf.Max(0.01f, avatarScale);
            var m = Matrix4x4.identity;
            m.SetColumn(0, new Vector4(x.x, x.y, x.z, 0f) * s);
            m.SetColumn(1, new Vector4(y.x, y.y, y.z, 0f) * s);
            m.SetColumn(2, new Vector4(z.x, z.y, z.z, 0f) * s);
            m.SetColumn(3, new Vector4(hole.x, hole.y, hole.z, 1f));
            return m;
        }

        /// <summary>Guns and gadgets you point at things.</summary>
        public static bool Aims(string prefabName)
        {
            string n = (prefabName ?? "").ToLowerInvariant();
            return n.Contains("gun") || n.Contains("scanner") || n.Contains("flashlight") || n.Contains("remote");
        }

        /// <summary>
        /// Which way the hand points: along the forearm, except that a hand held out with a gun bends at
        /// the wrist to point it where the player looks (by <paramref name="reach"/>, 0 at rest by the side).
        /// </summary>
        public static Vector3 HandForward(Vector3 forearm, Vector3 look, string prefabName, float reach)
        {
            if (!Aims(prefabName) || reach <= 0f)
            {
                return forearm;
            }
            Vector3 f = Vector3.Lerp(forearm, look, Mathf.Clamp01(reach));
            return f.sqrMagnitude > 1e-6f ? f.normalized : forearm;
        }

        /// <summary>
        /// Which way the back of the hand faces. Empty hands: up when reaching forward, away from the wall
        /// when reaching up, out to the side when the arm hangs down. A hand holding an item turns its
        /// back out to the side whenever it isn't reaching up, so the thumb (and the item) points up
        /// when the arm is forward and forward when the arm hangs, like a person holding a tool.
        /// </summary>
        public static Vector3 HandBack(Vector3 forearm, Vector3 up, Vector3 forward, Vector3 right, float side, bool holdsItem)
        {
            float rise = Vector3.Dot(forearm, up);
            Vector3 outward = right * side;
            if (holdsItem)
            {
                Vector3 back = rise > 0.6f ? Vector3.Lerp(outward, -forward, (rise - 0.6f) / 0.4f) : outward;
                // An arm straight out to the side: the back of the hand faces up instead.
                float sideways = Mathf.Abs(Vector3.Dot(forearm, outward));
                return Vector3.Lerp(back, up, Mathf.Clamp01((sideways - 0.75f) / 0.2f));
            }
            return rise >= 0f ? Vector3.Lerp(up, -forward, rise) : Vector3.Lerp(up, outward, -rise);
        }
    }
}
