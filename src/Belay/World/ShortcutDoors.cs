using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;

namespace Belay.World
{
    /// <summary>
    /// Blocks a shortcut or the main path with the level's own machinery instead of a barrier:
    ///  - Team picks the main path: the shortcut's own doors, hatches, elevators and animated gates
    ///    stay as they are. A shortcut elevator can still be ridden, but the door that would open onto
    ///    the shortcut never opens, and anything the shortcut's trigger would set moving doesn't move.
    ///  - Team picks the shortcut: the main path's doors (the elevator, or the doors at the level's exit)
    ///    never open, and the elevators and lifts at the exit don't move.
    /// The shortcut's parts are found by following the level's own wiring: whatever fires the shortcut
    /// (UT_BranchGenerator.Generate) from a button, zone, door or lift also drives the doors and lifts
    /// that belong to it, so those are the ones that stay shut. Nearby doors (under the shortcut's part of
    /// the level, or by its start) count too.
    /// Only where nothing at all can be locked is the way sealed with a plate cut from the level's own
    /// walls. The same check also writes a report of every shortcut in the game
    /// (BepInEx/cache/Belay/shortcut-report.txt) when the game starts.
    /// Every PC applies the same locks itself (the vote result reaches every PC), so nothing is sent.
    /// </summary>
    internal static class ShortcutDoors
    {
        private const float ExitRadius = 15f;
        private const float StartRadius = 8f;

        private static readonly HashSet<UT_Door> locked = new HashSet<UT_Door>();
        private static readonly HashSet<Component> lockedMovers = new HashSet<Component>();

        internal static void Clear()
        {
            locked.Clear();
            lockedMovers.Clear();
        }

        internal static bool IsLocked(UT_Door door) => door != null && locked.Contains(door);

        internal static bool IsLocked(Component mover) => mover != null && lockedMovers.Contains(mover);

        /// <summary>Don't swing a door shut on someone standing in it.</summary>
        private const float ClearOfDoor = 3f;
        private static float nextTick;
        private static readonly AccessTools.FieldRef<UT_Door, bool> DoorIsOpen = AccessTools.FieldRefAccess<UT_Door, bool>("open");
        internal static readonly AccessTools.FieldRef<UT_Door, float> DoorAmount = AccessTools.FieldRefAccess<UT_Door, float>("lerpAmount");

