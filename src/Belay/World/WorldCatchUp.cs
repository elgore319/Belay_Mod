using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Each PC builds the map ahead on its own, as its player climbs. Some levels pause the building until
    /// you walk through a trigger (the end of a lab, an interlude), and only the player who walks through
    /// it builds on. So a dead player spectating a teammate, or someone using the dev teleport to a
    /// teammate far ahead, ended up where nothing had been built: no terrain, and "No level found" on revive.
    ///
    /// This builds on past those pauses until the spot is covered. The body has to be at the top of the
    /// map for the game to build on (it only builds ahead of the level you're in).
    /// </summary>
    internal static class WorldCatchUp
    {
        /// <summary>Above the top of the map by more than this counts as "not built yet" (meters).</summary>
        private const float AboveSlack = 2f;

        private static M_Level lastNudged;

        /// <summary>The level whose bounds hold this spot, or null.</summary>
        internal static M_Level LevelAt(Vector3 worldPos)
        {
            M_Level best = null;
            float bestDist = float.MaxValue;
            foreach (M_Level level in Object.FindObjectsByType<M_Level>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (level == null) continue;
                Vector3 local = level.transform.worldToLocalMatrix.MultiplyPoint(worldPos);
                Bounds b = level.GetLocalBounds();
                b.Expand(4f);
                if (!b.Contains(local)) continue;
                float d = (b.center - local).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = level;
                }
            }
            return best;
        }

        /// <summary>The highest level built on the current path, or null.</summary>
        internal static M_Level TopLevel()
        {
            WorldLoader loader = WorldLoader.instance;
            WorldLoader.BranchInfo branch = loader != null ? loader.GetCurrentBranch() : null;
            if (branch == null || branch.levelTracker == null || branch.levelTracker.Count == 0)
            {
                return null;
            }
            for (int i = branch.levelTracker.Count - 1; i >= 0; i--)
            {
                M_Level level = branch.levelTracker[i]?.GetLevel();
                if (level != null)
                {
                    return level;
                }
            }
            return null;
        }

        internal static float TopOf(M_Level level)
        {
            Bounds b = level.GetLocalBounds();
            Matrix4x4 m = level.transform.localToWorldMatrix;
            float top = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3(
                    (i & 1) == 0 ? b.min.x : b.max.x,
                    (i & 2) == 0 ? b.min.y : b.max.y,
                    (i & 4) == 0 ? b.min.z : b.max.z);
                top = Mathf.Max(top, m.MultiplyPoint(corner).y);
            }
            return top;
        }

        /// <summary>
        /// When this spot is above everything built so far and the top level is holding the building back,
        /// let it carry on. Returns true when the spot is already covered.
        /// </summary>
        internal static bool Nudge(Vector3 worldPos)
        {
            if (LevelAt(worldPos) != null)
            {
                return true;
            }
            if (WorldLoader.instance == null || CL_GameManager.IsLoading())
            {
                return false;
            }
            M_Level top = TopLevel();
            if (top == null || worldPos.y < TopOf(top) + AboveSlack)
            {
                return false;
            }
            if (top.IsPausingGeneration())
            {
                if (lastNudged != top)
                {
                    lastNudged = top;
                    Plugin.Log.LogInfo("Building the map on past " + top.name + " to catch up with the team");
                }
                try
                {
                    top.ContinueGeneration();
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning("Couldn't build the map on: " + e.Message);
                }
            }
            return false;
        }

        /// <summary>A safe spot at the top of the map: the top level's exit, or else its spawn point.</summary>
        internal static Vector3? TopSpot()
        {
            M_Level top = TopLevel();
            if (top == null)
            {
                return null;
            }
            Transform exit = top.GetLevelExit();
            if (exit != null)
            {
                return exit.position + Vector3.up * 1f;
            }
            return top.GetSpawnPosition();
        }
    }
}
