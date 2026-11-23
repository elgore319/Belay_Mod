using System;
using System.Reflection;
using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>
    /// Live model of your climber for the customization page, standing far below the world and filmed into a
    /// texture. Dragging in its rect turns it. Nothing renders while hidden.
    /// </summary>
    public class OutfitPreview : MonoBehaviour
    {
        private const int Layer = 31;
        private static readonly Vector3 Spot = new Vector3(0f, -4000f, 0f);

        private GameObject stage;
        private Camera cam;
        private ClimberAvatar avatar;
        private Color builtFor;
        private RenderTexture texture;
        private float lastDrawn = -10f;
        private bool failed;
        private float nextWarm;
        private bool savedFog;
        private Color savedAmbient;
        private UnityEngine.Rendering.AmbientMode savedAmbientMode;
        private float yaw = 200f;
        private bool dragging;
        private FieldInfo hook;
        private Action<Rect> drawer;

        private void Start()
        {
            // The menu page owns the hook; looked up by name so either side can ship first.
            hook = typeof(UI.BelayMenu).GetField("OutfitPreviewDrawer", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            drawer = Draw;
            Camera.onPreCull += BeforeRender;
            Camera.onPostRender += AfterRender;
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= BeforeRender;
            Camera.onPostRender -= AfterRender;
        }

        // The game's fog and dim ambient light would swallow the model; light it plainly for our camera only.
        private void BeforeRender(Camera c)
        {
            if (c != cam || cam == null)
            {
                return;
            }
            savedFog = RenderSettings.fog;
            savedAmbient = RenderSettings.ambientLight;
            savedAmbientMode = RenderSettings.ambientMode;
            RenderSettings.fog = false;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.42f, 0.46f);
        }

        private void AfterRender(Camera c)
        {
            if (c != cam || cam == null)
            {
                return;
            }
            RenderSettings.fog = savedFog;
            RenderSettings.ambientMode = savedAmbientMode;
            RenderSettings.ambientLight = savedAmbient;
        }

        private void Update()
        {
            if (hook != null && hook.GetValue(null) == null)
            {
                hook.SetValue(null, drawer);
            }
            if (Time.unscaledTime >= nextWarm)
            {
                // Look up the game models early, so choosing a zombie or roach look shows it at once.
                nextWarm = Time.unscaledTime + 1f;
                try
                {
                    GameModelAvatar.Warm();
                }
                catch (Exception)
                {
                }
            }
            bool showing = Time.unscaledTime - lastDrawn < 0.3f;
            if (!showing)
            {
                if (stage != null && stage.activeSelf)
                {
                    stage.SetActive(false);
                }
                dragging = false;
                return;
            }
            if (!dragging)
            {
                yaw += 12f * Time.unscaledDeltaTime;
            }
            try
            {
                Build();
                float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
                Quaternion facing = Quaternion.Euler(0f, yaw, 0f);
                Vector3 body = Spot + Vector3.up * ClimberAvatar.FeetDrop;
                Vector3 side = facing * Vector3.right * 0.3f;
                avatar.Wear(Outfits.Local);
                avatar.Pose(body, yaw, 0f, body - side, body + side, false, false, Vector3.zero, false, dt,
                    SafeSkin(0), SafeSkin(1));
            }
            catch (Exception e)
            {
                if (!failed)
                {
                    failed = true;
                    Plugin.Log.LogWarning("Outfit preview couldn't pose the climber: " + e);
                }
            }
        }

        private static string SafeSkin(int hand)
        {
            try
            {
                return HandSkins.LocalSkin(hand);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Called for every IMGUI event with the model's rect: drags turn the model (events aren't consumed) and
        /// repaint draws it.
        /// </summary>
        private void Draw(Rect rect)
        {
            lastDrawn = Time.unscaledTime;
            Event e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
            {
                dragging = true;
            }
            else if (e.type == EventType.MouseDrag && dragging)
            {
                yaw -= e.delta.x * 0.5f;
            }
            else if (e.rawType == EventType.MouseUp)
            {
                dragging = false;
            }
            if (e.type == EventType.Repaint && texture != null && cam != null)
            {
                GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, false);
            }
        }

        private void Build()
        {
            Color color = SafeLocalColor();
            if (stage != null && builtFor != color)
            {
                Destroy(stage);
                stage = null;
            }
            if (stage != null)
            {
                if (!stage.activeSelf)
                {
                    stage.SetActive(true);
                }
                return;
            }
            builtFor = color;
            stage = new GameObject("Belay_OutfitPreview");
            DontDestroyOnLoad(stage);
            var model = new GameObject("Model");
            model.transform.SetParent(stage.transform, false);
            avatar = new ClimberAvatar(model.transform, color);

            if (texture == null)
            {
                texture = new RenderTexture(512, 740, 24, RenderTextureFormat.ARGB32) { name = "Belay_OutfitPreview", antiAliasing = 4 };
            }
            var camObject = new GameObject("Camera");
            camObject.transform.SetParent(stage.transform, false);
            camObject.transform.position = Spot + new Vector3(0f, 1.0f, 3.3f);
            camObject.transform.LookAt(Spot + Vector3.up * 0.82f);
            cam = camObject.AddComponent<Camera>();
            cam.cullingMask = 1 << Layer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.075f, 0.078f, 0.085f, 1f);
            cam.fieldOfView = 32f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 20f;
            cam.targetTexture = texture;
            cam.depth = -100f;

            var key = new GameObject("Key").AddComponent<Light>();
            key.transform.SetParent(stage.transform, false);
            key.transform.rotation = Quaternion.Euler(35f, 160f, 0f);
            key.type = LightType.Directional;
            key.intensity = 0.9f;
            key.color = new Color(1f, 0.92f, 0.82f);
            key.cullingMask = 1 << Layer;
            key.shadows = LightShadows.None;

            SetLayer(stage.transform);
            foreach (Light l in stage.GetComponentsInChildren<Light>(true))
            {
                l.cullingMask = 1 << Layer;
            }
        }

        private void LateUpdate()
        {
            // Parts made later (held items, swapped hands) must land on the preview layer too.
            if (stage != null && stage.activeSelf)
            {
                SetLayer(stage.transform);
            }
        }

        private static void SetLayer(Transform t)
        {
            t.gameObject.layer = Layer;
            for (int i = 0; i < t.childCount; i++)
            {
                SetLayer(t.GetChild(i));
            }
        }

        private static Color SafeLocalColor()
        {
            try
            {
                return PlayerSync.LocalColor;
            }
            catch (Exception)
            {
                return Color.white;
            }
        }
    }
}
