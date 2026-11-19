using System.Collections.Generic;
using System.IO;
using Belay.Net;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.Players
{
    /// <summary>
    /// A teammate's shots and throws now fly on everyone's screen with the same model and path. Look-alike only:
    /// it hits nothing, since the real one's damage is shared separately.
    /// </summary>
    public class ThrownSync : MonoBehaviour
    {
        private static readonly AccessTools.FieldRef<Projectile, Vector3> Velocity = AccessTools.FieldRefAccess<Projectile, Vector3>("velocity");
        private static readonly AccessTools.FieldRef<Projectile, float> VelMult = AccessTools.FieldRefAccess<Projectile, float>("velMult");

        private readonly Dictionary<string, Projectile> prefabs = new Dictionary<string, Projectile>();

        private void Start()
        {
            NetSession.Instance.On(PacketType.ProjectileFired, HandleFired);
        }

        internal static bool Live => NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready;

        internal static void OnLocalShot(Projectile p)
        {
            string kind = p.name.Replace("(Clone)", "").Trim();
            Vector3 pos = WorldSpace.ToShared(p.transform.position);
            Vector3 vel = Velocity(p) * VelMult(p);
            float gravity = p.gravity, drag = p.airResistance;
            NetSession.Instance.Send(PacketType.ProjectileFired, w =>
            {
                w.Write(kind);
                w.WriteVector(pos);
                w.WriteVector(vel);
                w.Write(gravity);
                w.Write(drag);
            });
        }

        private void HandleFired(SteamId origin, BinaryReader r)
        {
            string kind = r.ReadString();
            Vector3 pos = WorldSpace.ToLocal(r.ReadVector());
            Vector3 vel = r.ReadVector();
            float gravity = r.ReadSingle();
            float drag = r.ReadSingle();
            if (origin == NetSession.LocalId || !WorldSpace.Ready)
            {
                return;
            }
            Projectile prefab = Prefab(kind);
            if (prefab == null)
            {
                return;
            }
            try
            {
                GameObject ghost = Instantiate(prefab.gameObject, pos, vel.sqrMagnitude > 0.001f ? Quaternion.LookRotation(vel) : Quaternion.identity);
                ghost.name = "Belay_Shot_" + kind;
                // Strip everything that could hit, hurt, press or spawn: just the model is left.
                // Lights and glows stay (a flare's glow, a grenade's blinking light).
                foreach (MonoBehaviour b in ghost.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (b != null && !KeepOnGhost(b)) DestroyImmediate(b);
                }
                foreach (Collider c in ghost.GetComponentsInChildren<Collider>(true))
                {
                    if (c != null) c.enabled = false;
                }
                foreach (Rigidbody rb in ghost.GetComponentsInChildren<Rigidbody>(true))
                {
                    if (rb != null) rb.isKinematic = true;
                }
                ghost.AddComponent<GhostShot>().Set(prefab, vel, gravity, drag);
                PlayLaunch(prefab, pos, vel);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't show a teammate's {kind}: {e.Message}");
            }
        }

        private static bool KeepOnGhost(MonoBehaviour b)
        {
            return b is CL_Lamp || b is FX_RunningLightFlare || b is FX_FadeFlareHit;
        }

        /// <summary>What the real shot does as it leaves: its sound, muzzle flash and tracer.</summary>
        private static void PlayLaunch(Projectile prefab, Vector3 pos, Vector3 vel)
        {
            if (prefab.spawnSound != null && prefab.spawnSound.Count > 0)
            {
                AudioManager.PlaySound(prefab.spawnSound[Random.Range(0, prefab.spawnSound.Count)], pos);
            }
            if (prefab.useTracer)
            {
                if (!string.IsNullOrEmpty(prefab.particleTracerInfo.muzzleID))
                {
                    FXManager.PlayParticle(prefab.particleTracerInfo.muzzleID, pos, 15);
                }
                if (!string.IsNullOrEmpty(prefab.particleTracerInfo.tracerID) && vel.sqrMagnitude > 0.01f)
                {
                    FXManager.PlayTracer(prefab.particleTracerInfo.tracerID, pos, pos + vel * 0.5f, vel.magnitude);
                }
            }
        }

        private Projectile Prefab(string kind)
        {
            if (prefabs.TryGetValue(kind, out Projectile known) && known != null)
            {
                return known;
            }
            foreach (Projectile p in Resources.FindObjectsOfTypeAll<Projectile>())
            {
                if (p != null && p.name == kind && !p.gameObject.scene.IsValid())
                {
                    prefabs[kind] = p;
                    return p;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// A teammate's shot: flies like the real one, then plays the same impact. Sticking shots stay as long as the
    /// real one would.
    /// </summary>
    internal class GhostShot : MonoBehaviour
    {
        private const float MaxLife = 30f;
        private Projectile prefab;
        private Vector3 velocity;
        private float gravity, drag, age, life;
        private bool face, stuck;
        private int mask;

        internal void Set(Projectile p, Vector3 v, float g, float d)
        {
            prefab = p;
            velocity = v;
            gravity = g;
            drag = d;
            face = p.faceVelocity;
            life = Mathf.Clamp(p.lifetime, 1f, MaxLife);
            mask = ~LayerMask.GetMask("Ignore Raycast");
        }

        private void Update()
        {
            age += Time.deltaTime;
            if (age > life)
            {
                if (!stuck && prefab != null && prefab.lifetimeBreakEffectPrefab != null)
                {
                    Spawn(prefab.lifetimeBreakEffectPrefab, transform.position, transform.rotation, null);
                }
                Destroy(gameObject);
                return;
            }
            if (stuck)
            {
                return;
            }
            velocity += Vector3.down * gravity * Time.deltaTime;
            velocity += -velocity.normalized * drag * velocity.sqrMagnitude * Time.deltaTime;
            Vector3 step = velocity * Time.deltaTime;
            if (step.sqrMagnitude > 0f && Physics.Raycast(transform.position, step.normalized, out RaycastHit hit, step.magnitude, mask, QueryTriggerInteraction.Ignore)
                && hit.collider.GetComponentInParent<RemoteBody>() == null)
            {
                transform.position = hit.point;
                stuck = true;
                Impact(hit);
                if (prefab == null || prefab.destroyOnHit)
                {
                    Destroy(gameObject);
                }
                else
                {
                    // Stuck in the wall like the real one; it goes when the real one would.
                    transform.SetParent(hit.collider.transform, true);
                }
                return;
            }
            transform.position += step;
            if (face && velocity.sqrMagnitude > 0.01f)
            {
                transform.rotation = Quaternion.LookRotation(velocity);
            }
        }

        private void Impact(RaycastHit hit)
        {
            if (prefab == null)
            {
                return;
            }
            try
            {
                if (prefab.hitSound != null && prefab.hitSound.Count > 0)
                {
                    AudioManager.PlaySound(prefab.hitSound[Random.Range(0, prefab.hitSound.Count)], hit.point);
                }
                if (prefab.useHitEffects && prefab.hitEffect != null)
                {
                    Quaternion rot = Quaternion.Lerp(transform.rotation, Quaternion.LookRotation(-hit.normal), prefab.hitFaceNormalBlend);
                    if (prefab.flipHitEffect)
                    {
                        rot *= Quaternion.Euler(0f, 180f, 0f);
                    }
                    Spawn(prefab.hitEffect, hit.point, rot, hit.collider != null ? hit.collider.transform : null);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Teammate's impact not shown: " + e.Message);
            }
        }

        /// <summary>An impact effect as a copy: any blast in it shakes and shoves, but hurts no one here.</summary>
        private static void Spawn(GameObject effect, Vector3 at, Quaternion rot, Transform parent)
        {
            GameObject go = Instantiate(effect, at, rot);
            World.Blasts.MarkCopy(go);
            if (parent != null)
            {
                go.transform.SetParent(parent, true);
            }
            else if (WorldLoader.initialized)
            {
                go.transform.parent = WorldLoader.GetCurrentLevelParentRoot();
            }
        }
    }

    [HarmonyPatch(typeof(HandItem_Shoot), "CreateProjectile")]
    internal static class ThrownSyncPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Projectile __result)
        {
            if (__result == null || !ThrownSync.Live)
            {
                return;
            }
            try
            {
                ThrownSync.OnLocalShot(__result);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Shot not shared: " + e.Message);
            }
        }
    }
}