        /// <summary>
        /// A locked door that was already standing open (many start open) swings shut on its own, with its
        /// own sound, as soon as nobody is in the doorway, and then stays shut. Every PC does this itself.
        /// </summary>
        internal static void Tick()
        {
            if (locked.Count == 0 || Time.unscaledTime < nextTick)
            {
                return;
            }
            nextTick = Time.unscaledTime + 0.25f;
            locked.RemoveWhere(d => d == null);
            foreach (UT_Door d in locked)
            {
                bool open;
                try { open = DoorIsOpen(d); } catch { continue; }
                if (!open || !d.isActiveAndEnabled || SomeoneNear(d.transform.position))
                {
                    continue;
                }
                DoorSync.Applying = true;
                try
                {
                    d.Close();
                    Plugin.Log.LogInfo("Shut a locked door that was standing open: " + d.name);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"Couldn't shut locked door {d.name}: {e.Message}");
                }
                finally
                {
                    DoorSync.Applying = false;
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

        /// <summary>Doors, lifts and animated parts (hatches, gates) the game moves.</summary>
        private static bool IsMover(Component c) => c is UT_PathMover || c is UT_Platform || c is UT_SimpleAnimatorController;

        internal sealed class Sides
        {
            public readonly List<UT_Door> ShortcutDoors = new List<UT_Door>();
            public readonly List<Component> ShortcutMovers = new List<Component>();
            public readonly List<UT_Door> MainDoors = new List<UT_Door>();
            public readonly List<Component> MainMovers = new List<Component>();
            public bool HasShortcutSide => ShortcutDoors.Count > 0 || ShortcutMovers.Count > 0;
            public bool HasMainSide => MainDoors.Count > 0 || MainMovers.Count > 0;
        }

        internal static Sides Analyze(UT_BranchGenerator g, M_Level level)
        {
            var sides = new Sides();
            if (g == null || level == null)
            {
                return sides;
            }
            Transform levelRoot = level.transform;
            Transform root = ShortcutRoot(g, levelRoot);
            Transform start = g.startTransform != null ? g.startTransform : g.transform;
            Transform exit = level.GetLevelExit();
            HashSet<Component> chain = Wiring.ShortcutChain(g, level);

            UT_Door[] all = level.GetComponentsInChildren<UT_Door>(true);
            foreach (UT_Door d in all)
            {
                Transform t = d.transform;
                if (chain.Contains(d) || t.IsChildOf(root) || Vector3.Distance(t.position, start.position) < StartRadius)
                {
                    sides.ShortcutDoors.Add(d);
                }
            }
            sides.ShortcutMovers.AddRange(chain.Where(IsMover));

            var onShortcut = new HashSet<Component>(sides.ShortcutDoors);
            onShortcut.UnionWith(sides.ShortcutMovers);
            var rest = all.Where(d => !onShortcut.Contains(d)).ToList();
            // The main way on: elevator or lift doors first, else the doors at the level's exit.
            sides.MainDoors.AddRange(rest.Where(d => NameHas(d.transform, levelRoot, "elevator", "lift")));
            if (sides.MainDoors.Count == 0 && exit != null)
            {
                sides.MainDoors.AddRange(rest.Where(d => Vector3.Distance(d.transform.position, exit.position) < ExitRadius));
            }
            // And the lifts at the exit don't go.
            if (exit != null)
            {
                foreach (Component m in level.GetComponentsInChildren<UT_PathMover>(true).Cast<Component>()
                    .Concat(level.GetComponentsInChildren<UT_Platform>(true)))
                {
                    if (onShortcut.Contains(m) || ((m as UT_PathMover)?.loop ?? false))
                    {
                        continue;
                    }
                    Transform car = m is UT_PathMover pm && pm.moveObject != null ? pm.moveObject : m.transform;
                    if (Vector3.Distance(car.position, exit.position) < ExitRadius)
                    {
                        sides.MainMovers.Add(m);
                    }
                }
            }
            return sides;
        }

        /// <summary>Where a door actually is: the middle of its renderers (its root can sit far from the mesh).</summary>
        internal static Vector3 DoorPoint(Transform t)
        {
            bool any = false;
            var b = new Bounds(t.position, Vector3.zero);
            foreach (Renderer r in t.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.bounds.size.sqrMagnitude < 0.0001f) continue; // a prefab not in the world has no bounds
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            return any ? b.center : t.position;
        }

        /// <summary>
        /// Main-path doors a level animates as part of a scripted sequence, so nothing above can find them.
        /// Named by hand, by their path in the level. When the team picks the shortcut they're held in the
        /// pose they're in (shut) and made solid, while the rest of the sequence plays as normal.
        ///  - Silo to Pipeworks interlude: the elevator button opens the landing door on the button side,
        ///    the elevator drops, and the landing door across the shaft opens onto the way to Pipeworks.
        ///    The far landing door (and its grate) stays shut, so the way on is up the shaft into the
        ///    Expulsion Chute. The button-side door (Door_Elevator_01) and the room's grate
        ///    (Scripting/Doors/Door_Grate_01) are never touched, so nobody is shut in.
        /// </summary>
        private static readonly Dictionary<string, string[]> HeldShut = new Dictionary<string, string[]>
        {
            ["Campaign_Interlude_Silo_To_Pipeworks_01"] = new[]
            {
                "Scripting/Elevator/Elevator_Fakeout/Door_Elevator_01.01",
                "Scripting/Elevator/Elevator_Fakeout/Door_Grate_01",
            },
        };

        private static bool HoldShutByName(M_Level level)
        {
            string name = level.name.Replace("(Clone)", "").Trim();
            if (!HeldShut.TryGetValue(name, out string[] paths))
            {
                return false;
            }
            bool any = false;
            foreach (string path in paths)
            {
                Transform t = level.transform.Find(path);
                if (t == null)
                {
                    Plugin.Log.LogWarning($"Main path door not found in {level.name}: {path}");
                    continue;
                }
                if (t.GetComponent<HoldShut>() == null)
                {
                    t.gameObject.AddComponent<HoldShut>();
                }
                any = true;
                Plugin.Log.LogInfo($"Holding the main path door shut in {level.name}: {path}");
            }
            return any;
        }

        /// <summary>Locks the losing side. Returns false when there was nothing to lock.</summary>
        internal static bool LockSide(UT_BranchGenerator g, bool shortcutSide)
        {
            M_Level level = g != null ? (g.currentLevel ?? g.GetComponentInParent<M_Level>()) : null;
            if (!shortcutSide && level != null && HoldShutByName(level))
            {
                return true;
            }
            Sides sides = Analyze(g, level);
            List<UT_Door> doors = shortcutSide ? sides.ShortcutDoors : sides.MainDoors;
            List<Component> movers = shortcutSide ? sides.ShortcutMovers : sides.MainMovers;
            if (doors.Count == 0 && movers.Count == 0)
            {
                return false;
            }
            foreach (UT_Door d in doors)
            {
                locked.Add(d);
            }
            foreach (Component m in movers)
            {
                lockedMovers.Add(m);
            }
            Plugin.Log.LogInfo($"Locked the {(shortcutSide ? "shortcut" : "main path")} in {level.name}: doors [{string.Join(", ", doors.Select(d => d.name))}], movers [{string.Join(", ", movers.Select(m => m.GetType().Name + ":" + m.name))}]");
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
                    Sides sides = Analyze(g, level);
                    List<UT_Door> sd = sides.ShortcutDoors;
                    List<UT_Door> md = sides.MainDoors;
                    Transform exit = level.GetLevelExit();
                    string branches = g.branch != null ? string.Join(", ", g.branch.Where(b => b != null).Select(b => b.name)) : "";
                    sb.AppendLine($"{level.name} / {Path(g.transform, level.transform)}  -> {branches}");
                    sb.AppendLine("  shortcut side doors: " + (sd.Count > 0 ? string.Join(", ", sd.Select(d => Path(d.transform, level.transform))) : "NONE"));
                    sb.AppendLine("  main path doors:     " + (md.Count > 0 ? string.Join(", ", md.Select(d => Path(d.transform, level.transform))) : "NONE"));
                    sb.AppendLine("  shortcut side movers: " + (sides.ShortcutMovers.Count > 0 ? string.Join(", ", sides.ShortcutMovers.Select(m => m.GetType().Name + " " + Path(m.transform, level.transform))) : "NONE"));
                    sb.AppendLine("  main path movers:     " + (sides.MainMovers.Count > 0 ? string.Join(", ", sides.MainMovers.Select(m => m.GetType().Name + " " + Path(m.transform, level.transform))) : "NONE"));
                    foreach (string w in Wiring.Describe(g, level))
                    {
                        sb.AppendLine("    fired by " + w);
                    }
                    foreach (UT_Door d in level.GetComponentsInChildren<UT_Door>(true))
                    {
                        float toExit = exit != null ? Vector3.Distance(d.transform.position, exit.position) : -1f;
                        sb.AppendLine($"    door {Path(d.transform, level.transform)} ({toExit:0} m from exit)");
                    }
                    foreach (UT_SimpleAnimatorController a in level.GetComponentsInChildren<UT_SimpleAnimatorController>(true))
                    {
                        float toExit = exit != null ? Vector3.Distance(DoorPoint(a.transform), exit.position) : -1f;
                        sb.AppendLine($"    animated {Path(a.transform, level.transform)} ({toExit:0} m from exit)");
                    }
                    if (!sides.HasShortcutSide || !sides.HasMainSide)
                    {
                        noDoor.AppendLine($"{level.name} / {Path(g.transform, level.transform)}: shortcut side {(sides.HasShortcutSide ? "lockable" : "NOTHING TO LOCK")}, main path {(sides.HasMainSide ? "lockable" : "NOTHING TO LOCK")}");
                    }
                }
            }
            string dir = System.IO.Path.Combine(Paths.CachePath, "Belay");
            Directory.CreateDirectory(dir);
            string file = System.IO.Path.Combine(dir, "shortcut-report.txt");
            File.WriteAllText(file, $"Not Alone {Plugin.Version}: {count} shortcuts in {levels.Count} levels\n\nNOTHING TO LOCK (sealed with a wall plate instead):\n{noDoor}\nDETAILS:\n{sb}");
            Plugin.Log.LogInfo($"Wrote the shortcut report ({count} shortcuts): {file}");
        }

