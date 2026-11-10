using Belay.Net;
using Belay.Players;
using HarmonyLib;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// One death goo for the whole lobby. The host's goo is the real one: it rubberbands to the lowest
    /// living player instead of the host, and its height is streamed to clients, who pin their goo to it.
    /// The goo still kills each player locally when it reaches them.
    /// </summary>
    public class GooSync : MonoBehaviour
    {
        private const float SendInterval = 0.25f;

        public static GooSync Instance { get; private set; }

        private float sendTimer;
        private float lastHeight = float.NaN;
        private float lastHeightTime;

        // Client: latest authoritative state from the host, in shared space.
        private bool hasHostState;
        private float hostHeight;
        private float hostVelocity;
        private float hostStateTime;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.GooState, HandleGooState);
            NetSession.Instance.OnLobbyChanged += () => hasHostState = false;
        }

        private static bool Active => NetSession.Instance.InLobby && WorldSpace.Ready && DEN_DeathFloor.instance != null;

        private void Update()
        {
            if (!Active || !NetSession.Instance.IsHost)
            {
                return;
            }
            float height = WorldSpace.ToShared(((Component)DEN_DeathFloor.instance).transform.position).y;
            sendTimer -= Time.unscaledDeltaTime;
            if (sendTimer > 0f)
            {
                return;
            }
            sendTimer = SendInterval;
            float velocity = 0f;
            if (!float.IsNaN(lastHeight) && Time.time > lastHeightTime)
            {
                velocity = (height - lastHeight) / (Time.time - lastHeightTime);
            }
            lastHeight = height;
            lastHeightTime = Time.time;
            NetSession.Instance.Send(PacketType.GooState, w =>
            {
                w.Write(height);
                w.Write(velocity);
            }, reliable: false);
        }

        private void HandleGooState(Steamworks.SteamId origin, System.IO.BinaryReader reader)
        {
            if (origin != NetSession.Instance.HostId)
            {
                return;
            }
            hostHeight = reader.ReadSingle();
            hostVelocity = reader.ReadSingle();
            hostStateTime = Time.time;
            hasHostState = true;
        }

        /// <summary>Client: where the host's goo is now, in local space, or null if unknown.</summary>
        public float? ClientTargetHeight()
        {
            if (!hasHostState || Time.time - hostStateTime > 3f)
            {
                return null;
            }
            float shared = hostHeight + hostVelocity * Mathf.Min(Time.time - hostStateTime, 1f);
            return WorldSpace.ToLocal(new Vector3(0f, shared, 0f)).y;
        }

        /// <summary>Host: lowest living player's height, local space.</summary>
        public static float LowestPlayerHeight()
        {
            float lowest = ENT_Player.playerObject.dead ? float.MaxValue : ENT_Player.playerObject.transform.position.y;
            foreach (RemotePlayer remote in PlayerSync.Instance.Remotes.Values)
            {
                if (remote != null && !remote.Latest.Dead && Time.unscaledTime - remote.LastReceived < 2f)
                {
                    lowest = Mathf.Min(lowest, remote.LocalPosition.y);
                }
            }
            return lowest == float.MaxValue ? ENT_Player.playerObject.transform.position.y : lowest;
        }
    }

    [HarmonyPatch(typeof(DEN_DeathFloor), nameof(DEN_DeathFloor.Update))]
    internal static class DeathFloorPatches
    {
        /// <summary>How far below the lowest living climber a scripted goo jump is allowed to land.</summary>
        private const float ScriptedJumpMargin = 10f;

        // Host, in shared space (immune to world-offset shifts): goo height after our last Update, and a
        // scripted jump that's waiting for everyone to get above it.
        private static DEN_DeathFloor trackedGoo;
        private static float lastPostY = float.NaN;
        private static float pendingJumpY = float.NaN;

        private static bool Active => NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready;

        private static void PinToHost(DEN_DeathFloor goo)
        {
            float? target = GooSync.Instance.ClientTargetHeight();
            if (target.HasValue)
            {
                Transform t = ((Component)goo).transform;
                Vector3 pos = t.position;
                pos.y = target.Value;
                t.position = pos;
            }
        }

        /// <summary>
        /// Interludes and breakrooms move the goo with scene scripts (UT_GooController, UT_GooTakeover and
        /// friends) when the player who triggers them arrives. Single-player that's fine; in a lobby it
        /// dropped the goo on everyone still climbing below. On the host, any rise that happens outside
        /// the goo's own Update is held just under the lowest living player and applied once everyone is
        /// above it. The goo still climbs and kills normally.
        /// </summary>
        private static void HoldScriptedJumps(DEN_DeathFloor goo)
        {
            if (goo != trackedGoo)
            {
                trackedGoo = goo;
                lastPostY = float.NaN;
                pendingJumpY = float.NaN;
            }
            if (float.IsNaN(lastPostY))
            {
                return;
            }
            Transform t = ((Component)goo).transform;
            Vector3 pos = t.position;
            float y = WorldSpace.ToShared(pos).y;
            float limit = WorldSpace.ToShared(new Vector3(0f, GooSync.LowestPlayerHeight(), 0f)).y - ScriptedJumpMargin;
            float target = y;
            if (y > lastPostY + 0.05f && y > limit)
            {
                if (float.IsNaN(pendingJumpY))
                {
                    Plugin.Log.LogInfo($"Holding a scripted goo jump to {y:F1} until everyone is above it");
                }
                pendingJumpY = float.IsNaN(pendingJumpY) ? y : Mathf.Max(pendingJumpY, y);
                target = Mathf.Max(lastPostY, limit);
            }
            else if (!float.IsNaN(pendingJumpY) && limit >= pendingJumpY)
            {
                target = Mathf.Max(y, pendingJumpY);
                pendingJumpY = float.NaN;
            }
            if (target != y)
            {
                pos.y = WorldSpace.ToLocal(new Vector3(0f, target, 0f)).y;
                t.position = pos;
            }
        }

        [HarmonyPrefix]
        private static void Prefix(DEN_DeathFloor __instance, ref float ___rubberbandMult, out float __state)
        {
            __state = ___rubberbandMult;
            if (!Active)
            {
                return;
            }
            if (NetSession.Instance.IsHost)
            {
                HoldScriptedJumps(__instance);
                // The game rubberbands by distance to the host; rescale it so it's distance to the lowest climber.
                float gooY = ((Component)__instance).transform.position.y;
                float hostDistance = Mathf.Abs(gooY - ENT_Player.playerObject.transform.position.y);
                float lowestDistance = Mathf.Abs(gooY - GooSync.LowestPlayerHeight());
                if (hostDistance > 0.5f)
                {
                    ___rubberbandMult = __state * lowestDistance / hostDistance;
                }
            }
            else
            {
                PinToHost(__instance);
            }
        }

        [HarmonyPostfix]
        private static void Postfix(DEN_DeathFloor __instance, ref float ___rubberbandMult, float __state)
        {
            ___rubberbandMult = __state;
            if (!Active)
            {
                lastPostY = float.NaN;
                return;
            }
            if (NetSession.Instance.IsHost)
            {
                lastPostY = WorldSpace.ToShared(((Component)__instance).transform.position).y;
            }
            else
            {
                PinToHost(__instance);
            }
        }
    }
}
