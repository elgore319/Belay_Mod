using System.Collections.Generic;
using Belay.Players.Avatar;
using Steamworks;
using UnityEngine;

namespace Belay.Revive
{
    /// <summary>
    /// Totem revive visuals: teammates see the body rebuilt (<see cref="Reform"/>); the revived player's view
    /// opens outward through an ember glow.
    /// </summary>
    public class TotemRevive : MonoBehaviour
    {
        private static readonly Dictionary<SteamId, float> marked = new Dictionary<SteamId, float>();

        /// <summary>How long a marked revive waits for the body to reappear (they may wait for the ground).</summary>
        private const float MarkLife = 60f;

        internal static void Mark(SteamId target)
        {
            marked[target] = Time.unscaledTime;
        }

        /// <summary>True once for a teammate who was just brought back with a totem.</summary>
        internal static bool TakeReform(SteamId target)
        {
            if (!marked.TryGetValue(target, out float at))
            {
                return false;
            }
            marked.Remove(target);
            return Time.unscaledTime - at < MarkLife;
        }

        // ------------------------------------------------------------------ First person

        private const float ViewDuration = 1.3f;
        private static TotemRevive instance;
        private static Texture2D ring;
        private float start = -10f;

        internal static void PlayFirstPerson()
        {
            if (instance == null)
            {
                var go = new GameObject("Belay_TotemRevive");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<TotemRevive>();
            }
            instance.start = Time.unscaledTime;
        }

        /// <summary>Clear in the middle, an ember ring at half radius, dark outside it.</summary>
        private static Texture2D Ring()
        {
            if (ring != null)
            {
                return ring;
            }
            const int n = 128;
            ring = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[n * n];
            Color ember = Reform.GlowColor;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float d = new Vector2(x + 0.5f - n / 2f, y + 0.5f - n / 2f).magnitude / (n / 2f);
                    // 0..0.4 clear, 0.4..0.55 ember, then fading into black by the edge.
                    float glow = Mathf.Clamp01(1f - Mathf.Abs(d - 0.48f) / 0.1f);
                    float dark = Mathf.Clamp01((d - 0.45f) / 0.25f);
                    Color c = Color.Lerp(ember * glow, Color.black, dark * (1f - glow * 0.6f));
                    c.a = Mathf.Max(glow, dark);
                    px[y * n + x] = c;
                }
            }
            ring.SetPixels32(px);
            ring.Apply(false, true);
            return ring;
        }

        private void OnGUI()
        {
            float t = Time.unscaledTime - start;
            if (t < 0f || t > ViewDuration || Event.current.type != EventType.Repaint)
            {
                return;
            }
            GUI.depth = -900;
            float p = t / ViewDuration;
            float eased = p * p;
            float diagonal = Mathf.Sqrt(Screen.width * Screen.width + Screen.height * Screen.height);
            // The clear middle grows from a pinhole until the ring has passed the screen's corners.
            float size = Mathf.Lerp(diagonal * 0.06f, diagonal * 2.6f, eased);
            var rect = new Rect((Screen.width - size) * 0.5f, (Screen.height - size) * 0.5f, size, size);
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 1f - Mathf.Clamp01((p - 0.75f) / 0.25f));
            GUI.DrawTexture(rect, Ring());
            // Black around the ring while it's smaller than the screen.
            Texture2D black = Texture2D.whiteTexture;
            Color dim = Color.black;
            dim.a = GUI.color.a;
            GUI.color = dim;
            if (rect.yMin > 0f) GUI.DrawTexture(new Rect(0, 0, Screen.width, rect.yMin + 1f), black);
            if (rect.yMax < Screen.height) GUI.DrawTexture(new Rect(0, rect.yMax - 1f, Screen.width, Screen.height - rect.yMax + 1f), black);
            if (rect.xMin > 0f) GUI.DrawTexture(new Rect(0, rect.yMin, rect.xMin + 1f, rect.height), black);
            if (rect.xMax < Screen.width) GUI.DrawTexture(new Rect(rect.xMax - 1f, rect.yMin, Screen.width - rect.xMax + 1f, rect.height), black);
            GUI.color = old;
        }
    }
}
