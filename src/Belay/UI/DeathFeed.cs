using System.Collections.Generic;
using System.Text.RegularExpressions;
using Belay.Net;
using Steamworks;
using UnityEngine;

namespace Belay.UI
{
    /// <summary>
    /// Death messages. When anyone in the lobby dies, everyone sees their name and how they died near the top of the
    /// screen, using the game's own death-screen lines ("EATEN ALIVE", "FUSED INTO THE MASS", ...) so they read like the
    /// game's own text. Each line fades out after a few seconds.
    /// </summary>
    internal class DeathFeed : MonoBehaviour
    {
        public static DeathFeed Instance { get; private set; }

        private const float Lifetime = 6f;
        private const float FadeTime = 1f;
        private const int MaxLines = 4;
        private static readonly Regex Your = new Regex(@"\bYOUR\b", RegexOptions.IgnoreCase);

        private struct Line
        {
            public string Name;
            public string Cause;
            public float Shown;
        }

        private readonly List<Line> lines = new List<Line>();
        private readonly Dictionary<SteamId, float> lastDeath = new Dictionary<SteamId, float>();
        private GUIStyle style;
        private Font styleFont;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.PlayerDied, (origin, r) => Show(origin, r.ReadString()));
        }

        /// <summary>The local player died in a lobby: tell everyone and show it here too.</summary>
        public static void LocalDied(string type)
        {
            NetSession net = NetSession.Instance;
            if (Instance == null || net == null || !net.InLobby)
            {
                return;
            }
            string sent = type ?? "";
            net.Send(PacketType.PlayerDied, w => w.Write(sent));
            Instance.Show(NetSession.LocalId, sent);
        }

        /// <summary>Shows a death message for this member (from the network, the local player, or a local bot).</summary>
        public void Show(SteamId who, string type)
        {
            // Some deaths reach the game twice in a row; one message is enough.
            if (lastDeath.TryGetValue(who, out float at) && Time.unscaledTime - at < 2f)
            {
                return;
            }
            lastDeath[who] = Time.unscaledTime;
            string name = NetSession.NameOf(who);
            if (string.IsNullOrEmpty(name))
            {
                name = "SOMEONE";
            }
            lines.Add(new Line { Name = name.ToUpperInvariant(), Cause = CauseText(type), Shown = Time.unscaledTime });
            while (lines.Count > MaxLines)
            {
                lines.RemoveAt(0);
            }
            Plugin.Log.LogInfo($"{name} died ({type})");
        }

        /// <summary>The game's own death-screen line for this kind of death, said about someone else.</summary>
        private static string CauseText(string type)
        {
            string text = string.IsNullOrEmpty(type) ? "" : CL_LocalizationManager.GetLocalization("deathmessages", type);
            if (string.IsNullOrEmpty(text))
            {
                text = CL_LocalizationManager.GetLocalization("deathmessages", "basic");
            }
            if (string.IsNullOrEmpty(text))
            {
                text = "KILLED MYSTERIOUSLY";
            }
            // "FELL TO YOUR DEATH" is written to the player who died.
            return Your.Replace(text, "THEIR").ToUpperInvariant();
        }

        private void Update()
        {
            if (NetSession.Instance == null || !NetSession.Instance.InLobby)
            {
                lines.Clear();
                lastDeath.Clear();
                return;
            }
            lines.RemoveAll(l => Time.unscaledTime - l.Shown > Lifetime);
        }

        private void OnGUI()
        {
            if (lines.Count == 0 || Event.current.type != EventType.Repaint)
            {
                return;
            }
            float scale = Screen.height / 1080f;
            int size = Mathf.Max(14, Mathf.RoundToInt(30f * scale));
            Font font = GameFont.Font;
            if (style == null || styleFont != font)
            {
                style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, wordWrap = false, richText = false };
                style.padding = new RectOffset(0, 0, 0, 0);
                style.margin = new RectOffset(0, 0, 0, 0);
                if (font != null)
                {
                    style.font = font;
                }
                styleFont = font;
            }
            style.fontSize = size;

            float y = Screen.height * 0.14f;
            float gap = size * 0.5f;
            Color previous = GUI.color;
            foreach (Line line in lines)
            {
                float age = Time.unscaledTime - line.Shown;
                float alpha = Mathf.Clamp01((Lifetime - age) / FadeTime) * Mathf.Clamp01(age / 0.15f);
                string name = line.Name;
                Vector2 nameSize = style.CalcSize(new GUIContent(name));
                Vector2 causeSize = style.CalcSize(new GUIContent(line.Cause));
                float x = (Screen.width - (nameSize.x + gap + causeSize.x)) / 2f;
                float h = Mathf.Max(nameSize.y, causeSize.y);
                DrawText(new Rect(x, y, nameSize.x + 4f, h), name, BelayTheme.Text, alpha, scale);
                DrawText(new Rect(x + nameSize.x + gap, y, causeSize.x + 4f, h), line.Cause, BelayTheme.Red, alpha, scale);
                y += h + 4f * scale;
            }
            GUI.color = previous;
        }

        private void DrawText(Rect rect, string text, Color color, float alpha, float scale)
        {
            float offset = Mathf.Max(1f, 2f * scale);
            GUI.color = new Color(0f, 0f, 0f, 0.85f * alpha);
            style.normal.textColor = Color.white;
            GUI.Label(new Rect(rect.x + offset, rect.y + offset, rect.width, rect.height), text, style);
            GUI.color = new Color(color.r, color.g, color.b, alpha);
            GUI.Label(rect, text, style);
        }
    }
}
