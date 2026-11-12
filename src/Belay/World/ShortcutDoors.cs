using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Blocks a shortcut or the main path with the level's own doors instead of a red barrier:
    ///  - Team picks the main path: the doors on the shortcut's side never open.
    ///  - Team picks the shortcut: the main path's doors (the elevator, or the doors at the level's exit)
    ///    never open.
    /// Where there's no door to lock, the old barrier is used. The same check also writes a report of
    /// every shortcut in the game (BepInEx/cache/Belay/shortcut-report.txt) when the game starts.
    /// </summary>
    internal static class ShortcutDoors
    {
        private const float ExitRadius = 15f;
        private const float StartRadius = 8f;

        private static readonly HashSet<UT_Door> locked = new HashSet<UT_Door>();

        internal static void Clear() => locked.Clear();

        internal static bool IsLocked(UT_Door door) => door != null && locked.Contains(door);

        private static bool NameHas(Transform t, Transform stop, params string[] words)
        {
            for (Transform c = t; c != null && c != stop; c = c.parent)
            {
                string n = c.name.ToLowerInvariant();
                foreach (string w in words)
                {
                    if (n.Contains(w)) return true;
                }
            }
            return false;
        }

        /// <summary>The generator's own part of the level: its ancestor just below the level root.</summary>
        internal static Transform ShortcutRoot(UT_BranchGenerator g, Transform levelRoot)
        {
            Transform t = g.transform;
            while (t.parent != null && t.parent != levelRoot)
            {
                t = t.parent;
            }
            return t.parent == levelRoot ? t : g.transform;
        }

        internal static void Analyze(UT_BranchGenerator g, M_Level level, out List<UT_Door> shortcutDoors, out List<UT_Door> mainDoors)
        {
            shortcutDoors = new List<UT_Door>();
            mainDoors = new List<UT_Door>();
            if (g == null || level == null)
            {
                return;
            }
            Transform levelRoot = level.transform;
            Transform root = ShortcutRoot(g, levelRoot);
            Transform start = g.startTransform != null ? g.startTransform : g.transform;
            Transform exit = level.GetLevelExit();
            UT_Door[] all = level.GetComponentsInChildren<UT_Door>(true);
            foreach (UT_Door d in all)
            {
                Transform t = d.transform;
                if (t.IsChildOf(root) || Vector3.Distance(t.position, start.position) < StartRadius)
                {
                    shortcutDoors.Add(d);
                }
            }
            var onShortcut = new HashSet<UT_Door>(shortcutDoors);
            var rest = all.Where(d => !onShortcut.Contains(d)).ToList();
            // The main way on: elevator or lift doors first, else the doors at the level's exit.
            mainDoors.AddRange(rest.Where(d => NameHas(d.transform, levelRoot, "elevator", "lift")));
            if (mainDoors.Count == 0 && exit != null)
            {
                mainDoors.AddRange(rest.Where(d => Vector3.Distance(d.transform.position, exit.position) < ExitRadius));
            }
        }

        /// <summary>Locks the doors on the losing side. Returns false when there was no door to lock.</summary>
        internal static bool LockSide(UT_BranchGenerator g, bool shortcutSide)
        {
            M_Level level = g != null ? (g.currentLevel ?? g.GetComponentInParent<M_Level>()) : null;
            Analyze(g, level, out List<UT_Door> shortcutDoors, out List<UT_Door> mainDoors);
            List<UT_Door> doors = shortcutSide ? shortcutDoors : mainDoors;
            if (doors.Count == 0)
            {
                return false;
            }
            foreach (UT_Door d in doors)
            {
                locked.Add(d);
            }
            Plugin.Log.LogInfo($"Locked the {(shortcutSide ? "shortcut" : "main path")} doors in {level.name}: {string.Join(", ", doors.Select(d => d.name))}");
            return true;
        }

        // ---------------------------------------------------------------- Report

        private static bool reportWritten;

        internal static void WriteReportOnce()
        {
            if (reportWritten)
            {
                return;
            }
            var levels = new List<M_Level>();
            try
            {
                var holders = Traverse.Create(typeof(CL_AssetManager)).Field("databases").GetValue<System.Collections.IList>();
                if (holders == null)
                {
                    return; // the game hasn't loaded its asset lists yet
                }
                foreach (object h in holders)
                {
                    WKAssetDatabase db = Traverse.Create(h).Field("database").GetValue<WKAssetDatabase>();
                    if (db?.levelAssets == null) continue;
                    foreach (M_Level.LevelAssetHolder la in db.levelAssets)
                    {
                        if (la?.level != null && !levels.Contains(la.level)) levels.Add(la.level);
                    }
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Shortcut report: couldn't read the level list: " + e.Message);
                reportWritten = true;
                return;
            }
            if (levels.Count == 0)
            {
                return;
            }
            reportWritten = true;
            var sb = new StringBuilder();
            var noDoor = new StringBuilder();
            int count = 0;
            foreach (M_Level level in levels.OrderBy(l => l.name))
            {
                UT_BranchGenerator[] gens;
                try { gens = level.GetComponentsInChildren<UT_BranchGenerator>(true); }
                catch { continue; }
                foreach (UT_BranchGenerator g in gens)
                {
                    count++;
                    Analyze(g, level, out List<UT_Door> sd, out List<UT_Door> md);
                    Transform exit = level.GetLevelExit();
                    string branches = g.branch != null ? string.Join(", ", g.branch.Where(b => b != null).Select(b => b.name)) : "";
                    sb.AppendLine($"{level.name} / {Path(g.transform, level.transform)}  -> {branches}");
                    sb.AppendLine("  shortcut side doors: " + (sd.Count > 0 ? string.Join(", ", sd.Select(d => Path(d.transform, level.transform))) : "NONE"));
                    sb.AppendLine("  main path doors:     " + (md.Count > 0 ? string.Join(", ", md.Select(d => Path(d.transform, level.transform))) : "NONE"));
                    foreach (UT_Door d in level.GetComponentsInChildren<UT_Door>(true))
                    {
                        float toExit = exit != null ? Vector3.Distance(d.transform.position, exit.position) : -1f;
                        sb.AppendLine($"    door {Path(d.transform, level.transform)} ({toExit:0} m from exit)");
                    }
                    foreach (UT_SimpleAnimatorController a in level.GetComponentsInChildren<UT_SimpleAnimatorController>(true))
                    {
                        sb.AppendLine($"    animated {Path(a.transform, level.transform)}");
                    }
                    if (sd.Count == 0 || md.Count == 0)
                    {
                        noDoor.AppendLine($"{level.name} / {Path(g.transform, level.transform)}: shortcut side {(sd.Count > 0 ? "has a door" : "NO DOOR")}, main path {(md.Count > 0 ? "has a door" : "NO DOOR")}");
                    }
                }
            }
            string dir = System.IO.Path.Combine(Paths.CachePath, "Belay");
            Directory.CreateDirectory(dir);
            string file = System.IO.Path.Combine(dir, "shortcut-report.txt");
            File.WriteAllText(file, $"Belay {Plugin.Version}: {count} shortcuts in {levels.Count} levels\n\nMISSING A DOOR:\n{noDoor}\nDETAILS:\n{sb}");
            Plugin.Log.LogInfo($"Wrote the shortcut report ({count} shortcuts): {file}");
        }

        private static string Path(Transform t, Transform stop)
        {
            var parts = new List<string>();
            for (Transform c = t; c != null && c != stop; c = c.parent) parts.Insert(0, c.name);
            return string.Join("/", parts);
        }
    }

    [HarmonyPatch(typeof(UT_Door), nameof(UT_Door.Open))]
    internal static class ShortcutDoorLockPatch
    {
        private static float lastNotice = -10f;

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(UT_Door __instance)
        {
            if (!ShortcutDoors.IsLocked(__instance))
            {
                return true;
            }
            if (Time.unscaledTime - lastNotice > 4f)
            {
                lastNotice = Time.unscaledTime;
            }
            return false;
        }
    }
}
