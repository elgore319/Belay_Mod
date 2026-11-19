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
    /// Dying in a lobby means spectating. Teammates revive you at a terminal (roaches from their own wallet, see
    /// AnyTerminalRevive) or with a held totem (free, used up). If everyone is dead the team returns from its disk
    /// save (TeamDiskSave), or the run ends.
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

        /// <summary>Lets the game's own death call run its disk revive (the whole team coming back from a disk save).</summary>
        internal static bool AllowTeamReload;

        /// <summary>How long a team disk revive may take before the run can end again.</summary>
        private const float TeamReloadLimit = 30f;
        private float teamReloadAt = -1f;

        /// <summary>The run these flags belong to: a new run makes a new game manager.</summary>
        private CL_GameManager runManager;

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
            Run.WinSync.Listen();
            TeamDiskSave.Listen();
            net.On(PacketType.RunOver, (origin, r) =>
            {
                if (origin == NetSession.Instance.HostId)
                {
                    EndRun();
                }
            });
            net.On(PacketType.TeamDiskReload, (origin, r) =>
            {
                string id = r.ReadString();
                if (origin == NetSession.Instance.HostId)
                {
                    StartTeamReload(id);
                }
            });
        }

        private static bool Active => NetSession.Instance.InLobby && WorldSpace.Ready;

        private void Update()
        {
            Prompt = null;
            if (CL_GameManager.gMan != runManager)
            {
                // A new run (or back to the menu). The last run's game over must not carry over: it kept
                // runOver set, so the next time the whole team died nothing happened.
                runManager = CL_GameManager.gMan;
                ResetRun();
            }
            if (!Active && runOver && gameOverCheckAt >= 0f && NetSession.Instance.InLobby)
            {
                // The world can stop counting as ready once the run ends; still make sure the screen shows.
                CheckGameOverStarted();
                return;
            }
            if (!Active)
            {
                ResetRun();
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
                Prompt = "You died. A teammate can revive you at a terminal, or by using a totem.  [" + Plugin.SpectateNextKey.Value + "] next player";
            }

            if (pendingRevive && Time.unscaledTime >= nextPendingTry)
            {
                nextPendingTry = Time.unscaledTime + 0.5f;
                TryFinishRevive();
            }

            CheckRunOver();
            CheckGameOverStarted();
        }

        private void ResetRun()
        {
            pendingRevive = false;
            Spectating = false;
            runOver = false;
            allDeadSince = -1f;
            gameOverCheckAt = -1f;
            forcedGameOver = false;
            teamReloadAt = -1f;
        }

        private Vector3 spectateEye;
        private Quaternion spectateLook;
        private bool spectateEyeValid;
        private Vector3 spectatePivot;
        private RemotePlayer orbitTarget;
        private float orbitYaw;
        private float orbitPitch;
        private const float OrbitDistance = 3.2f;
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
            // Free look: the mouse orbits the camera around the teammate. A new target (or a fresh
            // death) starts behind them, facing the way they face.
            if (!spectateEyeValid || target != orbitTarget)
            {
                orbitTarget = target;
                orbitYaw = target.Latest.Yaw;
                orbitPitch = 15f;
            }
            if (Cursor.lockState == CursorLockMode.Locked && Time.timeScale > 0f)
            {
                Vector2 lookInput = InputManager.GetLookVector();
                float pitchInput = -lookInput.y;
                if (SettingsManager.settings.invertY)
                {
                    pitchInput = -pitchInput;
                }
                orbitYaw = Mathf.Repeat(orbitYaw + lookInput.x, 360f);
                orbitPitch = Mathf.Clamp(orbitPitch + pitchInput, -80f, 80f);
            }

            // Smooth the teammate's position every rendered frame, independent of network and physics
            // rates. The look itself follows the mouse directly so it never feels laggy.
            float dt = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
            Vector3 pivot = target.LocalPosition + Vector3.up * 1.4f;
            if (!spectateEyeValid || (spectatePivot - pivot).sqrMagnitude > 400f)
            {
                spectatePivot = pivot;
            }
            else
            {
                spectatePivot = Vector3.Lerp(spectatePivot, pivot, 1f - Mathf.Exp(-12f * dt));
            }
            Quaternion look = Quaternion.Euler(orbitPitch, orbitYaw, 0f);
            Vector3 back = look * Vector3.back;
            float distance = OrbitDistance;
            if (WorldProbe.Cast(spectatePivot, back, OrbitDistance + 0.3f, out RaycastHit hit))
            {
                // Keep the camera on our side of walls and floors.
                distance = Mathf.Clamp(hit.distance - 0.3f, 0.2f, OrbitDistance);
            }
            spectateEye = spectatePivot + back * distance;
            spectateLook = look;
            spectateEyeValid = true;

            // The dead player's controller is disabled, so we carry the (invisible) body along with the
            // camera. This keeps the levels around the teammate loaded.
            ENT_Player player = ENT_Player.playerObject;
            player.transform.position = spectateEye;
            player.transform.rotation = Quaternion.Euler(0f, orbitYaw, 0f);
            player.ResetCameraInterpolation();
            if (Time.unscaledTime >= nextLevelSync)
            {
                nextLevelSync = Time.unscaledTime + 1f;
                EnsureLevelAt(target.LocalPosition);
            }
            Prompt = "Spectating " + target.DisplayName + ".  [" + Plugin.SpectateNextKey.Value + "] next player";
        }

        /// <summary>
        /// The camera rig only moves on physics steps, so spectating looked choppy. While spectating it's placed
        /// every frame right before rendering.
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
                    && !remote.Latest.Dead && remote.Present)
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
            if (teamReloadAt >= 0f)
            {
                // Coming back from the team's disk save: everyone is down until their own PC finishes it.
                bool reloadDone = player != null && !player.dead && !CL_GameManager.gMan.IsReviving();
                if (!reloadDone && Time.unscaledTime - teamReloadAt < TeamReloadLimit)
                {
                    allDeadSince = -1f;
                    return;
                }
                teamReloadAt = -1f;
            }
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
                    if (TeamDiskSave.Available(out string saveId))
                    {
                        // The team would have had its game over: everyone comes back from the disk save.
                        NetSession.Instance.Send(PacketType.TeamDiskReload, w => w.Write(saveId));
                        StartTeamReload(saveId);
                    }
                    else
                    {
                        NetSession.Instance.Send(PacketType.RunOver, null);
                        EndRun();
                    }
                }
            }
            else if (waited > ClientRunOverDelay)
            {
                // The host should have decided by now; its message may have been lost.
                if (TeamDiskSave.Available(out string saveId))
                {
                    Plugin.Log.LogWarning("Everyone has been down for a while and the host didn't bring the team back; using the disk save here");
                    StartTeamReload(saveId);
                }
                else
                {
                    Plugin.Log.LogWarning("Everyone has been down for a while and the host didn't end the run; ending it here");
                    EndRun();
                }
            }
        }

        /// <summary>
        /// Makes sure the game-over screen started after the run ended; if the game's death call bailed early,
        /// starts it directly.
        /// </summary>
        private void CheckGameOverStarted()
        {
            if (!runOver || gameOverCheckAt < 0f || Time.unscaledTime < gameOverCheckAt)
            {
                return;
            }
            if (ScoreScreenShowing())
            {
                gameOverCheckAt = -1f;
                return;
            }
            CL_GameManager gMan = CL_GameManager.gMan;
            if (gMan == null)
            {
                gameOverCheckAt = -1f;
                return;
            }
            if (!CL_GameManager.runHasEnded && !forcedGameOver)
            {
                // The game's own death call returned early (it thought you were already dead, or went
                // for a disk revive): start the end sequence directly and check again shortly.
                forcedGameOver = true;
                gameOverCheckAt = Time.unscaledTime + GameOverCheckDelay;
                Plugin.Log.LogWarning("The game-over screen didn't start after the run ended; starting it directly");
                try
                {
                    var sequence = AccessTools.Method(typeof(CL_GameManager), "EndGameSequence");
                    gMan.StartCoroutine((System.Collections.IEnumerator)sequence.Invoke(gMan, new object[] { false }));
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning("Couldn't start the game-over screen: " + e.Message);
                }
                return;
            }
            // The end sequence started but stopped before the score screen (something in it threw, e.g.
            // saving stats for a co-op run). Put the screen up ourselves, one step at a time.
            gameOverCheckAt = -1f;
            Plugin.Log.LogWarning("The game-over sequence stalled before the score screen; showing it directly");
            StatManager stats = Traverse.Create(gMan).Field("statManager").GetValue<StatManager>();
            Try("pause", () => gMan.isPaused = true);
            Try("fade", () => gMan.uiMan.SetFadeTarget(1f));
            Try("score screen", () => stats.ShowScoreScreen(false));
            Try("cursor", () =>
            {
                Cursor.lockState = CursorLockMode.None;
                CL_GameManager.showCursor = true;
            });
        }

        private static void Try(string what, System.Action step)
        {
            try
            {
                step();
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Game-over screen ({what}) failed: {e.Message}");
            }
        }

        private static bool ScoreScreenShowing()
        {
            try
            {
                CL_GameManager gMan = CL_GameManager.gMan;
                StatManager stats = gMan != null ? Traverse.Create(gMan).Field("statManager").GetValue<StatManager>() : null;
                if (stats == null)
                {
                    return false;
                }
                foreach (string field in new[] { "loseScreen", "winScreen" })
                {
                    Component screen = Traverse.Create(stats).Field(field).GetValue<Component>();
                    if (screen != null && screen.gameObject.activeInHierarchy)
                    {
                        return true;
                    }
                }
            }
            catch (System.Exception)
            {
            }
            return false;
        }

        /// <summary>A teammate reached the ending: the run is over for everyone, dead or alive.</summary>
        internal void OnRunWon()
        {
            runOver = true;
            Spectating = false;
            gameOverCheckAt = -1f;
        }

        /// <summary>
        /// Whole team down with a disk save: every PC runs the game's disk revive when the host says so.
        /// </summary>
        private void StartTeamReload(string id)
        {
            if (runOver || teamReloadAt >= 0f)
            {
                return;
            }
            if (!TeamDiskSave.Prepare(id))
            {
                Plugin.Log.LogWarning("The team is coming back from a disk save this PC doesn't have; ending the run here");
                EndRun();
                return;
            }
            teamReloadAt = Time.unscaledTime;
            allDeadSince = -1f;
            pendingRevive = false;
            Spectating = false;
            Plugin.Log.LogInfo("Everyone is down: the team comes back from the disk save at " + id);
            AllowTeamReload = true;
            try
            {
                // Die() does nothing if the game already thinks you're dead, so clear that first.
                Traverse.Create(typeof(CL_GameManager)).Field("dead").SetValue(false);
                CL_GameManager.gMan.Die(string.IsNullOrEmpty(lastDeathType) ? "basic" : lastDeathType);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Coming back from the disk save failed: " + e.Message);
            }
            finally
            {
                AllowTeamReload = false;
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

        // ---------------------------------------------------------------- Totems in hand

        /// <summary>
        /// Using a held totem revives a dead teammate where you stand, free; the totem is used up. False when
        /// nobody is dead.
        /// </summary>
        internal static bool HeldTotemRevive()
        {
            ENT_Player player = ENT_Player.playerObject;
            if (!Active || Instance == null || player == null || player.dead || !FirstDeadTeammate(out SteamId target))
            {
                return false;
            }
            RequestRevive(target, SpotBehindLocalPlayer(), true);
            Plugin.Log.LogInfo("Totem revive requested for " + NetSession.NameOf(target));
            return true;
        }

        /// <summary>
        /// A climbing bot reached a terminal (or the room ahead) while you were down: it revives you next to
        /// itself, free. Host only.
        /// </summary>
        internal static bool BotRevive(SteamId bot, SteamId target, Vector3 localSpawn)
        {
            if (!Active || Instance == null || !NetSession.Instance.IsHost || !DeadMembers().Contains(target))
            {
                return false;
            }
            if (Instance.recentRevives.TryGetValue(target, out float at) && Time.unscaledTime - at < 5f)
            {
                return false;
            }
            Instance.HostApproveRevive(target, bot, WorldSpace.ToShared(localSpawn), true);
            return true;
        }

        // ---------------------------------------------------------------- Save terminals

        /// <summary>In a lobby: terminals offer revives, totems revive, and disk saves are the team's.</summary>
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
        /// Revives the first dead teammate next to you at the terminal, paid from your wallet. Returns the
        /// terminal message.
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
            // Free revives come from a held totem, or from a climbing bot.
            bool totem = free && !NetSession.Instance.IsLocalBot(reviver);
            if (totem)
            {
                TotemRevive.Mark(target);
            }
            if (reviver == NetSession.LocalId)
            {
                if (!free)
                {
                    // Paid from the reviver's own wallet only.
                    CL_GameManager.AddRoaches(-Cost);
                }
                RunFlowNotice("Revived " + NetSession.NameOf(target) + (free ? " with a totem" : ""));
            }
            if (target != NetSession.LocalId || !WorldSpace.Ready)
            {
                return;
            }
            // The revive spot may not be built on this PC yet (shortcut, Hadal diver), so stay down until the
            // ground exists.
            pendingRevive = true;
            pendingSpawn = sharedSpawn;
            pendingReviver = reviver;
            pendingTotem = totem;
            pendingSince = Time.unscaledTime;
            TryFinishRevive();
        }

        private bool pendingRevive;
        private Vector3 pendingSpawn;
        private SteamId pendingReviver;
        private bool pendingTotem;
        private float pendingSince;
        private float nextPendingTry;

        /// <summary>How long a revive waits for the ground at its spot before it goes ahead anyway.</summary>
        private const float ReviveWaitLimit = 45f;

        private void TryFinishRevive()
        {
            if (!pendingRevive || !WorldSpace.Ready)
            {
                return;
            }
            ENT_Player player = ENT_Player.playerObject;
            if (player == null)
            {
                return;
            }
            if (!player.dead)
            {
                pendingRevive = false; // revived some other way meanwhile
                return;
            }
            Vector3 spot = WorldSpace.ToLocal(pendingSpawn);
            bool waitedTooLong = Time.unscaledTime - pendingSince > ReviveWaitLimit;
            if (!GroundReady(spot) && !waitedTooLong)
            {
                EnsureLevelAt(spot);
                return;
            }
            if (waitedTooLong && !GroundReady(spot))
            {
                Plugin.Log.LogWarning("The revive spot still isn't built here; getting up there anyway");
            }
            pendingRevive = false;
            Spectating = false;
            player.Revive();
            player.GetTagger()?.RemoveTag("Dead");
            World.TeethSync.CleanUpAfterRevive(player);
            EnsureLevelAt(spot);
            player.Teleport(spot);
            player.ResetFallingHeight();
            player.AddIgnoreFallDamageTime(5f);
            if (pendingTotem)
            {
                TotemRevive.PlayFirstPerson();
            }
            RunFlowNotice(NetSession.NameOf(pendingReviver) + " revived you");
        }

        /// <summary>A built, switched-on level holds the spot and there's solid ground under it.</summary>
        private static bool GroundReady(Vector3 spot)
        {
            M_Level level = World.WorldCatchUp.LevelAt(spot);
            if (level == null)
            {
                return false;
            }
            if (!level.gameObject.activeInHierarchy)
            {
                EnsureLevelAt(spot);
                return false;
            }
            foreach (RaycastHit hit in Physics.RaycastAll(spot + Vector3.up * 1f, Vector3.down, 30f, ~0, QueryTriggerInteraction.Ignore))
            {
                // The reviver is standing right there: their body isn't ground.
                if (hit.collider != null && hit.collider.GetComponentInParent<RemoteBody>() == null)
                {
                    return true;
                }
            }
            return false;
        }

        private static float lastNoLevelWarning = -100f;

        /// <summary>
        /// Makes the level at this spot active (switching branch if needed) so its terrain is loaded before
        /// placing someone there.
        /// </summary>
        internal static void EnsureLevelAt(Vector3 worldPos)
        {
            M_Level best = World.WorldCatchUp.LevelAt(worldPos);
            if (best == null)
            {
                // Not built here yet (the teammate walked past a spot that holds the building back):
                // build on until it is.
                World.WorldCatchUp.Nudge(worldPos);
                if (Time.unscaledTime - lastNoLevelWarning > 30f)
                {
                    lastNoLevelWarning = Time.unscaledTime;
                    Plugin.Log.LogWarning("No level built at the revive spot yet, building on");
                }
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
    /// When the run ends (all dead, no disk save), it must really end instead of falling into the disk revive.
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
        /// <summary>
        /// In a lobby, dying starts spectating instead of game over or disk revive, until the whole team is down.
        /// </summary>
        [HarmonyPrefix]
        private static bool Prefix(CL_GameManager __instance, string type)
        {
            if (ReviveSystem.AllowGameOver || ReviveSystem.AllowTeamReload || NetSession.Instance == null || !NetSession.Instance.InLobby)
            {
                return true;
            }
            __instance.localPlayer.KillEffects(type);
            ReviveSystem.Instance.OnLocalDeath(type);
            UI.DeathFeed.LocalDied(type);
            return false;
        }
    }
}
