using System;
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
    /// Control seats (crane cab etc.) take one driver; while a teammate sits in one, it won't take you.
    /// </summary>
    public class SeatLock : MonoBehaviour
    {
        private static readonly Dictionary<string, SteamId> taken = new Dictionary<string, SteamId>();

        private void Start()
        {
            NetSession.Instance.On(PacketType.SeatTaken, Handle);
            NetSession.Instance.OnLobbyChanged += () => taken.Clear();
        }

        internal static bool Live => NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready;

        internal static void Share(VEH_ControlSeat seat, bool sitting)
        {
            string id = ButtonSync.ObjectId(seat);
            NetSession.Instance.Send(PacketType.SeatTaken, w =>
            {
                w.Write(id);
                w.Write(sitting);
            });
        }

        private static void Handle(SteamId origin, BinaryReader r)
        {
            string id = r.ReadString();
            bool sitting = r.ReadBoolean();
            if (origin == NetSession.LocalId)
            {
                return;
            }
            if (sitting)
            {
                taken[id] = origin;
            }
            else if (taken.TryGetValue(id, out SteamId who) && who == origin)
            {
                taken.Remove(id);
            }
        }

        /// <summary>A teammate who is still here and alive sits in this seat.</summary>
        internal static bool TakenByTeammate(VEH_ControlSeat seat)
        {
            if (taken.Count == 0)
            {
                return false;
            }
            string id = ButtonSync.ObjectId(seat);
            if (!taken.TryGetValue(id, out SteamId who))
            {
                return false;
            }
            if (PlayerSync.Instance != null && PlayerSync.Instance.Remotes.TryGetValue(who, out RemotePlayer remote)
                && remote != null && remote.Present && !remote.Latest.Dead)
            {
                return true;
            }
            taken.Remove(id);
            return false;
        }
    }

    [HarmonyPatch]
    internal static class SeatLockPatches
    {
        private static readonly AccessTools.FieldRef<VEH_ControlSeat, bool> IsControlled = AccessTools.FieldRefAccess<VEH_ControlSeat, bool>("isControlled");

        [HarmonyPatch(typeof(VEH_ControlSeat), nameof(VEH_ControlSeat.StartControl))]
        [HarmonyPrefix]
        private static bool Start(VEH_ControlSeat __instance, out bool __state)
        {
            __state = false;
            try
            {
                if (!SeatLock.Live)
                {
                    return true;
                }
                if (SeatLock.TakenByTeammate(__instance))
                {
                    return false;
                }
                __state = !IsControlled(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Seat check failed: " + e.Message);
            }
            return true;
        }

        [HarmonyPatch(typeof(VEH_ControlSeat), nameof(VEH_ControlSeat.StartControl))]
        [HarmonyPostfix]
        private static void Started(VEH_ControlSeat __instance, bool __state)
        {
            if (__state && IsControlled(__instance))
            {
                Safe(() => SeatLock.Share(__instance, true));
            }
        }

        [HarmonyPatch(typeof(VEH_ControlSeat), nameof(VEH_ControlSeat.EndControl))]
        [HarmonyPrefix]
        private static void End(VEH_ControlSeat __instance, out bool __state)
        {
            __state = SeatLock.Live && IsControlled(__instance);
        }

        [HarmonyPatch(typeof(VEH_ControlSeat), nameof(VEH_ControlSeat.EndControl))]
        [HarmonyPostfix]
        private static void Ended(VEH_ControlSeat __instance, bool __state)
        {
            if (__state)
            {
                Safe(() => SeatLock.Share(__instance, false));
            }
        }

        private static void Safe(Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Seat not shared: " + e.Message);
            }
        }
    }
}
