using System.Collections;
using UnityEngine;

namespace Belay.Revive
{
    /// <summary>
    /// Sounds for revives, synthesized in code (nothing bundled, nothing taken from the game's files):
    /// wet, fleshy squelches while a body is regrown (totem and biochamber revives alike), and the
    /// biochamber's machinery: a throbbing hum with pump thumps and bubbling, a steam hiss as the seal
    /// breaks, the grind of the glass sliding round and a heavy clunk as it stops.
    /// Played through the game's own AudioManager, so its volume settings apply.
    /// </summary>
    internal static class ReviveSounds
    {
        private const int Rate = 22050;
        private static AudioClip[] squelches;
        private static AudioClip hum, hiss, slide, clunk;
        private static ReviveSoundRunner runner;

        internal static AudioClip Hum => hum != null ? hum : (hum = Make("Belay_ChamberHum", 3.4f, HumWave, 11));
        internal static AudioClip Hiss => hiss != null ? hiss : (hiss = Make("Belay_ChamberHiss", 1.5f, HissWave, 12));
        internal static AudioClip Slide => slide != null ? slide : (slide = Make("Belay_ChamberSlide", 1.15f, SlideWave, 13));
        internal static AudioClip Clunk => clunk != null ? clunk : (clunk = Make("Belay_ChamberClunk", 0.6f, ClunkWave, 14));

        private static AudioClip Squelch(int i)
        {
            if (squelches == null)
            {
                squelches = new AudioClip[6];
                for (int k = 0; k < squelches.Length; k++)
                {
                    float length = 0.22f + 0.05f * k;
                    squelches[k] = Make("Belay_Squelch" + k, length, SquelchWave, 100 + k);
                }
            }
            return squelches[i % squelches.Length];
        }

        /// <summary>Plays a clip from where this transform is (it follows it), or flat when spatial is 0.</summary>
        internal static void Play(AudioClip clip, Transform at, float volume, float pitch = 1f, float spatial = 1f)
        {
            if (clip == null || at == null)
            {
                return;
            }
            try
            {
                AudioManager.PlaySound(clip, at, volume, pitch, spatial);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Revive sound failed: " + e.Message);
            }
        }

        /// <summary>Wet squelches and crackles for as long as a body is being regrown.</summary>
        internal static void PlayFlesh(Transform at, float seconds, float spatial = 1f)
        {
            if (at == null)
            {
                return;
            }
            if (runner == null)
            {
                var go = new GameObject("Belay_ReviveSounds");
                Object.DontDestroyOnLoad(go);
                runner = go.AddComponent<ReviveSoundRunner>();
            }
            runner.StartCoroutine(Flesh(at, seconds, spatial));
        }

        private static IEnumerator Flesh(Transform at, float seconds, float spatial)
        {
            float start = Time.time;
            int i = Random.Range(0, 6);
            while (at != null && Time.time - start < seconds)
            {
                float t = (Time.time - start) / seconds;
                // Busiest in the middle, as muscle and skin knit together.
                float busy = Mathf.Sin(Mathf.Clamp01(t) * Mathf.PI);
                Play(Squelch(i++), at, 0.35f + 0.45f * busy, Random.Range(0.8f, 1.2f), spatial);
                yield return new WaitForSeconds(Mathf.Lerp(0.4f, 0.12f, busy) * Random.Range(0.7f, 1.3f));
            }
        }

        // ---------------------------------------------------------------- Synthesis

        private delegate float Wave(float t, float length, System.Random rng, ref float state);

        private static AudioClip Make(string name, float seconds, Wave wave, int seed)
        {
            int n = Mathf.CeilToInt(seconds * Rate);
            var data = new float[n];
            var rng = new System.Random(seed);
            float state = 0f;
            float peak = 1e-4f;
            for (int i = 0; i < n; i++)
            {
                data[i] = wave((float)i / Rate, seconds, rng, ref state);
                peak = Mathf.Max(peak, Mathf.Abs(data[i]));
            }
            float gain = 0.85f / peak;
            for (int i = 0; i < n; i++)
            {
                data[i] *= gain;
            }
            // Filled through the reader callback (SetData's span overloads don't resolve against this game's mscorlib).
            int read = 0;
            AudioClip clip = AudioClip.Create(name, n, 1, Rate, false, buffer =>
            {
                for (int i = 0; i < buffer.Length; i++)
                {
                    buffer[i] = read < n ? data[read] : 0f;
                    read++;
                }
            });
            Object.DontDestroyOnLoad(clip);
            return clip;
        }

        private static float Noise(System.Random rng) => (float)(rng.NextDouble() * 2.0 - 1.0);

