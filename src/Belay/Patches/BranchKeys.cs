using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace Belay.Patches
{
    /// <summary>
    /// The game numbers levels with one counter for the whole map (WorldLoader.idCounter). Each PC builds the
    /// main way on ahead of its own player, so when a shortcut or area branch (like the Nest's Infested Labs or
    /// Hot Zone) gets built, PCs are at different counts, and every seed derived from the count split after that.
    /// This gives each map branch its own salt (the shortcut's seed, 0 for the main way) and counts levels within
    /// the branch, which is the same on every PC.
    /// </summary>
    internal static class BranchKeys
    {
        private class Salt
        {
            public int Value;
        }

        private static readonly ConditionalWeakTable<WorldLoader.BranchInfo, Salt> Salts = new ConditionalWeakTable<WorldLoader.BranchInfo, Salt>();
        private static readonly FieldInfo LevelIdsField = AccessTools.Field(typeof(WorldLoader.BranchInfo), "levelIDs");
        private static readonly FieldInfo BranchesField = AccessTools.Field(typeof(WorldLoader), "branches");
        private static readonly FieldInfo IdCounterField = AccessTools.Field(typeof(WorldLoader), "idCounter");
        private static readonly FieldInfo IdField = AccessTools.Field(typeof(WorldLoader.BranchInfo.LevelId), "id");

        /// <summary>A branch built from a shortcut gets that shortcut's seed as its salt.</summary>
        internal static void Register(WorldLoader.BranchInfo branch, int salt)
        {
            if (branch == null) return;
            Salts.Remove(branch);
            Salts.Add(branch, new Salt { Value = salt });
        }

        internal static int SaltOf(WorldLoader.BranchInfo branch)
        {
            return branch != null && Salts.TryGetValue(branch, out Salt s) ? s.Value : 0;
        }

        internal static int CountOf(WorldLoader.BranchInfo branch)
        {
            return branch != null && LevelIdsField?.GetValue(branch) is ICollection ids ? ids.Count : 0;
        }

        /// <summary>A key for the next build of this branch, the same on every PC.</summary>
        internal static string BuildKey(WorldLoader.BranchInfo branch) => SaltOf(branch) + ":" + CountOf(branch);

        /// <summary>
        /// The level being spawned right now (its id is the counter minus one): its branch's salt mixed with its
        /// place in that branch. Null if it can't be found.
        /// </summary>
        internal static int? SpawningLevelKey()
        {
            WorldLoader loader = WorldLoader.instance;
            if (loader == null || IdCounterField == null || BranchesField == null || IdField == null) return null;
            int id = (int)IdCounterField.GetValue(loader) - 1;
            if (!(BranchesField.GetValue(loader) is IEnumerable branches)) return null;
            foreach (object o in branches)
            {
                var branch = o as WorldLoader.BranchInfo;
                if (!(LevelIdsField.GetValue(branch) is IList ids) || ids.Count == 0) continue;
                object last = ids[ids.Count - 1];
                if (last != null && (int)IdField.GetValue(last) == id)
                {
                    unchecked
                    {
                        return SaltOf(branch) * 7919 + ids.Count - 1;
                    }
                }
            }
            return null;
        }
    }
}