        private static string Path(Transform t, Transform stop)
        {
            var parts = new List<string>();
            for (Transform c = t; c != null && c != stop; c = c.parent) parts.Insert(0, c.name);
            return string.Join("/", parts);
        }
    }

    /// <summary>
    /// Reads the level's own wiring (the UnityEvents its buttons, zones, doors and lifts call). The
    /// shortcut chain is everything that opens or moves in the same event that fires the shortcut's
    /// generator, plus whatever those parts open or move in turn when they run (a lift's arrival opening
    /// its far door, say). Calls that close or stop something are ignored, so the doors a shortcut shuts
    /// on the normal path never count as the shortcut's.
    /// </summary>
    internal static class Wiring
    {
        private const int Hops = 2;

        private static readonly Dictionary<System.Type, List<FieldInfo>> eventFields = new Dictionary<System.Type, List<FieldInfo>>();
        private static readonly Dictionary<System.Type, List<FieldInfo>> nestedFields = new Dictionary<System.Type, List<FieldInfo>>();

        private static readonly HashSet<string> OpeningCalls = new HashSet<string>
        {
            nameof(UT_Door.Open), nameof(UT_Door.AddOpenAmount),
            nameof(UT_PathMover.StartMove), nameof(UT_Platform.SetMoveSpeed),
            nameof(UT_SimpleAnimatorController.SetAnimatorBool),
        };

