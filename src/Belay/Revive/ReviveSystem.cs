using System.Collections.Generic;
using System.Globalization;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.Revive
{
    /// <summary>
    /// Death in a lobby doesn't end your run: you spectate your teammates instead. Every breakroom /
    /// interlude (an M_Level with safeArea) gets a revive station, where a living player pays roaches from
    /// their own wallet to bring one dead teammate back. Wallets are never shared. The run only ends when
    /// everyone is dead.
    /// </summary>
    public class ReviveSystem : MonoBehaviour
    {
        public const int DefaultCost = 10;
        public const int MaxCost = 50;
        private const float StationRange = 3f;
        private const float RunOverDelay = 3f;

        public static ReviveSystem Instance { get; private set; }

        /// <summary>Lets the game's own death/game-over sequence run (when the whole lobby is dead).</summary>
        internal static bool AllowGameOver;

        public bool Spectating { get; private set; }
        public string Prompt { get; private set; }

        private string lastDeathType = "";
        private int spectateIndex;
        private float allDeadSince = -1f;
        private bool runOver;
        private float nextStationScan;
        private readonly List<ReviveStation> stations = new List<ReviveStation>();
        /// <summary>Host: targets revived recently, to stop two players paying for the same revive.</summary>
        private readonly Dictionary<SteamId, float> recentRevives = new Dictionary<SteamId, float>();

        public int Cost
        {
            get
            {
                string raw = NetSession.Instance.GetLobbyData(NetSession.KeyReviveCost);
                return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cost)
                    ? Mathf.Clamp(cost, 0, MaxCost)
                    : DefaultCost;
            }
        }

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession net = NetSession.Instance;
            net.On(PacketType.ReviveRequest, HandleReviveRequest);
            net.On(PacketType.Revived, HandleRevived);
            net.On(PacketType.RunOver, (origin, r) =>
            {
                if (origin == NetSession.Instance.HostId)
                {
                    EndRun();
                }
            });
        }

        private static bool Active => NetSession.Instance.InLobby && WorldSpace.Ready;

        private void Update()
        {
            Prompt = null;
            if (!Active)
            {
                Spectating = false;
                runOver = false;
                allDeadSince = -1f;
                stations.Clear();
                return;
            }
            ENT_Player player = ENT_Player.playerObject;
            if (Spectating && !player.dead)
            {
                Spectating = false;
            }

            ScanForStations();
            if (Spectating)
            {
                if (Input.GetKeyDown(Plugin.SpectateNextKey.Value))
                {
                    spectateIndex++;
                }
                Prompt = "You died. A teammate can revive you at a revive station.  [" + Plugin.SpectateNextKey.Value + "] next player";
            }
            else
            {
                UpdateStationPrompt(player);
            }

            if (NetSession.Instance.IsHost)
            {
                CheckRunOver();
            }
        }

        private void LateUpdate()
        {
            if (!Active || !Spectating)
            {
                return;
            }
            List<RemotePlayer> alive = LivingRemotes();
            if (alive.Count == 0)
            {
                return;
            }
            RemotePlayer target = alive[Mathf.Abs(spectateIndex) % alive.Count];
            // The dead player's controller is disabled, so we can carry the camera along with the teammate.
            // This also keeps the levels around them loaded.
            Quaternion facing = Quaternion.Euler(0f, target.Latest.Yaw, 0f);
            ENT_Player.playerObject.transform.position = target.LocalPosition + facing * new Vector3(0f, 1.5f, -3f);
            Prompt = "Spectating " + target.DisplayName + ".  [" + Plugin.SpectateNextKey.Value + "] next player";
        }

        private static List<RemotePlayer> LivingRemotes()
        {
            var alive = new List<RemotePlayer>();
            foreach (SteamId id in NetSession.Instance.MemberOrder())
            {
                if (PlayerSync.Instance.Remotes.TryGetValue(id, out RemotePlayer remote) && remote != null
                    && !remote.Latest.Dead && Time.unscaledTime - remote.LastReceived < 3f)
                {
                    alive.Add(remote);
                }
            }
            return alive;
        }

        private static List<SteamId> DeadMembers()
        {
            var dead = new List<SteamId>();
            foreach (SteamId id in NetSession.Instance.MemberOrder())
            {
                if (id == SteamClient.SteamId)
                {
                    if (ENT_Player.playerObject.dead)
                    {
                        dead.Add(id);
                    }
                }
                else if (PlayerSync.Instance.Remotes.TryGetValue(id, out RemotePlayer remote) && remote != null && remote.Latest.Dead)
                {
                    dead.Add(id);
                }
            }
            return dead;
        }

        // ---------------------------------------------------------------- Death

        /// <summary>Called instead of the game-over sequence when the local player dies in a lobby.</summary>
        internal void OnLocalDeath(string type)
        {
            lastDeathType = type;
            Spectating = true;
            spectateIndex = 0;
            Plugin.Log.LogInfo("Died (" + type + "), spectating");
        }

        private void CheckRunOver()
        {
            if (runOver)
            {
                return;
            }
            bool everyoneDead = ENT_Player.playerObject.dead && LivingRemotes().Count == 0;
            if (!everyoneDead)
            {
                allDeadSince = -1f;
                return;
            }
            if (allDeadSince < 0f)
            {
                allDeadSince = Time.unscaledTime;
            }
            else if (Time.unscaledTime - allDeadSince > RunOverDelay)
            {
                NetSession.Instance.Send(PacketType.RunOver, null);
                EndRun();
            }
        }

        private void EndRun()
        {
            if (runOver)
            {
                return;
            }
            runOver = true;
            Spectating = false;
            AllowGameOver = true;
            try
            {
                CL_GameManager.gMan.Die(string.IsNullOrEmpty(lastDeathType) ? "basic" : lastDeathType);
            }
            finally
            {
                AllowGameOver = false;
            }
        }

        // ---------------------------------------------------------------- Stations

        private void ScanForStations()
        {
            if (Time.unscaledTime < nextStationScan || WorldLoader.instance == null || !WorldLoader.isLoaded)
            {
                return;
            }
            nextStationScan = Time.unscaledTime + 1f;
            stations.RemoveAll(s => s == null);
            WorldLoader.BranchInfo branch = WorldLoader.instance.GetCurrentBranch();
            if (branch == null)
            {
                return;
            }
            foreach (WorldLoader.LevelInfo info in branch.levelTracker)
            {
                M_Level level = info?.GetLevel();
                if (level != null && level.safeArea && ((Component)level).GetComponentInChildren<ReviveStation>(true) == null)
                {
                    stations.Add(ReviveStation.Create(level));
                }
            }
        }

        private void UpdateStationPrompt(ENT_Player player)
        {
            if (player.dead)
            {
                return;
            }
            ReviveStation near = null;
            foreach (ReviveStation station in stations)
            {
                if (station != null && Vector3.Distance(station.transform.position, player.transform.position) < StationRange)
                {
                    near = station;
                    break;
                }
            }
            if (near == null)
            {
                return;
            }
            List<SteamId> dead = DeadMembers();
            if (dead.Count == 0)
            {
                Prompt = "Revive station: everyone is alive.";
                return;
            }
            SteamId target = dead[0];
            string name = NetSession.NameOf(target);
            int cost = Cost;
            int wallet = CL_GameManager.GetRoaches();
            if (wallet < cost)
            {
                Prompt = $"Revive {name}: needs {cost} roaches (you have {wallet}).";
                return;
            }
            Prompt = $"[{Plugin.ReviveKey.Value}] Revive {name} for {cost} roaches (you have {wallet}).";
            if (Input.GetKeyDown(Plugin.ReviveKey.Value))
            {
                Vector3 spawn = WorldSpace.ToShared(near.SpawnPoint);
                if (NetSession.Instance.IsHost)
                {
                    HostApproveRevive(target, SteamClient.SteamId, spawn);
                }
                else
                {
                    NetSession.Instance.Send(PacketType.ReviveRequest, w =>
                    {
                        w.Write(target.Value);
                        w.WriteVector(spawn);
                    });
                }
            }
        }

        // ---------------------------------------------------------------- Network

        private void HandleReviveRequest(SteamId origin, System.IO.BinaryReader reader)
        {
            SteamId target = reader.ReadUInt64();
            Vector3 spawn = reader.ReadVector();
            if (NetSession.Instance.IsHost)
            {
                HostApproveRevive(target, origin, spawn);
            }
        }

        private void HostApproveRevive(SteamId target, SteamId reviver, Vector3 sharedSpawn)
        {
            if (!DeadMembers().Contains(target) || target == reviver)
            {
                return;
            }
            if (recentRevives.TryGetValue(target, out float at) && Time.unscaledTime - at < 5f)
            {
                return;
            }
            recentRevives[target] = Time.unscaledTime;
            NetSession.Instance.Send(PacketType.Revived, w =>
            {
                w.Write(target.Value);
                w.Write(reviver.Value);
                w.WriteVector(sharedSpawn);
            });
            ApplyRevive(target, reviver, sharedSpawn);
        }

        private void HandleRevived(SteamId origin, System.IO.BinaryReader reader)
        {
            SteamId target = reader.ReadUInt64();
            SteamId reviver = reader.ReadUInt64();
            Vector3 spawn = reader.ReadVector();
            if (origin == NetSession.Instance.HostId)
            {
                ApplyRevive(target, reviver, spawn);
            }
        }

        /// <summary>Raised on every machine when someone is revived (target, reviver, spawn in shared space).</summary>
        public static event System.Action<SteamId, SteamId, Vector3> Revived;

        private void ApplyRevive(SteamId target, SteamId reviver, Vector3 sharedSpawn)
        {
            Revived?.Invoke(target, reviver, sharedSpawn);
            if (reviver == SteamClient.SteamId)
            {
                // Paid from the reviver's own wallet only.
                CL_GameManager.AddRoaches(-Cost);
                RunFlowNotice("Revived " + NetSession.NameOf(target));
            }
            if (target != SteamClient.SteamId || !WorldSpace.Ready)
            {
                return;
            }
            ENT_Player player = ENT_Player.playerObject;
            Spectating = false;
            player.Revive();
            player.GetTagger()?.RemoveTag("Dead");
            player.Teleport(WorldSpace.ToLocal(sharedSpawn));
            player.ResetFallingHeight();
            player.AddIgnoreFallDamageTime(3f);
            RunFlowNotice(NetSession.NameOf(reviver) + " revived you");
        }

        private static void RunFlowNotice(string text)
        {
            Run.RunFlow.Instance?.ShowNotice(text);
        }
    }

    /// <summary>A glowing pillar placed at a safe area's spawn point. Visual only, no collider.</summary>
    public class ReviveStation : MonoBehaviour
    {
        public Vector3 SpawnPoint => transform.position + transform.forward * 1.2f + Vector3.up * 0.5f;

        public static ReviveStation Create(M_Level level)
        {
            Transform levelTransform = ((Component)level).transform;
            Transform entrance = level.GetLevelEntrance();
            Vector3 pos = level.spawnPosition != null ? level.GetSpawnPosition()
                : entrance != null ? entrance.position + Vector3.up
                : levelTransform.position;
            pos += levelTransform.forward * 1.5f;

            var root = new GameObject("Belay_ReviveStation");
            root.transform.SetParent(levelTransform, true);
            root.transform.position = pos;
            Vector3 flatForward = Vector3.ProjectOnPlane(levelTransform.forward, Vector3.up);
            root.transform.rotation = flatForward.sqrMagnitude > 0.001f ? Quaternion.LookRotation(flatForward) : Quaternion.identity;
            var station = root.AddComponent<ReviveStation>();

            GameObject pillar = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Object.Destroy(pillar.GetComponent<Collider>());
            pillar.transform.SetParent(root.transform, false);
            pillar.transform.localScale = new Vector3(0.5f, 0.6f, 0.5f);
            pillar.GetComponent<Renderer>().material.color = new Color(0.3f, 1f, 0.6f);

            var label = new GameObject("Label");
            label.transform.SetParent(root.transform, false);
            label.transform.localPosition = Vector3.up * 0.9f;
            TextMesh text = label.AddComponent<TextMesh>();
            text.text = "REVIVE STATION";
            text.characterSize = 0.04f;
            text.fontSize = 48;
            text.anchor = TextAnchor.LowerCenter;
            text.color = new Color(0.3f, 1f, 0.6f);
            return station;
        }

        private void Update()
        {
            Camera cam = Camera.main;
            if (cam != null)
            {
                Transform label = transform.Find("Label");
                if (label != null)
                {
                    label.rotation = Quaternion.LookRotation(label.position - cam.transform.position);
                }
            }
        }
    }

    [HarmonyPatch(typeof(CL_GameManager), nameof(CL_GameManager.Die))]
    internal static class DiePatch
    {
        /// <summary>In a lobby, dying starts spectating instead of the game-over screen or the disk revive.</summary>
        [HarmonyPrefix]
        private static bool Prefix(CL_GameManager __instance, string type)
        {
            if (ReviveSystem.AllowGameOver || NetSession.Instance == null || !NetSession.Instance.InLobby)
            {
                return true;
            }
            __instance.localPlayer.KillEffects(type);
            ReviveSystem.Instance.OnLocalDeath(type);
            return false;
        }
    }
}
