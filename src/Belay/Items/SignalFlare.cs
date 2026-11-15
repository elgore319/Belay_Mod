using System.Collections;
using System.Collections.Generic;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using UnityEngine;

namespace Belay.Items
{
    /// <summary>
    /// The signal flare: throw it and, a moment after it leaves your hand, it lights up where it is. For 30
    /// seconds everyone sees a red glow and a red beam over that spot, through walls, to show the route or
    /// where someone is stuck. One use: a burnt flare thrown again does nothing.
    /// </summary>
    public class SignalFlare : MonoBehaviour
    {
        public const string Id = "Belay_SignalFlare";
        private const string BurntKey = "belayflare";
        private const float BurnTime = 30f;
        private const float IgniteDelay = 1.2f;

        public static SignalFlare Instance { get; private set; }

        private static Material beamMaterial;

        internal static void Define()
        {
            CoopItems.Define(new CoopItems.Definition
            {
                Id = Id,
                DisplayName = "Signal Flare",
                Template = io => (io.itemData.prefabName ?? "").ToLowerInvariant().Contains("flare"),
                Fallback = io => io.itemData.handItemAsset is HandItem_Flashlight,
                SetupHand = hand => hand.gameObject.AddComponent<NoSelfUse>(),
                Price = 2,
                Chance = 0.5f,
            });
        }

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.FlareLit, (origin, r) =>
            {
                Vector3 at = r.ReadVector();
                if (origin != NetSession.LocalId && WorldSpace.Ready)
                {
                    Light(WorldSpace.ToLocal(at));
                }
            });
        }

        /// <summary>Before the drop is shared: a fresh flare is marked burnt, so the shared copy is too.</summary>
        internal static bool MarkBurnt(Item item)
        {
            if (!string.IsNullOrEmpty(item.GetFirstDataStringByType(BurntKey)))
            {
                return false;
            }
            if (item.data == null)
            {
                item.data = new List<string>();
            }
            item.data.Add(BurntKey + ":burnt");
            return true;
        }

        internal void OnLocalThrow(Item item)
        {
            StartCoroutine(Ignite(item));
        }

        private IEnumerator Ignite(Item item)
        {
            yield return new WaitForSeconds(IgniteDelay);
            Item_Object obj = item.GetDropObject();
            ENT_Player player = ENT_Player.playerObject;
            if (!WorldSpace.Ready || (obj == null && player == null))
            {
                yield break;
            }
            Vector3 at = obj != null && obj.gameObject.activeInHierarchy ? obj.transform.position : player.transform.position;
            Light(at);
            Vector3 shared = WorldSpace.ToShared(at);
            NetSession.Instance.Send(PacketType.FlareLit, w => w.WriteVector(shared));
            Plugin.Log.LogInfo($"Signal flare lit at {shared}");
        }

        private void Light(Vector3 at)
        {
            var go = new GameObject("Belay_FlareMarker");
            go.transform.position = at + Vector3.up * 0.2f;
            var glow = go.AddComponent<Light>();
            glow.type = LightType.Point;
            glow.color = new Color(1f, 0.15f, 0.1f);
            glow.range = 14f;
            glow.intensity = 3f;

            var beam = go.AddComponent<LineRenderer>();
            beam.sharedMaterial = BeamMaterial;
            beam.useWorldSpace = false;
            beam.positionCount = 2;
            beam.SetPosition(0, Vector3.zero);
            beam.SetPosition(1, Vector3.up * 6f);
            beam.widthMultiplier = 0.12f;
            beam.startColor = new Color(1f, 0.2f, 0.1f, 0.7f);
            beam.endColor = new Color(1f, 0.2f, 0.1f, 0f);
            beam.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            beam.receiveShadows = false;

            go.AddComponent<FlareMarker>().Life = BurnTime;
        }

        private static Material BeamMaterial
        {
            get
            {
                if (beamMaterial == null)
                {
                    Shader shader = Shader.Find("Hidden/Internal-Colored") ?? Shader.Find("Sprites/Default");
                    beamMaterial = new Material(shader) { name = "Belay_FlareBeam", hideFlags = HideFlags.DontUnloadUnusedAsset };
                    beamMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    beamMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
                    beamMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                    beamMaterial.SetInt("_ZWrite", 0);
                    // Seen through walls.
                    beamMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                    beamMaterial.renderQueue = 4000;
                }
                return beamMaterial;
            }
        }
    }

    /// <summary>Burns down, flickers, and follows the world when it shifts.</summary>
    public class FlareMarker : MonoBehaviour
    {
        public float Life;
        private float start;
        private float lastOffset;
        private Light glow;

        private void Start()
        {
            start = Time.time;
            lastOffset = WorldSpace.Offset;
            glow = GetComponent<Light>();
        }

        private void Update()
        {
            float offset = WorldSpace.Offset;
            if (!Mathf.Approximately(offset, lastOffset))
            {
                transform.position += Vector3.up * (offset - lastOffset);
                lastOffset = offset;
            }
            float age = Time.time - start;
            if (age > Life || !CL_GameManager.IsInGame())
            {
                Destroy(gameObject);
                return;
            }
            if (glow != null)
            {
                float fade = Mathf.Clamp01((Life - age) / 4f);
                glow.intensity = (2.5f + Mathf.PerlinNoise(Time.time * 9f, 0f) * 1.5f) * fade;
            }
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.DropItemIntoWorld))]
    internal static class FlareThrowPatch
    {
        [HarmonyPrefix]
        private static void Prefix(Inventory __instance, Item item, out bool __state)
        {
            __state = __instance == Inventory.instance && CoopItems.IsCoopItem(item, SignalFlare.Id) && WorldSpace.Ready && SignalFlare.MarkBurnt(item);
        }

        [HarmonyPostfix]
        private static void Postfix(Item item, bool __state)
        {
            if (__state && SignalFlare.Instance != null)
            {
                SignalFlare.Instance.OnLocalThrow(item);
            }
        }
    }
}
