using System.Collections.Generic;
using Belay.Players;
using UnityEngine;

namespace Belay.Bot
{
    /// <summary>
    /// The test bot's point of view: a second camera at the bot's head that renders into a texture shown
    /// in a movable, resizable window, or fullscreen on a second monitor. A stand-in body for you is drawn
    /// only in this camera (your own game shows no body for you), and the bot's own body is hidden from it.
    /// </summary>
    public class BotPov : MonoBehaviour
    {
        /// <summary>
        /// How far your camera sits above your body's position (what PlayerState carries), so the bot's eye
        /// is where yours would be.
        /// </summary>
        public static float EyeHeight
        {
            get
            {
                ENT_Player player = ENT_Player.playerObject;
                if (player == null || player.cam == null || player.dead)
                {
                    return lastEyeHeight;
                }
                float h = player.cam.transform.position.y - player.transform.position.y;
                if (h > 0.1f && h < 2.5f)
                {
                    lastEyeHeight = h;
                }
                return lastEyeHeight;
            }
        }

        private static float lastEyeHeight = 0.6f;

        public static readonly string[] QualityLabels = { "Low", "Medium", "High" };
        private static readonly Vector2Int[] Resolutions = { new Vector2Int(480, 270), new Vector2Int(854, 480), new Vector2Int(1280, 720) };

        private const int WindowId = 0x8E1A8;

        public BotPlayer Watched { get; private set; }
        public bool Visible { get; private set; }
        public bool OnSecondMonitor { get; private set; }

        private int quality = 1;
        private Camera cam;
        private Camera copiedFrom;
        private RenderTexture texture;
        private RemotePlayer youStandIn;
        private Rect window = new Rect(20, 20, 480, 300);
        private bool resizing;
        private Vector2 resizeStart;
        private Vector2 sizeStart;
        private float pitch;
        private float yaw;
        private BotPlayer aimedAt;

        public static bool SecondMonitorAvailable => Display.displays.Length > 1;

        public int Quality
        {
            get => quality;
            set
            {
                quality = Mathf.Clamp(value, 0, Resolutions.Length - 1);
                ReleaseTexture();
            }
        }

        public void Watch(BotPlayer bot)
        {
            Watched = bot;
        }

        public void Toggle()
        {
            if (Visible)
            {
                Visible = false;
            }
            else
            {
                Show();
            }
        }

        public void Show()
        {
            Visible = true;
            if (Watched == null && TestBot.Instance.Bots.Count > 0)
            {
                Watched = TestBot.Instance.Bots[0];
            }
        }

        /// <summary>Unity can turn a second monitor on but never off again, so this lasts until restart.</summary>
        public void UseSecondMonitor()
        {
            if (!SecondMonitorAvailable || OnSecondMonitor)
            {
                return;
            }
            Display.displays[1].Activate();
            OnSecondMonitor = true;
            Visible = true;
        }

        private void Awake()
        {
            window.x = Mathf.Max(20f, Screen.width - 500f);
        }

        private void OnEnable()
        {
            Camera.onPreCull += HandlePreCull;
            Camera.onPostRender += HandlePostRender;
        }

        private void OnDisable()
        {
            Camera.onPreCull -= HandlePreCull;
            Camera.onPostRender -= HandlePostRender;
        }

        private void OnDestroy()
        {
            ReleaseTexture();
            if (cam != null)
            {
                Destroy(cam.gameObject);
            }
        }

        // ---------------------------------------------------------------- Camera

        private bool Active => Visible && Watched != null && WorldSpace.Ready
            && PlayerSync.Instance.Remotes.TryGetValue(Watched.Id, out RemotePlayer body) && body != null;

