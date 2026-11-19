using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Belay.Players
{
    /// <summary>
    /// The solid part of a teammate: a capsule you bump into, stand on and grip like a handhold. Hammer swings
    /// knock it back.
    /// </summary>
    public class RemoteBody : MonoBehaviour, Damageable
    {
        public const float Radius = 0.32f;
        public const float Height = 1.5f;

        public RemotePlayer Owner { get; private set; }
        public CapsuleCollider Collider { get; private set; }
        public CL_Handhold Handhold { get; private set; }

        private Rigidbody body;
        private bool solid = true;
        private float nextPhysicsScan;
        private readonly HashSet<Collider> ignored = new HashSet<Collider>();
        private static readonly Collider[] nearby = new Collider[64];

        private const float ScanRadius = 3f;
        private const float ScanInterval = 0.1f;

        /// <summary>Every live teammate body, to look up which teammate a collider belongs to.</summary>
        public static readonly List<RemoteBody> All = new List<RemoteBody>();

        public static RemoteBody Create(RemotePlayer owner)
        {
            var go = new GameObject("Body");
            go.transform.SetParent(owner.transform, false);
            go.layer = PlayerContact.BodyLayer;
            var result = go.AddComponent<RemoteBody>();
            result.Owner = owner;
            result.Build();
            World.PropSync.Instance?.OnBodyCreated(result);
            return result;
        }

        private void Build()
        {
            Collider = gameObject.AddComponent<CapsuleCollider>();
            Collider.radius = Radius;
            Collider.height = Height;
            Collider.direction = 1;

            body = gameObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.None;

            var tagger = gameObject.AddComponent<ObjectTagger>();
            tagger.tags = new List<string> { "Handhold", "Damageable" };

            Handhold = gameObject.AddComponent<CL_Handhold>();
            if (Handhold.activeEvent == null) Handhold.activeEvent = new UnityEvent();
            if (Handhold.stopEvent == null) Handhold.stopEvent = new UnityEvent();
            if (Handhold.hammerEvent == null) Handhold.hammerEvent = new UnityEvent();
            Handhold.useSharedHandholdMaterial = false;
            Handhold.canHammer = false;
            Handhold.secure = true;
            // A person is a worse hold than a rung: you slide a bit more and tire a bit faster.
            Handhold.dragMult = 8f;
            Handhold.strainRate = 1.25f;
        }

        private void OnEnable()
        {
            All.Add(this);
        }

        private void OnDisable()
        {
            All.Remove(this);
        }

        /// <summary>Turns the body on or off (dead or hidden teammates, or the "solid teammates" option off).</summary>
        public void SetSolid(bool on)
        {
            if (solid == on)
            {
                return;
            }
            solid = on;
            if (!on && Handhold != null)
            {
                Handhold.ForceDropHands();
            }
            Collider.enabled = on;
            Handhold.active = on;
        }

        /// <summary>
        /// Network-moved bodies act as unstoppable walls to physics and shoved physics objects around on this PC
        /// only. Their own PC handles those, so here the body passes through physics-driven objects.
        /// </summary>
        private void FixedUpdate()
        {
            if (Collider == null || !Collider.enabled || Time.time < nextPhysicsScan)
            {
                return;
            }
            nextPhysicsScan = Time.time + ScanInterval;
            int count = Physics.OverlapSphereNonAlloc(Collider.bounds.center, ScanRadius, nearby, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Collider c = nearby[i];
                Rigidbody rb = c != null ? c.attachedRigidbody : null;
                if (rb == null || rb.isKinematic || rb == body || c.isTrigger || ignored.Contains(c) || c.GetComponent<RemoteBody>() != null)
                {
                    continue;
                }
                if (ENT_Player.playerObject != null && rb.transform.IsChildOf(ENT_Player.playerObject.transform))
                {
                    continue;
                }
                Physics.IgnoreCollision(Collider, c, true);
                ignored.Add(c);
            }
            if (ignored.Count > 512)
            {
                ignored.RemoveWhere(x => x == null);
            }
        }

        public static RemoteBody Of(Collider c)
        {
            return c != null ? c.GetComponent<RemoteBody>() : null;
        }

        // ---------------------------------------------------------------- Damageable: hits knock back, never hurt

        public bool Damage(Damageable.DamageInfo info)
        {
            if (info == null || Owner == null)
            {
                return false;
            }
            // Only our own swings count. Hazards that happen to overlap the body (fire, goo, traps) hurt the
            // real teammate on their own PC already.
            bool fromLocalPlayer = info.sourceEntity != null && info.sourceEntity == ENT_Player.playerObject
                || info.sourceObject != null && ENT_Player.playerObject != null && info.sourceObject == ENT_Player.playerObject.gameObject
                || info.HasTag("player");
            if (!fromLocalPlayer)
            {
                // A local test bot has no PC of its own to get hurt on, so enemies and hazards hurt it here.
                return Bot.TestBot.Instance != null && Bot.TestBot.Instance.IsBot(Owner.SteamId)
                    && Bot.TestBot.Instance.Hurt(Owner.SteamId, info.amount, info.sourceObject != null ? info.sourceObject.name : "something", info.type);
            }
            Vector3 dir = info.direction.sqrMagnitude > 0.01f ? info.direction.normalized
                : Camera.main != null ? Camera.main.transform.forward : transform.forward;
            float strength = Mathf.Clamp(6f + info.amount * 2f, 6f, 14f);
            PlayerContact.Instance?.SendKnockback(Owner, dir, strength, "hammer");
            return true;
        }

        public bool Damage(float amount)
        {
            return Damage(Damageable.DamageInfo.CreateDamageInfo(amount));
        }

        public GameObject GetGameObject()
        {
            return gameObject;
        }
    }
}