        private static float Fade(float t, float length, float inTime, float outTime)
        {
            return Mathf.Clamp01(t / inTime) * Mathf.Clamp01((length - t) / outTime);
        }

        // Filter and oscillator state, reset at the start of each clip (clips are made one at a time).
        private static float lp1, lp2, phase;
        private static float bubbleAt = -1f, bubbleFreq;

        private static float HumWave(float t, float length, System.Random rng, ref float state)
        {
            if (t == 0f) { lp1 = 0f; bubbleAt = -1f; }
            float wobble = 1f + 0.03f * Mathf.Sin(2f * Mathf.PI * 0.7f * t);
            float s = 0.5f * Mathf.Sin(2f * Mathf.PI * 48f * t * wobble)
                + 0.28f * Mathf.Sin(2f * Mathf.PI * 96f * t)
                + 0.12f * Mathf.Sign(Mathf.Sin(2f * Mathf.PI * 144f * t)) * 0.5f;
            lp1 += 0.05f * (Noise(rng) - lp1);
            s += 0.4f * lp1;
            // Pump thumps every 0.8 s.
            float p = t % 0.8f;
            s += 0.9f * Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(75f, 38f, p / 0.18f) * p) * Mathf.Exp(-p * 22f);
            // Bubbling: short rising blips at random.
            if (bubbleAt < 0f || t - bubbleAt > 0.04f)
            {
                if (rng.NextDouble() < 14.0 / Rate)
                {
                    bubbleAt = t;
                    bubbleFreq = 280f + (float)rng.NextDouble() * 500f;
                }
            }
            if (bubbleAt >= 0f && t - bubbleAt < 0.04f)
            {
                float b = t - bubbleAt;
                s += 0.25f * Mathf.Sin(2f * Mathf.PI * bubbleFreq * (1f + b * 30f) * b) * Mathf.Sin(Mathf.PI * b / 0.04f);
            }
            return s * Fade(t, length, 0.35f, 0.6f);
        }

        private static float SquelchWave(float t, float length, System.Random rng, ref float state)
        {
            if (t == 0f) { lp1 = 0f; lp2 = 0f; phase = 0f; }
            float x = t / length;
            // Wet body: low-passed noise whose brightness wobbles, under a sinking "glorp".
            float cutoff = 0.04f + 0.1f * Mathf.Abs(Mathf.Sin(2f * Mathf.PI * (5f + state) * t));
            lp1 += cutoff * (Noise(rng) - lp1);
            lp2 += 0.3f * (lp1 - lp2);
            phase += 2f * Mathf.PI * Mathf.Lerp(190f, 70f, x) / Rate;
            float glorp = Mathf.Sin(phase + 2.5f * lp2) * Mathf.Clamp01(1f - x * 1.3f);
            // Sticky clicks.
            float click = rng.NextDouble() < 0.0015 ? Noise(rng) * 2f : 0f;
            float env = Mathf.Clamp01(t / 0.015f) * Mathf.Pow(1f - x, 1.6f);
            return (1.4f * lp2 + 0.6f * glorp + click) * env;
        }

        private static float HissWave(float t, float length, System.Random rng, ref float state)
        {
            if (t == 0f) { lp1 = 0f; }
            float n = Noise(rng);
            lp1 += 0.25f * (n - lp1);
            float high = n - lp1;
            return high * Mathf.Clamp01(t / 0.03f) * Mathf.Exp(-t * 2.2f);
        }

        private static float SlideWave(float t, float length, System.Random rng, ref float state)
        {
            if (t == 0f) { lp1 = 0f; phase = 0f; }
            lp1 += 0.06f * (Noise(rng) - lp1);
            phase += 2f * Mathf.PI * (110f + 15f * Mathf.Sin(t * 9f)) / Rate;
            float grind = (phase / Mathf.PI % 2f) - 1f; // saw
            return (1.2f * lp1 + 0.25f * grind * (0.5f + 0.5f * lp1)) * Fade(t, length, 0.12f, 0.25f);
        }

        private static float ClunkWave(float t, float length, System.Random rng, ref float state)
        {
            if (t == 0f) { lp1 = 0f; }
            lp1 += 0.1f * (Noise(rng) - lp1);
            float thud = Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(80f, 45f, t / 0.2f) * t) * Mathf.Exp(-t * 14f);
            float ring = (0.3f * Mathf.Sin(2f * Mathf.PI * 420f * t) + 0.2f * Mathf.Sin(2f * Mathf.PI * 913f * t)) * Mathf.Exp(-t * 9f);
            return thud + ring + 1.5f * lp1 * Mathf.Exp(-t * 30f);
        }
    }

    internal class ReviveSoundRunner : MonoBehaviour
    {
    }
}
