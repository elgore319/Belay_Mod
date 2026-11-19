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

        /// <summary>
        /// Faster than this (m/s) between two updates is a scripted jump or a teleport, not climbing.
        /// Clients don't extrapolate those: running a 50 m jump forward for another second put their mass
        /// far above the host's, on top of the lowest climber.
        /// </summary>
        private const float MaxStreamedSpeed = 40f;

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
        private float diagPace = 1f;

        public void NoteHostFrame(float gooY, float lowestDistance, float pace)
        {
            float shared = WorldSpace.ToShared(new Vector3(0f, gooY, 0f)).y;
            if (float.IsNaN(diagStartY))
            {
                diagStartY = shared;
            }
            diagDistanceSum += lowestDistance;
            diagPace = pace;
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
                + $"base speed {(goo != null ? goo.GetCurrentSpeed() : 0f):F2}, active {(goo != null && goo.IsActive())}, level pace x{diagPace:F2}");
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
                if (Mathf.Abs(velocity) > MaxStreamedSpeed)
                {
                    velocity = 0f;
                }
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
            float shared = hostHeight + hostVelocity * Mathf.Min(Time.time - hostStateTime, 0.5f);
            return WorldSpace.ToLocal(new Vector3(0f, shared, 0f)).y;
        }

        /// <summary>Host: the lowest living player's position, local space.</summary>
        public static Vector3 LowestPlayerPosition()
        {
            return LowestPlayerPosition(DEN_DeathFloor.instance != null ? ((Component)DEN_DeathFloor.instance).transform.position.y : float.MinValue);
        }

        /// <summary>
        /// Same, judging "already under the mass" against a given goo height (local space). A scripted jump
        /// is judged against where the goo was before it jumped: against the new height, everyone it jumped
        /// over looked already dead, didn't count, and the jump landed on them.
        /// </summary>
        public static Vector3 LowestPlayerPosition(float gooY)
        {
            Vector3 own = ENT_Player.playerObject.transform.position;
            bool found = !(ENT_Player.playerObject.dead || own.y < gooY - 1f);
            Vector3 lowest = own;
            foreach (RemotePlayer remote in PlayerSync.Instance.Remotes.Values)
            {
                if (remote != null && !remote.Latest.Dead && remote.Present
                    && remote.LocalPosition.y >= gooY - 1f && (!found || remote.LocalPosition.y < lowest.y))
                {
                    lowest = remote.LocalPosition;
                    found = true;
                }
            }
            return lowest;
        }

        private static float LevelMassMult(M_Level level)
        {
            if (level == null)
            {
                return 1f;
            }
            return level.massSpeedMult * (level.subRegion != null ? level.subRegion.massSpeedMult : 1f);
        }

        /// <summary>
        /// Host: the game paces the mass by the level the host is in. In a lobby it chases the lowest
        /// climber, so it should go at their level's pace: a host up in a faster region made the mass race
        /// through the slower levels the others were still climbing. Returns lowest's pace / host's pace.
        /// </summary>
        private static float paceCache = 1f;
        private static float paceCheckedAt = -10f;

        public static float LevelPaceRatio(Vector3 lowest)
        {
            // Walking the level graph every frame isn't worth it: the levels only change as people climb.
            if (Time.unscaledTime - paceCheckedAt < 0.5f)
            {
                return paceCache;
            }
            paceCheckedAt = Time.unscaledTime;
            paceCache = ComputePace(lowest);
            return paceCache;
        }

        private static float ComputePace(Vector3 lowest)
        {
            try
            {
                if (!WorldLoader.initialized || !WorldLoader.isLoaded || WorldLoader.instance == null)
                {
                    return 1f;
                }
                WorldLoader.LevelInfo hostLevel = WorldLoader.instance.GetCurrentLevel();
                WorldLoader.LevelInfo lowLevel = WorldLoader.GetClosestLevelToPosition(lowest);
                if (hostLevel == null || lowLevel == null || hostLevel == lowLevel)
                {
                    return 1f;
                }
                float hostMult = LevelMassMult(hostLevel.GetLevel());
                if (hostMult < 0.01f)
                {
                    return 1f;
                }
                return Mathf.Clamp(LevelMassMult(lowLevel.GetLevel()) / hostMult, 0.1f, 10f);
            }
            catch (System.Exception)
            {
                return 1f;
            }
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
            float before = WorldSpace.ToLocal(new Vector3(0f, lastPostY, 0f)).y;
            float limit = WorldSpace.ToShared(new Vector3(0f, GooSync.LowestPlayerPosition(before).y, 0f)).y - ScriptedJumpMargin;
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

        /// <summary>After the lowest climber drops out, how fast (m/s) the paced-on distance may grow back.</summary>
        private const float DistanceRegrowth = 0.15f;
        /// <summary>The lowest climber jumping up by more than this in one frame means they dropped out (died or left).</summary>
        private const float DropoutJump = 5f;
        private static float lastLowestY = float.NaN;
        private static float lastDistance = float.NaN;
        private static float cappedDistance = float.NaN;
        private static float lastFrameAt;

        /// <summary>
        /// The mass speeds up with its distance to the lowest living climber. When that climber died, the
        /// next lowest could be a hundred meters higher, the distance jumped, and the mass raced up at many
        /// times its speed. After a drop-out, the distance it paces on stays where it was and only grows
        /// back very slowly, until the mass has caught up to the normal gap again.
        /// </summary>
        private static float PacedDistance(float actual, float lowestY)
        {
            float now = Time.time;
            float dt = Mathf.Max(now - lastFrameAt, 0f);
            bool stale = float.IsNaN(lastLowestY) || dt > 2f;
            if (!stale && lowestY - lastLowestY > DropoutJump && actual > lastDistance)
            {
                if (float.IsNaN(cappedDistance))
                {
                    Plugin.Log.LogInfo($"Lowest climber dropped out; mass keeps pacing on {lastDistance:F0} m instead of {actual:F0} m");
                }
                cappedDistance = float.IsNaN(cappedDistance) ? lastDistance : Mathf.Min(cappedDistance, lastDistance);
            }
            float result = actual;
            if (!float.IsNaN(cappedDistance))
            {
                if (stale || actual <= cappedDistance)
                {
                    cappedDistance = float.NaN;
                }
                else
                {
                    cappedDistance += DistanceRegrowth * dt;
                    result = cappedDistance;
                }
            }
            lastLowestY = lowestY;
            lastDistance = result;
            lastFrameAt = now;
            return result;
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
                Vector3 lowestPos = GooSync.LowestPlayerPosition();
                float lowestDistance = PacedDistance(Mathf.Abs(gooY - lowestPos.y), WorldSpace.ToShared(lowestPos).y);
                float pace = GooSync.LevelPaceRatio(lowestPos);
                if (pace != 1f)
                {
                    savedFrameMult = ___speedMultFrameStorage;
                    ___speedMultFrameStorage *= pace;
                }
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
                        if (float.IsNaN(savedFrameMult))
                        {
                            savedFrameMult = ___speedMultFrameStorage;
                        }
                        ___speedMultFrameStorage *= 0.25f;
                    }
                    ___rubberbandMult = __state * mult;
                }
                GooSync.Instance?.NoteHostFrame(gooY, lowestDistance, pace);
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
