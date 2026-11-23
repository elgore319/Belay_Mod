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
    /// The hands in the vents (DEN_VentThing). The host hands each one to the closest player's PC (EnemyMotion),
    /// and that PC's hand goes after its own player. Before, only the vent itself was copied, and every other PC
    /// ran its own hand after its own player. Now the PC running a hand streams the hand, its arm and whether
    /// it's holding someone, and the other PCs show exactly that, so everyone sees who it reaches for and drags.
    /// It isn't handed to another player while it's holding someone.
    /// </summary>
    public class VentHandSync : MonoBehaviour
    {
        public static VentHandSync Instance { get; private set; }

        private const float SendInterval = 0.1f;
        private const float StaleAfter = 1.5f;
        private const int MaxJoints = 30;

        private class Remote
        {
            public SteamId From;
            public Vector3 Hand;
            public Quaternion HandRot;
            public bool Holding;
            public bool Retreating;
            public readonly List<Vector3> Joints = new List<Vector3>();
            public float LastHeard;
            public bool Placed;
            public Rigidbody HandBody;
            public bool MadeKinematic;
        }

        private readonly Dictionary<DEN_VentThing, Remote> remotes = new Dictionary<DEN_VentThing, Remote>();
        private float nextSend;

        private static readonly AccessTools.FieldRef<DEN_VentThing, bool> HasGrabbed = AccessTools.FieldRefAccess<DEN_VentThing, bool>("hasGrabbedTarget");
        private static readonly AccessTools.FieldRef<DEN_VentThing, bool> Retreating = AccessTools.FieldRefAccess<DEN_VentThing, bool>("retreating");
        private static readonly AccessTools.FieldRef<DEN_VentThing, List<Transform>> Arms = AccessTools.FieldRefAccess<DEN_VentThing, List<Transform>>("arms");
        private static readonly AccessTools.FieldRef<DEN_VentThing, List<MeshRenderer>> ArmRenderers = AccessTools.FieldRefAccess<DEN_VentThing, List<MeshRenderer>>("armRenderers");
        private static readonly AccessTools.FieldRef<DEN_VentThing, Animator> BodyAnimator = AccessTools.FieldRefAccess<DEN_VentThing, Animator>("bodyAnimator");

        private static bool Live => NetSession.InLobbyRun && EnemyMotion.Instance != null && EnemySync.Instance != null;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.VentHand, Handle);
        }

        /// <summary>True when this hand (on any PC) is holding a player.</summary>
        internal static bool Holding(AIGameEntity e)
        {
            if (!(e is DEN_VentThing vent) || Instance == null)
            {
                return false;
            }
            if (Instance.remotes.TryGetValue(vent, out Remote r) && Time.unscaledTime - r.LastHeard <= StaleAfter)
            {
                return r.Holding;
            }
            return HasGrabbed(vent);
        }

        /// <summary>Another PC runs this hand and we're showing theirs.</summary>
        internal static bool IsPuppet(DEN_VentThing vent)
        {
            return Instance != null && Instance.remotes.TryGetValue(vent, out Remote r) && Time.unscaledTime - r.LastHeard <= StaleAfter
                && Live && !EnemyMotion.Instance.RunsHere(vent);
        }

        private void Update()
        {
            if (!Live)
            {
                ReleaseAll();
                return;
            }
            if (Time.unscaledTime < nextSend)
            {
                return;
            }
            nextSend = Time.unscaledTime + SendInterval;
            try
            {
                foreach (AIGameEntity e in EnemySync.Instance.Known())
                {
                    if (e is DEN_VentThing vent && vent.isActiveAndEnabled && EnemyMotion.Instance.RunsHere(vent))
                    {
                        if (remotes.TryGetValue(vent, out Remote r))
                        {
                            Release(r);
                            remotes.Remove(vent);
                        }
                        Send(vent);
                    }
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning("Vent hands: " + ex.Message);
                nextSend = Time.unscaledTime + 5f;
            }
        }

        private void Send(DEN_VentThing vent)
        {
            if (!WorldSpace.Ready || vent.hand == null)
            {
                return;
            }
            var joints = new List<Vector3>();
            List<Transform> arms = Arms(vent);
            if (arms != null)
            {
                // Each arm piece after the first starts at a bend; the first starts at the vent.
                for (int k = 1; k < arms.Count && joints.Count < MaxJoints; k++)
                {
                    if (arms[k] != null && arms[k].gameObject.activeSelf)
                    {
                        joints.Add(WorldSpace.ToShared(arms[k].position));
                    }
                }
            }
            string kind = EnemySync.KindOf(vent);
            Vector3 spawn = EnemySync.Instance.SpawnOf(vent);
            Vector3 now = WorldSpace.ToShared(vent.transform.position);
            Vector3 hand = WorldSpace.ToShared(vent.hand.position);
            Quaternion rot = vent.hand.rotation;
            byte flags = 0;
            if (HasGrabbed(vent)) flags |= 1;
            if (Retreating(vent)) flags |= 2;
            NetSession.Instance.Send(PacketType.VentHand, w =>
            {
                w.Write(kind);
                w.WriteVector(spawn);
                w.WriteVector(now);
                w.WriteVector(hand);
                w.Write(rot.x); w.Write(rot.y); w.Write(rot.z); w.Write(rot.w);
                w.Write(flags);
                w.Write((byte)joints.Count);
                foreach (Vector3 j in joints) w.WriteVector(j);
            }, reliable: false);
        }

        private void Handle(SteamId origin, BinaryReader r)
        {
            string kind = r.ReadString();
            Vector3 spawn = r.ReadVector();
            Vector3 now = r.ReadVector();
            Vector3 hand = r.ReadVector();
            var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            byte flags = r.ReadByte();
            int count = r.ReadByte();
            var joints = new List<Vector3>(count);
            for (int i = 0; i < count; i++) joints.Add(r.ReadVector());
            if (origin == NetSession.LocalId || !Live || !WorldSpace.Ready)
            {
                return;
            }
            if (!(EnemySync.Instance.Find(kind, spawn, now) is DEN_VentThing vent) || vent.dead || EnemyMotion.Instance.RunsHere(vent))
            {
                return;
            }
            if (!remotes.TryGetValue(vent, out Remote rem))
            {
                rem = new Remote { HandBody = vent.hand != null ? vent.hand.GetComponent<Rigidbody>() : null };
                remotes[vent] = rem;
                Plugin.Log.LogInfo($"Showing {NetSession.NameOf(origin)}'s vent hand");
                if (HasGrabbed(vent))
                {
                    vent.ReleaseGrapple(); // our own copy had hold of us: theirs is the real one
                }
            }
            bool wasHolding = rem.Holding;
            rem.From = origin;
            rem.Hand = hand;
            rem.HandRot = rot;
            rem.Holding = (flags & 1) != 0;
            rem.Retreating = (flags & 2) != 0;
            rem.Joints.Clear();
            rem.Joints.AddRange(joints);
            rem.LastHeard = Time.unscaledTime;
            if (rem.Holding != wasHolding && vent.hand != null)
            {
                vent.clipHandler?.PlaySound(rem.Holding ? "hand:grabplayer" : "hand:dropplayer", vent.hand.position);
            }
        }

        /// <summary>Right after the hand's own Update, before its animation runs: show the other PC's.</summary>
        internal static void AfterUpdate(DEN_VentThing vent)
        {
            if (Instance == null || !IsPuppet(vent) || !Instance.remotes.TryGetValue(vent, out Remote r))
            {
                return;
            }
            vent.handAnimator?.SetBool("retreating", r.Retreating);
            Animator body = BodyAnimator(vent);
            body?.SetBool("chomping", r.Holding);
        }

        private void LateUpdate()
        {
            if (remotes.Count == 0 || !Live)
            {
                return;
            }
            float k = 1f - Mathf.Exp(-15f * Mathf.Max(Time.deltaTime, 0.0001f));
            List<DEN_VentThing> stale = null;
            foreach (var pair in remotes)
            {
                DEN_VentThing vent = pair.Key;
                Remote r = pair.Value;
                if (vent == null || vent.dead || !IsPuppet(vent))
                {
                    Release(r);
                    if (vent == null || vent.dead || Time.unscaledTime - r.LastHeard > StaleAfter || EnemyMotion.Instance.RunsHere(vent))
                    {
                        (stale ??= new List<DEN_VentThing>()).Add(vent);
                    }
                    continue;
                }
                try
                {
                    Pose(vent, r, k);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning("Couldn't show a teammate's vent hand: " + ex.Message);
                    Release(r);
                    (stale ??= new List<DEN_VentThing>()).Add(vent);
                }
            }
            if (stale != null)
            {
                foreach (DEN_VentThing v in stale) remotes.Remove(v);
            }
        }

        private static void Pose(DEN_VentThing vent, Remote r, float k)
        {
            Transform hand = vent.hand;
            if (r.HandBody != null && !r.HandBody.isKinematic)
            {
                r.HandBody.isKinematic = true;
                r.MadeKinematic = true;
            }
            Vector3 target = WorldSpace.ToLocal(r.Hand);
            bool snap = !r.Placed || (hand.position - target).sqrMagnitude > 64f;
            hand.SetPositionAndRotation(snap ? target : Vector3.Lerp(hand.position, target, k),
                snap ? r.HandRot : Quaternion.Slerp(hand.rotation, r.HandRot, k));
            if (r.HandBody != null)
            {
                r.HandBody.position = hand.position;
                r.HandBody.rotation = hand.rotation;
            }
            r.Placed = true;
            // Seen from outside: the real hand, never the one drawn on the held player's own screen.
            if (vent.handRenderer != null && !vent.handRenderer.activeSelf) vent.handRenderer.SetActive(true);
            if (vent.handSpriteRenderer != null && vent.handSpriteRenderer.gameObject.activeSelf) vent.handSpriteRenderer.gameObject.SetActive(false);
            DrawArm(vent, r);
        }

        /// <summary>The arm from the vent through each bend to the hand, made of the game's own arm pieces.</summary>
        private static void DrawArm(DEN_VentThing vent, Remote r)
        {
            List<Transform> arms = Arms(vent);
            List<MeshRenderer> renderers = ArmRenderers(vent);
            if (arms == null || renderers == null || vent.armAsset == null)
            {
                return;
            }
            var points = new List<Vector3>(r.Joints.Count + 2) { vent.transform.position };
            foreach (Vector3 j in r.Joints) points.Add(WorldSpace.ToLocal(j));
            points.Add(vent.hand.position);
            int pieces = points.Count - 1;
            while (arms.Count < pieces && arms.Count < 50)
            {
                Transform made = Object.Instantiate(vent.armAsset, vent.transform).transform;
                arms.Add(made);
                renderers.Add(made.GetComponent<MeshRenderer>());
            }
            for (int i = 0; i < arms.Count; i++)
            {
                Transform arm = arms[i];
                if (arm == null)
                {
                    continue;
                }
                if (i >= pieces)
                {
                    if (arm.gameObject.activeSelf) arm.gameObject.SetActive(false);
                    continue;
                }
                if (!arm.gameObject.activeSelf) arm.gameObject.SetActive(true);
                Vector3 from = points[i];
                Vector3 to = points[i + 1];
                arm.position = from;
                if (to - from != Vector3.zero)
                {
                    arm.rotation = Quaternion.LookRotation(to - from, Vector3.up);
                }
                float length = Vector3.Distance(to, from);
                arm.localScale = new Vector3(1f, 1f, length);
                if (i < renderers.Count && renderers[i] != null)
                {
                    float slack = Mathf.Clamp01(length) * 0.1f;
                    renderers[i].material.SetFloat("_Slack", slack);
                    renderers[i].material.SetFloat("_Wiggle", slack);
                }
            }
        }

        private static void Release(Remote r)
        {
            if (r.HandBody != null && r.MadeKinematic)
            {
                r.HandBody.isKinematic = false;
            }
            r.MadeKinematic = false;
            r.Placed = false;
        }

        private void ReleaseAll()
        {
            if (remotes.Count == 0)
            {
                return;
            }
            foreach (Remote r in remotes.Values) Release(r);
            remotes.Clear();
        }
    }

    [HarmonyPatch]
    internal static class VentHandPatches
    {
        /// <summary>A hand another PC runs doesn't reach, grab or retreat here on its own.</summary>
        [HarmonyPatch(typeof(DEN_VentThing), "LateUpdate")]
        [HarmonyPrefix]
        private static bool LateUpdate(DEN_VentThing __instance) => !VentHandSync.IsPuppet(__instance);

        [HarmonyPatch(typeof(DEN_VentThing), nameof(DEN_VentThing.Update))]
        [HarmonyPostfix]
        private static void Update(DEN_VentThing __instance)
        {
            try { VentHandSync.AfterUpdate(__instance); } catch (System.Exception e) { Plugin.Log.LogWarning("Vent hand animation: " + e.Message); }
        }
    }
}
