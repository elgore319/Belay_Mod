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
    /// Shootable level objects (UT_Damage, and ENT_Hitbox parts with their own health) used to take damage on the
    /// shooter's PC only, so things like the Nest elevator you shoot down or the plants you shoot only reacted
    /// there. Every health drop is now shared:
    /// - A hit from the local player is replayed on every PC with the same damage, so the object's per-weapon
    ///   events, damage tiers and kill run everywhere.
    /// - Any other drop (fall, blast, the level's own script) is shared as the health it left; a PC that is
    ///   higher takes it down to that, so damage that happened on several PCs isn't counted twice.
    /// The lowest health seen per object is kept, so a PC that builds that part of the map later (or a hidden
    /// object that turns on later) catches up when it appears.
    /// </summary>
    public class LevelDamage : MonoBehaviour
    {
        private const byte KindDamage = 0, KindHitbox = 1;
        private const float ScanEvery = 2f;

        public static LevelDamage Instance { get; private set; }

        internal static bool Applying;

        private class State
        {
            public byte Kind;
            public float Health;
        }

        /// <summary>Lowest health per object id this run, from any PC.</summary>
        private readonly Dictionary<string, State> known = new Dictionary<string, State>();
        /// <summary>Known states not applied here yet (the object isn't built or switched on here).</summary>
        private readonly HashSet<string> pending = new HashSet<string>();
        private float nextScan;
        private bool levelsChanged;
        private int runId;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.LevelDamaged, HandleDamaged);
        }

        internal static bool Live => Instance != null && !Applying && !EnemySync.Applying && NetSession.InLobbyRun;

        private void Update()
        {
            NetSession net = NetSession.Instance;
            if (net == null || !net.InLobby)
            {
                return;
            }
            if (runId != net.CurrentRun.RunId)
            {
                runId = net.CurrentRun.RunId;
                Forget();
            }
            if (pending.Count == 0 || !WorldSpace.Ready || Time.unscaledTime < nextScan)
            {
                return;
            }
            nextScan = Time.unscaledTime + ScanEvery;
            if (!levelsChanged)
            {
                return;
            }
            levelsChanged = false;
            CatchUp();
        }

        /// <summary>New run, or the world was rebuilt from a save: earlier damage no longer applies.</summary>
        internal void Forget()
        {
            found.Clear();
            known.Clear();
            pending.Clear();
        }

        internal void OnLevelSpawned()
        {
            levelsChanged = true;
            nextScan = Mathf.Max(nextScan, Time.unscaledTime + 1f);
        }

        // ---------------------------------------------------------------- Local damage

        internal static bool FromLocalPlayer(Damageable.DamageInfo info)
        {
            ENT_Player player = ENT_Player.playerObject;
            if (player == null || info == null)
            {
                return false;
            }
            return info.sourceEntity == player
                || (info.sourceObject != null && info.sourceObject == player.gameObject)
                || info.HasTag("player");
        }

        internal void OnLocalDrop(Component c, byte kind, float before, float after, Damageable.DamageInfo info)
        {
            string id = ButtonSync.ObjectId(c);
            Remember(id, kind, after);
            bool replay = FromLocalPlayer(info);
            float amount = info?.amount ?? 0f;
            string type = info?.type ?? "";
            string tags = info?.tags != null ? string.Join("|", info.tags) : "";
            Vector3 at = WorldSpace.ToShared(info != null && info.position != Vector3.zero ? info.position : c.transform.position);
            Plugin.Log.LogInfo($"Level damage here: {c.name} {before:0.##} -> {after:0.##}{(replay ? " (your hit)" : "")}");
            NetSession.Instance.Send(PacketType.LevelDamaged, w =>
            {
                w.Write(id);
                w.Write(kind);
                w.Write(replay);
                w.Write(amount);
                w.Write(type);
                w.Write(tags);
                w.Write(after);
                w.WriteVector(at);
            });
        }

        private void Remember(string id, byte kind, float health)
        {
            if (!known.TryGetValue(id, out State s))
            {
                known[id] = new State { Kind = kind, Health = health };
            }
            else if (health < s.Health)
            {
                s.Health = health;
            }
        }

        // ---------------------------------------------------------------- Teammates

        private void HandleDamaged(SteamId origin, BinaryReader r)
        {
            string id = r.ReadString();
            byte kind = r.ReadByte();
            bool replay = r.ReadBoolean();
            float amount = r.ReadSingle();
            string type = r.ReadString();
            string tags = r.ReadString();
            float after = r.ReadSingle();
            Vector3 at = r.ReadVector();
            if (origin == NetSession.LocalId)
            {
                return;
            }
            Remember(id, kind, after);
            if (!WorldSpace.Ready)
            {
                pending.Add(id);
                return;
            }
            Component c = Find(id, kind);
            if (c == null)
            {
                pending.Add(id);
                return;
            }
            var info = new Damageable.DamageInfo
            {
                amount = amount,
                type = type,
                tags = new List<string>(tags.Length > 0 ? tags.Split('|') : new string[0]),
                position = WorldSpace.ToLocal(at),
            };
            Apply(c, kind, replay ? info : null, known[id].Health);
        }

        private void CatchUp()
        {
            var done = new List<string>();
            foreach (string id in pending)
            {
                if (!known.TryGetValue(id, out State s))
                {
                    done.Add(id);
                    continue;
                }
                Component c = Find(id, s.Kind);
                if (c != null)
                {
                    Plugin.Log.LogInfo($"Catching up a teammate's damage on {c.name}");
                    Apply(c, s.Kind, null, s.Health);
                    done.Add(id);
                }
            }
            foreach (string id in done)
            {
                pending.Remove(id);
            }
        }

        private static readonly Dictionary<string, Component> found = new Dictionary<string, Component>();

        private static Component Find(string id, byte kind)
        {
            if (found.TryGetValue(id, out Component hit))
            {
                if (hit != null)
                {
                    return hit;
                }
                found.Remove(id);
            }
            hit = Search(id, kind);
            if (hit != null)
            {
                found[id] = hit;
            }
            return hit;
        }

        private static Component Search(string id, byte kind)
        {
            if (kind == KindHitbox)
            {
                foreach (ENT_Hitbox h in FindObjectsByType<ENT_Hitbox>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (h != null && h.independentHealth && ButtonSync.ObjectId(h) == id) return h;
                }
                return null;
            }
            foreach (UT_Damage d in FindObjectsByType<UT_Damage>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (d != null && d.maxHealth > 0f && ButtonSync.ObjectId(d) == id) return d;
            }
            return null;
        }

        private static void Apply(Component c, byte kind, Damageable.DamageInfo replay, float target)
        {
            Applying = true;
            try
            {
                if (kind == KindHitbox)
                {
                    ApplyHitbox((ENT_Hitbox)c, replay, target);
                }
                else
                {
                    ApplyDamage((UT_Damage)c, replay, target);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't apply a teammate's damage on {c.name}: {e.Message}");
            }
            finally
            {
                Applying = false;
            }
        }

        private static readonly AccessTools.FieldRef<UT_Damage, bool> DamageDead = AccessTools.FieldRefAccess<UT_Damage, bool>("dead");
        private static readonly AccessTools.FieldRef<ENT_Hitbox, bool> HitboxDestroyed = AccessTools.FieldRefAccess<ENT_Hitbox, bool>("destroyed");

        private static void ApplyDamage(UT_Damage d, Damageable.DamageInfo replay, float target)
        {
            if (DamageDead(d))
            {
                return;
            }
            if (replay != null)
            {
                // The teammate's own hit: same weapon, same multipliers, same events. Turn damage on in case this
                // PC hasn't reached the point where the level allows it yet.
                bool could = d.canDamage;
                d.canDamage = true;
                d.Damage(replay);
                d.canDamage = could;
            }
            if (DamageDead(d) || d.health <= target + 0.001f)
            {
                return;
            }
            // Bring health down to the team's, running what the game runs on the way.
            float amount = d.health - target;
            foreach (UT_Damage.DamageTiers tier in d.damageTiers)
            {
                if (!tier.hasEntered && target <= tier.healthThreshold)
                {
                    tier.hasEntered = true;
                    tier.onEnterTier?.Invoke();
                    if (tier.audioClip != null)
                    {
                        AudioManager.PlaySound(tier.audioClip, d.transform.position);
                    }
                }
            }
            d.DamageAction?.Invoke(amount);
            d.DamageEvent?.Invoke();
            d.health = target;
            if (d.health <= 0f && !DamageDead(d))
            {
                DamageDead(d) = true;
                d.Kill();
            }
        }

        private static void ApplyHitbox(ENT_Hitbox h, Damageable.DamageInfo replay, float target)
        {
            if (HitboxDestroyed(h) || (replay == null && h.health <= target + 0.001f))
            {
                return;
            }
            // The enemy behind the part keeps its own health sync, so only the part itself is hit here.
            GameEntity entity = h.entity;
            if (replay != null)
            {
                bool detach = entity != null && EnemySync.Tracked(entity);
                if (detach) h.entity = null;
                try
                {
                    h.Damage(replay);
                }
                finally
                {
                    if (detach) h.entity = entity;
                }
                if (HitboxDestroyed(h) || h.health <= target + 0.001f)
                {
                    return;
                }
            }
            h.health = target;
            h.onDamageHealthEvent?.Invoke();
            if (h.health <= 0f && !HitboxDestroyed(h))
            {
                h.onDestroyEvent?.Invoke();
                HitboxDestroyed(h) = true;
                // The enemy's own death (if this part kills it) comes through enemy sync.
                if (h.killEntityOnDestroy && entity != null && !EnemySync.Tracked(entity) && !entity.dead)
                {
                    entity.Kill("");
                }
            }
            if (h.healthStateEvents != null)
            {
                foreach (ENT_Hitbox.HealthStateEvent e in h.healthStateEvents)
                {
                    e.CheckEvent(h);
                }
            }
        }

        internal static byte DamageKind => KindDamage;
        internal static byte HitboxKind => KindHitbox;
    }

    [HarmonyPatch]
    internal static class LevelDamagePatches
    {
        [HarmonyPatch(typeof(UT_Damage), nameof(UT_Damage.Damage), new[] { typeof(Damageable.DamageInfo) })]
        [HarmonyPrefix]
        private static void DamagePrefix(UT_Damage __instance, out float __state)
        {
            // NaN: it couldn't be damaged, so the hit did nothing here.
            __state = __instance != null && __instance.canDamage ? __instance.health : float.NaN;
        }

        [HarmonyPatch(typeof(UT_Damage), nameof(UT_Damage.Damage), new[] { typeof(Damageable.DamageInfo) })]
        [HarmonyPostfix]
        private static void DamagePostfix(UT_Damage __instance, Damageable.DamageInfo info, float __state)
        {
            // maxHealth 0 means it can't be destroyed (reactive surfaces): nothing lasting to share.
            if (!LevelDamage.Live || __instance == null || float.IsNaN(__state) || __instance.maxHealth <= 0f)
            {
                return;
            }
            // Your hits always go out, even when health doesn't move: the Nest elevator's cable takes no damage
            // (damage multiplier 0) but any hit on it drops the elevator.
            bool dropped = __instance.health < __state - 0.0001f;
            if (!dropped && !LevelDamage.FromLocalPlayer(info))
            {
                return;
            }
            try
            {
                LevelDamage.Instance.OnLocalDrop(__instance, LevelDamage.DamageKind, __state, __instance.health, info);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Level damage not shared ({__instance.name}): {e.Message}");
            }
        }

        [HarmonyPatch(typeof(ENT_Hitbox), nameof(ENT_Hitbox.Damage), new[] { typeof(Damageable.DamageInfo) })]
        [HarmonyPrefix]
        private static void HitboxPrefix(ENT_Hitbox __instance, out float __state)
        {
            __state = __instance != null ? __instance.health : 0f;
        }

        [HarmonyPatch(typeof(ENT_Hitbox), nameof(ENT_Hitbox.Damage), new[] { typeof(Damageable.DamageInfo) })]
        [HarmonyPostfix]
        private static void HitboxPostfix(ENT_Hitbox __instance, Damageable.DamageInfo info, float __state)
        {
            if (!LevelDamage.Live || __instance == null || !__instance.independentHealth || __instance.health >= __state - 0.0001f)
            {
                return;
            }
            try
            {
                LevelDamage.Instance.OnLocalDrop(__instance, LevelDamage.HitboxKind, __state, __instance.health, info);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Hit on {__instance.name} not shared: {e.Message}");
            }
        }

        [HarmonyPatch(typeof(M_Level), nameof(M_Level.OnSpawn))]
        [HarmonyPostfix]
        private static void LevelSpawned()
        {
            LevelDamage.Instance?.OnLevelSpawned();
        }
    }
}
