using System.Collections.Generic;
using Belay.Net;
using Belay.Players;
using Belay.Rope;
using HarmonyLib;
using UnityEngine;

namespace Belay.Items
{
    /// <summary>
    /// The harpoon: a grapple gun copy whose head sticks in the aimed wall. A rope is strung from there to the
    /// surface behind you (or hangs down if there's none) for the team to climb. One use.
    /// </summary>
    public class Harpoon : MonoBehaviour
    {
        public const string Id = "Belay_Harpoon";

        /// <summary>How far behind you the line can find a surface to pin to.</summary>
        private const float BackReach = 30f;
        /// <summary>A strung rope is this much longer than the gap, so it sags a little.</summary>
        private const float Slack = 1.04f;
        private const float MinHanging = 6f;
        private const float MaxHanging = 22f;
        private const float MaxRope = 60f;
        /// <summary>Ropes in the world at once; the oldest goes first.</summary>
        private const int MaxRopes = 8;
        private const float RopeLifetime = 600f;
        private const float DespawnDistance = 200f;

        internal static void Define()
        {
            CoopItems.Define(new CoopItems.Definition
            {
                Id = Id,
                DisplayName = "Harpoon",
                Template = io => io.itemData.handItemAsset is HandItem_Shoot_Grapple,
                SetupHand = hand => hand.gameObject.AddComponent<HarpoonShot>(),
                Price = 3,
                Chance = 0.5f,
            });
        }

        public static Harpoon Instance { get; private set; }

        private class Line
        {
            public TeamRope Rope;
            public float Until;
        }

