using System.Collections.Generic;
using UnityEngine;

namespace Belay.Players
{
    /// <summary>
    /// Raycasts against the level geometry only (what the local player's capsule bumps into), skipping
    /// teammate bodies, loose props and trigger volumes. Used to keep teammate avatars out of walls.
    /// </summary>
    public static class WorldProbe
    {
        private static readonly RaycastHit[] hits = new RaycastHit[16];
        private static readonly Dictionary<int, bool> ignored = new Dictionary<int, bool>();
        private static int mask;
        private static int maskForLayer = -1;

        private static int Mask
        {
            get
            {
                ENT_Player player = ENT_Player.playerObject;
                if (player == null)
                {
                    return 0;
                }
                int playerLayer = player.gameObject.layer;
                if (playerLayer != maskForLayer)
                {
                    maskForLayer = playerLayer;
                    mask = 0;
                    for (int layer = 0; layer < 32; layer++)
                    {
                        if (layer != playerLayer && !Physics.GetIgnoreLayerCollision(layer, playerLayer))
                        {
                            mask |= 1 << layer;
                        }
                    }
                    ignored.Clear();
                }
                return mask;
            }
        }

        /// <summary>Distance to the first wall along <paramref name="direction"/>, or <paramref name="max"/> when clear.</summary>
        public static float Distance(Vector3 origin, Vector3 direction, float max)
        {
            return Cast(origin, direction, max, out _) ? lastDistance : max;
        }

        private static float lastDistance;

        /// <summary>The first wall along the ray, if any.</summary>
        public static bool Cast(Vector3 origin, Vector3 direction, float max, out RaycastHit hit)
        {
            hit = default;
            int m = Mask;
            if (m == 0 || max <= 0f || direction.sqrMagnitude < 1e-8f)
            {
                return false;
            }
            int count = Physics.RaycastNonAlloc(origin, direction.normalized, hits, max, m, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                if (hits[i].distance < best && !Ignored(hits[i].collider))
                {
                    best = hits[i].distance;
                    hit = hits[i];
                    found = true;
                }
            }
            lastDistance = found ? best : max;
            return found;
        }

        /// <summary>Whether level geometry lies between two points.</summary>
        public static bool Blocked(Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            float length = d.magnitude;
            return length > 0.05f && Cast(from, d, length, out _);
        }

        private static bool Ignored(Collider c)
        {
            if (c == null)
            {
                return true;
            }
            int id = c.GetInstanceID();
            if (ignored.TryGetValue(id, out bool skip))
            {
                return skip;
            }
            Rigidbody rb = c.attachedRigidbody;
            skip = c.GetComponentInParent<RemoteBody>() != null
                || rb != null && !rb.isKinematic
                || c is CharacterController
                || ENT_Player.playerObject != null && c.transform.IsChildOf(ENT_Player.playerObject.transform);
            if (ignored.Count > 4096)
            {
                ignored.Clear();
            }
            ignored[id] = skip;
            return skip;
        }
    }
}
