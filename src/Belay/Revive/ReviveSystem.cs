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
    /// Death in a lobby doesn't end your run: you spectate your teammates instead. A living player brings
    /// a dead teammate back at a save terminal (paying roaches from their own wallet; see
    /// SaveTerminalPatches) or by using a disk they're holding (free, uses up the disk). Wallets are never
    /// shared. The run only ends when everyone is dead.
    /// </summary>
    public class ReviveSystem : MonoBehaviour
    {
        public const int DefaultCost = 4;
        public const int MaxCost = 50;
        private const float RunOverDelay = 3f;
        /// <summary>A client ends the run itself if everyone has looked dead this long and the host's word never came.</summary>
        private const float ClientRunOverDelay = 12f;
        /// <summary>How long after ending the run we wait to see the game-over screen start before forcing it.</summary>
        private const float GameOverCheckDelay = 4f;

        public static ReviveSystem Instance { get; private set; }

        /// <summary>Lets the game's own death/game-over sequence run (when the whole lobby is dead).</summary>
        internal static bool AllowGameOver;

        public bool Spectating { get; private set; }
        public string Prompt { get; private set; }

        private string lastDeathType = "";
        private int spectateIndex;
        private float allDeadSince = -1f;
        private bool runOver;
        private float gameOverCheckAt = -1f;
        private bool forcedGameOver;
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
                gameOverCheckAt = -1f;
                forcedGameOver = false;
                return;
            }
            ENT_Player player = ENT_Player.playerObject;
            if (Spectating && !player.dead)
            {
                Spectating = false;
            }

            if (Spectating)
            {
                if (Input.GetKeyDown(Plugin.SpectateNextKey.Value))
                {
                    spectateIndex++;
                }
                Prompt = "You died. A teammate can revive you at a save terminal, or by using a disk.  [" + Plugin.SpectateNextKey.Value + "] next player";
            }

            CheckRunOver();
            CheckGameOverStarted();
        }

        private Vector3 spectateEye;
        private Quaternion spectateLook;
        private bool spectateEyeValid;
        private float nextLevelSync;

        private void OnEnable()
        {
            Camera.onPreCull += PlaceSpectateCamera;
            Camera.onPostRender += RestoreSpectateCamera;
        }

        private void OnDisable()
        {
            Camera.onPreCull -= PlaceSpectateCamera;
            Camera.onPostRender -= RestoreSpectateCamera;
        }

        private Camera movedCamera;
        private Vector3 movedLocalPos;
        private Quaternion movedLocalRot;

        /// <summary>Put the camera back where the game had it, so nothing else sees our move.</summary>
        private void RestoreSpectateCamera(Camera camera)
        {
            if (camera != null && camera == movedCamera)
            {
                camera.transform.localPosition = movedLocalPos;
                camera.transform.localRotation = movedLocalRot;
                movedCamera = null;
            }
        }

        private RemotePlayer SpectateTarget()
        {
            List<RemotePlayer> alive = LivingRemotes();
            return alive.Count == 0 ? null : alive[Mathf.Abs(spectateIndex) % alive.Count];
        }

        private void LateUpdate()
        {
            if (!Active || !Spectating)
            {
                spectateEyeValid = false;
                return;
            }
            RemotePlayer target = SpectateTarget();
            if (target == null)
            {
                return;
            }
            // The dead player's controller is disabled, so we carry the (invisible) body along with the
            // teammate. This keeps the levels around them loaded.
            ENT_Player player = ENT_Player.playerObject;
            Quaternion facing = Quaternion.Euler(0f, target.Latest.Yaw, 0f);
            Vector3 eye = target.LocalPosition + facing * new Vector3(0f, 1.5f, -3f);
            player.transform.position = eye;
            player.transform.rotation = facing;
            player.ResetCameraInterpolation();
            if (Time.unscaledTime >= nextLevelSync)
            {
                nextLevelSync = Time.unscaledTime + 1f;
                EnsureLevelAt(target.LocalPosition);
            }

            // Smooth the view every rendered frame, independent of network and physics rates.
            float dt = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
            Quaternion look = facing * Quaternion.Euler(15f, 0f, 0f);
            if (!spectateEyeValid || (spectateEye - eye).sqrMagnitude > 400f)
            {
                spectateEye = eye;
                spectateLook = look;
                spectateEyeValid = true;
            }
            else
            {
                float t = 1f - Mathf.Exp(-12f * dt);
                spectateEye = Vector3.Lerp(spectateEye, eye, t);
                spectateLook = Quaternion.Slerp(spectateLook, look, 1f - Mathf.Exp(-8f * dt));
            }
            Prompt = "Spectating " + target.DisplayName + ".  [" + Plugin.SpectateNextKey.Value + "] next player";
        }

        /// <summary>
        /// The game moves its camera rig only on physics steps while you're alive, so following a
        /// teammate through it looked choppy. While spectating, the camera is placed right before it
        /// renders instead, every frame.
        /// </summary>
        private void PlaceSpectateCamera(Camera camera)
        {
            if (!spectateEyeValid || !Spectating || !Active)
            {
                return;
            }
            ENT_Player player = ENT_Player.playerObject;
            if (player == null || camera == null || (camera != player.cam && camera != Camera.main))
            {
                return;
            }
            if (movedCamera == null)
            {
                movedCamera = camera;
                movedLocalPos = camera.transform.localPosition;
                movedLocalRot = camera.transform.localRotation;
            }
            camera.transform.SetPositionAndRotation(spectateEye, spectateLook);
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
                if (id == NetSession.LocalId)
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
            ENT_Player player = ENT_Player.playerObject;
            bool everyoneDead = player != null && player.dead && LivingRemotes().Count == 0;
            if (!everyoneDead)
            {
                allDeadSince = -1f;
                return;
            }
            if (allDeadSince < 0f)
            {
                allDeadSince = Time.unscaledTime;
                Plugin.Log.LogInfo("Everyone is down, the run ends soon");
                return;
            }
            float waited = Time.unscaledTime - allDeadSince;
            if (NetSession.Instance.IsHost)
            {
                if (waited > RunOverDelay)
                {
                    NetSession.Instance.Send(PacketType.RunOver, null);
                    EndRun();
                }
            }
            else if (waited > ClientRunOverDelay)
            {
                // The host should have ended it by now; its message may have been lost.
                Plugin.Log.LogWarning("Everyone has been down for a while and the host didn't end the run; ending it here");
                EndRun();
            }
        }

        /// <summary>
        /// Checks that the game-over screen really started after the run ended. If the game's own death
        /// call returned early (it thought you were already dead, or it went for a disk revive instead),
        /// the end sequence is started directly.
        /// </summary>
        private void CheckGameOverStarted()
        {
            if (!runOver || forcedGameOver || gameOverCheckAt < 0f || Time.unscaledTime < gameOverCheckAt)
            {
                return;
            }
            gameOverCheckAt = -1f;
            if (CL_GameManager.runHasEnded)
            {
                return;
            }
            forcedGameOver = true;
            Plugin.Log.LogWarning("The game-over screen didn't start after the run ended; starting it directly");
            try
            {
                CL_GameManager gMan = CL_GameManager.gMan;
                var sequence = AccessTools.Method(typeof(CL_GameManager), "EndGameSequence");
                if (gMan != null && sequence != null)
                {
                    gMan.StartCoroutine((System.Collections.IEnumerator)sequence.Invoke(gMan, new object[] { false }));
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't start the game-over screen: " + e.Message);
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
            gameOverCheckAt = Time.unscaledTime + GameOverCheckDelay;
            Plugin.Log.LogInfo("Everyone is dead: ending the run");
            AllowGameOver = true;
            try
            {
                // Die() does nothing if the game already thinks you're dead, so clear that first.
                Traverse.Create(typeof(CL_GameManager)).Field("dead").SetValue(false);
                CL_GameManager.gMan.Die(string.IsNullOrEmpty(lastDeathType) ? "basic" : lastDeathType);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Ending the run failed: " + e.Message);
            }
            finally
            {
                AllowGameOver = false;
            }
        }

        private static void RequestRevive(SteamId target, Vector3 localSpawn, bool free)
        {
            Vector3 spawn = WorldSpace.ToShared(localSpawn);
            if (NetSession.Instance.IsHost)
            {
                Instance.HostApproveRevive(target, NetSession.LocalId, spawn, free);
            }
            else
            {
                NetSession.Instance.Send(PacketType.ReviveRequest, w =>
                {
                    w.Write(target.Value);
                    w.WriteVector(spawn);
                    w.Write(free);
                });
            }
        }

        /// <summary>Just behind the local player, so a revived teammate doesn't land in front of them.</summary>
        private static Vector3 SpotBehindLocalPlayer()
        {
            ENT_Player player = ENT_Player.playerObject;
            Vector3 back = Vector3.ProjectOnPlane(-player.transform.forward, Vector3.up);
            if (back.sqrMagnitude < 0.001f)
            {
                back = Vector3.back;
            }
            return player.transform.position + back.normalized * 1.2f + Vector3.up * 0.3f;
        }

        /// <summary>The first dead teammate (not counting yourself), if any.</summary>
        internal static bool FirstDeadTeammate(out SteamId target)
        {
            List<SteamId> dead = DeadMembers();
            dead.Remove(NetSession.LocalId);
            target = dead.Count > 0 ? dead[0] : default;
            return dead.Count > 0;
        }

        // ---------------------------------------------------------------- Disks in hand

        /// <summary>
        /// Using a disk held in your hand revives a dead teammate right where you are, for free. The disk
        /// is used up. Returns false (so the disk does whatever it normally does) when nobody is dead.
        /// </summary>
        internal static bool DiskRevive()
        {
            ENT_Player player = ENT_Player.playerObject;
            if (!Active || Instance == null || player == null || player.dead || !FirstDeadTeammate(out SteamId target))
            {
                return false;
            }
            RequestRevive(target, SpotBehindLocalPlayer(), true);
            Plugin.Log.LogInfo("Disk revive requested for " + NetSession.NameOf(target));
            return true;
        }

        // ---------------------------------------------------------------- Save terminals

        /// <summary>
        /// In a lobby, the save terminal's disk page is a revive station instead. Loading a disk save
        /// would drop you back down at the terminal you saved at (under the mass) and regenerate the map
        /// out of sync with everyone else, so disk saves are off in co-op. Physical disks aren't used.
        /// </summary>
        internal static bool TerminalsAreReviveStations => Active && Instance != null;

        /// <summary>What the terminal's revive page should say right now.</summary>
        internal static string TerminalStatus()
        {
            int cost = Instance.Cost;
            int wallet = CL_GameManager.GetRoaches();
            if (!FirstDeadTeammate(out SteamId target))
            {
                return $"REVIVE | EVERYONE IS ALIVE | COST {cost} ROACHES";
            }
            return $"REVIVE | {NetSession.NameOf(target).ToUpperInvariant()} IS DOWN | COST {cost} | YOU HAVE {wallet}";
        }

        /// <summary>
        /// Revive the first dead teammate next to the local player (who is standing at the terminal),
        /// paid from the local player's own wallet. Returns the message to show on the terminal.
        /// </summary>
        internal static string TerminalRevive()
        {
            ENT_Player player = ENT_Player.playerObject;
            if (player == null || player.dead)
            {
                return "You can't revive anyone while you're dead.";
            }
            if (!FirstDeadTeammate(out SteamId target))
            {
                return "Everyone is alive. Nobody to revive.";
            }
            int cost = Instance.Cost;
            int wallet = CL_GameManager.GetRoaches();
            if (wallet < cost)
            {
                return $"Reviving {NetSession.NameOf(target)} costs {cost} roaches. You have {wallet}.";
            }
            // Bring them back just behind the player at the terminal, out of the way of the screen.
            RequestRevive(target, SpotBehindLocalPlayer(), false);
            Plugin.Log.LogInfo("Terminal revive requested for " + NetSession.NameOf(target));
            return $"Reviving {NetSession.NameOf(target)} for {cost} roaches.";
        }

        // ---------------------------------------------------------------- Network

        private void HandleReviveRequest(SteamId origin, System.IO.BinaryReader reader)
        {
            SteamId target = reader.ReadUInt64();
            Vector3 spawn = reader.ReadVector();
            bool free = reader.ReadBoolean();
            if (NetSession.Instance.IsHost)
            {
                HostApproveRevive(target, origin, spawn, free);
            }
        }

        private void HostApproveRevive(SteamId target, SteamId reviver, Vector3 sharedSpawn, bool free)
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
                w.Write(free);
            });
            ApplyRevive(target, reviver, sharedSpawn, free);
        }

        private void HandleRevived(SteamId origin, System.IO.BinaryReader reader)
        {
            SteamId target = reader.ReadUInt64();
            SteamId reviver = reader.ReadUInt64();
            Vector3 spawn = reader.ReadVector();
            bool free = reader.ReadBoolean();
            if (origin == NetSession.Instance.HostId)
            {
                ApplyRevive(target, reviver, spawn, free);
            }
        }

        /// <summary>Raised on every machine when someone is revived (target, reviver, spawn in shared space).</summary>
        public static event System.Action<SteamId, SteamId, Vector3> Revived;

        private void ApplyRevive(SteamId target, SteamId reviver, Vector3 sharedSpawn, bool free)
        {
            Revived?.Invoke(target, reviver, sharedSpawn);
            if (reviver == NetSession.LocalId)
            {
                if (!free)
                {
                    // Paid from the reviver's own wallet only.
                    CL_GameManager.AddRoaches(-Cost);
                }
                RunFlowNotice("Revived " + NetSession.NameOf(target) + (free ? " with a disk" : ""));
            }
            if (target != NetSession.LocalId || !WorldSpace.Ready)
            {
                return;
            }
            ENT_Player player = ENT_Player.playerObject;
            Spectating = false;
            player.Revive();
            player.GetTagger()?.RemoveTag("Dead");
            Vector3 spot = WorldSpace.ToLocal(sharedSpawn);
            EnsureLevelAt(spot);
            player.Teleport(spot);
            player.ResetFallingHeight();
            player.AddIgnoreFallDamageTime(3f);
            RunFlowNotice(NetSession.NameOf(reviver) + " revived you");
        }

        /// <summary>
        /// Make the level at this spot the active one (switching map branch if needed) so its terrain
        /// is loaded before we put someone there. While dead, the game stopped tracking where you are,
        /// so a revive far above could drop you into a level that was never switched on.
        /// </summary>
        internal static void EnsureLevelAt(Vector3 worldPos)
        {
            M_Level best = null;
            float bestDist = float.MaxValue;
            foreach (M_Level level in FindObjectsByType<M_Level>(FindObjectsInactive.Include, FindObjectsSortMode.None))
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
            if (best == null)
            {
                Plugin.Log.LogWarning("No level found at the revive spot");
                return;
            }
            try
            {
                if (!best.gameObject.activeSelf)
                {
                    best.gameObject.SetActive(true);
                }
                WorldLoader loader = WorldLoader.instance;
                if (loader != null)
                {
                    // ChangeBranch hides the old branch's other levels, so only switch when it differs.
                    var branch = loader.GetBranchOfLevel(best);
                    if (branch != null && branch != loader.GetCurrentBranch())
                    {
                        loader.ChangeBranch(branch);
                        Plugin.Log.LogInfo("Switched map branch to follow " + best.name);
                    }
                    if (loader.GetCurrentLevel() == null || loader.GetCurrentLevel().GetLevel() != best)
                    {
                        best.SetAsCurrentLevel();
                    }
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't load the level at the revive spot: " + e.Message);
            }
        }

        private static void RunFlowNotice(string text)
        {
            Run.RunFlow.Instance?.ShowNotice(text);
        }
    }

    /// <summary>
    /// When the whole team is dead the run must end, not drop into the game's disk revive, even if a
    /// disk save is lying around from earlier.
    /// </summary>
    [HarmonyPatch(typeof(CL_SaveManager), nameof(CL_SaveManager.GetMostRecentSaveStateByType))]
    internal static class NoDiskReviveAtGameOverPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(CL_SaveManager.SaveState.SaveType type, ref CL_SaveManager.SaveState __result)
        {
            if (ReviveSystem.AllowGameOver && type == CL_SaveManager.SaveState.SaveType.disk)
            {
                __result = null;
                return false;
            }
            return true;
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
