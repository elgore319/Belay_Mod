using System.Collections.Generic;
using Belay.Players;
using Belay.Rope;
using UnityEngine;

namespace Belay.Items
{
    /// <summary>
    /// The anchor bolt: a copy of the game's piton, hammered into a wall the same way (and shared with the
    /// team the same way, so it's a handhold for everyone too). It also works like a lead climber's
    /// protection: climb past it and you clip in, and if you then fall, a short rope catches you just
    /// below it instead of letting you drop all the way. Each PC catches its own player.
    /// </summary>
    public class AnchorBolt : MonoBehaviour
    {
        public const string Id = "Belay_AnchorBolt";

        internal static readonly List<AnchorBolt> All = new List<AnchorBolt>();

        internal static void Define()
        {
            CoopItems.Define(new CoopItems.Definition
            {
                Id = Id,
                DisplayName = "Anchor Bolt",
                Template = IsPitonItem,
                SetupHand = hand =>
                {
                    var piton = (HandItem_Piton)hand;
                    if (piton.pitonWorldObject == null)
                    {
                        return;
                    }
                    GameObject world = Instantiate(piton.pitonWorldObject, hand.transform.parent);
                    world.name = Id + "_World";
                    world.AddComponent<AnchorBolt>();
                    Tint(world);
                    piton.pitonWorldObject = world;
                },
                Price = 2,
                Chance = 0.5f,
            });
        }

        private static bool IsPitonItem(Item_Object io)
        {
            if (!(io.itemData.handItemAsset is HandItem_Piton piton) || piton.GetType() != typeof(HandItem_Piton))
            {
                return false;
            }
            return piton.pitonWorldObject != null && piton.pitonWorldObject.GetComponentInChildren<CL_Handhold>(true) != null
                && (io.itemData.prefabName ?? "").ToLowerInvariant().Contains("piton");
        }

        /// <summary>A brass tint so a bolt reads differently from a plain piton.</summary>
        private static void Tint(GameObject world)
        {
            var brass = new Color(1f, 0.72f, 0.35f);
            foreach (Renderer r in world.GetComponentsInChildren<Renderer>(true))
            {
                Material[] mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null && mats[i].HasProperty("_Color"))
                    {
                        var m = new Material(mats[i]) { name = mats[i].name + "_Belay" };
                        m.color = mats[i].color * brass;
                        mats[i] = m;
                    }
                }
                r.sharedMaterials = mats;
            }
        }

        public Vector3 Point => transform.position;

        private void OnEnable()
        {
            All.Add(this);
        }

        private void OnDisable()
        {
            All.Remove(this);
        }
    }

    /// <summary>Clips the local player into anchor bolts they climb past and catches their falls.</summary>
    public class AnchorCatch : MonoBehaviour
    {
        // ENT_Player moves by vel * fixedDeltaTime * 175, and AddForce(v) adds v / 10 to vel.
        private const float VelToMetersPerSecond = 175f;
        private const float ForceToVel = 0.1f;

        /// <summary>How close sideways you must pass a bolt to clip in.</summary>
        private const float ClipRadius = 2.5f;
        /// <summary>Rope between the bolt and your harness once it has caught you.</summary>
        private const float Slack = 1.6f;
        /// <summary>Wander this far from the bolt's line and the clip comes off.</summary>
        private const float UnclipRadius = 6f;
        private const float MaxRun = 30f;

        private AnchorBolt clipped;
        private bool caught;
        private LineRenderer line;

        private void FixedUpdate()
        {
            if (!WorldSpace.Ready || AnchorBolt.All.Count == 0 && clipped == null)
            {
                Unclip();
                return;
            }
            ENT_Player player = ENT_Player.playerObject;
            if (player == null || player.dead || player.IsFlying())
            {
                Unclip();
                return;
            }
            Vector3 harness = player.transform.position + Vector3.down * 0.4f;
            if (clipped == null || !clipped.isActiveAndEnabled)
            {
                clipped = null;
                caught = false;
            }

            // Clip into the highest bolt you're above and close to.
            AnchorBolt best = clipped;
            foreach (AnchorBolt bolt in AnchorBolt.All)
            {
                if (bolt == null)
                {
                    continue;
                }
                Vector3 off = harness - bolt.Point;
                if (off.y > 0.3f && off.y < MaxRun && Flat(off) < ClipRadius && (best == null || bolt.Point.y > best.Point.y))
                {
                    best = bolt;
                }
            }
            if (best != clipped)
            {
                clipped = best;
                caught = false;
            }
            if (clipped == null)
            {
                return;
            }

            Vector3 toHarness = harness - clipped.Point;
            if (toHarness.y > 0f && (Flat(toHarness) > UnclipRadius || toHarness.y > MaxRun))
            {
                Unclip();
                return;
            }
            bool holding = player.IsHanging();
            if (caught && (holding || player.IsGrounded()))
            {
                // Back on the wall or on your feet: the rope goes slack until you climb past again.
                if (toHarness.y < 0f)
                {
                    Unclip();
                }
                caught = false;
                return;
            }
            if (!caught && toHarness.y < 0f && player.IsGrounded())
            {
                // Walked or dropped down onto something below the bolt on purpose: unclip.
                Unclip();
                return;
            }
            if (holding || toHarness.y > -Slack * 0.5f)
            {
                return;
            }

            float distance = toHarness.magnitude;
            if (distance <= Slack)
            {
                return;
            }
            // Below the bolt and past the rope's length: the rope goes taut.
            Vector3 dir = -toHarness / distance; // toward the bolt
            Vector3 vel = player.GetVelocity();
            float outward = -Vector3.Dot(vel, dir);
            if (outward > 0f)
            {
                if (!caught && outward * VelToMetersPerSecond > 3f)
                {
                    Plugin.Log.LogInfo("Anchor bolt caught a fall");
                }
                caught = true;
                player.AddForce(dir * (outward / ForceToVel));
                player.ResetFallingHeight();
                player.AddIgnoreFallDamageTime(0.5f);
            }
            float pull = 8f * (distance - Slack) * Time.fixedDeltaTime;
            player.AddForce(dir * (pull / VelToMetersPerSecond / ForceToVel));
        }

        private void LateUpdate()
        {
            ENT_Player player = ENT_Player.playerObject;
            bool show = clipped != null && player != null && WorldSpace.Ready;
            if (show)
            {
                if (line == null)
                {
                    var go = new GameObject("Belay_BoltRope");
                    DontDestroyOnLoad(go);
                    line = go.AddComponent<LineRenderer>();
                    line.sharedMaterial = RopeLook.Material;
                    line.widthMultiplier = 0.03f;
                    line.positionCount = 2;
                    line.textureMode = LineTextureMode.Tile;
                }
                line.SetPosition(0, clipped.Point);
                line.SetPosition(1, player.transform.position + Vector3.down * 0.4f);
            }
            if (line != null && line.enabled != show)
            {
                line.enabled = show;
            }
        }

        private void Unclip()
        {
            clipped = null;
            caught = false;
        }

        private static float Flat(Vector3 v)
        {
            v.y = 0f;
            return v.magnitude;
        }
    }
}
