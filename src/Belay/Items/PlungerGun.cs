using System.Collections.Generic;
using System.IO;
using Belay.Net;
using Belay.Players;
using Belay.Rope;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.Items
{
    /// <summary>
    /// The plunger gun: a copy of the game's grapple gun whose cup sticks where it lands instead of
    /// reeling you in. A climbable cord then hangs from the cup, on everyone's screen, so the whole team can
    /// climb past the gap or overhang one person reached. The gun is used up by the shot, like the grapple.
    /// </summary>
    public class PlungerGun : MonoBehaviour
    {
        public const string Id = "Belay_PlungerGun";

        private const float MinCord = 6f;
        private const float MaxCord = 22f;
        /// <summary>Cords in the world at once; the oldest goes first.</summary>
        private const int MaxCords = 8;
        private const float CordLifetime = 600f;
        private const float DespawnDistance = 200f;

        internal static void Define()
        {
            CoopItems.Define(new CoopItems.Definition
            {
                Id = Id,
                DisplayName = "Plunger Gun",
                Template = io => io.itemData.handItemAsset is HandItem_Shoot_Grapple,
                SetupHand = hand => hand.gameObject.AddComponent<PlungerShot>(),
                Price = 3,
                Chance = 0.5f,
            });
        }

        public static PlungerGun Instance { get; private set; }

        private class Cord
        {
            public TeamRope Rope;
            public float Until;
        }

        private readonly List<Cord> cords = new List<Cord>();

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.PlungerStuck, (origin, r) =>
            {
                Vector3 anchor = r.ReadVector();
                float length = r.ReadSingle();
                if (origin != NetSession.LocalId)
                {
                    Spawn(anchor, length);
                }
            });
        }

        /// <summary>The cup stuck on the local player's PC: hang a cord there and tell the team.</summary>
        internal void OnLocalStick(Vector3 anchorLocal, Vector3 shooterFeet)
        {
            float length = Mathf.Clamp(anchorLocal.y - shooterFeet.y + 1.5f, MinCord, MaxCord);
            Vector3 shared = WorldSpace.ToShared(anchorLocal);
            Spawn(shared, length);
            NetSession.Instance.Send(PacketType.PlungerStuck, w =>
            {
                w.WriteVector(shared);
                w.Write(length);
            });
            Plugin.Log.LogInfo($"Plunger stuck at {shared} with a {length:0.0} m cord");
        }

        private void Spawn(Vector3 anchorShared, float length)
        {
            if (cords.Count >= MaxCords)
            {
                Remove(0);
            }
            TeamRope rope = TeamRope.CreateHanging(anchorShared, length);
            cords.Add(new Cord { Rope = rope, Until = Time.unscaledTime + CordLifetime });
        }

        private void Remove(int index)
        {
            if (cords[index].Rope != null)
            {
                Destroy(cords[index].Rope.gameObject);
            }
            cords.RemoveAt(index);
        }

        private void FixedUpdate()
        {
            bool ready = WorldSpace.Ready;
            bool inRun = NetSession.Instance != null && NetSession.Instance.InLobby;
            Vector3 me = ready ? ENT_Player.playerObject.transform.position : Vector3.zero;
            for (int i = cords.Count - 1; i >= 0; i--)
            {
                Cord cord = cords[i];
                bool stale = cord.Rope == null || !inRun || Time.unscaledTime > cord.Until
                    || ready && (WorldSpace.ToLocal(cord.Rope.AnchorShared) - me).sqrMagnitude > DespawnDistance * DespawnDistance;
                if (stale || !ready && !CL_GameManager.IsInGame())
                {
                    Remove(i);
                    continue;
                }
                if (ready)
                {
                    cord.Rope.Step(Time.fixedDeltaTime, true);
                }
                else
                {
                    cord.Rope.Hide();
                }
            }
        }
    }

    /// <summary>Rides on the plunger gun's hand item and watches its cup.</summary>
    public class PlungerShot : MonoBehaviour
    {
        private Projectile cup;
        private HandItem hand;

        internal void Track(Projectile p)
        {
            cup = p;
        }

        private void Awake()
        {
            hand = GetComponent<HandItem>();
        }

        private void LateUpdate()
        {
            if (cup == null || !cup.HasHitSurface())
            {
                return;
            }
            Projectile stuck = cup;
            cup = null;
            ENT_Player player = ENT_Player.playerObject;
            Vector3 point = stuck.transform.position;
            if (player != null)
            {
                // Sit the cup just off the surface, on the side the shot came from.
                Vector3 back = player.transform.position - point;
                if (back.sqrMagnitude > 1e-4f)
                {
                    point += back.normalized * 0.06f;
                }
            }
            Destroy(stuck.gameObject);
            try
            {
                if (PlungerGun.Instance != null && player != null)
                {
                    PlungerGun.Instance.OnLocalStick(point, player.transform.position + Vector3.down * 1f);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Plunger cord failed: " + e.Message);
            }
            if (hand != null && hand.item != null)
            {
                hand.RemoveItem();
            }
        }
    }

    [HarmonyPatch(typeof(HandItem_Shoot_Grapple), "CreateProjectile")]
    internal static class PlungerShotPatch
    {
        [HarmonyPostfix]
        private static void Postfix(HandItem_Shoot_Grapple __instance, Projectile __result)
        {
            if (__result == null)
            {
                return;
            }
            PlungerShot shot = __instance.GetComponent<PlungerShot>();
            if (shot != null)
            {
                shot.Track(__result);
            }
        }
    }

    /// <summary>The plunger cup holds: don't let the grapple code reel the shooter toward it.</summary>
    [HarmonyPatch(typeof(HandItem_Shoot_Grapple), "Update")]
    internal static class PlungerNoReelPatch
    {
        private static readonly AccessTools.FieldRef<HandItem_Shoot_Grapple, bool> HasHit =
            AccessTools.FieldRefAccess<HandItem_Shoot_Grapple, bool>("hasHit");

        [HarmonyPrefix]
        private static void Prefix(HandItem_Shoot_Grapple __instance)
        {
            if (__instance.GetComponent<PlungerShot>() != null)
            {
                HasHit(__instance) = false;
            }
        }
    }
}
