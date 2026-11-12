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

        // Host diagnostics: how fast the mass actually climbs vs how far it is from the lowest climber.
        private float diagTimer = 15f;
        private float diagStartY = float.NaN;
        private float diagDistanceSum;
        private int diagFrames;

        public void NoteHostFrame(float gooY, float lowestDistance)
        {
            float shared = WorldSpace.ToShared(new Vector3(0f, gooY, 0f)).y;
            if (float.IsNaN(diagStartY))
            {
                diagStartY = shared;
            }
            diagDistanceSum += lowestDistance;
            diagFrames++;
            diagTimer -= Time.deltaTime;
            if (diagTimer > 0f)
            {
                return;
            }
            float span = 15f - diagTimer;
            DEN_DeathFloor goo = DEN_DeathFloor.instance;
            Plugin.Log.LogInfo($"[Mass] climbed {(shared - diagStartY) / span:F2} m/s over {span:F0}s, "
                + $"lowest climber {diagDistanceSum / Mathf.Max(diagFrames, 1):F1} m above it on average, "
                + $"base speed {(goo != null ? goo.GetCurrentSpeed() : 0f):F2}, active {(goo != null && goo.IsActive())}");
            diagTimer = 15f;
            diagStartY = shared;
            diagDistanceSum = 0f;
            diagFrames = 0;
        }

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

        /// <summary>
        /// Host: lowest living player's height, local space. A player already under the mass is about to
        /// die (their own PC kills them a moment later), so they don't hold the mass back.
        /// </summary>
        public static float LowestPlayerHeight()
        {
            float gooY = DEN_DeathFloor.instance != null ? ((Component)DEN_DeathFloor.instance).transform.position.y : float.MinValue;
            float own = ENT_Player.playerObject.transform.position.y;
            float lowest = ENT_Player.playerObject.dead || own < gooY - 1f ? float.MaxValue : own;
            foreach (RemotePlayer remote in PlayerSync.Instance.Remotes.Values)
            {
                if (remote != null && !remote.Latest.Dead && Time.unscaledTime - remote.LastReceived < 2f
                    && remote.LocalPosition.y >= gooY - 1f)
                {
                    lowest = Mathf.Min(lowest, remote.LocalPosition.y);
                }
            }
            return lowest == float.MaxValue ? own : lowest;
        }
    }

    [HarmonyPatch(typeof(DEN_DeathFloor), nameof(DEN_DeathFloor.Update))]
    internal static class DeathFloorPatches
    {
        /// <summary>How far below the lowest living climber a scripted goo jump is allowed to land.</summary>
        private const float ScriptedJumpMargin = 10f;

        /// <summary>
        /// Rises slower than this (m/s) outside the goo's own Update are the level speeding the mass up
        /// (UT_GooController.RaiseOverTime and friends) and go through. Only real jumps get held.
        /// </summary>
        private const float ScriptedJumpSpeed = 8f;

        // Host, in shared space (immune to world-offset shifts): goo height after our last Update, and a
        // scripted jump that's waiting for everyone to get above it.
        private static DEN_DeathFloor trackedGoo;
        private static float lastPostY = float.NaN;
        private static float pendingJumpY = float.NaN;
        private static float savedFrameMult = float.NaN;

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
            float rise = y - lastPostY;
            bool jump = rise > 0.05f && rise > ScriptedJumpSpeed * Mathf.Max(Time.deltaTime, 0.001f);
            if (jump && y > limit)
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
        private static void Prefix(DEN_DeathFloor __instance, ref float ___rubberbandMult, ref float ___speedMultFrameStorage, out float __state)
        {
            __state = ___rubberbandMult;
            savedFrameMult = float.NaN;
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
                    float mult = lowestDistance / hostDistance;
                    // The game also goes 4x when the mass is over 300 m behind the host; make that the lowest climber too.
                    if (lowestDistance > 300f && hostDistance <= 300f)
                    {
                        mult *= 4f;
                    }
                    else if (hostDistance > 300f && lowestDistance <= 300f)
                    {
                        savedFrameMult = ___speedMultFrameStorage;
                        ___speedMultFrameStorage *= 0.25f;
                    }
                    ___rubberbandMult = __state * mult;
                }
                GooSync.Instance?.NoteHostFrame(gooY, lowestDistance);
            }
            else
            {
                PinToHost(__instance);
            }
        }

        [HarmonyPostfix]
        private static void Postfix(DEN_DeathFloor __instance, ref float ___rubberbandMult, ref float ___speedMultFrameStorage, float __state)
        {
            ___rubberbandMult = __state;
            if (!float.IsNaN(savedFrameMult))
            {
                ___speedMultFrameStorage = savedFrameMult;
                savedFrameMult = float.NaN;
            }
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
