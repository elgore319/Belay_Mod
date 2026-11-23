using System.Collections;
using System.Collections.Generic;
using Belay.Net;
using Belay.Players;
using Belay.Players.Avatar;
using Belay.World;
using UnityEngine;

namespace Belay.Revive
{
    /// <summary>One placed revive biochamber (see <see cref="BioChambers"/>).</summary>
    public class BioChamberMarker : MonoBehaviour
    {
        public M_Level Level;
        public Transform Door;
        public Light Lamp;
        public Material Glow;

        /// <summary>Where a revived player stands: the middle of the chamber floor.</summary>
        public Vector3 Spot => transform.position + Vector3.up * (BioChamberLook.FloorHeight + 0.05f);
    }

    /// <summary>
    /// In co-op, every room with a game terminal gets a revive biochamber (<see cref="BioChamberLook"/>)
    /// standing on clear floor near the terminal, facing it. Paying for a revive at that terminal brings the
    /// teammate back inside the chamber: the lamp ring flares while they're regrown (the same reform as a
    /// totem revive), then the front glass swings round the back and they step out.
    ///
    /// Placement only depends on the level's geometry (and the vote terminal copy, placed first), so every
    /// PC puts the chamber in the same spot. Chambers in shortcut branches are placed the same way, as soon
    /// as the level is loaded or someone uses its terminal.
    /// </summary>
    public class BioChambers : MonoBehaviour
    {
        public static BioChambers Instance { get; private set; }

        private const float ScanInterval = 2f;

        /// <summary>How long the chamber stays shut while the teammate is regrown, in seconds.</summary>
        private const float GrowSeconds = 2.4f;
        private const float OpenSeconds = 1.1f;
        private const float StayOpenSeconds = 6f;

        private static readonly Dictionary<M_Level, BioChamberMarker> placed = new Dictionary<M_Level, BioChamberMarker>();
        private static readonly HashSet<M_Level> noRoom = new HashSet<M_Level>();
        private static Mesh bodyMesh, doorMesh;
        private static Material[] materials;
        private static Material glassMaterial;
        private float nextScan;
        private int runId;

        private void Awake()
        {
            Instance = this;
        }

        internal static bool IsChamber(Collider c) => c != null && c.GetComponentInParent<BioChamberMarker>() != null;

        // ---------------------------------------------------------------- Placing

        private void Update()
        {
            NetSession net = NetSession.Instance;
            if (net == null || !net.InLobby)
            {
                return;
            }
            if (runId != net.CurrentRun.RunId)
            {
                runId = net.CurrentRun.RunId;
                placed.Clear();
                noRoom.Clear();
            }
            if (Time.unscaledTime < nextScan || WorldLoader.instance == null || !ReviveSystem.TerminalsAreReviveStations)
            {
                return;
            }
            nextScan = Time.unscaledTime + ScanInterval;
            try
            {
                WorldLoader.BranchInfo branch = WorldLoader.instance.GetCurrentBranch();
                if (branch?.levelTracker == null)
                {
                    return;
                }
                foreach (WorldLoader.LevelInfo info in branch.levelTracker)
                {
                    M_Level level = info?.GetLevel();
                    if (level != null && level.gameObject.activeInHierarchy)
                    {
                        For(level);
                    }
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Biochamber scan failed: " + e.Message);
            }
        }

        /// <summary>The level's chamber, placing it now if the level has a terminal and room for one.</summary>
        internal static BioChamberMarker For(M_Level level)
        {
            if (level == null)
            {
                return null;
            }
            if (placed.TryGetValue(level, out BioChamberMarker m) && m != null)
            {
                return m;
            }
            if (noRoom.Contains(level) || !level.gameObject.activeInHierarchy)
            {
                return null;
            }
            OS_Computer_Interface terminal = VoteTerminal.GameTerminalIn(level);
            if (terminal == null)
            {
                return null;
            }
            // The vote terminal copy goes in first, so both PCs see the same room when picking a spot.
            VoteTerminal.EnsureFor(level);
            try
            {
                m = Place(level, terminal);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't place a biochamber in " + level.name + ": " + e.Message);
                m = null;
            }
            if (m == null)
            {
                noRoom.Add(level);
                return null;
            }
            placed[level] = m;
            return m;
        }

        private static BioChamberMarker Place(M_Level level, OS_Computer_Interface terminal)
        {
            Vector3 stand = terminal.playerPosition != null ? terminal.playerPosition.position : terminal.transform.position;
            if (!Physics.Raycast(stand + Vector3.up * 0.6f, Vector3.down, out RaycastHit standHit, 3f, ~0, QueryTriggerInteraction.Ignore))
            {
                return null;
            }
            float floorY = standHit.point.y;
            Vector3 origin = new Vector3(stand.x, floorY, stand.z);

            // First choice: back against a wall near the terminal, its hoses running into the wall.
            if (AgainstWall(origin, floorY, out Vector3 spot, out Quaternion facing, out float wallDist))
            {
                BioChamberMarker onWall = Build(spot, facing, level.transform);
                onWall.Level = level;
                Plugin.Log.LogInfo($"Placed a biochamber against a wall in {level.name} at {spot} ({wallDist:0.0} m from the terminal)");
                return onWall;
            }

            Vector3 best = Vector3.zero;
            int bestHits = int.MaxValue;
            float bestDist = float.MaxValue;
            foreach (float dist in new[] { 2f, 2.6f, 3.2f, 3.9f, 4.6f })
            {
                for (int i = 0; i < 16; i++)
                {
                    float a = i * Mathf.PI * 2f / 16f;
                    Vector3 p = origin + new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a)) * dist;
                    int hits = Blockers(p, floorY, out Vector3 floor);
                    if (hits < bestHits || (hits == bestHits && dist < bestDist))
                    {
                        best = floor;
                        bestHits = hits;
                        bestDist = dist;
                    }
                }
                if (bestHits == 0)
                {
                    break; // nearest clear ring wins
                }
            }
            if (bestHits > 0)
            {
                Plugin.Log.LogInfo($"No clear floor for a biochamber in {level.name}; revives there happen behind the reviver");
                return null;
            }
            Vector3 face = Vector3.ProjectOnPlane(origin - best, Vector3.up);
            Quaternion rotation = face.sqrMagnitude > 0.01f ? Quaternion.LookRotation(face.normalized, Vector3.up) : Quaternion.identity;
            BioChamberMarker marker = Build(best, rotation, level.transform);
            marker.Level = level;
            Plugin.Log.LogInfo($"Placed a biochamber in {level.name} at {best} ({bestDist:0.0} m from the terminal)");
            return marker;
        }

