using Belay.Net;
using HarmonyLib;

namespace Belay.Patches
{
    /// <summary>
    /// Everyone in a lobby generates the same tower: WorldLoader seeds level generation from
    /// presetSeed, so we feed it the lobby's seed right before each run is generated.
    /// </summary>
    [HarmonyPatch(typeof(WorldLoader))]
    internal static class WorldLoaderPatches
    {
        [HarmonyPatch(nameof(WorldLoader.Initialize))]
        [HarmonyPrefix]
        private static void UseLobbySeed()
        {
            NetSession net = NetSession.Instance;
            if (net == null || !net.InLobby || net.Seed == 0)
            {
                return;
            }
            WorldLoader.SetPresetSeed(net.Seed.ToString());
            Plugin.Log.LogInfo("Using lobby seed " + net.Seed);
        }

        /// <summary>The save system bumps the seed on some restarts; that would split the lobby.</summary>
        [HarmonyPatch("IncrementSeed")]
        [HarmonyPrefix]
        private static bool BlockSeedIncrement()
        {
            NetSession net = NetSession.Instance;
            return net == null || !net.InLobby;
        }
    }
}