        private static bool Lockable(Component c) => c is UT_Door || c is UT_PathMover || c is UT_Platform || c is UT_SimpleAnimatorController;

        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static List<FieldInfo> EventFields(System.Type t)
        {
            if (!eventFields.TryGetValue(t, out var list))
            {
                list = new List<FieldInfo>();
                for (System.Type c = t; c != null && c != typeof(MonoBehaviour); c = c.BaseType)
                {
                    foreach (FieldInfo f in c.GetFields(Fields | BindingFlags.DeclaredOnly))
                    {
                        if (typeof(UnityEventBase).IsAssignableFrom(f.FieldType)) list.Add(f);
                    }
                }
                eventFields[t] = list;
            }
            return list;
        }

        /// <summary>Fields holding lists or arrays of plain serialized classes, which may carry events of their own.</summary>
        private static List<FieldInfo> NestedFields(System.Type t)
        {
            if (!nestedFields.TryGetValue(t, out var list))
            {
                list = new List<FieldInfo>();
                for (System.Type c = t; c != null && c != typeof(MonoBehaviour); c = c.BaseType)
                {
                    foreach (FieldInfo f in c.GetFields(Fields | BindingFlags.DeclaredOnly))
                    {
                        System.Type e = f.FieldType.IsArray ? f.FieldType.GetElementType()
                            : f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(List<>) ? f.FieldType.GetGenericArguments()[0]
                            : (!f.FieldType.IsPrimitive && f.FieldType.IsClass && !typeof(Object).IsAssignableFrom(f.FieldType)
                               && f.FieldType != typeof(string) && !typeof(UnityEventBase).IsAssignableFrom(f.FieldType)) ? f.FieldType : null;
                        if (e != null && e.IsClass && !typeof(Object).IsAssignableFrom(e) && e != typeof(string) && EventFields(e).Count > 0)
                        {
                            list.Add(f);
                        }
                    }
                }
                nestedFields[t] = list;
            }
            return list;
        }

