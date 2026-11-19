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
            Vector3 center = floor + Vector3.up * (0.08f + half.y);
            int hits = 0;
            foreach (Collider c in Physics.OverlapBox(center, half, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
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

        private static IEnumerator Run(BioChamberMarker m)
        {
            Color baseGlow = Color.white;
            float start = Time.time;
            while (m != null && Time.time - start < GrowSeconds)
            {
                float t = (Time.time - start) / GrowSeconds;
                float pulse = 1f + 2.5f * Mathf.Sin(t * Mathf.PI) * (0.75f + 0.25f * Mathf.Sin(Time.time * 18f));
                SetGlow(m, baseGlow * pulse, 0.6f + 2.4f * Mathf.Sin(t * Mathf.PI));
                Turn(m, 0f);
                yield return null;
            }
            SetGlow(m, baseGlow, 0.6f);
            yield return Swing(m, 0f, 1f);
            start = Time.time;
            while (m != null && Time.time - start < StayOpenSeconds)
            {
                yield return null;
            }
            yield return Swing(m, 1f, 0f);
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