        private void LateUpdate()
        {
            KeepCursorOnMainWindow();
            if (!Active)
            {
                if (cam != null)
                {
                    cam.enabled = false;
                }
                HideStandIn();
                return;
            }
            EnsureCamera();
            if (cam == null)
            {
                return;
            }
            RemotePlayer body = PlayerSync.Instance.Remotes[Watched.Id];
            // Follow the smoothed body so the view doesn't stutter at the network send rate.
            float t = 1f - Mathf.Exp(-15f * Time.unscaledDeltaTime);
            // Aim from the bot's own look direction, not the body transform: the avatar turns its model
            // itself and can leave the root's rotation fixed.
            if (aimedAt != Watched)
            {
                aimedAt = Watched;
                yaw = Watched.State.Yaw;
                pitch = Watched.State.Pitch;
            }
            yaw = Mathf.LerpAngle(yaw, Watched.State.Yaw, t);
            pitch = Mathf.LerpAngle(pitch, Watched.State.Pitch, t);
            cam.transform.position = body.transform.position + Vector3.up * EyeHeight;
            cam.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
            cam.enabled = true;
            UpdateStandIn();
        }

        /// <summary>
        /// With the POV on monitor 2, Unity opens a second game window there. Any time the cursor is free
        /// (menus, inventory, a frame where the game unlocks it), it can slide onto that window, and a click
        /// there (climbing is all clicks) moves focus to it, which feels like being tabbed out. While that
        /// window exists, a free cursor is kept inside the main window instead. Alt-Tab still works.
        /// </summary>
        private void KeepCursorOnMainWindow()
        {
            if (OnSecondMonitor && Application.isFocused && Cursor.lockState == CursorLockMode.None)
            {
                Cursor.lockState = CursorLockMode.Confined;
            }
        }

        private void EnsureCamera()
        {
            Camera main = ENT_Player.playerObject != null ? ENT_Player.playerObject.cam : null;
            if (main == null)
            {
                main = Camera.main;
            }
            if (main == null)
            {
                return;
            }
            if (cam == null)
            {
                var go = new GameObject("Belay_BotPovCamera");
                DontDestroyOnLoad(go);
                cam = go.AddComponent<Camera>();
                copiedFrom = null;
            }
            if (copiedFrom != main)
            {
                // Same look as your view (clear color, fog culling, layers), minus your view model overlay.
                cam.CopyFrom(main);
                copiedFrom = main;
                cam.depth = main.depth - 1f;
                if (cam.nearClipPlane > 0.05f)
                {
                    cam.nearClipPlane = 0.05f;
                }
                ApplyTarget();
            }
            if (!OnSecondMonitor && texture == null)
            {
                ApplyTarget();
            }
        }

        private void ApplyTarget()
        {
            if (OnSecondMonitor)
            {
                ReleaseTexture();
                cam.targetTexture = null;
                cam.targetDisplay = 1;
                cam.rect = new Rect(0f, 0f, 1f, 1f);
                return;
            }
            if (texture == null)
            {
                Vector2Int size = Resolutions[quality];
                texture = new RenderTexture(size.x, size.y, 24) { name = "Belay_BotPov", antiAliasing = 1 };
                texture.Create();
            }
            cam.targetDisplay = 0;
            cam.targetTexture = texture;
            cam.rect = new Rect(0f, 0f, 1f, 1f);
        }

        private void ReleaseTexture()
        {
            if (cam != null && cam.targetTexture == texture)
            {
                cam.targetTexture = null;
            }
            if (texture != null)
            {
                texture.Release();
                Destroy(texture);
                texture = null;
            }
        }

        // ---------------------------------------------------------------- What the bot camera sees

        private void UpdateStandIn()
        {
            if (youStandIn == null)
            {
                youStandIn = RemotePlayer.Create(default, "You", new Color(1f, 1f, 1f));
                // It stands exactly where you are, so it must never be solid.
                youStandIn.Ghost = true;
            }
            youStandIn.Apply(PlayerSync.CaptureLocal());
            SetRenderers(youStandIn, false);
        }

        private void HideStandIn()
        {
            if (youStandIn != null)
            {
                SetRenderers(youStandIn, false);
            }
        }

        private readonly List<Renderer> hiddenSelf = new List<Renderer>();

