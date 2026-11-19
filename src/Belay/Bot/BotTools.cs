using System.Reflection;
using Belay.Players;
using HarmonyLib;
using UnityEngine;

namespace Belay.Bot
{
    /// <summary>
    /// Climbing tools a bot carries: with no hold in reach it hammers in a piton or throws a rebar, just like you
    /// would.
    /// </summary>
    internal static class BotTools
    {
        private static readonly MethodInfo AddPlaced = AccessTools.Method(typeof(M_Level), "AddPlacedObject");

        public static HandItem HandAsset(Item item)
        {
            if (item == null)
            {
                return null;
            }
            HandItem asset = item.handItemAsset;
            if (asset == null && item.itemAsset != null && item.itemAsset.itemData != null)
            {
                asset = item.itemAsset.itemData.handItemAsset;
            }
            return asset;
        }

        public static bool IsPiton(Item item) => HandAsset(item) is HandItem_Piton p && p.pitonWorldObject != null;

        public static bool IsRebar(Item item) =>
            item != null && HandAsset(item) is HandItem_Shoot s && s.projectile != null
            && (item.prefabName ?? "").ToLowerInvariant().Contains("rebar");

        public static bool IsTool(Item item) => IsPiton(item) || IsRebar(item);

        /// <summary>Make a hold between the shoulder and the goal, if the bot carries something to make one with.</summary>
        public static bool MakeHold(BotPlayer bot, Vector3 shoulder, Vector3 goal, Vector3 into)
        {
            Item piton = bot.Items.Find(IsPiton);
            Item rebar = bot.Items.Find(IsRebar);
            if (piton == null && rebar == null)
            {
                return false;
            }
            // Aim at the wall, partway toward the goal (above or to the side), within arm's reach.
            into.y = 0f;
            into = into.sqrMagnitude > 0.001f ? into.normalized : Vector3.forward;
            Vector3 toGoal = (goal - shoulder).normalized;
            Vector3 dir = (into + toGoal * 0.9f).normalized;
            if (!WallHit(bot, shoulder, dir, 1.6f, out RaycastHit hit))
            {
                dir = (into + Vector3.up * 0.6f).normalized;
                if (!WallHit(bot, shoulder, dir, 1.6f, out hit))
                {
                    return false;
                }
            }
            if (piton != null && PlacePiton(bot, piton, hit))
            {
                return true;
            }
            return rebar != null && ThrowRebar(bot, rebar, shoulder, hit);
        }

        private static bool WallHit(BotPlayer bot, Vector3 from, Vector3 dir, float range, out RaycastHit wall)
        {
            wall = default;
            float nearest = float.MaxValue;
            foreach (RaycastHit h in Physics.RaycastAll(from, dir, range, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                if (h.collider == null || RemoteBody.Of(h.collider) != null || h.collider.GetComponentInParent<ENT_Player>() != null
                    || h.collider.attachedRigidbody != null && !h.collider.attachedRigidbody.isKinematic)
                {
                    continue;
                }
                if (h.distance < nearest)
                {
                    nearest = h.distance;
                    wall = h;
                }
            }
            // Only walls, not floors or ceilings.
            return nearest < float.MaxValue && Mathf.Abs(wall.normal.y) < 0.6f;
        }

        private static bool TagsAllow(Collider c, string[] need, string[] block)
        {
            ObjectTagger tagger = c.GetComponent<ObjectTagger>();
            if (need != null && need.Length > 0)
            {
                if (tagger == null)
                {
                    return false;
                }
                foreach (string t in need)
                {
                    if (!tagger.HasTag(t))
                    {
                        return false;
                    }
                }
            }
            if (tagger != null && block != null)
            {
                foreach (string t in block)
                {
                    if (tagger.HasTag(t))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool PlacePiton(BotPlayer bot, Item item, RaycastHit hit)
        {
            var piton = (HandItem_Piton)HandAsset(item);
            if (!TagsAllow(hit.collider, piton.placementTags, piton.blockTags))
            {
                return false;
            }
            try
            {
                Vector3 at = hit.point + hit.normal * piton.placeOffset;
                GameObject placed = Object.Instantiate(piton.pitonWorldObject, at,
                    Quaternion.LookRotation(hit.normal) * Quaternion.Euler(0f, 0f, Random.Range(-30, 30)));
                placed.transform.parent = WorldLoader.GetCurrentLevelParentRoot();
                placed.AddComponent<UT_SoftParent>().Initialize(hit.collider.transform);
                // Shared like a player's piton (PlacedSync picks it up from here).
                World.PlacedSync.CurrentPiton = piton;
                try
                {
                    M_Level level = World.WorldCatchUp.LevelAt(at);
                    if (level == null && WorldLoader.initialized)
                    {
                        level = WorldLoader.instance.GetCurrentLevel().GetLevel();
                    }
                    if (level != null && AddPlaced != null)
                    {
                        AddPlaced.Invoke(level, new object[] { placed });
                    }
                }
                finally
                {
                    World.PlacedSync.CurrentPiton = null;
                }
                // Hammered all the way in, so it holds.
                CL_Handhold handhold = placed.GetComponentInChildren<CL_Handhold>();
                if (handhold != null && !handhold.secure)
                {
                    handhold.HammerIn(1f);
                }
                bot.Items.Remove(item);
                Item_Object obj = item.GetDropObject();
                if (obj != null)
                {
                    Object.Destroy(obj.gameObject);
                }
                Plugin.Log.LogInfo($"{bot.Name} hammered in a piton");
                return true;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"{bot.Name} couldn't place a piton: {e.Message}");
                return false;
            }
        }

        private static bool ThrowRebar(BotPlayer bot, Item item, Vector3 shoulder, RaycastHit hit)
        {
            var shoot = (HandItem_Shoot)HandAsset(item);
            try
            {
                Vector3 dir = (hit.point - shoulder).normalized;
                GameObject go = Object.Instantiate(shoot.projectile, shoulder + dir * 0.3f, Quaternion.LookRotation(dir));
                Projectile projectile = go.GetComponent<Projectile>();
                if (projectile == null)
                {
                    Object.Destroy(go);
                    return false;
                }
                if (PlayerSync.Instance.Remotes.TryGetValue(bot.Id, out RemotePlayer body) && body != null)
                {
                    projectile.AddIgnore(body.gameObject);
                }
                if (ENT_Player.playerObject != null)
                {
                    projectile.AddIgnore(ENT_Player.playerObject.gameObject);
                }
                projectile.Initialize(dir * shoot.shootSpeed, null, item);
                if (shoot.useSelfItemAsProjectileHitEffect)
                {
                    projectile.SetHitItem(item);
                }
                bot.Items.Remove(item);
                Plugin.Log.LogInfo($"{bot.Name} threw a rebar into the wall");
                return true;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"{bot.Name} couldn't throw a rebar: {e.Message}");
                return false;
            }
        }
    }
}
