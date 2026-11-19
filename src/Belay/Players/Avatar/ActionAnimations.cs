using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>Something a player just did with the item in one hand, shown on their avatar.</summary>
    public enum PlayerAction : byte
    {
        None = 0,
        /// <summary>A hammer (or any melee item) swing.</summary>
        Swing = 1,
        /// <summary>A charged melee swing: a bigger wind-up and a harder strike.</summary>
        HeavySwing = 2,
        /// <summary>Holding a melee item back, charging a heavy swing. Lasts until the swing.</summary>
        Charge = 3,
        /// <summary>Setting a piton: pressing it to the wall and knocking it in.</summary>
        Place = 4,
        /// <summary>Dropping or throwing the held item.</summary>
        Toss = 5,
        /// <summary>Firing a shooting item (recoil).</summary>
        Shoot = 6,
        /// <summary>Eating or taking a consumable: the hand goes to the mouth.</summary>
        Eat = 7,
        /// <summary>Any other item use: the item is raised in front.</summary>
        Use = 8,
    }

    /// <summary>
    /// Arm motions for <see cref="PlayerAction"/>s: keyframes of hand position relative to the shoulder in look
    /// space (x side, y up, z forward, meters at 1.8 m), plus wrist bend and torso lean. Timed like the game's 2D
    /// item animations.
    /// </summary>
    public static class ActionAnimations
    {
        public struct Key
        {
            public float Time;
            public Vector3 Hand;
            public float Wrist;
            public float Lean;

            public Key(float time, float x, float y, float z, float wrist, float lean = 0f)
            {
                Time = time;
                Hand = new Vector3(x, y, z);
                Wrist = wrist;
                Lean = lean;
            }
        }

        public struct Sample
        {
            /// <summary>0 = the arm's normal pose, 1 = fully in the action.</summary>
            public float Weight;
            public Vector3 Hand;
            public float Wrist;
            public float Lean;
            /// <summary>The hand position is relative to the mouth instead of the shoulder.</summary>
            public bool AtMouth;
            /// <summary>Hammer grip: thumb up, so <see cref="Wrist"/> swings the head forward and down.</summary>
            public bool ThumbUp;
        }

        private static readonly Key[] Swing =
        {
            new Key(0.00f, 0.10f, 0.05f, 0.30f, 0f),
            new Key(0.13f, 0.14f, 0.38f, -0.02f, -65f, -4f),  // wound up above the shoulder
            new Key(0.23f, 0.04f, -0.08f, 0.50f, 45f, 8f),    // strike
            new Key(0.32f, 0.06f, -0.04f, 0.46f, 35f, 6f),
            new Key(0.50f, 0.10f, 0.02f, 0.32f, 0f),
        };

        private static readonly Key[] HeavySwing =
        {
            new Key(0.00f, 0.16f, 0.45f, -0.10f, -75f, -8f),  // already back from the charge
            new Key(0.10f, 0.18f, 0.48f, -0.16f, -80f, -10f),
            new Key(0.22f, 0.02f, -0.12f, 0.52f, 55f, 16f),   // big strike, whole body behind it
            new Key(0.36f, 0.04f, -0.10f, 0.48f, 45f, 12f),
            new Key(0.60f, 0.10f, 0.02f, 0.32f, 0f),
        };

        private static readonly Key[] Charge =
        {
            new Key(0.00f, 0.10f, 0.05f, 0.30f, 0f),
            new Key(0.30f, 0.16f, 0.45f, -0.10f, -75f, -8f),
        };

        private static readonly Key[] Place =
        {
            new Key(0.00f, 0.08f, 0.05f, 0.30f, 0f),
            new Key(0.12f, 0.04f, 0.10f, 0.50f, 10f, 4f),     // press it to the wall
            new Key(0.20f, 0.04f, 0.12f, 0.40f, -15f, 2f),
            new Key(0.27f, 0.04f, 0.10f, 0.52f, 15f, 5f),     // knock
            new Key(0.34f, 0.04f, 0.12f, 0.42f, -10f, 2f),
            new Key(0.41f, 0.04f, 0.10f, 0.52f, 15f, 5f),     // knock
            new Key(0.60f, 0.08f, 0.05f, 0.30f, 0f),
        };

        private static readonly Key[] Toss =
        {
            new Key(0.00f, 0.10f, 0.00f, 0.25f, 0f),
            new Key(0.12f, 0.18f, 0.20f, -0.05f, -30f, -4f),  // draw back
            new Key(0.24f, 0.05f, 0.12f, 0.52f, 25f, 8f),     // let go
            new Key(0.45f, 0.08f, 0.02f, 0.35f, 0f),
        };

        private static readonly Key[] Shoot =
        {
            new Key(0.00f, 0.06f, 0.08f, 0.48f, 0f),
            new Key(0.04f, 0.06f, 0.14f, 0.38f, -30f, -4f),   // kick
            new Key(0.30f, 0.06f, 0.08f, 0.48f, 0f),
        };

        private static readonly Key[] Eat =
        {
            new Key(0.00f, 0.05f, -0.20f, 0.18f, 0f),
            new Key(0.25f, 0.03f, -0.06f, 0.14f, 40f),        // up to the mouth
            new Key(0.55f, 0.03f, -0.04f, 0.12f, 50f),
            new Key(0.80f, 0.03f, -0.06f, 0.14f, 40f),
            new Key(1.05f, 0.03f, -0.04f, 0.12f, 50f),
            new Key(1.30f, 0.05f, -0.20f, 0.18f, 0f),
        };

        private static readonly Key[] Use =
        {
            new Key(0.00f, 0.08f, 0.00f, 0.30f, 0f),
            new Key(0.15f, 0.06f, 0.12f, 0.46f, 10f),         // held out in front
            new Key(0.35f, 0.06f, 0.12f, 0.46f, 10f),
            new Key(0.55f, 0.08f, 0.00f, 0.30f, 0f),
        };

        private static Key[] KeysOf(PlayerAction action)
        {
            switch (action)
            {
                case PlayerAction.Swing: return Swing;
                case PlayerAction.HeavySwing: return HeavySwing;
                case PlayerAction.Charge: return Charge;
                case PlayerAction.Place: return Place;
                case PlayerAction.Toss: return Toss;
                case PlayerAction.Shoot: return Shoot;
                case PlayerAction.Eat: return Eat;
                case PlayerAction.Use: return Use;
                default: return null;
            }
        }

        /// <summary>A charge is held until the swing (or until it's been this long, in case the swing got lost).</summary>
        public const float MaxChargeHold = 8f;

        /// <summary>Whether the action is still playing <paramref name="elapsed"/> seconds after it started.</summary>
        public static bool Playing(PlayerAction action, float elapsed)
        {
            Key[] keys = KeysOf(action);
            if (keys == null)
            {
                return false;
            }
            return action == PlayerAction.Charge ? elapsed < MaxChargeHold : elapsed < keys[keys.Length - 1].Time;
        }

        /// <summary>Where the hand is <paramref name="elapsed"/> seconds into the action.</summary>
        public static Sample Evaluate(PlayerAction action, float elapsed)
        {
            var sample = new Sample { AtMouth = action == PlayerAction.Eat };
            Key[] keys = KeysOf(action);
            if (keys == null || !Playing(action, elapsed))
            {
                return sample;
            }
            float end = keys[keys.Length - 1].Time;
            if (action == PlayerAction.Charge)
            {
                // Wind up, then hold there with a slight tremble while it charges.
                Key held = keys[keys.Length - 1];
                sample = Interpolate(keys, Mathf.Min(elapsed, end));
                sample.AtMouth = false;
                sample.ThumbUp = true;
                if (elapsed > end)
                {
                    float shake = Mathf.Sin(elapsed * 47f) * 0.006f + Mathf.Sin(elapsed * 31f) * 0.004f;
                    sample.Hand = held.Hand + new Vector3(shake, shake * 0.7f, 0f);
                }
                sample.Weight = Mathf.Clamp01(elapsed / 0.08f);
                return sample;
            }
            sample = Interpolate(keys, elapsed);
            sample.AtMouth = action == PlayerAction.Eat;
            sample.ThumbUp = action == PlayerAction.Swing || action == PlayerAction.HeavySwing || action == PlayerAction.Place;
            // Blend in quickly (the first key is near the arm's usual spot) and out over the last stretch.
            // A heavy swing starts from the charge pose, so it's at full weight right away.
            float fadeIn = action == PlayerAction.HeavySwing ? 1f : Mathf.Clamp01(elapsed / 0.06f);
            float fadeOut = Mathf.Clamp01((end - elapsed) / Mathf.Min(0.15f, end * 0.4f));
            float w = Mathf.Min(fadeIn, fadeOut);
            sample.Weight = w * w * (3f - 2f * w);
            return sample;
        }

        private static Sample Interpolate(Key[] keys, float t)
        {
            int i = 0;
            while (i < keys.Length - 2 && t > keys[i + 1].Time)
            {
                i++;
            }
            Key a = keys[i], b = keys[Mathf.Min(i + 1, keys.Length - 1)];
            float span = Mathf.Max(1e-4f, b.Time - a.Time);
            float u = Mathf.Clamp01((t - a.Time) / span);
            u = u * u * (3f - 2f * u);
            return new Sample
            {
                Hand = Vector3.Lerp(a.Hand, b.Hand, u),
                Wrist = Mathf.Lerp(a.Wrist, b.Wrist, u),
                Lean = Mathf.Lerp(a.Lean, b.Lean, u),
            };
        }
    }
}
