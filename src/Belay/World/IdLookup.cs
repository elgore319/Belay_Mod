using System;
using System.Collections.Generic;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Finds scene objects by their shared id (<see cref="ButtonSync.ObjectId"/>) without walking every object of
    /// the type each time. A remembered hit is used only if it still has that id now, so an object that moved or
    /// was renumbered is looked up again instead of being trusted.
    /// </summary>
    internal static class IdLookup
    {
        private static readonly Dictionary<(Type, string), Component> hits = new Dictionary<(Type, string), Component>();

        internal static T Find<T>(string id, FindObjectsInactive inactive = FindObjectsInactive.Exclude) where T : Component
        {
            if (id == null)
            {
                return null;
            }
            var key = (typeof(T), id);
            if (hits.TryGetValue(key, out Component known))
            {
                if (known is T t && t != null && (inactive == FindObjectsInactive.Include || t.gameObject.activeInHierarchy)
                    && ButtonSync.ObjectId(t) == id)
                {
                    return t;
                }
                hits.Remove(key);
            }
            foreach (T c in UnityEngine.Object.FindObjectsByType<T>(inactive, FindObjectsSortMode.None))
            {
                if (c != null && ButtonSync.ObjectId(c) == id)
                {
                    if (hits.Count > 4096)
                    {
                        hits.Clear();
                    }
                    hits[key] = c;
                    return c;
                }
            }
            return null;
        }

        /// <summary>Every object of a type by id, in one pass (the first one wins on a repeated id).</summary>
        internal static Dictionary<string, T> All<T>(FindObjectsInactive inactive = FindObjectsInactive.Exclude) where T : Component
        {
            var map = new Dictionary<string, T>();
            foreach (T c in UnityEngine.Object.FindObjectsByType<T>(inactive, FindObjectsSortMode.None))
            {
                if (c != null)
                {
                    string id = ButtonSync.ObjectId(c);
                    if (!map.ContainsKey(id))
                    {
                        map[id] = c;
                    }
                }
            }
            return map;
        }

        internal static void Clear() => hits.Clear();
    }

    /// <summary>
    /// Lets "retry what we missed" loops skip their full scans while nothing new has loaded: the set of loaded
    /// levels is cheap to check, and a full retry still runs now and then for things that switch on later.
    /// </summary>
    internal static class LevelStamp
    {
        private static int frame = -1;
        private static int stamp;

        /// <summary>Changes whenever a level loads or unloads, or is switched on or off.</summary>
        internal static int Current
        {
            get
            {
                if (frame != Time.frameCount)
                {
                    frame = Time.frameCount;
                    int s = 17;
                    foreach (M_Level level in UnityEngine.Object.FindObjectsByType<M_Level>(FindObjectsSortMode.None))
                    {
                        if (level != null)
                        {
                            s += level.GetInstanceID() * 31 + (level.gameObject.activeInHierarchy ? 1 : 0);
                        }
                    }
                    stamp = s;
                }
                return stamp;
            }
        }

        /// <summary>
        /// True when a retry pass is worth doing: the loaded levels changed since <paramref name="last"/>, or
        /// <paramref name="fallbackSeconds"/> passed since the last pass.
        /// </summary>
        internal static bool Due(ref int last, ref float lastFull, float fallbackSeconds)
        {
            int now = Current;
            if (now != last || Time.unscaledTime - lastFull >= fallbackSeconds)
            {
                last = now;
                lastFull = Time.unscaledTime;
                return true;
            }
            return false;
        }
    }
}
