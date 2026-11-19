using System.Collections.Generic;
using System.IO;
using Belay.Net;
using Belay.World;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.Players
{
    /// <summary>
    /// Things that catch you (barnacle tongue, vent hand, bear trap) happen on your PC only, so teammates now see
    /// a line from the catcher to you while it holds. Turret shots at you also show the flash and tracer on their
    /// screens.
    /// </summary>
    public class GrabSync : MonoBehaviour
    {
        private class Grab
        {
            public RemotePlayer Victim;
            public AIGameEntity Enemy;
            public Vector3 FixedShared;
            public LineRenderer Line;
        }

        private readonly Dictionary<SteamId, Grab> grabs = new Dictionary<SteamId, Grab>();
        private static Material lineMaterial;

        private void Start()
        {
            NetSession.Instance.On(PacketType.PlayerGrabbed, HandleGrabbed);
            NetSession.Instance.On(PacketType.EnemyShot, HandleShot);
        }

        internal static bool Live => NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready;

        // ---------------------------------------------------------------- Grabs

        internal static void OnLocalGrapple(bool on, object grappler)
        {
            Component c = grappler as Component;
            AIGameEntity enemy = c != null ? c.GetComponentInParent<AIGameEntity>() : null;
            string kind = enemy != null ? EnemySync.KindOf(enemy) : "";
            Vector3 spawn = enemy != null ? EnemySync.Instance.SpawnOf(enemy) : Vector3.zero;
            Vector3 at = WorldSpace.ToShared(c != null ? c.transform.position : Vector3.zero);
            NetSession.Instance.Send(PacketType.PlayerGrabbed, w =>
            {
                w.Write(on);
                w.Write(kind);
                w.WriteVector(spawn);
                w.WriteVector(at);
            });
        }

        private void HandleGrabbed(SteamId origin, BinaryReader r)
        {
            bool on = r.ReadBoolean();
            string kind = r.ReadString();
            Vector3 spawn = r.ReadVector();
            Vector3 at = r.ReadVector();
            if (origin == NetSession.LocalId || !Live)
            {
                return;
            }
            if (grabs.TryGetValue(origin, out Grab old) && old.Enemy is ENV_BearTrap oldTrap && !on)
            {
                Snap(oldTrap, false);
            }
            Clear(origin);
            if (!on || !PlayerSync.Instance.Remotes.TryGetValue(origin, out RemotePlayer victim) || victim == null)
            {
                return;
            }
            var g = new Grab { Victim = victim, FixedShared = at };
            if (kind.Length > 0)
            {
                g.Enemy = EnemySync.Instance.Find(kind, spawn, at);
            }
            if (g.Enemy is ENV_BearTrap trap)
            {
                Snap(trap, true);
            }
            var go = new GameObject("Belay_Grab");
            g.Line = go.AddComponent<LineRenderer>();
            g.Line.positionCount = 2;
            g.Line.startWidth = 0.09f;
            g.Line.endWidth = 0.05f;
            if (lineMaterial == null)
            {
                lineMaterial = new Material(Shader.Find("Sprites/Default")) { color = new Color(0.35f, 0.08f, 0.1f, 1f) };
            }
            g.Line.sharedMaterial = lineMaterial;
            g.Line.enabled = false;
            grabs[origin] = g;
        }

        private static readonly AccessTools.FieldRef<ENV_BearTrap, UT_AudioClipHandler> TrapSounds =
            AccessTools.FieldRefAccess<ENV_BearTrap, UT_AudioClipHandler>("clipHandler");

        /// <summary>A bear trap that caught (or let go of) a teammate on their PC shuts (or opens) here too.</summary>
        private static void Snap(ENV_BearTrap trap, bool shut)
        {
            try
            {
                if (trap == null || trap.dead || trap.animator == null || trap.animator.GetBool("Holding") == shut)
                {
                    return;
                }
                trap.animator.SetBool("Holding", shut);
                TrapSounds(trap)?.PlaySound(shut ? "trap:activate" : "trap:release");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Bear trap not shown: " + e.Message);
            }
        }

        private void Clear(SteamId id)
        {
            if (grabs.TryGetValue(id, out Grab g))
            {
                if (g.Line != null) Destroy(g.Line.gameObject);
                grabs.Remove(id);
            }
        }

        private void LateUpdate()
        {
            if (grabs.Count == 0)
            {
                return;
            }
            foreach (SteamId id in new List<SteamId>(grabs.Keys))
            {
                Grab g = grabs[id];
                if (!Live || g.Victim == null || g.Victim.Latest.Dead || g.Line == null || (g.Enemy != null && g.Enemy.dead))
                {
                    Clear(id);
                    continue;
                }
                Vector3 from = g.Enemy != null ? g.Enemy.transform.position : WorldSpace.ToLocal(g.FixedShared);
                Vector3 to = g.Victim.LocalPosition + Vector3.up * 1.1f;
                g.Line.enabled = (to - from).sqrMagnitude > 1.5f * 1.5f;
                g.Line.SetPosition(0, from);
                g.Line.SetPosition(1, to);
            }
        }

        // ---------------------------------------------------------------- Turret shots

        private static readonly System.Reflection.MethodInfo PlayTracer = AccessTools.Method(typeof(DEN_Turret), "PlayTracer");

        internal static void OnLocalTurretShot(DEN_Turret turret, Vector3 end)
        {
            string kind = EnemySync.KindOf(turret);
            Vector3 spawn = EnemySync.Instance.SpawnOf(turret);
            Vector3 now = WorldSpace.ToShared(turret.transform.position);
            Vector3 endShared = WorldSpace.ToShared(end);
            NetSession.Instance.Send(PacketType.EnemyShot, w =>
            {
                w.Write(kind);
                w.WriteVector(spawn);
                w.WriteVector(now);
                w.WriteVector(endShared);
            }, reliable: false);
        }

        private void HandleShot(SteamId origin, BinaryReader r)
        {
            string kind = r.ReadString();
            Vector3 spawn = r.ReadVector();
            Vector3 now = r.ReadVector();
            Vector3 end = WorldSpace.ToLocal(r.ReadVector());
            if (origin == NetSession.LocalId || !Live || PlayTracer == null)
            {
                return;
            }
            if (EnemySync.Instance.Find(kind, spawn, now) is DEN_Turret turret && !turret.dead && turret.turretBarrel != null)
            {
                try
                {
                    turret.GetComponent<UT_AudioClipHandler>()?.PlaySound("turret:shoot");
                    PlayTracer.Invoke(turret, new object[] { turret.turretBarrel.position, end, 100f, true });
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning("Turret shot not shown: " + e.Message);
                }
            }
        }
    }

    [HarmonyPatch]
    internal static class GrabSyncPatches
    {
        [HarmonyPatch(typeof(GameEntity), nameof(GameEntity.SetGrappled))]
        [HarmonyPostfix]
        private static void Grappled(GameEntity __instance, bool b, AIGameEntity.Grappler grappler)
        {
            if (!GrabSync.Live || ENT_Player.playerObject == null || __instance != ENT_Player.playerObject)
            {
                return;
            }
            try
            {
                GrabSync.OnLocalGrapple(b, grappler);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Grab not shared: " + e.Message);
            }
        }

        /// <summary>A turret firing at this PC's player: show it on the teammates' screens.</summary>
        [HarmonyPatch(typeof(DEN_Turret), nameof(DEN_Turret.Shoot))]
        [HarmonyPostfix]
        private static void TurretShot(DEN_Turret __instance)
        {
            ENT_Player me = ENT_Player.playerObject;
            if (!GrabSync.Live || me == null || __instance.target == null || (__instance.target != me.transform && !__instance.target.IsChildOf(me.transform)))
            {
                return;
            }
            try
            {
                GrabSync.OnLocalTurretShot(__instance, me.transform.position + Vector3.up * 1f);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Turret shot not shared: " + e.Message);
            }
        }
    }
}
