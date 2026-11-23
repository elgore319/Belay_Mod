using System.IO;
using Belay.Net;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Level scripts set run flags (UT_CheckFlag.SetFlag) when something happens, like walking into one of the
    /// Nest's area generators ("session-nest-locked-infested"). Only the PC where it happened set the flag, and
    /// later level picks and scripted checks read it, so teammates' maps and sequences drifted. These run flags
    /// are now set on every PC.
    /// </summary>
    public class FlagSync : MonoBehaviour
    {
        public static FlagSync Instance { get; private set; }

        internal static bool Applying;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.RunFlagSet, HandleFlag);
        }

        internal static bool Live => Instance != null && !Applying && NetSession.InLobbyRun;

        internal void OnLocalFlag(string name, bool state)
        {
            Plugin.Log.LogInfo($"Run flag set here, sharing: {name}={state}");
            NetSession.Instance.Send(PacketType.RunFlagSet, w =>
            {
                w.Write(name);
                w.Write(state);
            });
        }

        private void HandleFlag(SteamId origin, BinaryReader r)
        {
            string name = r.ReadString();
            bool state = r.ReadBoolean();
            if (origin == NetSession.LocalId || CL_GameManager.gMan == null || string.IsNullOrEmpty(name))
            {
                return;
            }
            CL_GameManager.SessionFlag current = CL_GameManager.GetGameFlag(name);
            if (current != null && current.state == state)
            {
                return;
            }
            Applying = true;
            try
            {
                CL_GameManager.SetGameFlag(name, state);
                Plugin.Log.LogInfo($"Teammate set run flag {name}={state}");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't set run flag {name}: {e.Message}");
            }
            finally
            {
                Applying = false;
            }
        }
    }

    [HarmonyPatch(typeof(UT_CheckFlag), nameof(UT_CheckFlag.SetFlag))]
    internal static class FlagSyncPatch
    {
        /// <summary>
        /// A generator's own "this way was taken" flag isn't set when the team took the other way instead
        /// (both set at once would build both ways' levels later).
        /// </summary>
        [HarmonyPrefix]
        private static bool Prefix(UT_CheckFlag __instance)
        {
            if (!NetSession.InLobbyRun || ShortcutSync.Refused.Count == 0 || __instance == null)
            {
                return true;
            }
            UT_BranchGenerator g = __instance.GetComponent<UT_BranchGenerator>();
            return g == null || !ShortcutSync.Refused.Contains(ButtonSync.ObjectId(g));
        }

        [HarmonyPostfix]
        private static void Postfix(string dat, bool __runOriginal)
        {
            if (!__runOriginal || !FlagSync.Live || string.IsNullOrEmpty(dat))
            {
                return;
            }
            string[] parts = dat.Split(':');
            if (parts.Length < 2 || !bool.TryParse(parts[1], out bool state))
            {
                return;
            }
            FlagSync.Instance.OnLocalFlag(parts[0], state);
        }
    }
}