        /// <summary>
        /// The nearest clear spot (to the terminal) where the chamber's back is against a wall: looks out from
        /// rings of floor points round the terminal for a wall within reach, stands the chamber off it by
        /// <see cref="BioChamberLook.WallBack"/> facing away from it, and checks the chamber and a step-out
        /// space in front of it are clear.
        /// </summary>
        private static bool AgainstWall(Vector3 origin, float floorY, out Vector3 spot, out Quaternion facing, out float distance)
        {
            spot = Vector3.zero;
            facing = Quaternion.identity;
            distance = float.MaxValue;
            float bodyHalfY = (BioChamberLook.GlassTop + 0.3f - 0.08f) * 0.5f;
            foreach (float ring in new[] { 1.2f, 2f, 2.8f, 3.6f, 4.4f })
            {
                for (int i = 0; i < 16; i++)
                {
                    float a = i * Mathf.PI * 2f / 16f;
                    Vector3 p = origin + new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a)) * ring;
                    // The closest wall from this point, felt for at chest height in 16 directions.
                    Vector3 eye = new Vector3(p.x, floorY + 1.3f, p.z);
                    RaycastHit wall = default;
                    bool found = false;
                    for (int j = 0; j < 16; j++)
                    {
                        float b = j * Mathf.PI * 2f / 16f;
                        Vector3 dir = new Vector3(Mathf.Sin(b), 0, Mathf.Cos(b));
                        if (Physics.Raycast(eye, dir, out RaycastHit hit, 1.8f, ~0, QueryTriggerInteraction.Ignore)
                            && hit.collider.attachedRigidbody == null && Mathf.Abs(hit.normal.y) < 0.25f
                            && (!found || hit.distance < wall.distance))
                        {
                            wall = hit;
                            found = true;
                        }
                    }
                    if (!found)
                    {
                        continue;
                    }
                    Vector3 n = Vector3.ProjectOnPlane(wall.normal, Vector3.up).normalized;
                    Vector3 c = new Vector3(wall.point.x, floorY, wall.point.z) + n * BioChamberLook.WallBack;
                    float d = Vector3.Distance(c, origin);
                    if (d < 1.5f || d >= distance)
                    {
                        continue; // not on top of the terminal's stand spot; and only nearer than the best so far
                    }
                    if (Blockers(c, floorY, out Vector3 floor) >= 100)
                    {
                        continue; // no level floor there
                    }
                    Quaternion rot = Quaternion.LookRotation(n, Vector3.up);
                    // The chamber, its back edge kept just off the wall, and a space to step out in front.
                    Vector3 body = floor + rot * new Vector3(0, 0.08f + bodyHalfY, 0.08f);
                    Vector3 step = floor + rot * new Vector3(0, 1.05f, 1.15f);
                    if (Overlaps(body, new Vector3(0.78f, bodyHalfY, 0.7f), rot) > 0
                        || Overlaps(step, new Vector3(0.4f, 0.9f, 0.3f), rot) > 0)
                    {
                        continue;
                    }
                    spot = floor;
                    facing = rot;
                    distance = d;
                }
                if (distance < float.MaxValue)
                {
                    return true; // nearest clear ring wins
                }
            }
            return false;
        }

        /// <summary>How many solid things a chamber at p would overlap, or many if there's no level floor there.</summary>
        private static int Blockers(Vector3 p, float floorY, out Vector3 floor)
        {
            floor = p;
            if (!Physics.Raycast(new Vector3(p.x, floorY + 1f, p.z), Vector3.down, out RaycastHit hit, 1.6f, ~0, QueryTriggerInteraction.Ignore)
                || Mathf.Abs(hit.point.y - floorY) > 0.3f || Vector3.Angle(hit.normal, Vector3.up) > 10f)
            {
                return 100;
            }
            floor = hit.point;
            // The plinth's footprint, from just above the floor to above the hoses' bend.
            Vector3 half = new Vector3(0.8f, (BioChamberLook.GlassTop + 0.3f - 0.08f) * 0.5f, 0.8f);
            return Overlaps(floor + Vector3.up * (0.08f + half.y), half, Quaternion.identity);
        }

        /// <summary>Solid, fixed things inside this box (loose props, players and teammates don't count).</summary>
        private static int Overlaps(Vector3 center, Vector3 half, Quaternion rotation)
        {
            int hits = 0;
            foreach (Collider c in Physics.OverlapBox(center, half, rotation, ~0, QueryTriggerInteraction.Ignore))
            {
                if (c == null || c.attachedRigidbody != null)
                {
                    continue; // loose props get pushed aside
                }
                if (ENT_Player.playerObject != null && c.transform.IsChildOf(ENT_Player.playerObject.transform))
                {
                    continue;
                }
                if (c.GetComponentInParent<RemoteBody>() != null || c.GetComponentInParent<RemotePlayer>() != null)
                {
                    continue;
                }
                hits++;
            }
            return hits;
        }

        private static BioChamberMarker Build(Vector3 position, Quaternion rotation, Transform parent)
        {
            Prepare();
            var root = new GameObject("Belay_BioChamber");
            root.transform.SetParent(parent, true);
            root.transform.SetPositionAndRotation(position, rotation);
            var marker = root.AddComponent<BioChamberMarker>();

            var body = new GameObject("Body");
            body.transform.SetParent(root.transform, false);
            body.AddComponent<MeshFilter>().sharedMesh = bodyMesh;
            var bodyMaterials = (Material[])materials.Clone();
            marker.Glow = new Material(materials[BioChamberLook.GlowSlot]);
            bodyMaterials[BioChamberLook.GlowSlot] = marker.Glow;
            bodyMaterials[BioChamberLook.GlassSlot] = glassMaterial;
            body.AddComponent<MeshRenderer>().sharedMaterials = bodyMaterials;

            var door = new GameObject("Door");
            door.transform.SetParent(root.transform, false);
            door.AddComponent<MeshFilter>().sharedMesh = doorMesh;
            var doorMaterials = (Material[])bodyMaterials.Clone();
            door.AddComponent<MeshRenderer>().sharedMaterials = doorMaterials;
            marker.Door = door.transform;

            // Solid parts: the plinth, the back of the glass, the cap. The front stays walkable, so the
            // chamber never traps anyone (the closed glass is only a look).
            AddBox(root, new Vector3(0, BioChamberLook.FloorHeight * 0.5f, 0), new Vector3(1.4f, BioChamberLook.FloorHeight, 1.4f));
            AddBox(root, new Vector3(0, (BioChamberLook.FloorHeight + BioChamberLook.GlassTop) * 0.5f, -0.6f),
                new Vector3(1.2f, BioChamberLook.GlassTop - BioChamberLook.FloorHeight, 0.12f));
            AddBox(root, new Vector3(-0.6f, (BioChamberLook.FloorHeight + BioChamberLook.GlassTop) * 0.5f, -0.2f),
                new Vector3(0.12f, BioChamberLook.GlassTop - BioChamberLook.FloorHeight, 0.7f));
            AddBox(root, new Vector3(0.6f, (BioChamberLook.FloorHeight + BioChamberLook.GlassTop) * 0.5f, -0.2f),
                new Vector3(0.12f, BioChamberLook.GlassTop - BioChamberLook.FloorHeight, 0.7f));
            AddBox(root, new Vector3(0, BioChamberLook.GlassTop + 0.15f, 0), new Vector3(1.3f, 0.3f, 1.3f));
            AddBox(root, new Vector3(0.74f, 1.0f, 0.08f), new Vector3(0.16f, 0.55f, 0.3f));

            var lampObject = new GameObject("Lamp");
            lampObject.transform.SetParent(root.transform, false);
            lampObject.transform.localPosition = new Vector3(0, BioChamberLook.GlassTop - 0.2f, 0);
            marker.Lamp = lampObject.AddComponent<Light>();
            marker.Lamp.type = LightType.Point;
            marker.Lamp.color = new Color(1f, 0.62f, 0.25f);
            marker.Lamp.range = 3f;
            marker.Lamp.intensity = 0.6f;
            marker.Lamp.shadows = LightShadows.None;
            return marker;
        }

        private static void AddBox(GameObject root, Vector3 center, Vector3 size)
        {
            var box = root.AddComponent<BoxCollider>();
            box.center = center;
            box.size = size;
        }

        private static void Prepare()
        {
            if (bodyMesh != null)
            {
                return;
            }
            var kit = new MeshKit(BioChamberLook.SlotCount);
            BioChamberLook.BuildBody(kit);
            bodyMesh = kit.ToMesh("Belay_BioChamber");
            kit = new MeshKit(BioChamberLook.SlotCount);
            BioChamberLook.BuildDoor(kit, 0f);
            doorMesh = kit.ToMesh("Belay_BioChamberDoor");
            DontDestroyOnLoad(bodyMesh);
            DontDestroyOnLoad(doorMesh);
            materials = ClimberAvatar.MakeItemMaterials(BioChamberLook.Recipes(), BioChamberLook.SteelSlot);
            foreach (Material m in materials)
            {
                DontDestroyOnLoad(m);
            }
            Material glow = materials[BioChamberLook.GlowSlot];
            if (glow.HasProperty("_EmissionColor"))
            {
                glow.SetColor("_EmissionColor", Color.white);
            }
            // See-through glass: the sprite shader is in every build and blends by alpha.
            glassMaterial = new Material(Shader.Find("Sprites/Default"))
            {
                name = "Belay_BioChamberGlass",
                color = new Color(0.62f, 0.8f, 0.72f, 0.22f),
                mainTexture = materials[BioChamberLook.GlassSlot].mainTexture,
            };
            DontDestroyOnLoad(glassMaterial);
        }

        // ---------------------------------------------------------------- Reviving

        /// <summary>Where a revive paid at this terminal brings the teammate back, if its room has a chamber.</summary>
        internal static bool SpotFor(OS_Manager os, out Vector3 spot)
        {
            spot = Vector3.zero;
            BioChamberMarker m = For(ShortcutSync.LevelOf(os));
            if (m == null)
            {
                return false;
            }
            spot = m.Spot;
            return true;
        }

        /// <summary>The chamber whose floor is at this spot (local space), placing its level's chamber if needed.</summary>
        internal static BioChamberMarker At(Vector3 localSpot)
        {
            foreach (BioChamberMarker m in placed.Values)
            {
                if (m != null && (m.Spot - localSpot).sqrMagnitude < 1f)
                {
                    return m;
                }
            }
            M_Level level = WorldCatchUp.LevelAt(localSpot);
            BioChamberMarker placedNow = level != null ? For(level) : null;
            return placedNow != null && (placedNow.Spot - localSpot).sqrMagnitude < 1f ? placedNow : null;
        }

        /// <summary>Plays the revive in the chamber at this spot (local space): glow, then the glass opens.</summary>
        internal static void Play(BioChamberMarker m)
        {
            if (m != null && Instance != null)
            {
                Instance.StartCoroutine(Run(m));
            }
        }

        private const float FillSeconds = 0.6f;
        private const float DrainSeconds = 0.7f;

        private static IEnumerator Run(BioChamberMarker m)
        {
            if (m == null)
            {
                yield break;
            }
            ChamberFx fx = ChamberFx.For(m);
            Color baseGlow = Color.white;
            Turn(m, 0f);
            // Sealed: sparks off the control box, a hum, and the pod floods with growth medium.
            fx.Sparks(18);
            fx.Sound(ReviveSounds.Hum, 0.6f);
            float start = Time.time;
            while (m != null && Time.time - start < GrowSeconds)
            {
                float t = (Time.time - start) / GrowSeconds;
                float tSec = Time.time - start;
                float level = Mathf.Min(Mathf.SmoothStep(0f, 1f, tSec / FillSeconds),
                    Mathf.SmoothStep(0f, 1f, (GrowSeconds - tSec) / DrainSeconds));
                fx.SetFluid(level);
                fx.Bubbles(level > 0.3f ? 0.06f : 0f);
                // The lamp ring throbs and flickers while the body is regrown.
                float flicker = Random.value < 0.06f ? 0.3f : 1f;
                float pulse = (1f + 2.5f * Mathf.Sin(t * Mathf.PI) * (0.75f + 0.25f * Mathf.Sin(Time.time * 18f))) * flicker;
                SetGlow(m, baseGlow * pulse, (0.6f + 2.4f * Mathf.Sin(t * Mathf.PI)) * flicker);
                yield return null;
            }
            if (m == null)
            {
                yield break;
            }
            fx.SetFluid(0f);
            SetGlow(m, baseGlow, 0.6f);
            // Unseal: steam vents round the base, the glass jolts free and swings round the back.
            fx.Steam(40);
            fx.Sound(ReviveSounds.Hiss, 0.8f);
            yield return Jolt(m);
            fx.Sound(ReviveSounds.Slide, 0.6f);
            yield return Swing(m, 0f, 1f);
            fx.Sound(ReviveSounds.Clunk, 0.6f);
            start = Time.time;
            // Stays open a while, and then until nobody is standing inside it.
            float clearSince = -1f;
            while (m != null)
            {
                bool occupied = Occupied(m);
                if (occupied) clearSince = -1f;
                else if (clearSince < 0f) clearSince = Time.time;
                if (Time.time - start >= StayOpenSeconds && clearSince >= 0f && Time.time - clearSince >= 1f)
                {
                    break;
                }
                yield return null;
            }
            fx.Sound(ReviveSounds.Slide, 0.5f);
            yield return Swing(m, 1f, 0f);
            fx.Sound(ReviveSounds.Clunk, 0.7f);
            fx.Steam(10);
        }

        /// <summary>The glass pops out a few centimeters as the seal breaks, then settles back.</summary>
        private static IEnumerator Jolt(BioChamberMarker m)
        {
            float start = Time.time;
            while (m != null && m.Door != null)
            {
                float t = (Time.time - start) / 0.35f;
                if (t >= 1f)
                {
                    m.Door.localPosition = Vector3.zero;
                    yield break;
                }
                m.Door.localPosition = Vector3.forward * (0.04f * Mathf.Sin(t * Mathf.PI)) + Random.insideUnitSphere * 0.004f;
                yield return null;
            }
        }

        /// <summary>Whether you, a teammate or a bot is inside the chamber (within the glass, above its floor).</summary>
        private static bool Occupied(BioChamberMarker m)
        {
            Vector3 floor = m.transform.position;
            bool Inside(Vector3 p)
            {
                Vector3 local = m.transform.InverseTransformPoint(p);
                return new Vector2(local.x, local.z).magnitude < BioChamberLook.GlassRadius + 0.35f
                    && local.y > -0.5f && local.y < BioChamberLook.GlassTop + 0.5f;
            }
            if (ENT_Player.playerObject != null && !ENT_Player.playerObject.dead && Inside(ENT_Player.playerObject.transform.position))
            {
                return true;
            }
            foreach (RemotePlayer r in Object.FindObjectsByType<RemotePlayer>(FindObjectsSortMode.None))
            {
                if (r != null && r.isActiveAndEnabled && Inside(r.transform.position))
                {
                    return true;
                }
            }
            return false;
        }

        private static IEnumerator Swing(BioChamberMarker m, float from, float to)
        {
            float start = Time.time;
            while (m != null)
            {
                float t = Mathf.Clamp01((Time.time - start) / OpenSeconds);
                Turn(m, Mathf.SmoothStep(from, to, t));
                if (t >= 1f)
                {
                    yield break;
                }
                yield return null;
            }
        }

        private static void Turn(BioChamberMarker m, float open)
        {
            if (m != null && m.Door != null)
            {
                m.Door.localRotation = Quaternion.Euler(0, open * BioChamberLook.OpenAngle, 0);
            }
        }

        private static void SetGlow(BioChamberMarker m, Color emission, float lamp)
        {
            if (m == null)
            {
                return;
            }
            if (m.Glow != null && m.Glow.HasProperty("_EmissionColor"))
            {
                m.Glow.SetColor("_EmissionColor", emission);
            }
            if (m.Lamp != null)
            {
                m.Lamp.intensity = lamp;
            }
        }
    }
}
