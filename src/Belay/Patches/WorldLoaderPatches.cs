using Belay.Net;
using HarmonyLib;

namespace Belay.Patches
{
    /// <summary>
    /// Same level layout for the whole lobby: feed WorldLoader the lobby seed before each run is generated.
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
        /// WorldLoader reseeds from a field it bumps each time it builds more map, and each PC does that at
        /// different moments, so seeds drifted. Seed from the lobby seed plus the level count instead.
        /// </summary>
        [HarmonyPatch("GenerateLevels")]
        [HarmonyPrefix]
        private static void SeedEachBuild(WorldLoader.BranchInfo branch, WorldLoader.GenerationParameters genParams, ref int ___seed)
        {
            NetSession net = NetSession.Instance;
            if (net == null || !net.InLobby || net.Seed == 0 || ___seed == -1)
            {
                return;
            }
            if (World.ShortcutSync.BuildSeed.HasValue)
            {
                BranchKeys.Register(branch, World.ShortcutSync.BuildSeed.Value);
            }
            int offset = genParams != null ? genParams.seedOffset : 0;
            int seed;
            unchecked
            {
                // A branch built later on one PC than another still gets the same levels, and so does every
                // later build in that branch: count levels within the branch, not across the whole map.
                seed = World.ShortcutSync.BuildSeed
                    ?? (net.Seed * 486187739 + BranchKeys.CountOf(branch) * 16777619 + BranchKeys.SaltOf(branch) * 31 + 0x2F1);
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
