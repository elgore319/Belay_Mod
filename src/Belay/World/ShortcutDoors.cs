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
    /// Blocks a shortcut or the main path with the level's own machinery instead of a red barrier:
    ///  - Team picks the main path: the doors on the shortcut's side never open.
    ///  - Team picks the shortcut: the main path's doors (the elevator, or the doors at the level's exit)
    ///    never open.
    /// It's meant to feel like part of the level, with nothing on screen about it:
    ///  - A locked door that's standing open swings shut on its own (with its own sound) as soon as
    ///    nobody is standing in it, and stays shut, cranks and pull handles included.
    ///  - An elevator or lift on the losing side is dead: calling it or pressing go does nothing, so
    ///    it stays where it is with its doors shut. One already under way finishes its trip, but its
    ///    doors don't open at the other end.
    /// Where there's no door to lock, a slab in the level's own wall material closes the way. The same
    /// check also writes a report of every shortcut in the game (BepInEx/cache/Belay/shortcut-report.txt)
    /// when the game starts.
    /// </summary>
    internal static class ShortcutDoors
    {
        private const float ExitRadius = 15f;
        private const float StartRadius = 8f;

        /// <summary>Don't swing a door shut on someone standing in it.</summary>
        private const float ClearOfDoor = 3f;
        private const float TickEvery = 0.25f;

        private static readonly HashSet<UT_Door> locked = new HashSet<UT_Door>();
        private static readonly HashSet<Component> lockedMovers = new HashSet<Component>();
        private static readonly HashSet<UT_Door> shutLogged = new HashSet<UT_Door>();
        private static float nextTick;

        private static readonly AccessTools.FieldRef<UT_Door, bool> DoorOpen = AccessTools.FieldRefAccess<UT_Door, bool>("open");
        private static readonly AccessTools.FieldRef<UT_PathMover, float> MoverWait = AccessTools.FieldRefAccess<UT_PathMover, float>("currentWaitTime");
        internal static readonly AccessTools.FieldRef<UT_Door, float> DoorAmount = AccessTools.FieldRefAccess<UT_Door, float>("lerpAmount");

        internal static void Clear()
        {
            locked.Clear();
            lockedMovers.Clear();
            shutLogged.Clear();
        }

        internal static bool IsLocked(UT_Door door) => door != null && locked.Contains(door);

        internal static bool IsLockedMover(Component mover) => mover != null && lockedMovers.Contains(mover);

        /// <summary>Closes locked doors that are still open, once nobody is in the way. Every PC does this itself.</summary>
        internal static void Tick()
        {
            if ((locked.Count == 0 && lockedMovers.Count == 0) || Time.unscaledTime < nextTick)
            {
                return;
            }
            nextTick = Time.unscaledTime + TickEvery;
            // A shuttling elevator that runs by itself: let it finish its trip, then it stays at the stop.
            lockedMovers.RemoveWhere(m => m == null);
            foreach (Component m in lockedMovers)
            {
                if (m is UT_PathMover p && p.active && p.moveObject != null)
                {
                    float wait;
                    try { wait = MoverWait(p); } catch { continue; }
                    if (wait > 0f && !SomeoneNear(p.moveObject.position))
                    {
                        p.StopMove();
                        Plugin.Log.LogInfo("Stopped a locked elevator at its stop: " + p.name);
                    }
                }
            }
            locked.RemoveWhere(d => d == null);
            foreach (UT_Door d in locked)
            {
                bool open;
                try
                {
                    open = DoorOpen(d);
                }
                catch
                {
                    continue;
                }
                if (!open || !d.isActiveAndEnabled || SomeoneNear(d.transform.position))
                {
                    continue;
                }
                DoorSync.Applying = true;
                try
                {
                    d.Close();
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"Couldn't shut locked door {d.name}: {e.Message}");
                }
                finally
                {
                    DoorSync.Applying = false;
                }
                if (shutLogged.Add(d))
                {
                    Plugin.Log.LogInfo("Shut a locked door: " + d.name);
                }
            }
        }

        private static bool SomeoneNear(Vector3 pos)
        {
            float r2 = ClearOfDoor * ClearOfDoor;
            ENT_Player me = ENT_Player.playerObject;
            if (me != null && !me.dead && (me.transform.position - pos).sqrMagnitude < r2)
            {
                return true;
            }
            if (Players.PlayerSync.Instance != null)
            {
                foreach (Players.RemotePlayer remote in Players.PlayerSync.Instance.Remotes.Values)
                {
                    if (remote != null && !remote.Latest.Dead && (remote.LocalPosition - pos).sqrMagnitude < r2)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

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

        /// <summary>
        /// Elevators and lifts (path movers and platforms) on each side: on the shortcut side, anything in
        /// the shortcut's own part of the level; on the main path, anything named elevator or lift.
        /// Looping movers (conveyors, moving scenery) are left alone.
        /// </summary>
        internal static void AnalyzeMovers(UT_BranchGenerator g, M_Level level, out List<Component> shortcutMovers, out List<Component> mainMovers)
        {
            shortcutMovers = new List<Component>();
            mainMovers = new List<Component>();
            if (g == null || level == null)
            {
                return;
            }
            Transform levelRoot = level.transform;
            Transform root = ShortcutRoot(g, levelRoot);
            Transform start = g.startTransform != null ? g.startTransform : g.transform;
            var movers = new List<(Component c, Transform car)>();
            foreach (UT_PathMover p in level.GetComponentsInChildren<UT_PathMover>(true))
            {
                if (p != null && !p.loop) movers.Add((p, p.moveObject != null ? p.moveObject : p.transform));
            }
            foreach (UT_Platform p in level.GetComponentsInChildren<UT_Platform>(true))
            {
                if (p != null) movers.Add((p, p.transform));
            }
            foreach (var (c, car) in movers)
            {
                if (c.transform.IsChildOf(root) || car.IsChildOf(root) || Vector3.Distance(car.position, start.position) < StartRadius)
                {
                    shortcutMovers.Add(c);
                }
                else if (NameHas(c.transform, levelRoot, "elevator", "lift") || NameHas(car, levelRoot, "elevator", "lift"))
                {
                    mainMovers.Add(c);
                }
            }
        }

        /// <summary>
        /// Locks the doors (and elevators) on the losing side. Returns false when there was no door to lock,
        /// so the way needs a slab instead.
        /// </summary>
        internal static bool LockSide(UT_BranchGenerator g, bool shortcutSide)
        {
            M_Level level = g != null ? (g.currentLevel ?? g.GetComponentInParent<M_Level>()) : null;
            Analyze(g, level, out List<UT_Door> shortcutDoors, out List<UT_Door> mainDoors);
            AnalyzeMovers(g, level, out List<Component> shortcutMovers, out List<Component> mainMovers);
            List<UT_Door> doors = shortcutSide ? shortcutDoors : mainDoors;
            List<Component> movers = shortcutSide ? shortcutMovers : mainMovers;
            foreach (Component m in movers)
            {
                lockedMovers.Add(m);
            }
            if (movers.Count > 0)
            {
                Plugin.Log.LogInfo($"Stopped the {(shortcutSide ? "shortcut" : "main path")} elevators in {level.name}: {string.Join(", ", movers.Select(m => m.name))}");
            }
            if (doors.Count == 0)
            {
                return false;
            }
            foreach (UT_Door d in doors)
            {
                locked.Add(d);
            }
            nextTick = 0f;
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
                    AnalyzeMovers(g, level, out List<Component> sm, out List<Component> mm);
                    Transform exit = level.GetLevelExit();
                    string branches = g.branch != null ? string.Join(", ", g.branch.Where(b => b != null).Select(b => b.name)) : "";
                    sb.AppendLine($"{level.name} / {Path(g.transform, level.transform)}  -> {branches}");
                    sb.AppendLine("  shortcut side doors: " + (sd.Count > 0 ? string.Join(", ", sd.Select(d => Path(d.transform, level.transform))) : "NONE"));
                    sb.AppendLine("  main path doors:     " + (md.Count > 0 ? string.Join(", ", md.Select(d => Path(d.transform, level.transform))) : "NONE"));
                    sb.AppendLine("  shortcut elevators:  " + (sm.Count > 0 ? string.Join(", ", sm.Select(m => Path(m.transform, level.transform))) : "none"));
                    sb.AppendLine("  main path elevators: " + (mm.Count > 0 ? string.Join(", ", mm.Select(m => Path(m.transform, level.transform))) : "none"));
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

    [HarmonyPatch]
    internal static class ShortcutDoorLockPatch
    {
        [HarmonyPatch(typeof(UT_Door), nameof(UT_Door.Open))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Open(UT_Door __instance) => !ShortcutDoors.IsLocked(__instance);

        // Cranks and pull handles move doors bit by bit.
        [HarmonyPatch(typeof(UT_Door), nameof(UT_Door.AddOpenAmount))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool AddOpenAmount(UT_Door __instance, float amount) => !(amount > 0f && ShortcutDoors.IsLocked(__instance));

        [HarmonyPatch(typeof(UT_Door), nameof(UT_Door.SetOpenAmount))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool SetOpenAmount(UT_Door __instance, float amount)
        {
            if (!ShortcutDoors.IsLocked(__instance))
            {
                return true;
            }
            try
            {
                return amount <= ShortcutDoors.DoorAmount(__instance);
            }
            catch
            {
                return false;
            }
        }

        // A dead elevator: whatever calls it, it doesn't set off.
        [HarmonyPatch(typeof(UT_PathMover), nameof(UT_PathMover.StartMove))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool PathStart(UT_PathMover __instance) => !ShortcutDoors.IsLockedMover(__instance);

        [HarmonyPatch(typeof(UT_Platform), nameof(UT_Platform.SetMoveSpeed))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool PlatformSpeed(UT_Platform __instance, float m) => Mathf.Approximately(m, 0f) || !ShortcutDoors.IsLockedMover(__instance);
    }
}
