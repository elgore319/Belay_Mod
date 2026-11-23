using System.IO;
using Belay.Net;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// The Mother: whoever angers her (hits her, crushes the grub) is the only one she kills. Her anger is
    /// shared, so every PC sees her rage and the room's anger events run everywhere, but her crush only kills
    /// the player who angered her. A replayed teammate action (button, zone, shot, flag) that angers her here
    /// counts as the teammate's doing, not ours.
    /// </summary>
    public class MotherSync : MonoBehaviour
    {
        public static MotherSync Instance { get; private set; }

        internal static bool Applying;

        /// <summary>This run, the local player angered the Mother.</summary>
        internal static bool AggressorHere;

        private int runId;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.MotherEnraged, HandleEnraged);
        }

        private void Update()
        {
            NetSession net = NetSession.Instance;
            if (net != null && net.InLobby && runId != net.CurrentRun.RunId)
            {
                runId = net.CurrentRun.RunId;
                AggressorHere = false;
            }
        }

        internal static bool Live => Instance != null && !Applying && NetSession.InLobbyRun;

        /// <summary>Something here is replaying a teammate's action right now.</summary>
        internal static bool Replaying =>
            Applying || ButtonSync.Applying || ZoneSync.Applying || LevelDamage.Applying || EnemySync.Applying
            || FlagSync.Applying || WorldEvents.Applying || DoorSync.Applying || ItemSync.Applying
            || LevelBreaks.Applying || LevelPulses.Applying || HostSpawns.Applying || ShortcutSync.Applying;

        internal void OnLocalEnrage(bool crush)
        {
            AggressorHere = true;
            Plugin.Log.LogInfo("You angered the Mother; sharing her anger (only you get crushed)");
            NetSession.Instance.Send(PacketType.MotherEnraged, w => w.Write(crush));
        }

        private void HandleEnraged(SteamId origin, BinaryReader r)
        {
            bool crush = r.ReadBoolean();
            if (origin == NetSession.LocalId)
            {
                return;
            }
            DEN_Mother mother = DEN_Mother.instance;
            if (mother == null || mother.angry)
            {
                return;
            }
            Plugin.Log.LogInfo($"{NetSession.NameOf(origin)} angered the Mother; she only goes after them");
            Applying = true;
            try
            {
                // Her anger only switches on if she's out of focus and allowed to be angry; a teammate who got
                // her there already passed both on their PC.
                mother.focused = false;
                mother.canAnger = true;
                mother.Enrage(crush);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't share the Mother's anger here: " + e.Message);
            }
            finally
            {
                Applying = false;
            }
        }
    }

    [HarmonyPatch]
    internal static class MotherSyncPatches
    {
        [HarmonyPatch(typeof(DEN_Mother), nameof(DEN_Mother.Enrage))]
        [HarmonyPrefix]
        private static void Enrage(DEN_Mother __instance, bool crush)
        {
            if (!MotherSync.Live || __instance == null || __instance.angry || __instance.focused || !__instance.canAnger)
            {
                return;
            }
            if (MotherSync.Replaying)
            {
                return; // a teammate's action angered her here; their own PC shares it
            }
            MotherSync.Instance.OnLocalEnrage(crush);
        }

        /// <summary>Her crush lands (animation event): only the player who angered her dies.</summary>
        [HarmonyPatch(typeof(DEN_Mother), nameof(DEN_Mother.KillPlayer))]
        [HarmonyPrefix]
        private static bool KillPlayer()
        {
            if (MotherSync.Instance == null || !NetSession.InLobbyRun || MotherSync.AggressorHere)
            {
                return true;
            }
            Plugin.Log.LogInfo("The Mother's crush spared you: a teammate angered her");
            return false;
        }
    }
}
