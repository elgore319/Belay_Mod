using System;
using System.Reflection;
using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>
    /// A live model of the local player's climber for the customization page: the climber in their
    /// current outfit and hand skins, standing in an empty spot far below the world, filmed by its own
    /// camera into a texture. The menu reserves a rect and calls the drawer hook on repaint; dragging
    /// inside that rect turns the model. Nothing is rendered while the page isn't showing it.
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
        private Rect screenRect;
        private float yaw = 200f;
        private bool dragging;
        private FieldInfo hook;
        private Action<Rect> drawer;

        private void Start()
        {
            // The menu page owns the hook; looked up by name so either side can ship first.
            hook = typeof(UI.BelayMenu).GetField("OutfitPreviewDrawer", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            drawer = Draw;
        }

        private void Update()
        {
            if (hook != null && hook.GetValue(null) == null)
            {
                hook.SetValue(null, drawer);
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
            Build();
            if (!dragging)
            {
                yaw += 12f * Time.unscaledDeltaTime;
            }
            float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            Quaternion facing = Quaternion.Euler(0f, yaw, 0f);
            Vector3 body = Spot + Vector3.up * ClimberAvatar.FeetDrop;
            Vector3 side = facing * Vector3.right * 0.3f;
            avatar.Wear(Outfits.Local);
            avatar.Pose(body, yaw, 0f, body - side, body + side, false, false, Vector3.zero, false, dt,
                HandSkins.LocalSkin(0), HandSkins.LocalSkin(1));
        }

        private void OnGUI()
        {
            // Drag to turn. Events are only read, never used, so the page still gets them.
            Event e = Event.current;
            if (Time.unscaledTime - lastDrawn > 0.3f)
            {
                return;
            }
            if (e.type == EventType.MouseDown && e.button == 0 && screenRect.Contains(e.mousePosition))
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
        }

        /// <summary>Called by the menu during repaint with the rect it reserved for the model.</summary>
        private void Draw(Rect rect)
        {
            lastDrawn = Time.unscaledTime;
            Vector2 a = GUIUtility.GUIToScreenPoint(rect.min);
            Vector2 b = GUIUtility.GUIToScreenPoint(rect.max);
            screenRect = Rect.MinMaxRect(a.x, a.y, b.x, b.y);
            if (texture != null && cam != null)
            {
                GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit);
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
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
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