        internal static IEnumerable<UnityEventBase> EventsOf(object owner)
        {
            if (owner == null) yield break;
            System.Type t = owner.GetType();
            foreach (FieldInfo f in EventFields(t))
            {
                if (f.GetValue(owner) is UnityEventBase ev) yield return ev;
            }
            foreach (FieldInfo f in NestedFields(t))
            {
                object v = f.GetValue(owner);
                if (v is System.Collections.IEnumerable items && !(v is string))
                {
                    foreach (object item in items)
                    {
                        if (item == null) continue;
                        foreach (FieldInfo inner in EventFields(item.GetType()))
                        {
                            if (inner.GetValue(item) is UnityEventBase ev) yield return ev;
                        }
                    }
                }
                else if (v != null)
                {
                    foreach (FieldInfo inner in EventFields(v.GetType()))
                    {
                        if (inner.GetValue(v) is UnityEventBase ev) yield return ev;
                    }
                }
            }
        }

        private struct Call
        {
            public Component Target;
            public string Method;
            public bool Opening;
        }

        /// <summary>The persistent calls' arguments (Unity keeps them private).</summary>
        private static object ArgumentsOf(UnityEventBase ev, int i)
        {
            try
            {
                var calls = Traverse.Create(ev).Field("m_PersistentCalls").Field("m_Calls").GetValue<System.Collections.IList>();
                return calls != null && i < calls.Count ? Traverse.Create(calls[i]).Field("m_Arguments").GetValue() : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Whether this call opens or moves its target (rather than closing or stopping it).</summary>
        private static bool IsOpening(string method, object args)
        {
            if (!OpeningCalls.Contains(method)) return false;
            Traverse a = args != null ? Traverse.Create(args) : null;
            switch (method)
            {
                case nameof(UT_SimpleAnimatorController.SetAnimatorBool):
                    string s = a?.Field("m_StringArgument").GetValue<string>() ?? "";
                    return !s.EndsWith(":false", System.StringComparison.OrdinalIgnoreCase);
                case nameof(UT_Platform.SetMoveSpeed):
                    return a == null || !Mathf.Approximately(a.Field("m_FloatArgument").GetValue<float>(), 0f);
                case nameof(UT_Door.AddOpenAmount):
                    return a == null || a.Field("m_FloatArgument").GetValue<float>() > 0f;
                default:
                    return true;
            }
        }

        /// <summary>The components an event calls, with the method it calls on each.</summary>
        private static IEnumerable<Call> Calls(UnityEventBase ev)
        {
            int n;
            try { n = ev.GetPersistentEventCount(); }
            catch { yield break; }
            for (int i = 0; i < n; i++)
            {
                Object target = ev.GetPersistentTarget(i);
                string method = ev.GetPersistentMethodName(i);
                bool opening = OpeningCalls.Contains(method) && IsOpening(method, ArgumentsOf(ev, i));
                if (target is Component c)
                {
                    yield return new Call { Target = c, Method = method, Opening = opening };
                }
                else if (target is GameObject go && method != "SetActive")
                {
                    foreach (Component part in go.GetComponents<Component>())
                    {
                        if (part != null) yield return new Call { Target = part, Method = method, Opening = opening };
                    }
                }
            }
        }

        private static bool FiresGenerator(UnityEventBase ev, UT_BranchGenerator g)
        {
            foreach (var call in Calls(ev))
            {
                if (call.Target == g && call.Method == nameof(UT_BranchGenerator.Generate)) return true;
            }
            return false;
        }

        /// <summary>Every component in the level whose events fire the generator, with those events.</summary>
        private static List<KeyValuePair<Component, UnityEventBase>> Firing(UT_BranchGenerator g, M_Level level)
        {
            var found = new List<KeyValuePair<Component, UnityEventBase>>();
            foreach (MonoBehaviour mb in level.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null || mb == g) continue;
                foreach (UnityEventBase ev in EventsOf(mb))
                {
                    if (FiresGenerator(ev, g)) found.Add(new KeyValuePair<Component, UnityEventBase>(mb, ev));
                }
            }
            return found;
        }

