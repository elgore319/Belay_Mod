using System.Collections.Generic;
using Belay.World;
using UnityEngine;

namespace Belay.Players
{
    /// <summary>
    /// Elevators and lifts move on every PC by themselves, never at exactly the same height at the same
    /// moment. A rider's position used to be sent in world space, so on other screens they hovered above
    /// or sank through the floor of the elevator, as if they weren't on it. Now a player standing on a
    /// moving floor sends their position in that floor's own space, and every PC places them on its own
    /// copy of the elevator, moving with it smoothly between updates.
    /// </summary>
    internal static class PlatformRide
    {
        /// <summary>How long after the floor last moved it still counts as a moving floor (seconds).</summary>
        private const float MovingGrace = 3f;
        private const float RayLength = 2.5f;

        private static Transform ground;
        private static Vector3 groundLastPos;
        private static Quaternion groundLastRot;
        private static float groundMovedAt = -100f;

        private static readonly Dictionary<string, Transform> resolved = new Dictionary<string, Transform>();
        private static readonly Dictionary<string, float> failedAt = new Dictionary<string, float>();

        /// <summary>Every frame: what are we standing on, and is it moving?</summary>
        public static void TrackLocal()
        {
            Transform now = FindGround();
            if (now != ground)
            {
                ground = now;
                groundMovedAt = -100f;
                if (now != null)
                {
                    groundLastPos = now.position;
                    groundLastRot = now.rotation;
                }
                return;
            }
            if (ground == null)
            {
                return;
            }
            if ((ground.position - groundLastPos).sqrMagnitude > 1e-6f || Quaternion.Angle(ground.rotation, groundLastRot) > 0.01f)
            {
                groundMovedAt = Time.time;
            }
            groundLastPos = ground.position;
            groundLastRot = ground.rotation;
        }

        private static Transform FindGround()
        {
            ENT_Player player = ENT_Player.playerObject;
            if (player == null || player.dead)
            {
                return null;
            }
            int mask = ~(1 << player.gameObject.layer);
            Vector3 from = player.transform.position + Vector3.up * 0.2f;
            if (!Physics.Raycast(from, Vector3.down, out RaycastHit hit, RayLength, mask, QueryTriggerInteraction.Ignore))
            {
                return null;
            }
            // Level geometry only: not a teammate's body or a loose prop.
            if (hit.collider.GetComponentInParent<M_Level>() == null || hit.collider.attachedRigidbody != null && !hit.collider.attachedRigidbody.isKinematic)
            {
                return null;
            }
            return hit.collider.transform;
        }

        /// <summary>When we're riding a moving floor, add our position in its space to the outgoing state.</summary>
        public static void FillLocal(ref Net.PlayerState state)
        {
            ENT_Player player = ENT_Player.playerObject;
            if (ground == null || player == null || Time.time - groundMovedAt > MovingGrace)
            {
                return;
            }
            state.PlatformId = ButtonSync.ObjectId(ground);
            state.PlatformPosition = ground.InverseTransformPoint(player.transform.position);
            state.PlatformLeftHand = ground.InverseTransformPoint(WorldSpace.ToLocal(state.LeftHand));
            state.PlatformRightHand = ground.InverseTransformPoint(WorldSpace.ToLocal(state.RightHand));
        }

        /// <summary>Our own copy of a teammate's moving floor, or null when it isn't loaded here.</summary>
        public static Transform Resolve(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            if (resolved.TryGetValue(id, out Transform cached) && cached != null)
            {
                return cached;
            }
            if (failedAt.TryGetValue(id, out float when) && Time.unscaledTime - when < 1f)
            {
                return null;
            }
            Transform found = Find(id);
            if (found != null)
            {
                resolved[id] = found;
                failedAt.Remove(id);
                Plugin.Log.LogInfo("Teammate is riding " + id);
            }
            else
            {
                failedAt[id] = Time.unscaledTime;
            }
            return found;
        }

        /// <summary>Walk an id from ButtonSync.ObjectId ("Level@height/3Child/0Floor") back to the object.</summary>
        private static Transform Find(string id)
        {
            int slash = id.IndexOf('/');
            if (slash < 0)
            {
                return null;
            }
            string head = id.Substring(0, slash);
            int at = head.LastIndexOf('@');
            string levelName = at >= 0 ? head.Substring(0, at) : head;
            int.TryParse(at >= 0 ? head.Substring(at + 1) : "0", out int height);
            string[] parts = id.Substring(slash + 1).Split('/');
            // Same level name at the closest height first (a level that itself moves, like a lift shaft,
            // won't be at the exact height the rider saw).
            var levels = new List<M_Level>();
            foreach (M_Level level in Object.FindObjectsByType<M_Level>(FindObjectsSortMode.None))
            {
                if (level != null && level.name == levelName)
                {
                    levels.Add(level);
                }
            }
            levels.Sort((a, b) => Mathf.Abs(WorldSpace.ToShared(a.transform.position).y - height)
                .CompareTo(Mathf.Abs(WorldSpace.ToShared(b.transform.position).y - height)));
            foreach (M_Level level in levels)
            {
                Transform cursor = level.transform;
                foreach (string part in parts)
                {
                    Transform next = null;
                    for (int i = 0; i < cursor.childCount; i++)
                    {
                        Transform child = cursor.GetChild(i);
                        if (i + child.name == part)
                        {
                            next = child;
                            break;
                        }
                    }
                    cursor = next;
                    if (cursor == null)
                    {
                        break;
                    }
                }
                if (cursor != null && Mathf.Abs(WorldSpace.ToShared(level.transform.position).y - height) < 200f)
                {
                    return cursor;
                }
            }
            return null;
        }
    }
}
