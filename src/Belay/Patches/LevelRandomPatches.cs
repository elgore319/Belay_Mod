using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Belay.Net;
using HarmonyLib;
using UnityEngine;

namespace Belay.Patches
{
    /// <summary>
    /// The lobby seed alone only makes the level *list* match: WorldLoader seeds UnityEngine.Random once,
    /// then waits frames for level assets to load while everything else in the game keeps drawing from
    /// the same Random. So each player ended up with different mirroring, props, textures and spawns in
    /// the same levels.
    ///
    /// Fix: right before each piece of level setup that uses Random, reseed it from things every player
    /// agrees on: the lobby seed, which level this is in generation order, and the object's place in
    /// that level's hierarchy.
    ///  - M_Level.OnSpawn (runs right before WorldLoader decides whether to mirror the level), and
    ///  - Awake / Start / OnEnable of every level component whose setup code draws from Random
    ///    (found by scanning the game's IL at startup, so new randomizers are covered too).
    /// </summary>
    internal static class LevelRandomPatches
    {
        private static readonly ConditionalWeakTable<M_Level, LevelKey> Keys = new ConditionalWeakTable<M_Level, LevelKey>();
        private static readonly FieldInfo IdCounter = AccessTools.Field(typeof(WorldLoader), "idCounter");

        private class LevelKey
        {
            public int Id;
        }

        private static bool Active
        {
            get
            {
                NetSession net = NetSession.Instance;
                return net != null && net.InLobby && net.Seed != 0;
            }
        }

        public static void Apply(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(M_Level), nameof(M_Level.OnSpawn)),
                prefix: new HarmonyMethod(typeof(LevelRandomPatches), nameof(OnSpawnPrefix)));

            var componentPrefix = new HarmonyMethod(typeof(LevelRandomPatches), nameof(ComponentPrefix));
            int patched = 0;
            foreach (MethodInfo method in FindRandomSetupMethods())
            {
                try
                {
                    harmony.Patch(method, prefix: componentPrefix);
                    patched++;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Couldn't sync randomness in {method.DeclaringType?.Name}.{method.Name}: {e.Message}");
                }
            }
            Plugin.Log.LogInfo($"Level randomness synced for {patched} setup methods");
        }

        // ---------------------------------------------------------------- Seeding

        /// <summary>
        /// Which level this is, in generation order. WorldLoader bumps idCounter right before it spawns a
        /// level, and the level's own setup (component Awakes, then OnSpawn) runs in that same frame, so
        /// the first lookup for a level always sees its own id. Later lookups (Start, OnEnable) reuse it.
        /// </summary>
        private static int LevelId(M_Level level)
        {
            if (Keys.TryGetValue(level, out LevelKey key))
            {
                return key.Id;
            }
            int id = WorldLoader.instance != null && IdCounter != null ? (int)IdCounter.GetValue(WorldLoader.instance) - 1 : 0;
            Keys.Add(level, new LevelKey { Id = id });
            return id;
        }

        /// <summary>The level's generation-order id, the same on every PC.</summary>
        internal static int LevelIdOf(M_Level level) => LevelId(level);

        private static void OnSpawnPrefix(M_Level __instance)
        {
            if (!Active)
            {
                return;
            }
            int id = LevelId(__instance);
            UnityEngine.Random.InitState(Hash(NetSession.Instance.Seed, id, "level:" + LevelName(__instance)));
        }

        private static void ComponentPrefix(Component __instance)
        {
            if (!Active || __instance == null)
            {
                return;
            }
            M_Level level = __instance.GetComponentInParent<M_Level>(true);
            if (level == null)
            {
                return;
            }
            string path = PathInLevel(__instance, level);
            UnityEngine.Random.InitState(Hash(NetSession.Instance.Seed, LevelId(level), path));
        }

        private static string LevelName(M_Level level)
        {
            return level.name.Replace("(Clone)", "");
        }

        /// <summary>Sibling-index path from the level root, plus the component's type and index on its object.</summary>
        private static string PathInLevel(Component component, M_Level level)
        {
            var sb = new StringBuilder(LevelName(level));
            var stack = new List<int>();
            for (Transform t = component.transform; t != null && t != level.transform; t = t.parent)
            {
                stack.Add(t.GetSiblingIndex());
            }
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                sb.Append('/').Append(stack[i]);
            }
            Component[] all = component.GetComponents(component.GetType());
            sb.Append(':').Append(component.GetType().Name).Append('#').Append(Array.IndexOf(all, component));
            return sb.ToString();
        }

        private static int Hash(int seed, int levelId, string text)
        {
            unchecked
            {
                uint h = 2166136261;
                void Mix(uint v)
                {
                    h ^= v;
                    h *= 16777619;
                }
                Mix((uint)seed);
                Mix((uint)levelId);
                foreach (char c in text)
                {
                    Mix(c);
                }
                return (int)h;
            }
        }

        // ---------------------------------------------------------------- Finding setup code that uses Random

        private static readonly string[] SetupMethods = { "Awake", "Start", "OnEnable" };

        /// <summary>
        /// Awake/Start/OnEnable methods of game MonoBehaviours that reach UnityEngine.Random within a few
        /// calls. Players, the game manager, UI and enemies are skipped: only level props matter here,
        /// and the prefix does nothing for objects that aren't inside a level anyway.
        /// </summary>
        private static IEnumerable<MethodInfo> FindRandomSetupMethods()
        {
            Assembly game = typeof(M_Level).Assembly;
            Type[] types;
            try
            {
                types = game.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = Array.FindAll(e.Types, t => t != null);
            }

            var cache = new Dictionary<MethodBase, bool>();
            foreach (Type type in types)
            {
                if (type.ContainsGenericParameters || !typeof(MonoBehaviour).IsAssignableFrom(type))
                {
                    continue;
                }
                if (typeof(ENT_Player).IsAssignableFrom(type) || type.Name.StartsWith("UI_") || type.Name.StartsWith("CL_") || type.Name.StartsWith("DEN_"))
                {
                    continue;
                }
                foreach (string name in SetupMethods)
                {
                    MethodInfo method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                    if (method == null || method.ReturnType != typeof(void) || method.GetMethodBody() == null)
                    {
                        continue;
                    }
                    if (UsesRandom(method, game, cache, 3))
                    {
                        yield return method;
                    }
                }
            }
        }

        private static bool UsesRandom(MethodBase method, Assembly game, Dictionary<MethodBase, bool> cache, int depth)
        {
            if (cache.TryGetValue(method, out bool known))
            {
                return known;
            }
            cache[method] = false;
            bool uses = false;
            try
            {
                foreach (CodeInstruction ins in PatchProcessor.GetOriginalInstructions(method))
                {
                    if (!(ins.operand is MethodBase callee))
                    {
                        continue;
                    }
                    if (callee.DeclaringType == typeof(UnityEngine.Random))
                    {
                        uses = true;
                        break;
                    }
                    if (depth > 0 && callee.DeclaringType != null && callee.DeclaringType.Assembly == game
                        && !callee.IsAbstract && UsesRandom(callee, game, cache, depth - 1))
                    {
                        uses = true;
                        break;
                    }
                }
            }
            catch
            {
                uses = false;
            }
            cache[method] = uses;
            return uses;
        }
    }
}
