using System.Collections.Generic;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Things players fix into the world are shared: a piton hammered into a wall (or anything else
    /// climbable a player leaves stuck in a surface) appears for everyone at the same spot, and everyone
    /// can climb it. Hammering it in and it breaking off are shared too, so the team always agrees on
    /// whether it will hold.
    /// </summary>
    public class PlacedSync : MonoBehaviour
    {
        public static PlacedSync Instance { get; private set; }

        private readonly Dictionary<string, GameObject> byId = new Dictionary<string, GameObject>();
        private readonly Dictionary<GameObject, string> idOf = new Dictionary<GameObject, string>();
        private readonly Dictionary<string, GameObject> prefabCache = new Dictionary<string, GameObject>();
        private int counter;
        private int runId;

        internal static bool Applying;
        /// <summary>The piton whose hit is running right now, so we know which item placed the object.</summary>
        internal static HandItem_Piton CurrentPiton;

        private static readonly AccessTools.FieldRef<CL_Handhold, bool> InUse = AccessTools.FieldRefAccess<CL_Handhold, bool>("inUse");
        private static readonly System.Reflection.MethodInfo AddPlaced = AccessTools.Method(typeof(M_Level), "AddPlacedObject");
        private static readonly AccessTools.FieldRef<CL_Handhold, bool> SkipFixedUpdate = AccessTools.FieldRefAccess<CL_Handhold, bool>("skipFixedUpdate");

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.ObjectPlaced, HandlePlaced);
            NetSession.Instance.On(PacketType.ObjectHammered, (origin, r) => ApplyHammer(r.ReadString(), r.ReadSingle()));
            NetSession.Instance.On(PacketType.ObjectBroken, (origin, r) => ApplyBreak(r.ReadString()));
        }

        internal static bool Live => Instance != null && NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready && !Applying;

        private void Update()
        {
            NetSession net = NetSession.Instance;
            if (net != null && net.InLobby && runId != net.CurrentRun.RunId)
            {
                runId = net.CurrentRun.RunId;
                byId.Clear();
                idOf.Clear();
            }
        }

        private void Register(string id, GameObject obj)
        {
            byId[id] = obj;
            idOf[obj] = id;
        }

        private bool TryGetId(CL_Handhold handhold, out string id)
        {
            id = null;
            if (handhold == null)
            {
                return false;
            }
            Transform t = handhold.transform;
            while (t != null)
            {
                if (idOf.TryGetValue(t.gameObject, out id))
                {
                    return true;
                }
                t = t.parent;
            }
            return false;
        }

        private static string BaseName(string name)
        {
            return name.Replace("(Clone)", "").Trim();
        }

        // ---------------------------------------------------------------- Local placing

        internal void OnLocalPlaced(GameObject obj)
        {
            if (obj == null || idOf.ContainsKey(obj))
            {
                return;
            }
            string sourceItem = "";
            string prefab = BaseName(obj.name);
            if (CurrentPiton != null)
            {
                sourceItem = CurrentPiton.item?.prefabName ?? "";
                if (CurrentPiton.pitonWorldObject != null)
                {
                    prefab = CurrentPiton.pitonWorldObject.name;
                    prefabCache[prefab] = CurrentPiton.pitonWorldObject;
                }
            }
            else if (obj.GetComponentInChildren<CL_Handhold>() == null)
            {
                // Only climbable things matter to the team; sparks and decals stay local.
                return;
            }
            string id = "place:" + NetSession.LocalId.Value + ":" + (++counter);
            Register(id, obj);
            CL_Handhold handhold = obj.GetComponentInChildren<CL_Handhold>();
            bool secure = handhold == null || handhold.secure;
            float secureAmount = handhold != null ? handhold.secureAmount : 1f;
            Vector3 pos = WorldSpace.ToShared(obj.transform.position);
            Quaternion rot = obj.transform.rotation;
            Plugin.Log.LogInfo($"Placed {prefab} ({id})");
            NetSession.Instance.Send(PacketType.ObjectPlaced, w =>
            {
                w.Write(id);
                w.Write(sourceItem);
                w.Write(prefab);
                w.WriteVector(pos);
                w.Write(rot.x); w.Write(rot.y); w.Write(rot.z); w.Write(rot.w);
                w.Write(secure);
                w.Write(secureAmount);
            });
        }

        internal void OnLocalHammer(CL_Handhold handhold, float amount)
        {
            if (TryGetId(handhold, out string id))
            {
                NetSession.Instance.Send(PacketType.ObjectHammered, w =>
                {
                    w.Write(id);
                    w.Write(amount);
                });
            }
        }

        internal void OnLocalBreak(CL_Handhold handhold)
        {
            if (TryGetId(handhold, out string id))
            {
                GameObject obj = byId[id];
                byId.Remove(id);
                if (obj != null)
                {
                    idOf.Remove(obj);
                }
                Plugin.Log.LogInfo($"Placed object broke off ({id})");
                NetSession.Instance.Send(PacketType.ObjectBroken, w => w.Write(id));
            }
        }

        // ---------------------------------------------------------------- Teammates' placing

        private GameObject FindPrefab(string sourceItem, string prefab)
        {
            if (prefabCache.TryGetValue(prefab, out GameObject cached) && cached != null)
            {
                return cached;
            }
            GameObject found = null;
            if (!string.IsNullOrEmpty(sourceItem))
            {
                try
                {
                    Item_Object itemPrefab = CL_AssetManager.GetItemObjectPrefab(sourceItem);
                    if (itemPrefab == null)
                    {
                        GameObject asset = CL_AssetManager.GetAssetGameObject(sourceItem);
                        itemPrefab = asset != null ? asset.GetComponent<Item_Object>() : null;
                    }
                    HandItem_Piton piton = itemPrefab?.itemData?.handItemAsset as HandItem_Piton;
                    if (piton == null && itemPrefab?.itemData?.handItemAsset != null)
                    {
                        piton = itemPrefab.itemData.handItemAsset.GetComponentInChildren<HandItem_Piton>(true);
                    }
                    if (piton != null && piton.pitonWorldObject != null && piton.pitonWorldObject.name == prefab)
                    {
                        found = piton.pitonWorldObject;
                    }
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"Couldn't look up {sourceItem}: {e.Message}");
                }
            }
            if (found == null)
            {
                try
                {
                    found = CL_AssetManager.GetAssetGameObject(prefab);
                }
                catch
                {
                    found = null;
                }
            }
            if (found == null)
            {
                // Any loaded prefab (not a scene object) with that name, e.g. a piton another piton item uses.
                foreach (HandItem_Piton piton in Resources.FindObjectsOfTypeAll<HandItem_Piton>())
                {
                    if (piton != null && piton.pitonWorldObject != null && piton.pitonWorldObject.name == prefab)
                    {
                        found = piton.pitonWorldObject;
                        break;
                    }
                }
            }
            if (found == null)
            {
                foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (go != null && go.name == prefab && !go.scene.IsValid() && go.transform.parent == null)
                    {
                        found = go;
                        break;
                    }
                }
            }
            if (found != null)
            {
                prefabCache[prefab] = found;
            }
            return found;
        }

        private void HandlePlaced(SteamId origin, System.IO.BinaryReader r)
        {
            string id = r.ReadString();
            string sourceItem = r.ReadString();
            string prefabName = r.ReadString();
            Vector3 pos = WorldSpace.ToLocal(r.ReadVector());
            var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            bool secure = r.ReadBoolean();
            float secureAmount = r.ReadSingle();
            if (!WorldSpace.Ready || byId.ContainsKey(id))
            {
                return;
            }
            GameObject prefab = FindPrefab(sourceItem, prefabName);
            if (prefab == null)
            {
                Plugin.Log.LogWarning($"Teammate placed something this game can't find: {prefabName} (from {sourceItem})");
                return;
            }
            Applying = true;
            try
            {
                GameObject obj = Instantiate(prefab, pos, rot);
                obj.transform.parent = WorldLoader.GetCurrentLevelParentRoot();
                obj.SetActive(true);
                Transform surface = FindSurface(obj, pos, rot);
                if (surface != null)
                {
                    obj.AddComponent<UT_SoftParent>().Initialize(surface);
                }
                CL_Handhold handhold = obj.GetComponentInChildren<CL_Handhold>();
                if (handhold != null)
                {
                    handhold.secureAmount = secureAmount;
                    handhold.SetSecure(secure);
                    if (secure)
                    {
                        UT_Highlight highlight = handhold.GetComponent<UT_Highlight>();
                        if (highlight != null)
                        {
                            highlight.active = false;
                        }
                    }
                }
                if (WorldLoader.initialized)
                {
                    M_Level level = WorldLoader.GetClosestLevelToPosition(pos)?.GetLevel();
                    if (level != null)
                    {
                        AddPlaced.Invoke(level, new object[] { obj });
                    }
                }
                Register(id, obj);
                Plugin.Log.LogInfo($"Teammate placed {prefabName} ({id})" + (surface != null ? " on " + surface.name : ""));
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't place teammate's {prefabName}: {e.Message}");
            }
            finally
            {
                Applying = false;
            }
        }

        /// <summary>The wall the object sticks out of: placed things face away from their surface.</summary>
        private static Transform FindSurface(GameObject obj, Vector3 pos, Quaternion rot)
        {
            Vector3 back = rot * Vector3.back;
            RaycastHit[] hits = Physics.RaycastAll(pos + rot * Vector3.forward * 0.2f, back, 1.5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (RaycastHit hit in hits)
            {
                Transform t = hit.collider.transform;
                if (t.IsChildOf(obj.transform) || hit.collider.GetComponentInParent<RemoteBody>() != null
                    || hit.collider.GetComponentInParent<ENT_Player>() != null)
                {
                    continue;
                }
                ObjectTagger tagger = hit.collider.GetComponent<ObjectTagger>();
                if (tagger != null && tagger.HasTag("Player"))
                {
                    continue;
                }
                return t;
            }
            return null;
        }

        private void ApplyHammer(string id, float amount)
        {
            if (!byId.TryGetValue(id, out GameObject obj) || obj == null)
            {
                return;
            }
            CL_Handhold handhold = obj.GetComponentInChildren<CL_Handhold>();
            if (handhold == null)
            {
                return;
            }
            Applying = true;
            try
            {
                handhold.HammerIn(amount);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Teammate's hammer hit failed here ({id}): {e.Message}");
            }
            finally
            {
                Applying = false;
            }
        }

        private void ApplyBreak(string id)
        {
            if (!byId.TryGetValue(id, out GameObject obj))
            {
                return;
            }
            byId.Remove(id);
            if (obj == null)
            {
                return;
            }
            idOf.Remove(obj);
            CL_Handhold handhold = obj.GetComponentInChildren<CL_Handhold>();
            Applying = true;
            try
            {
                if (handhold != null)
                {
                    if (handhold.breakObject != null)
                    {
                        Instantiate(handhold.breakObject, handhold.transform.position, handhold.transform.rotation);
                    }
                    handhold.GetComponent<UT_AudioClipHandler>()?.PlaySound("piton:break", handhold.transform.position);
                    handhold.ForceDropHands();
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Breaking teammate's piton here failed ({id}): {e.Message}");
            }
            finally
            {
                Applying = false;
            }
            Plugin.Log.LogInfo($"Teammate's placed object broke off ({id})");
            Destroy(obj);
        }

        // ---------------------------------------------------------------- Patches

        [HarmonyPatch]
        internal static class Patches
        {
            [HarmonyPatch(typeof(HandItem_Piton), nameof(HandItem_Piton.PitonHit))]
            [HarmonyPrefix]
            private static void PitonHitPrefix(HandItem_Piton __instance)
            {
                CurrentPiton = __instance;
            }

            [HarmonyPatch(typeof(HandItem_Piton), nameof(HandItem_Piton.PitonHit))]
            [HarmonyFinalizer]
            private static void PitonHitFinalizer()
            {
                CurrentPiton = null;
            }

            [HarmonyPatch(typeof(M_Level), "AddPlacedObject")]
            [HarmonyPostfix]
            private static void AddPlacedObject(GameObject o)
            {
                if (Live)
                {
                    try
                    {
                        Instance.OnLocalPlaced(o);
                    }
                    catch (System.Exception e)
                    {
                        Plugin.Log.LogWarning("Sharing a placed object failed: " + e.Message);
                    }
                }
            }

            [HarmonyPatch(typeof(CL_Handhold), nameof(CL_Handhold.HammerIn))]
            [HarmonyPrefix]
            private static void HammerIn(CL_Handhold __instance, float amount)
            {
                if (Live && !__instance.secure)
                {
                    Instance.OnLocalHammer(__instance, amount);
                }
            }

            // The game breaks a loose piton inside FixedUpdate once it has worked free.
            [HarmonyPatch(typeof(CL_Handhold), "FixedUpdate")]
            [HarmonyPrefix]
            private static void HandholdFixedUpdate(CL_Handhold __instance)
            {
                if (!__instance.secure && __instance.secureAmount < 0f && Live && InUse(__instance) && !SkipFixedUpdate(__instance))
                {
                    Instance.OnLocalBreak(__instance);
                }
            }
        }
    }
}
