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

        /// <summary>
        /// WorldLoader reseeds Random from its seed field every time it builds more map, and adds 1 to that
        /// field each time it carries on building. Each PC carries on at its own moments (spectating,
        /// catching up, a branch firing late), so by the Nest the seeds had drifted apart and a branch
        /// built there picked different levels on each PC. Seed each build from the lobby seed and how
        /// many levels the map has had so far instead, which every PC agrees on.
        /// </summary>
        [HarmonyPatch("GenerateLevels")]
        [HarmonyPrefix]
        private static void SeedEachBuild(WorldLoader.GenerationParameters genParams, ref int ___seed, int ___idCounter)
        {
            NetSession net = NetSession.Instance;
            if (net == null || !net.InLobby || net.Seed == 0 || ___seed == -1)
            {
                return;
            }
            int offset = genParams != null ? genParams.seedOffset : 0;
            int seed;
            unchecked
            {
                seed = net.Seed * 486187739 + ___idCounter * 16777619 + 0x2F1;
            }
            if (seed + offset == -1)
            {
                seed += 2;
            }
            ___seed = seed - offset;
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
