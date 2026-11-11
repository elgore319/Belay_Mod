using Belay.Net;
using Belay.Players;
using HarmonyLib;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Shortcuts build a new branch of the map from the level they start in (UT_BranchGenerator) and,
    /// in solo, drop the normal path ahead. That used to happen only on the PC of whoever went in, with
    /// an unseeded pick of which branch to build, so the team ended up on different maps.
    /// Now the pick is seeded from the lobby, and when anyone opens a shortcut it opens for everyone,
    /// building the same branch on every PC. Teammates get a notice to follow.
    /// </summary>
    public class ShortcutSync : MonoBehaviour
    {
        public static ShortcutSync Instance { get; private set; }

        internal static bool Applying;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.ShortcutOpened, (origin, r) => Apply(origin, r.ReadString()));
        }

        internal static bool Live => NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready;

        internal void Broadcast(UT_BranchGenerator generator)
        {
            string id = ButtonSync.ObjectId(generator);
            Plugin.Log.LogInfo("Opened a shortcut: " + id);
            NetSession.Instance.Send(PacketType.ShortcutOpened, w => w.Write(id));
        }

        private void Apply(Steamworks.SteamId origin, string id)
        {
            foreach (UT_BranchGenerator g in FindObjectsByType<UT_BranchGenerator>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (g == null || ButtonSync.ObjectId(g) != id)
                {
                    continue;
                }
                string who = NetSession.NameOf(origin);
                Plugin.Log.LogInfo($"{who} opened a shortcut: {id}");
                Applying = true;
                try
                {
                    g.Generate();
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"Couldn't open {who}'s shortcut here ({id}): {e.Message}");
                }
                finally
                {
                    Applying = false;
                }
                Run.RunFlow.Instance?.ShowNotice($"{who} took a shortcut. Follow them: the normal path ahead is closed.", 8f);
                return;
            }
            Plugin.Log.LogWarning("Teammate opened a shortcut that isn't loaded here: " + id);
        }

        internal static int Seed(UT_BranchGenerator g)
        {
            unchecked
            {
                int h = NetSession.Instance.Seed * 31 + 0x5C;
                foreach (char c in ButtonSync.ObjectId(g))
                {
                    h = h * 31 + c;
                }
                return h;
            }
        }
    }

    [HarmonyPatch(typeof(UT_BranchGenerator), nameof(UT_BranchGenerator.Generate))]
    internal static class ShortcutSyncPatch
    {
        /// <summary>Generate only once per generator, whoever triggers it first.</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UT_BranchGenerator, object> Done =
            new System.Runtime.CompilerServices.ConditionalWeakTable<UT_BranchGenerator, object>();

        [HarmonyPrefix]
        private static bool Prefix(UT_BranchGenerator __instance)
        {
            if (!ShortcutSync.Live || WorldLoader.instance == null)
            {
                return true;
            }
            if (Done.TryGetValue(__instance, out _))
            {
                // Already opened by a teammate (or by us): don't build a second branch.
                return false;
            }
            Done.Add(__instance, null);
            Random.InitState(ShortcutSync.Seed(__instance));
            return true;
        }

        [HarmonyPostfix]
        private static void Postfix(UT_BranchGenerator __instance)
        {
            if (ShortcutSync.Live && !ShortcutSync.Applying && ShortcutSync.Instance != null)
            {
                ShortcutSync.Instance.Broadcast(__instance);
            }
        }
    }
}
