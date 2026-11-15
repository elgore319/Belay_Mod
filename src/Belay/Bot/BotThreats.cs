using System.Collections.Generic;
using Belay.Players;
using UnityEngine;

namespace Belay.Bot
{
    /// <summary>
    /// How a climbing bot deals with creatures. Enemies already go after bots like any teammate (they're
    /// solid bodies the host points enemies at, and hits hurt them). A bot that sees one close by hurries on,
    /// and if one gets right up to it while it has its feet down, it swings at it.
    /// </summary>
    internal static class BotThreats
    {
        private const float AlertRange = 10f;
        private const float StrikeRange = 2f;
        private const float StrikeEvery = 1.1f;
        private const float StrikeDamage = 1f;
        private const float RefreshEvery = 0.5f;

        private static readonly List<Denizen> nearby = new List<Denizen>();
        private static float nextRefresh;

        private static void Refresh()
        {
            if (Time.time < nextRefresh)
            {
                return;
            }
            nextRefresh = Time.time + RefreshEvery;
            nearby.Clear();
            foreach (Denizen d in Object.FindObjectsByType<Denizen>(FindObjectsSortMode.None))
            {
                if (d == null || d.dead || !d.gameObject.activeInHierarchy || Harmless(d))
                {
                    continue;
                }
                nearby.Add(d);
            }
        }

        /// <summary>Roaches are money, and the personal creatures (mask, ticks...) are each player's own.</summary>
        private static bool Harmless(Denizen d)
        {
            return d is DEN_Roach || World.EnemyTargets.Personal(d);
        }

        /// <summary>Returns true while a creature is close enough that the bot should hurry.</summary>
        public static bool Step(BotPlayer bot)
        {
            BotClimber c = bot.Climber;
            if (c == null || bot.Dead)
            {
                return false;
            }
            Refresh();
            Vector3 me = WorldSpace.ToLocal(c.Shared);
            Denizen closest = null;
            float best = AlertRange;
            foreach (Denizen d in nearby)
            {
                if (d == null || d.dead)
                {
                    continue;
                }
                float distance = Vector3.Distance(d.transform.position, me);
                if (distance < best)
                {
                    best = distance;
                    closest = d;
                }
            }
            bot.Threat = closest != null ? closest.name : null;
            if (closest == null)
            {
                return false;
            }
            bot.StrikeTimer -= Time.deltaTime;
            if (best < StrikeRange && c.Grounded && bot.StrikeTimer <= 0f)
            {
                bot.StrikeTimer = StrikeEvery;
                Vector3 dir = closest.transform.position - me;
                if (dir.sqrMagnitude > 0.001f)
                {
                    c.Yaw = Quaternion.LookRotation(new Vector3(dir.x, 0f, dir.z).normalized + Vector3.forward * 0.0001f).eulerAngles.y;
                }
                try
                {
                    Damageable.DamageInfo info = Damageable.DamageInfo.CreateDamageInfo(StrikeDamage, "hammer");
                    info.direction = dir.normalized;
                    closest.Damage(info);
                    Plugin.Log.LogInfo($"{bot.Name} hit {closest.name}");
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogDebug($"{bot.Name} couldn't hit {closest.name}: {e.Message}");
                }
            }
            return true;
        }
    }
}