        private readonly List<Line> lines = new List<Line>();

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.HarpoonRope, (origin, r) =>
            {
                Vector3 head = r.ReadVector();
                bool strung = r.ReadBoolean();
                Vector3 tail = r.ReadVector();
                float length = r.ReadSingle();
                if (origin != NetSession.LocalId)
                {
                    Spawn(head, strung, tail, length);
                }
            });
        }

        /// <summary>The head stuck on the local player's PC: pin the line behind them and tell the team.</summary>
        internal void OnLocalStick(Vector3 headLocal, Vector3 shooterEye, Vector3 shooterFeet, Transform ignore, LayerMask mask)
        {
            Vector3 back = shooterEye - headLocal;
            bool strung = false;
            Vector3 tailLocal = Vector3.zero;
            if (back.sqrMagnitude > 0.25f)
            {
                Vector3 dir = back.normalized;
                foreach (RaycastHit hit in Physics.RaycastAll(shooterEye, dir, BackReach, mask.value != 0 ? mask.value : ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider == null || ignore != null && hit.collider.transform.IsChildOf(ignore) || hit.collider.GetComponentInParent<RemoteBody>() != null)
                    {
                        continue;
                    }
                    if (!strung || hit.distance < (tailLocal - shooterEye).magnitude)
                    {
                        tailLocal = hit.point + hit.normal * 0.06f;
                        strung = true;
                    }
                }
            }
            float length;
            if (strung)
            {
                length = Mathf.Min(Vector3.Distance(headLocal, tailLocal) * Slack, MaxRope);
            }
            else
            {
                length = Mathf.Clamp(headLocal.y - shooterFeet.y + 1.5f, MinHanging, MaxHanging);
            }
            Vector3 head = WorldSpace.ToShared(headLocal);
            Vector3 tail = WorldSpace.ToShared(tailLocal);
            Spawn(head, strung, tail, length);
            NetSession.Instance.Send(PacketType.HarpoonRope, w =>
            {
                w.WriteVector(head);
                w.Write(strung);
                w.WriteVector(tail);
                w.Write(length);
            });
            Plugin.Log.LogInfo(strung
                ? $"Harpoon line strung from {head} to {tail} ({length:0.0} m)"
                : $"Harpoon stuck at {head}, nothing behind: rope hangs {length:0.0} m");
        }

        private void Spawn(Vector3 head, bool strung, Vector3 tail, float length)
        {
            if (lines.Count >= MaxRopes)
            {
                Remove(0);
            }
            TeamRope rope = strung ? TeamRope.CreateFixed(head, tail, length) : TeamRope.CreateHanging(head, length);
            lines.Add(new Line { Rope = rope, Until = Time.unscaledTime + RopeLifetime });
        }

        private void Remove(int index)
        {
            if (lines[index].Rope != null)
            {
                Destroy(lines[index].Rope.gameObject);
            }
            lines.RemoveAt(index);
        }

        private void FixedUpdate()
        {
            bool ready = WorldSpace.Ready;
            bool inRun = NetSession.Instance != null && NetSession.Instance.InLobby;
            Vector3 me = ready ? ENT_Player.playerObject.transform.position : Vector3.zero;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                Line line = lines[i];
                bool stale = line.Rope == null || !inRun || Time.unscaledTime > line.Until
                    || ready && (WorldSpace.ToLocal(line.Rope.AnchorShared) - me).sqrMagnitude > DespawnDistance * DespawnDistance;
                if (stale || !ready && !CL_GameManager.IsInGame())
                {
                    Remove(i);
                    continue;
                }
                if (ready)
                {
                    line.Rope.Step(Time.fixedDeltaTime, true);
                }
                else
                {
                    line.Rope.Hide();
                }
            }
        }
    }

    /// <summary>Rides on the harpoon's hand item and watches its head.</summary>
    public class HarpoonShot : MonoBehaviour
    {
        private Projectile head;
        private bool tracking;
        private HandItem hand;

        internal void Track(Projectile p)
        {
            head = p;
            tracking = true;
        }

        private void Awake()
        {
            hand = GetComponent<HandItem>();
        }

        private void LateUpdate()
        {
            if (!tracking)
            {
                return;
            }
            if (head == null)
            {
                // Missed and came back (or vanished): one use, so the harpoon is spent anyway.
                tracking = false;
                Spend();
                return;
            }
            if (!head.HasHitSurface())
            {
                return;
            }
            Projectile stuck = head;
            head = null;
            tracking = false;
            ENT_Player player = ENT_Player.playerObject;
            Vector3 point = stuck.transform.position;
            LayerMask mask = stuck.hitMask;
            if (player != null)
            {
                // Sit the head just off the surface, on the side the shot came from.
                Vector3 back = player.transform.position - point;
                if (back.sqrMagnitude > 1e-4f)
                {
                    point += back.normalized * 0.06f;
                }
            }
            Destroy(stuck.gameObject);
            try
            {
                if (Harpoon.Instance != null && player != null)
                {
                    Vector3 eye = player.cam != null ? player.cam.transform.position : player.transform.position;
                    Harpoon.Instance.OnLocalStick(point, eye, player.transform.position + Vector3.down * 1f, player.transform, mask);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Harpoon line failed: " + e.Message);
            }
            Spend();
        }

        private void Spend()
        {
            if (hand != null && hand.item != null)
            {
                hand.RemoveItem();
            }
        }
    }

    [HarmonyPatch(typeof(HandItem_Shoot_Grapple), "CreateProjectile")]
    internal static class HarpoonShotPatch
    {
        [HarmonyPostfix]
        private static void Postfix(HandItem_Shoot_Grapple __instance, Projectile __result)
        {
            if (__result == null)
            {
                return;
            }
            HarpoonShot shot = __instance.GetComponent<HarpoonShot>();
            if (shot != null)
            {
                shot.Track(__result);
            }
        }
    }

    /// <summary>The harpoon head holds: don't let the grapple code reel the shooter toward it.</summary>
    [HarmonyPatch(typeof(HandItem_Shoot_Grapple), "Update")]
    internal static class HarpoonNoReelPatch
    {
        private static readonly AccessTools.FieldRef<HandItem_Shoot_Grapple, bool> HasHit =
            AccessTools.FieldRefAccess<HandItem_Shoot_Grapple, bool>("hasHit");

        [HarmonyPrefix]
        private static void Prefix(HandItem_Shoot_Grapple __instance)
        {
            if (__instance.GetComponent<HarpoonShot>() != null)
            {
                HasHit(__instance) = false;
            }
        }
    }
}
