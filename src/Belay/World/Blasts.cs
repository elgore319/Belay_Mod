using System;
using System.Collections.Generic;
using System.IO;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Explosions from items (a grenade going off in hand, on the floor or after a throw) used to happen
    /// only on the PC of whoever owned the grenade. Now everyone sees and hears the blast where it went
    /// off and feels it: the camera shakes and it shoves you if you're close. A teammate's blast
    /// doesn't hurt you, like every other teammate hit; its damage to enemies is shared on its own.
    /// </summary>
    public class Blasts : MonoBehaviour
    {
        /// <summary>Put on a copy of someone else's blast: it shakes and shoves, never damages.</summary>
        internal class BlastCopy : MonoBehaviour
        {
        }

        private struct Recent
        {
            public Vector3 Position;
            public float Time;
        }

        private static readonly List<Recent> recentReal = new List<Recent>();
        private readonly Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();

        private void Start()
        {
            NetSession.Instance.On(PacketType.Blast, HandleBlast);
        }

        internal static bool Live => NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready;

        internal static void MarkCopy(GameObject go)
        {
            foreach (UT_Explosion e in go.GetComponentsInChildren<UT_Explosion>(true))
            {
                if (e != null && e.GetComponent<BlastCopy>() == null)
                {
                    e.gameObject.AddComponent<BlastCopy>();
                }
            }
        }

        internal static void NoteReal(Vector3 at)
        {
            float now = Time.unscaledTime;
            recentReal.RemoveAll(r => now - r.Time > 2f);
            recentReal.Add(new Recent { Position = at, Time = now });
        }

        internal static void Share(GameObject asset, Vector3 at, Quaternion rot)
        {
            if (asset == null)
            {
                return;
            }
            string name = asset.name;
            Vector3 shared = WorldSpace.ToShared(at);
            NetSession.Instance.Send(PacketType.Blast, w =>
            {
                w.Write(name);
                w.WriteVector(shared);
                w.Write(rot.x);
                w.Write(rot.y);
                w.Write(rot.z);
                w.Write(rot.w);
            });
        }

        private void HandleBlast(SteamId origin, BinaryReader r)
        {
            string name = r.ReadString();
            Vector3 at = WorldSpace.ToLocal(r.ReadVector());
            var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (origin == NetSession.LocalId || !Live)
            {
                return;
            }
            // The same grenade can be live on two PCs (dropped items exist on both); one blast is enough.
            float now = Time.unscaledTime;
            foreach (Recent real in recentReal)
            {
                if (now - real.Time < 1.5f && (real.Position - at).sqrMagnitude < 4f * 4f)
                {
                    return;
                }
            }
            GameObject prefab = Prefab(name);
            if (prefab == null)
            {
                Plugin.Log.LogWarning($"Teammate's blast {name} not found here");
                return;
            }
            try
            {
                GameObject go = Instantiate(prefab, at, rot);
                MarkCopy(go);
                if (WorldLoader.initialized)
                {
                    go.transform.parent = WorldLoader.GetCurrentLevelParentRoot();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't show a teammate's blast {name}: {e.Message}");
            }
        }

        private GameObject Prefab(string name)
        {
            if (prefabs.TryGetValue(name, out GameObject known) && known != null)
            {
                return known;
            }
            foreach (UT_Explosion e in Resources.FindObjectsOfTypeAll<UT_Explosion>())
            {
                if (e == null || e.gameObject.scene.IsValid())
                {
                    continue;
                }
                GameObject root = e.transform.root.gameObject;
                if (root.name == name)
                {
                    prefabs[name] = root;
                    return root;
                }
            }
            return null;
        }

        /// <summary>A copied blast: the shake, and a shove for the local player if close enough.</summary>
        internal static void Feel(UT_Explosion e)
        {
            Vector3 at = e.transform.position;
            CL_CameraControl.ShakeAtPosition?.Invoke(at, e.shakeMagnitude, e.shakeDistance);
            ENT_Player me = ENT_Player.playerObject;
            if (me == null || me.IsDead() || e.effects == null)
            {
                return;
            }
            float dist = Vector3.Distance(me.transform.position, at);
            foreach (UT_Explosion.ExplosionEffect fx in e.effects)
            {
                if (fx == null || dist > fx.distance || fx.distance <= 0f)
                {
                    continue;
                }
                float k = fx.forceCurve != null ? fx.forceCurve.Evaluate(dist / fx.distance) : 1f;
                me.AddForce((me.transform.position - at).normalized * (fx.force * k) + Vector3.up * fx.upwardsForce * k);
                break;
            }
        }
    }

    [HarmonyPatch]
    internal static class BlastPatches
    {
        [HarmonyPatch(typeof(UT_Explosion), nameof(UT_Explosion.Explode))]
        [HarmonyPrefix]
        private static bool Explode(UT_Explosion __instance)
        {
            if (__instance == null || __instance.GetComponent<Blasts.BlastCopy>() == null)
            {
                if (__instance != null)
                {
                    Blasts.NoteReal(__instance.transform.position);
                }
                return true;
            }
            try
            {
                Blasts.Feel(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Teammate's blast not felt: " + e.Message);
            }
            return false;
        }

        private static readonly AccessTools.FieldRef<ItemExecutionModule, Item> ModuleItem = AccessTools.FieldRefAccess<ItemExecutionModule, Item>("item");
        private static Vector3 pendingAt;
        private static Quaternion pendingRot;

        /// <summary>A grenade's timer running out on this PC: the same blast goes off for everyone.</summary>
        [HarmonyPatch(typeof(ItemExecutionModule_Grenade), nameof(ItemExecutionModule_Grenade.Execute))]
        [HarmonyPrefix]
        private static void GrenadeBefore(ItemExecutionModule_Grenade __instance, out bool __state)
        {
            __state = false;
            try
            {
                Item item = ModuleItem(__instance);
                if (!Blasts.Live || item == null || item.hasBeenDestroyed || !__instance.active
                    || __instance.timer - Time.deltaTime > 0f || __instance.explosionAsset == null)
                {
                    return;
                }
                // Same spot the game uses for the blast.
                Transform drop = item.GetDropObject().transform;
                Vector3 at = drop.position;
                if (item.GetHandItem() != null)
                {
                    at = item.GetHandItem().transform.position + Vector3.down;
                }
                else if (item.InBag() && ENT_Player.playerObject != null)
                {
                    at = ENT_Player.playerObject.transform.position + Vector3.down * 0.25f - ENT_Player.playerObject.transform.forward * 0.25f;
                }
                pendingAt = at;
                pendingRot = drop.rotation;
                __state = true;
            }
            catch (Exception)
            {
            }
        }

        [HarmonyPatch(typeof(ItemExecutionModule_Grenade), nameof(ItemExecutionModule_Grenade.Execute))]
        [HarmonyPostfix]
        private static void GrenadeAfter(ItemExecutionModule_Grenade __instance, bool __state)
        {
            if (!__state || ModuleItem(__instance) == null || !ModuleItem(__instance).hasBeenDestroyed)
            {
                return;
            }
            try
            {
                Blasts.Share(__instance.explosionAsset, pendingAt, pendingRot);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Blast not shared: " + e.Message);
            }
        }
    }
}
