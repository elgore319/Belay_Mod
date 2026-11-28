using System.Collections.Generic;
using System.IO;
using Belay.Net;
using Belay.Players;
using DarkMachine.AI;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// The Teeth, the Face and the Hunter are run by the host only. Before, every PC ran its own copy and the host
    /// just nudged its position, so each player saw them leap, burrow, hunt and turn on their own. Now the host
    /// streams each one's body, model, state and animation, and on other PCs the copy is a puppet: its own
    /// movement and decisions are switched off and it follows the host's. Each PC's copy still bites, hurts and
    /// kills its own player where the host's creature reaches them. A copy the host has but this PC doesn't is
    /// spawned here, and one the host gets rid of goes here too.
    /// </summary>
    public class CreatureSync : MonoBehaviour
    {
        public static CreatureSync Instance { get; private set; }

        private const float SendInterval = 0.1f;
        private const float EmptyInterval = 0.5f;
        private const int MaxPerPacket = 10;
        private const float GoneAfter = 2.5f;
        private const float HostQuietAfter = 3f;
        private const float SpawnRange = 150f;

        private const byte KindTeeth = 0, KindFace = 1, KindHunter = 2;

        /// <summary>The copies on this PC that follow the host's.</summary>
        internal static readonly HashSet<AIGameEntity> Puppets = new HashSet<AIGameEntity>();

        /// <summary>Set while this PC applies the host's state change to a puppet.</summary>
        internal static bool ApplyingState;

        internal static bool Handles(GameEntity e) => e is DEN_Teeth || e is DEN_Face || e is DEN_Hunter;

        internal static bool IsPuppet(GameEntity e) => e is AIGameEntity a && Puppets.Contains(a);

        private static bool Live => NetSession.InLobbyRun;

        private static readonly AccessTools.FieldRef<DEN_Teeth, bool> TeethLocked = AccessTools.FieldRefAccess<DEN_Teeth, bool>("locked");
        private static readonly AccessTools.FieldRef<AIGameEntity, AIStateComponent> CurrentState = AccessTools.FieldRefAccess<AIGameEntity, AIStateComponent>("currentState");

        // Host
        private readonly Dictionary<AIGameEntity, int> netIds = new Dictionary<AIGameEntity, int>();
        private int nextNetId = 1;
        private float nextSend;

        // Other PCs
        private class Copy
        {
            public byte Kind;
            public string KindName;
            public AIGameEntity Enemy;
            public Rigidbody Body;
            public bool MadeKinematic;
            public Vector3 Pos;
            public Quaternion Rot;
            public Vector3 GeoPos;
            public Quaternion GeoRot;
            public bool GeoActive = true;
            public string State = "";
            public byte Flags;
            public float Value;
            public float LastHeard;
            public float NextSpawnTry;
            public bool Placed;
        }

        private readonly Dictionary<int, Copy> copies = new Dictionary<int, Copy>();
        private float lastHostList = -100f;
        private int runId;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.CreatureStates, HandleStates);
            NetSession.Instance.On(PacketType.CreatureRequest, HandleRequest);
        }

        private void Update()
        {
            NetSession net = NetSession.Instance;
            if (net != null && net.InLobby && runId != net.CurrentRun.RunId)
            {
                runId = net.CurrentRun.RunId;
                ReleaseAll();
                netIds.Clear();
            }
            if (!Live)
            {
                ReleaseAll();
                return;
            }
            if (net.IsHost)
            {
                ReleaseAll();
                if (Time.unscaledTime >= nextSend)
                {
                    try { SendStates(); } catch (System.Exception e) { Plugin.Log.LogWarning("Creature states: " + e.Message); }
                }
                return;
            }
            try { CheckCopies(); } catch (System.Exception e) { Plugin.Log.LogWarning("Creature copies: " + e.Message); }
        }

        // ---------------------------------------------------------------- Host

        private static IEnumerable<AIGameEntity> HostCreatures()
        {
            DEN_Teeth teeth = DEN_Teeth.instance;
            if (teeth != null && !teeth.dead && teeth.isActiveAndEnabled)
            {
                yield return teeth;
            }
            if (DEN_Face.faceInstances != null)
            {
                foreach (DEN_Face face in DEN_Face.faceInstances)
                {
                    if (face != null && !face.dead && face.isActiveAndEnabled) yield return face;
                }
            }
            if (DEN_Hunter.activeHunters != null)
            {
                foreach (DEN_Hunter hunter in DEN_Hunter.activeHunters)
                {
                    if (hunter != null && !hunter.dead && hunter.isActiveAndEnabled) yield return hunter;
                }
            }
        }

        private void SendStates()
        {
            var list = new List<AIGameEntity>(HostCreatures());
            if (list.Count > MaxPerPacket)
            {
                list.RemoveRange(MaxPerPacket, list.Count - MaxPerPacket);
            }
            nextSend = Time.unscaledTime + (list.Count == 0 ? EmptyInterval : SendInterval);
            if (!WorldSpace.Ready)
            {
                return;
            }
            NetSession.Instance.Send(PacketType.CreatureStates, w =>
            {
                w.Write((byte)list.Count);
                foreach (AIGameEntity e in list)
                {
                    WriteCreature(w, e);
                }
            }, reliable: false);
        }

        private void WriteCreature(BinaryWriter w, AIGameEntity e)
        {
            if (!netIds.TryGetValue(e, out int id))
            {
                id = nextNetId++;
                netIds[e] = id;
            }
            byte kind = e is DEN_Teeth ? KindTeeth : e is DEN_Face ? KindFace : KindHunter;
            w.Write(kind);
            w.Write(id);
            w.Write(EnemySync.KindOf(e));
            w.WriteVector(EnemySync.Instance != null ? EnemySync.Instance.SpawnOf(e) : WorldSpace.ToShared(e.transform.position));
            WritePose(w, e.transform.position, e.transform.rotation);
            AIStateComponent state = CurrentState(e);
            w.Write(state != null ? state.name ?? "" : "");
            switch (e)
            {
                case DEN_Teeth teeth:
                {
                    WritePose(w, teeth.geoRoot.position, teeth.geoRoot.rotation);
                    byte flags = 0;
                    Animator a = teeth.anim;
                    if (teeth.geoRoot.gameObject.activeSelf) flags |= 1;
                    if (a != null && a.GetBool("idle")) flags |= 2;
                    if (a != null && a.GetBool("climbing")) flags |= 4;
                    if (a != null && a.GetBool("leaping")) flags |= 8;
                    if (a != null && a.GetBool("shortleap")) flags |= 16;
                    if (a != null && a.GetBool("afraid")) flags |= 32;
                    w.Write(flags);
                    w.Write(a != null ? a.GetFloat("speedmult") : 1f);
                    break;
                }
                case DEN_Face face:
                {
                    byte flags = 0;
                    if (face.active) flags |= 1;
                    w.Write(flags);
                    w.Write(face.aggression);
                    break;
                }
                case DEN_Hunter hunter:
                {
                    w.Write((byte)0);
                    w.Write(hunter.health);
                    break;
                }
            }
        }

        private static void WritePose(BinaryWriter w, Vector3 pos, Quaternion rot)
        {
            w.WriteVector(WorldSpace.ToShared(pos));
            w.Write(rot.x); w.Write(rot.y); w.Write(rot.z); w.Write(rot.w);
        }

        private static Quaternion ReadRot(BinaryReader r) => new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        /// <summary>After a revive on another PC: the host's Teeth backs off, like it does when hurt.</summary>
        private void HandleRequest(SteamId origin, BinaryReader r)
        {
            string what = r.ReadString();
            if (!Live || !NetSession.Instance.IsHost || origin == NetSession.LocalId)
            {
                return;
            }
            DEN_Teeth teeth = DEN_Teeth.instance;
            if (what == "teeth-retreat" && teeth != null && !teeth.dead)
            {
                AIStateComponent state = CurrentState(teeth);
                if (state != null && state.name == "chase")
                {
                    teeth.ChangeState("retreat");
                    Plugin.Log.LogInfo("Teeth backs off after " + NetSession.NameOf(origin) + " was revived");
                }
            }
        }

        /// <summary>Called on this PC after its player is revived.</summary>
        internal static void AfterRevive()
        {
            DEN_Teeth teeth = DEN_Teeth.instance;
            if (Live && !NetSession.Instance.IsHost && teeth != null && IsPuppet(teeth))
            {
                NetSession.Instance.Send(PacketType.CreatureRequest, w => w.Write("teeth-retreat"));
            }
        }

        // ---------------------------------------------------------------- Other PCs

        private void HandleStates(SteamId origin, BinaryReader r)
        {
            if (origin == NetSession.LocalId || origin != NetSession.Instance.HostId || NetSession.Instance.IsHost || !Live)
            {
                return;
            }
            int count = r.ReadByte();
            for (int i = 0; i < count; i++)
            {
                byte kind = r.ReadByte();
                int id = r.ReadInt32();
                string kindName = r.ReadString();
                Vector3 spawn = r.ReadVector();
                Vector3 pos = r.ReadVector();
                Quaternion rot = ReadRot(r);
                string state = r.ReadString();
                Vector3 geoPos = pos;
                Quaternion geoRot = rot;
                if (kind == KindTeeth)
                {
                    geoPos = r.ReadVector();
                    geoRot = ReadRot(r);
                }
                byte flags = r.ReadByte();
                float value = r.ReadSingle();
                if (!WorldSpace.Ready)
                {
                    continue;
                }
                if (!copies.TryGetValue(id, out Copy c))
                {
                    c = new Copy { Kind = kind, KindName = kindName };
                    copies[id] = c;
                }
                byte before = c.Flags;
                c.Pos = pos;
                c.Rot = rot;
                c.GeoPos = geoPos;
                c.GeoRot = geoRot;
                c.Flags = flags;
                c.Value = value;
                c.LastHeard = Time.unscaledTime;
                if (c.Enemy == null || c.Enemy.dead)
                {
                    Claim(c, spawn);
                }
                if (c.Enemy != null)
                {
                    ApplyState(c, state, flags, before);
                }
            }
            lastHostList = Time.unscaledTime;
        }

        private void Claim(Copy c, Vector3 spawn)
        {
            if (c.Enemy != null)
            {
                Unpuppet(c);
                c.Enemy = null;
            }
            AIGameEntity best = null;
            float bestScore = float.MaxValue;
            foreach (AIGameEntity e in LocalOfKind(c.Kind))
            {
                if (e == null || e.dead || IsClaimed(e))
                {
                    continue;
                }
                Vector3 here = WorldSpace.ToShared(e.transform.position);
                float score = Vector3.Distance(here, c.Pos);
                if (EnemySync.Instance != null && Vector3.Distance(EnemySync.Instance.SpawnOf(e), spawn) < 5f)
                {
                    score -= 1000f; // same spawn spot: surely the same one
                }
                if (score < bestScore)
                {
                    bestScore = score;
                    best = e;
                }
            }
            if (best == null)
            {
                best = SpawnHere(c);
            }
            if (best == null)
            {
                return;
            }
            c.Enemy = best;
            c.Body = best.GetComponent<Rigidbody>();
            c.Placed = false;
            Puppets.Add(best);
            Plugin.Log.LogInfo($"{Name(c.Kind)} now follows the host's");
        }

        private static IEnumerable<AIGameEntity> LocalOfKind(byte kind)
        {
            if (kind == KindTeeth)
            {
                if (DEN_Teeth.instance != null) yield return DEN_Teeth.instance;
            }
            else if (kind == KindFace)
            {
                if (DEN_Face.faceInstances != null)
                {
                    foreach (DEN_Face f in DEN_Face.faceInstances) yield return f;
                }
            }
            else if (DEN_Hunter.activeHunters != null)
            {
                foreach (DEN_Hunter h in DEN_Hunter.activeHunters) yield return h;
            }
        }

        private bool IsClaimed(AIGameEntity e)
        {
            foreach (Copy c in copies.Values)
            {
                if (c.Enemy == e) return true;
            }
            return false;
        }

        /// <summary>The host has one and this PC doesn't (it never passed the spot that brings it): make one.</summary>
        private AIGameEntity SpawnHere(Copy c)
        {
            if (Time.unscaledTime < c.NextSpawnTry)
            {
                return null;
            }
            c.NextSpawnTry = Time.unscaledTime + 10f;
            ENT_Player me = ENT_Player.playerObject;
            Vector3 at = WorldSpace.ToLocal(c.Pos);
            if (me == null || Vector3.Distance(me.transform.position, at) > SpawnRange)
            {
                c.NextSpawnTry = Time.unscaledTime + 2f;
                return null;
            }
            AIGameEntity prefab = FindPrefab(c.Kind, c.KindName);
            if (prefab == null)
            {
                Plugin.Log.LogWarning($"The host has {Name(c.Kind)} but it isn't loaded here, so it can't be copied");
                return null;
            }
            try
            {
                // Put the whole prefab down so the creature lands where the host's is.
                Transform root = prefab.transform.root;
                Vector3 offset = prefab.transform.position - root.position;
                GameObject made = Instantiate(root.gameObject, at - offset, root.rotation);
                AIGameEntity e = made.GetComponentInChildren<AIGameEntity>(true);
                if (e == null || !Handles(e))
                {
                    Destroy(made);
                    return null;
                }
                e.transform.SetPositionAndRotation(at, c.Rot);
                Plugin.Log.LogInfo($"Made {Name(c.Kind)} here to match the host's");
                return e;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"Couldn't make {Name(c.Kind)} here: {ex.Message}");
                return null;
            }
        }

        private static AIGameEntity FindPrefab(byte kind, string kindName)
        {
            System.Type type = kind == KindTeeth ? typeof(DEN_Teeth) : kind == KindFace ? typeof(DEN_Face) : typeof(DEN_Hunter);
            AIGameEntity fallback = null;
            foreach (Object o in Resources.FindObjectsOfTypeAll(type))
            {
                if (!(o is AIGameEntity e) || e.gameObject.scene.IsValid())
                {
                    continue; // something in the world, not a prefab
                }
                if (EnemySync.KindOf(e) == kindName)
                {
                    return e;
                }
                fallback = fallback ?? e;
            }
            return fallback;
        }

        private void ApplyState(Copy c, string state, byte flags, byte before)
        {
            AIGameEntity e = c.Enemy;
            if (c.Kind == KindTeeth && e is DEN_Teeth teeth)
            {
                Animator a = teeth.anim;
                bool wasLeaping = (before & 8) != 0;
                bool leaping = (flags & 8) != 0;
                if (leaping && !wasLeaping)
                {
                    // Winding up a leap: the host's jump itself comes through as its position.
                    a?.SetTrigger("leap");
                    teeth.clipHandler?.PlaySound("teeth:grunt");
                }
                else if (!leaping && wasLeaping)
                {
                    teeth.clipHandler?.PlaySound("teeth:leap");
                }
                bool geoActive = (flags & 1) != 0;
                if (geoActive && !c.GeoActive && state == "burrow")
                {
                    // Coming back up out of a burrow.
                    a?.SetTrigger("burrowend");
                    teeth.clipHandler?.PlaySound("teeth:burrowend");
                    CL_CameraControl.ShakeAtPosition(teeth.transform.position, 0.2f, 15f);
                }
                c.GeoActive = geoActive;
            }
            if (c.State == state)
            {
                return;
            }
            string was = c.State;
            c.State = state;
            if (string.IsNullOrEmpty(state) || e.GetState(state) == null)
            {
                return;
            }
            AIStateComponent current = CurrentState(e);
            if (current != null && current.name == state)
            {
                return;
            }
            if (e is DEN_Teeth t)
            {
                TeethState(t, was, state);
                return;
            }
            ApplyingState = true;
            try
            {
                e.ChangeState(state);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"Couldn't copy the host's {Name(c.Kind)} state {state}: {ex.Message}");
            }
            finally
            {
                ApplyingState = false;
            }
        }

        /// <summary>
        /// The Teeth's burrow runs a long routine that moves it to a burrow near this PC's player, so on a puppet
        /// it's played as just its sounds and animation; the host decides where it comes back up.
        /// </summary>
        private static void TeethState(DEN_Teeth teeth, string before, string state)
        {
            AIStateComponent current = CurrentState(teeth);
            if (state == "burrow")
            {
                current?.Exit(teeth);
                CurrentState(teeth) = teeth.GetState(state);
                TeethLocked(teeth) = true;
                teeth.anim?.SetTrigger("burrowstart");
                teeth.clipHandler?.PlaySound("teeth:burrowstart");
                teeth.handParticle?.Stop();
                teeth.screenHandEffect?.Stop();
                CL_CameraControl.ShakeAtPosition(teeth.transform.position, 0.2f, 15f);
                return;
            }
            if (before == "burrow")
            {
                teeth.handParticle?.Play();
                teeth.screenHandEffect?.Play();
                TeethLocked(teeth) = false;
            }
            ApplyingState = true;
            try
            {
                teeth.ChangeState(state);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"Couldn't copy the host's Teeth state {state}: {ex.Message}");
            }
            finally
            {
                ApplyingState = false;
            }
        }

        private void CheckCopies()
        {
            if (copies.Count == 0)
            {
                return;
            }
            bool hostQuiet = Time.unscaledTime - lastHostList > HostQuietAfter;
            List<int> done = null;
            foreach (var pair in copies)
            {
                Copy c = pair.Value;
                if (hostQuiet)
                {
                    // Not hearing from the host at all: let the copies act on their own again.
                    Unpuppet(c);
                    (done ??= new List<int>()).Add(pair.Key);
                    continue;
                }
                if (Time.unscaledTime - c.LastHeard > GoneAfter)
                {
                    // The host's is gone (killed, despawned): this one goes too.
                    if (c.Enemy != null && !c.Enemy.dead)
                    {
                        Plugin.Log.LogInfo($"The host's {Name(c.Kind)} is gone, removing it here");
                        Unpuppet(c);
                        Remove(c.Enemy);
                    }
                    (done ??= new List<int>()).Add(pair.Key);
                }
            }
            if (done != null)
            {
                foreach (int id in done) copies.Remove(id);
            }
        }

        private static void Remove(AIGameEntity e)
        {
            try
            {
                switch (e)
                {
                    case DEN_Teeth teeth:
                        teeth.Despawn();
                        break;
                    case DEN_Face face:
                        Destroy(face.transform.parent != null ? face.transform.parent.gameObject : face.gameObject);
                        break;
                    case DEN_Hunter hunter:
                        EnemySync.Applying = true;
                        try { hunter.Kill("", null); } finally { EnemySync.Applying = false; }
                        break;
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning("Couldn't remove a creature here: " + ex.Message);
                e.gameObject.SetActive(false);
            }
        }

        private void LateUpdate()
        {
            if (copies.Count == 0 || !Live || NetSession.Instance.IsHost)
            {
                return;
            }
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            float k = 1f - Mathf.Exp(-15f * dt);
            ENT_Player me = ENT_Player.playerObject;
            foreach (Copy c in copies.Values)
            {
                AIGameEntity e = c.Enemy;
                if (e == null || e.dead || !Puppets.Contains(e))
                {
                    continue;
                }
                try
                {
                    if (c.Body != null && !c.Body.isKinematic)
                    {
                        c.Body.isKinematic = true;
                        c.MadeKinematic = true;
                    }
                    Transform t = e.transform;
                    Vector3 target = WorldSpace.ToLocal(c.Pos);
                    bool snap = !c.Placed || (t.position - target).sqrMagnitude > 64f;
                    Vector3 pos = snap ? target : Vector3.Lerp(t.position, target, k);
                    Quaternion rot = snap ? c.Rot : Quaternion.Slerp(t.rotation, c.Rot, k);
                    t.SetPositionAndRotation(pos, rot);
                    if (c.Body != null)
                    {
                        c.Body.position = pos;
                        c.Body.rotation = rot;
                    }
                    switch (e)
                    {
                        case DEN_Teeth teeth:
                            PoseTeeth(c, teeth, snap, k);
                            break;
                        case DEN_Face face:
                            face.aggression = c.Value;
                            face.active = (c.Flags & 1) != 0;
                            break;
                        case DEN_Hunter hunter:
                            hunter.health = Mathf.Min(c.Value, hunter.maxHealth);
                            if (snap && c.Placed) hunter.ResetSegments();
                            // Its own movement is off here, and that's where it catches you.
                            if (me != null && !me.dead && Vector3.Distance(me.transform.position, pos) < 2f)
                            {
                                me.Kill(hunter.objectType);
                            }
                            break;
                    }
                    c.Placed = true;
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"Couldn't move {Name(c.Kind)} here: {ex.Message}");
                    Unpuppet(c);
                    c.Enemy = null;
                }
            }
        }

        private static void PoseTeeth(Copy c, DEN_Teeth teeth, bool snap, float k)
        {
            Transform geo = teeth.geoRoot;
            if (geo == null)
            {
                return;
            }
            bool active = (c.Flags & 1) != 0;
            if (geo.gameObject.activeSelf != active)
            {
                geo.gameObject.SetActive(active);
                snap = true;
            }
            Vector3 target = WorldSpace.ToLocal(c.GeoPos);
            snap |= (geo.position - target).sqrMagnitude > 64f;
            geo.SetPositionAndRotation(snap ? target : Vector3.Lerp(geo.position, target, k),
                snap ? c.GeoRot : Quaternion.Slerp(geo.rotation, c.GeoRot, k));
        }

        /// <summary>Right after the Teeth's own Update, before its animation runs: use the host's animation.</summary>
        internal static void AfterTeethUpdate(DEN_Teeth teeth)
        {
            if (Instance == null || !Puppets.Contains(teeth) || teeth.anim == null)
            {
                return;
            }
            foreach (Copy c in Instance.copies.Values)
            {
                if (c.Enemy != teeth)
                {
                    continue;
                }
                Animator a = teeth.anim;
                a.SetBool("idle", (c.Flags & 2) != 0);
                a.SetBool("climbing", (c.Flags & 4) != 0);
                a.SetBool("leaping", (c.Flags & 8) != 0);
                a.SetBool("shortleap", (c.Flags & 16) != 0);
                a.SetBool("afraid", (c.Flags & 32) != 0);
                a.SetFloat("speedmult", c.Value);
                return;
            }
        }

        private static void Unpuppet(Copy c)
        {
            if (c.Enemy != null)
            {
                Puppets.Remove(c.Enemy);
            }
            if (c.Body != null && c.MadeKinematic)
            {
                c.Body.isKinematic = false;
            }
            c.MadeKinematic = false;
        }

        private void ReleaseAll()
        {
            if (copies.Count == 0 && Puppets.Count == 0)
            {
                return;
            }
            foreach (Copy c in copies.Values)
            {
                Unpuppet(c);
            }
            copies.Clear();
            Puppets.Clear();
        }

        private static string Name(byte kind) => kind == KindTeeth ? "the Teeth" : kind == KindFace ? "the Face" : "the Hunter";
    }

    // ---------------------------------------------------------------- Puppets don't think for themselves

    [HarmonyPatch]
    internal static class CreaturePuppetPatches
    {
        [HarmonyPatch(typeof(DEN_Teeth), "Navigation")]
        [HarmonyPrefix]
        private static bool TeethNavigation(DEN_Teeth __instance) => !CreatureSync.IsPuppet(__instance);

        [HarmonyPatch(typeof(DEN_Teeth), "ClimbTowardsTarget")]
        [HarmonyPrefix]
        private static bool TeethClimb(DEN_Teeth __instance) => !CreatureSync.IsPuppet(__instance);

        [HarmonyPatch(typeof(DEN_Teeth), nameof(DEN_Teeth.Update))]
        [HarmonyPostfix]
        private static void TeethUpdate(DEN_Teeth __instance)
        {
            try { CreatureSync.AfterTeethUpdate(__instance); } catch (System.Exception e) { Plugin.Log.LogWarning("Teeth animation: " + e.Message); }
        }

        [HarmonyPatch(typeof(DenizenMovement_SteeringVehicle), nameof(DenizenMovement_SteeringVehicle.Update))]
        [HarmonyPrefix]
        private static bool FaceSteering(DenizenMovement_SteeringVehicle __instance)
        {
            if (CreatureSync.Puppets.Count == 0)
            {
                return true;
            }
            GameEntity ent = MovementOwner(__instance);
            return ent == null || !CreatureSync.IsPuppet(ent);
        }

        private static readonly AccessTools.FieldRef<DenizenMovementModule, GameEntity> MovementOwner = AccessTools.FieldRefAccess<DenizenMovementModule, GameEntity>("ent");

        [HarmonyPatch(typeof(DEN_Hunter), "Movement")]
        [HarmonyPrefix]
        private static bool HunterMovement(DEN_Hunter __instance) => !CreatureSync.IsPuppet(__instance);

        [HarmonyPatch(typeof(DEN_Hunter), "TrackPlayerPosition")]
        [HarmonyPrefix]
        private static bool HunterTrack(DEN_Hunter __instance) => !CreatureSync.IsPuppet(__instance);

        /// <summary>A puppet only changes state when the host's does.</summary>
        [HarmonyPatch(typeof(AIGameEntity), nameof(AIGameEntity.ChangeState))]
        [HarmonyPrefix]
        private static bool ChangeState(AIGameEntity __instance) => CreatureSync.ApplyingState || !CreatureSync.IsPuppet(__instance)
            || CurrentStateOf(__instance) == null; // its first state: without one its Update throws every frame

        private static readonly AccessTools.FieldRef<AIGameEntity, DarkMachine.AI.AIStateComponent> CurrentStateOf =
            AccessTools.FieldRefAccess<AIGameEntity, DarkMachine.AI.AIStateComponent>("currentState");

        private static readonly AccessTools.FieldRef<DEN_Face, float> HeadDistance = AccessTools.FieldRefAccess<DEN_Face, float>("headDistance");
        private static readonly AccessTools.FieldRef<DEN_Face, GameEntity> TargetEntity = AccessTools.FieldRefAccess<DEN_Face, GameEntity>("targetEntity");

        /// <summary>
        /// On the host, a Face chasing a teammate has no entity to hurt, and the game then never updates how far its
        /// head is from its target, which sets its speed. Keep that distance right.
        /// </summary>
        [HarmonyPatch(typeof(DEN_Face), "TargetCollisionCheck")]
        [HarmonyPostfix]
        private static void FaceDistance(DEN_Face __instance)
        {
            if (TargetEntity(__instance) == null && __instance.target != null)
            {
                HeadDistance(__instance) = Vector3.Distance(__instance.transform.position, __instance.target.position);
            }
        }

        /// <summary>A teammate's hit replayed here doesn't freeze this PC's game for a moment.</summary>
        [HarmonyPatch(typeof(CL_GameManager), nameof(CL_GameManager.FreezeForTime))]
        [HarmonyPrefix]
        private static bool NoFreezeOnReplay() => !EnemySync.Applying;
    }
}