        internal static HashSet<Component> ShortcutChain(UT_BranchGenerator g, M_Level level)
        {
            var chain = new HashSet<Component>();
            if (g == null || level == null)
            {
                return chain;
            }
            try
            {
                var visited = new HashSet<Component> { g };
                var frontier = new List<UnityEventBase>(Firing(g, level).Select(p => p.Value));
                for (int hop = 0; hop <= Hops && frontier.Count > 0; hop++)
                {
                    var next = new List<UnityEventBase>();
                    foreach (UnityEventBase ev in frontier)
                    {
                        foreach (var call in Calls(ev))
                        {
                            Component c = call.Target;
                            bool opening = call.Opening;
                            if (Lockable(c) && !opening)
                            {
                                continue; // closing or stopping something: not the shortcut's
                            }
                            if (Lockable(c) && opening)
                            {
                                chain.Add(c);
                            }
                            if (visited.Add(c) && c is MonoBehaviour)
                            {
                                next.AddRange(EventsOf(c));
                            }
                        }
                    }
                    frontier = next;
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't follow a shortcut's wiring: " + e.Message);
            }
            return chain;
        }

        /// <summary>For the report: what fires this generator.</summary>
        internal static IEnumerable<string> Describe(UT_BranchGenerator g, M_Level level)
        {
            List<KeyValuePair<Component, UnityEventBase>> firing;
            try { firing = Firing(g, level); }
            catch { yield break; }
            foreach (var p in firing)
            {
                yield return p.Key.GetType().Name + " " + p.Key.name;
            }
        }
    }

    [HarmonyPatch]
    internal static class ShortcutLockPatches
    {
        [HarmonyPatch(typeof(UT_Door), nameof(UT_Door.Open))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool DoorOpen(UT_Door __instance) => !ShortcutDoors.IsLocked(__instance);

        /// <summary>Cranked and hand-pushed doors open bit by bit: a locked one won't budge.</summary>
        [HarmonyPatch(typeof(UT_Door), nameof(UT_Door.AddOpenAmount))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool DoorPush(UT_Door __instance, float amount) => amount <= 0f || !ShortcutDoors.IsLocked(__instance);

        /// <summary>Doors set straight to an open amount (handles, scripted pulls) can't open further either.</summary>
        [HarmonyPatch(typeof(UT_Door), nameof(UT_Door.SetOpenAmount))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool DoorSet(UT_Door __instance, float amount)
        {
            if (!ShortcutDoors.IsLocked(__instance))
            {
                return true;
            }
            try { return amount <= ShortcutDoors.DoorAmount(__instance); } catch { return false; }
        }

        [HarmonyPatch(typeof(UT_PathMover), nameof(UT_PathMover.StartMove))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool PathStart(UT_PathMover __instance) => !ShortcutDoors.IsLocked(__instance);

        [HarmonyPatch(typeof(UT_Platform), nameof(UT_Platform.SetMoveSpeed))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool PlatformSpeed(UT_Platform __instance, float m) => Mathf.Approximately(m, 0f) || !ShortcutDoors.IsLocked(__instance);

        [HarmonyPatch(typeof(UT_SimpleAnimatorController), nameof(UT_SimpleAnimatorController.SetAnimatorBool))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Animate(UT_SimpleAnimatorController __instance, string s)
            => s == null || s.EndsWith(":false", System.StringComparison.OrdinalIgnoreCase) || !ShortcutDoors.IsLocked(__instance);
    }

    /// <summary>
    /// Keeps an animated door in the pose it had when this was added (shut), whatever its animator does,
    /// and gives its panels solid colliders (some only have them while the level means them to block).
    /// </summary>
    [DefaultExecutionOrder(10000)]
    internal class HoldShut : MonoBehaviour
    {
        private Transform[] parts;
        private Vector3[] positions;
        private Quaternion[] rotations;
        private Vector3[] scales;

        private void Awake()
        {
            parts = GetComponentsInChildren<Transform>(true);
            positions = parts.Select(p => p.localPosition).ToArray();
            rotations = parts.Select(p => p.localRotation).ToArray();
            scales = parts.Select(p => p.localScale).ToArray();
            foreach (MeshFilter mf in GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.GetComponent<Collider>() == null && mf.sharedMesh != null)
                {
                    var box = mf.gameObject.AddComponent<BoxCollider>();
                    box.center = mf.sharedMesh.bounds.center;
                    box.size = mf.sharedMesh.bounds.size;
                }
            }
        }

        private void LateUpdate()
        {
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == null) continue;
                parts[i].localPosition = positions[i];
                parts[i].localRotation = rotations[i];
                parts[i].localScale = scales[i];
            }
        }
    }
}
