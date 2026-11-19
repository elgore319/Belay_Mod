using System.Collections.Generic;
using Belay.Players;
using UnityEngine;

namespace Belay.Bot
{
    /// <summary>
    /// How a climbing bot deals with creatures: it hurries past nearby enemies and swings at one that gets right
    /// up to it.
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

        // ---------------------------------------------------------------- Barnacles

        private const float TongueReach = 0.45f;
        private const int HitsToBreakFree = 4;

        /// <summary>
        /// Barnacle tongues only grab the player and creatures, so bots check them here: caught, hauled up and
        /// bitten until it kills the barnacle or tears free.
        /// </summary>
        private static bool Barnacles(BotPlayer bot, BotClimber c, Vector3 me)
        {
            if (c.Grabbed)
            {
                DEN_Barnacle holder = bot.GrabbedBarnacle;
                if (holder == null || holder.dead || !holder.gameObject.activeInHierarchy)
                {
                    LetGo(bot, c, "the barnacle let go");
                    return false;
                }
                bot.Threat = holder.name;
                // At the mouth: it bites.
                if (Vector3.Distance(me, c.GrabbedBy.position) < 1.6f)
                {
                    bot.BiteTimer -= Time.deltaTime;
                    if (bot.BiteTimer <= 0f)
                    {
                        bot.BiteTimer = Mathf.Max(0.3f, holder.attackRate);
                        TestBot.Instance.Hurt(bot.Id, holder.damage, holder.name, "barnacle");
                        if (bot.Dead)
                        {
                            c.GrabbedBy = null;
                            return true;
                        }
                    }
                }
                // Fight it.
                bot.StrikeTimer -= Time.deltaTime;
                if (bot.StrikeTimer <= 0f)
                {
                    bot.StrikeTimer = StrikeEvery;
                    Hit(bot, holder, holder.transform.position - me);
                    if (++bot.BarnacleHits >= HitsToBreakFree && !holder.dead)
                    {
                        LetGo(bot, c, "tore free of the barnacle");
                    }
                }
                return true;
            }

            bot.GrabCooldown -= Time.deltaTime;
            if (bot.GrabCooldown > 0f)
            {
                return false;
            }
            foreach (Denizen d in nearby)
            {
                if (!(d is DEN_Barnacle barnacle) || barnacle.dead || Vector3.Distance(barnacle.transform.position, me) > 14f
                    || !(barnacle.tongueInterface is Barnacle_Tongue_Interface_Module_Basic basic) || basic.tongues == null)
                {
                    continue;
                }
                foreach (Barnacle_Tongue_Basic tongue in basic.tongues)
                {
                    if (tongue == null || tongue.tongue == null || tongue.GetGrabbed() != null)
                    {
                        continue;
                    }
                    Vector3 root = tongue.tongue.position;
                    Vector3 tip = tongue.GetReachPosition();
                    if (Vector3.Distance(root, tip) < 0.6f || DistanceToSegment(me, root, tip, BotHalf()) > TongueReach)
                    {
                        continue;
                    }
                    c.GrabbedBy = tongue.tongue;
                    bot.GrabbedBarnacle = barnacle;
                    bot.BarnacleHits = 0;
                    bot.BiteTimer = 0.5f;
                    bot.StrikeTimer = 0.6f;
                    Plugin.Log.LogInfo($"{bot.Name} was grabbed by {barnacle.name}");
                    try
                    {
                        barnacle.OnGrab();
                    }
                    catch (System.Exception)
                    {
                        // Only sound and events; the grab works without them.
                    }
                    return true;
                }
            }
            return false;
        }

        private static void LetGo(BotPlayer bot, BotClimber c, string why)
        {
            c.GrabbedBy = null;
            bot.GrabbedBarnacle = null;
            bot.GrabCooldown = 3f;
            Plugin.Log.LogInfo($"{bot.Name}: {why}");
        }

        private static float BotHalf() => RemoteBody.Height * 0.5f;

        /// <summary>Closest distance from the bot's body (a vertical segment) to the tongue.</summary>
        private static float DistanceToSegment(Vector3 center, Vector3 a, Vector3 b, float half)
        {
            float best = float.MaxValue;
            for (int i = -1; i <= 1; i++)
            {
                Vector3 p = center + Vector3.up * (half * 0.8f * i);
                Vector3 ab = b - a;
                float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 0.0001f));
                best = Mathf.Min(best, Vector3.Distance(p, a + ab * t));
            }
            return best;
        }

        private static void Hit(BotPlayer bot, Denizen target, Vector3 dir)
        {
            try
            {
                Damageable.DamageInfo info = Damageable.DamageInfo.CreateDamageInfo(StrikeDamage, "hammer");
                info.direction = dir.sqrMagnitude > 0.001f ? dir.normalized : Vector3.up;
                target.Damage(info);
                Plugin.Log.LogInfo($"{bot.Name} hit {target.name}");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogDebug($"{bot.Name} couldn't hit {target.name}: {e.Message}");
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
            if (Barnacles(bot, c, me))
            {
                return true;
            }
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
                Hit(bot, closest, dir);
            }
            return true;
        }
    }
}
