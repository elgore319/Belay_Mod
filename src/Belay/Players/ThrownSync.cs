using System.Collections.Generic;
using System.IO;
using Belay.Net;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.Players
{
    /// <summary>
    /// What a teammate shoots or throws (spears, rebar, grenades, grapples) used to show only as the throw
    /// animation. Now the shot flies on everyone's screen: the same projectile model, on the same path
    /// (same speed, drop and drag). It's only a look-alike: it hits nothing and leaves nothing, because
    /// the real one already does its damage on the thrower's PC and that damage is shared on its own.
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
                foreach (MonoBehaviour b in ghost.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (b != null) DestroyImmediate(b);
                }
                foreach (Collider c in ghost.GetComponentsInChildren<Collider>(true))
                {
                    if (c != null) c.enabled = false;
                }
                foreach (Rigidbody rb in ghost.GetComponentsInChildren<Rigidbody>(true))
                {
                    if (rb != null) rb.isKinematic = true;
                }
                ghost.AddComponent<GhostShot>().Set(vel, gravity, drag, prefab.faceVelocity);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't show a teammate's {kind}: {e.Message}");
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

    /// <summary>A teammate's shot: flies like the real one until it meets something, then goes.</summary>
    internal class GhostShot : MonoBehaviour
    {
        private const float Life = 8f;
        private Vector3 velocity;
        private float gravity, drag, age;
        private bool face, stuck;
        private int mask;

        internal void Set(Vector3 v, float g, float d, bool faceVelocity)
        {
            velocity = v;
            gravity = g;
            drag = d;
            face = faceVelocity;
            mask = ~LayerMask.GetMask("Ignore Raycast");
        }

        private void Update()
        {
            age += Time.deltaTime;
            if (age > Life)
            {
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
                Destroy(gameObject, 0.6f);
                return;
            }
            transform.position += step;
            if (face && velocity.sqrMagnitude > 0.01f)
            {
                transform.rotation = Quaternion.LookRotation(velocity);
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
