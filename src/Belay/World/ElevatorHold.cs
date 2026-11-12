using System.Collections.Generic;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// An elevator in an interlude (the ascent's, for one) doesn't move off while part of the team is off
    /// it. When something tells it to go (the button someone got off to press, say) and some riders are
    /// aboard but not every living player, the start is held until everyone is back on, then it goes. It waits quietly: nothing on screen says who it's waiting for.
    /// An empty elevator (nobody aboard, e.g. one being called) is never held. Every PC runs this check
    /// itself, since every PC moves its own copy of the elevator.
    /// Covers the game's path movers (UT_PathMover) and simple platforms (UT_Platform).
    /// </summary>
    public class ElevatorHold : MonoBehaviour
    {
        public static ElevatorHold Instance { get; private set; }

        /// <summary>Set while Belay releases a held start, so the patches let it through.</summary>
        internal static bool Releasing;

        private const float AboardSlack = 1f;

        private readonly Dictionary<Component, System.Action> held = new Dictionary<Component, System.Action>();

        private void Awake()
        {
            Instance = this;
        }

        internal static bool Live => Instance != null && !Releasing && NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready;

        /// <summary>Returns false (hold it) when this mover should wait for the team before starting.</summary>
        internal bool OnStart(Component mover, Transform car, System.Action start)
        {
            M_Level level = mover.GetComponentInParent<M_Level>();
            if (!InterludeGate.IsGatedRoom(level) || car == null)
            {
                return true;
            }
            if (!ShouldWait(car, out List<string> missing))
            {
                held.Remove(mover);
                return true;
            }
            if (!held.ContainsKey(mover))
            {
                Plugin.Log.LogInfo($"Elevator held for {string.Join(", ", missing)}: {ButtonSync.ObjectId(mover)}");
            }
            held[mover] = start;
            return false;
        }

        /// <summary>True when someone is aboard but not every living player is.</summary>
        private static bool ShouldWait(Transform car, out List<string> missing)
        {
            Bounds bounds;
            if (!CarBounds(car, out bounds))
            {
                missing = new List<string>();
                return false;
            }
            System.Func<Vector3, bool> aboard = p => bounds.Contains(p);
            ENT_Player me = ENT_Player.playerObject;
            bool meAlive = me != null && !me.dead;
            bool meAboard = meAlive && aboard(me.transform.position);
            bool anyoneAboard = meAboard;
            foreach (RemotePlayer remote in PlayerSync.Instance.Remotes.Values)
            {
                if (remote != null && !remote.Latest.Dead && Time.unscaledTime - remote.LastReceived <= 3f && aboard(remote.LocalPosition))
                {
                    anyoneAboard = true;
                }
            }
            missing = InterludeGate.Missing(aboard);
            if (meAlive && !meAboard)
            {
                missing.Insert(0, "you");
            }
            return anyoneAboard && missing.Count > 0;
        }

        /// <summary>The space the car takes up: its solid colliders, a little bigger.</summary>
        private static bool CarBounds(Transform car, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (Collider c in car.GetComponentsInChildren<Collider>())
            {
                if (c == null || c.isTrigger || !c.enabled)
                {
                    continue;
                }
                if (!any)
                {
                    bounds = c.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(c.bounds);
                }
            }
            if (any)
            {
                bounds.Expand(AboardSlack * 2f);
                // Room to stand on the floor.
                bounds.Encapsulate(bounds.max + Vector3.up * 2f);
            }
            return any;
        }

        private void Update()
        {
            if (held.Count == 0)
            {
                return;
            }
            if (Instance == null || NetSession.Instance == null || !NetSession.Instance.InLobby || !WorldSpace.Ready)
            {
                held.Clear();
                return;
            }
            foreach (var pair in new List<KeyValuePair<Component, System.Action>>(held))
            {
                if (pair.Key == null)
                {
                    held.Remove(pair.Key);
                    continue;
                }
                Transform car = CarOf(pair.Key);
                if (car != null && ShouldWait(car, out List<string> missing))
                {
                            continue;
                }
                held.Remove(pair.Key);
                Plugin.Log.LogInfo("Everyone's aboard, elevator going: " + ButtonSync.ObjectId(pair.Key));
                Releasing = true;
                try
                {
                    pair.Value();
                }
                finally
                {
                    Releasing = false;
                }
            }
        }

        internal static Transform CarOf(Component mover)
        {
            if (mover is UT_PathMover path) return path.moveObject;
            return mover.transform;
        }
    }

    [HarmonyPatch]
    internal static class ElevatorHoldPatches
    {
        [HarmonyPatch(typeof(UT_PathMover), nameof(UT_PathMover.StartMove))]
        [HarmonyPrefix]
        private static bool PathStart(UT_PathMover __instance)
        {
            if (!ElevatorHold.Live || __instance.active || __instance.loop)
            {
                return true;
            }
            UT_PathMover mover = __instance;
            return ElevatorHold.Instance.OnStart(mover, mover.moveObject, () => mover.StartMove());
        }

        [HarmonyPatch(typeof(UT_Platform), nameof(UT_Platform.SetMoveSpeed))]
        [HarmonyPrefix]
        private static bool PlatformSpeed(UT_Platform __instance, float m, float ___moveSpeed)
        {
            if (!ElevatorHold.Live || Mathf.Approximately(m, 0f) || !Mathf.Approximately(___moveSpeed, 0f))
            {
                return true;
            }
            UT_Platform platform = __instance;
            return ElevatorHold.Instance.OnStart(platform, platform.transform, () => platform.SetMoveSpeed(m));
        }
    }
}