        // RemotePlayer (and the avatar on it) may switch its own renderers back on in Update/LateUpdate, so
        // all hiding happens per camera, right before each one culls: the stand-in for you (and its
        // headlamp) only exists for the bot camera, and the bot's own body is hidden only from it.
        private void HandlePreCull(Camera rendering)
        {
            if (youStandIn == null)
            {
                return;
            }
            bool botView = cam != null && rendering == cam && Watched != null;
            SetStandIn(botView);
            if (!botView)
            {
                return;
            }
            Transform tag = youStandIn.transform.Find("NameTag");
            if (tag != null)
            {
                tag.rotation = Quaternion.LookRotation(tag.position - cam.transform.position);
            }
            hiddenSelf.Clear();
            if (PlayerSync.Instance.Remotes.TryGetValue(Watched.Id, out RemotePlayer self) && self != null)
            {
                foreach (Renderer r in self.GetComponentsInChildren<Renderer>(true))
                {
                    if (r.enabled)
                    {
                        r.enabled = false;
                        hiddenSelf.Add(r);
                    }
                }
            }
        }

        private void HandlePostRender(Camera rendering)
        {
            if (cam == null || rendering != cam)
            {
                return;
            }
            SetStandIn(false);
            foreach (Renderer r in hiddenSelf)
            {
                if (r != null)
                {
                    r.enabled = true;
                }
            }
            hiddenSelf.Clear();
        }

        private void SetStandIn(bool on)
        {
            if (youStandIn != null)
            {
                SetRenderers(youStandIn, on);
            }
        }

        private static void SetRenderers(RemotePlayer player, bool on)
        {
            foreach (Renderer r in player.GetComponentsInChildren<Renderer>(true))
            {
                r.enabled = on;
            }
            foreach (Light l in player.GetComponentsInChildren<Light>(true))
            {
                l.enabled = on;
            }
        }

        // ---------------------------------------------------------------- Window

        private void OnGUI()
        {
            if (!Visible || OnSecondMonitor || Watched == null)
            {
                return;
            }
            window.width = Mathf.Clamp(window.width, 200f, Screen.width);
            window.height = Mathf.Clamp(window.height, 140f, Screen.height);
            window.x = Mathf.Clamp(window.x, 0f, Screen.width - 60f);
            window.y = Mathf.Clamp(window.y, 0f, Screen.height - 40f);
            window = GUI.Window(WindowId, window, DrawWindow, Watched.Name + " POV" + (Watched.Dead ? " (dead)" : ""));
        }

        private void DrawWindow(int id)
        {
            var view = new Rect(4f, 20f, window.width - 8f, window.height - 24f);
            if (texture != null && Active)
            {
                GUI.DrawTexture(view, texture, ScaleMode.ScaleToFit, false);
            }
            else
            {
                GUI.Label(view, WorldSpace.Ready ? "Waiting for the bot..." : "Start a run to see the bot's view.");
            }
            if (GUI.Button(new Rect(window.width - 26f, 2f, 22f, 16f), "x"))
            {
                Visible = false;
            }

            // Drag the bottom-right corner to resize.
            var grip = new Rect(window.width - 16f, window.height - 16f, 16f, 16f);
            GUI.Label(grip, "◢");
            Event e = Event.current;
            if (e.type == EventType.MouseDown && grip.Contains(e.mousePosition))
            {
                resizing = true;
                resizeStart = GUIUtility.GUIToScreenPoint(e.mousePosition);
                sizeStart = new Vector2(window.width, window.height);
                e.Use();
            }
            else if (resizing && e.type == EventType.MouseDrag)
            {
                Vector2 now = GUIUtility.GUIToScreenPoint(e.mousePosition);
                Vector2 size = sizeStart + (now - resizeStart);
                window.width = size.x;
                window.height = size.y;
                e.Use();
            }
            else if (e.rawType == EventType.MouseUp)
            {
                resizing = false;
            }
            if (!resizing)
            {
                GUI.DragWindow(new Rect(0f, 0f, window.width - 30f, 20f));
            }
        }
    }
}
