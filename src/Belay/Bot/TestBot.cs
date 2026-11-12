using System;
using System.Collections.Generic;
using Belay.Net;
using Belay.Players;
using Belay.Revive;
using Steamworks;
using UnityEngine;

namespace Belay.Bot
{
    public enum BotMode
    {
        /// <summary>Retrace your own path a few seconds behind you.</summary>
        Follow,
        /// <summary>Stay put (optionally gripping the wall, so it anchors the rope).</summary>
        Hold,
    }

    /// <summary>One fake teammate. Lives only on this machine.</summary>
    public class BotPlayer
    {
        public SteamId Id;
        public string Name;
        public BotMode Mode = BotMode.Follow;
        public bool Anchored;
        public bool LookAtYou;
        public bool Dead;
        public float Health = TestBot.MaxHealth;
        public PlayerState State;
        public bool HasState;

        /// <summary>Shove from a hit, added on top of the path and fading out.</summary>
        public Vector3 KnockOffset;
        public Vector3 KnockVelocity;

        /// <summary>Seconds a closed door (or anything solid) has blocked its path.</summary>
        public float Blocked;

        /// <summary>Follow mode: the point in your recorded trail the bot is replaying.</summary>
        public float Playhead = -1f;

        /// <summary>Hazard triggers it's standing in, and when each one hurts it next.</summary>
        public readonly Dictionary<UT_DamageTrigger, float> Hazards = new Dictionary<UT_DamageTrigger, float>();

        /// <summary>When it was first seen outside a room you're waiting in (-1 when not).</summary>
        public float OutsideSince = -1f;
        public float OutsideChecked = -1f;
    }

    /// <summary>
    /// Test bots for solo multiplayer testing. A bot is a fake lobby member that runs inside your own game:
    /// it shows up as a teammate (body, rope, goo, revive, interlude gate) and climbs by retracing the path
    /// you took, a couple of seconds behind you. Its camera can be watched in a separate window (BotPov).
    /// Bots are never sent to other players, so they're for testing on your own.
    /// </summary>
    public class TestBot : MonoBehaviour
    {
        private const ulong BotIdBase = 0x00BE1A7000000000UL;
        private const float TrailStep = 0.05f;
        private const float TrailSeconds = 180f;
        private const float KeepDistance = 1.6f;
        private const float MinBotSpacing = 0.9f;
        private const float SpawnDistance = 1.6f;
        /// <summary>Same health pool as you (the game's player has 10), so hits hurt a bot like they'd hurt you.</summary>
        public static float MaxHealth => ENT_Player.playerObject != null ? ENT_Player.playerObject.maxHealth : 10f;

        /// <summary>How far behind you (along your path) a following bot walks.</summary>
        private const float FollowGap = 2.6f;

        private struct Sample
        {
            public float Time;
            public PlayerState State;
        }

        public static TestBot Instance { get; private set; }

        public readonly List<BotPlayer> Bots = new List<BotPlayer>();

        private readonly List<Sample> trail = new List<Sample>();
        private float trailTimer;
        private float sendTimer;
        private bool spawnWhenHosting;
        private int nextBotNumber = 1;
        private BotPov pov;
        private float testClientLaunched = -1f;
        private string testClientError;

        public BotPlayer Watched => pov != null ? pov.Watched : null;

        private void Awake()
        {
            Instance = this;
            pov = gameObject.AddComponent<BotPov>();
        }

        private void Start()
        {
            NetSession.Instance.OnLobbyChanged += HandleLobbyChanged;
            ReviveSystem.Revived += HandleRevived;
        }

        // ---------------------------------------------------------------- Spawning

        public void StartSoloTest()
        {
            NetSession net = NetSession.Instance;
            if (net.InLobby)
            {
                Spawn();
                return;
            }
            spawnWhenHosting = true;
            net.HostLobby("Bot test", LobbyPrivacy.Private);
        }

        public BotPlayer Spawn()
        {
            NetSession net = NetSession.Instance;
            if (!net.InLobby || net.MemberOrder().Count >= NetSession.MaxPlayers)
            {
                return null;
            }
            int number = nextBotNumber++;
            var bot = new BotPlayer
            {
                Id = new SteamId { Value = BotIdBase + (ulong)number },
                Name = "Bot " + number,
            };
            Bots.Add(bot);
            net.AddLocalBot(bot.Id, bot.Name);
            if (WorldSpace.Ready)
            {
                TeleportToYou(bot);
            }
            pov.Watch(bot);
            Plugin.Log.LogInfo($"Spawned test bot {bot.Name}");
            return bot;
        }

        public void Remove(BotPlayer bot)
        {
            Bots.Remove(bot);
            NetSession.Instance.RemoveLocalBot(bot.Id);
            if (pov.Watched == bot)
            {
                pov.Watch(Bots.Count > 0 ? Bots[0] : null);
            }
        }

        public void RemoveAll()
        {
            foreach (BotPlayer bot in new List<BotPlayer>(Bots))
            {
                Remove(bot);
            }
        }

        private void HandleLobbyChanged()
        {
            NetSession net = NetSession.Instance;
            if (!net.InLobby)
            {
                // Left the lobby: drop every bot (RemoveLocalBot raises this event again, so clear first).
                var gone = new List<BotPlayer>(Bots);
                Bots.Clear();
                foreach (BotPlayer bot in gone)
                {
                    net.RemoveLocalBot(bot.Id);
                }
                pov.Watch(null);
                return;
            }
            if (spawnWhenHosting && net.IsHost)
            {
                spawnWhenHosting = false;
                Spawn();
            }
        }

        // ---------------------------------------------------------------- Actions

        public void TeleportToYou(BotPlayer bot)
        {
            if (!WorldSpace.Ready)
            {
                return;
            }
            PlayerState you = PlayerSync.CaptureLocal();
            Vector3 offset = ClearSpotNear(WorldSpace.ToLocal(you.Position), you.Yaw);
            bot.State = Offset(you, offset);
            bot.KnockOffset = bot.KnockVelocity = Vector3.zero;
            bot.Blocked = 0f;
            bot.State.InventoryOpen = false;
            bot.State.Dead = bot.Dead;
            bot.State.LeftHanging = bot.State.RightHanging = bot.Anchored && bot.Mode == BotMode.Hold;
            bot.HasState = true;
            // Start replaying from now, never from a point on your path where you're still standing.
            bot.Playhead = trail.Count > 0 ? trail[trail.Count - 1].Time : float.MaxValue;
            Push(bot);
        }

        /// <summary>
        /// Teammates are solid, so a bot must never appear inside you or a wall. Try behind, beside and in
        /// front of you, then above.
        /// </summary>
        private Vector3 ClearSpotNear(Vector3 you, float yaw)
        {
            Quaternion facing = Quaternion.Euler(0f, yaw, 0f);
            Vector3[] tries =
            {
                facing * new Vector3(0f, 0f, -SpawnDistance),
                facing * new Vector3(-SpawnDistance, 0f, 0f),
                facing * new Vector3(SpawnDistance, 0f, 0f),
                facing * new Vector3(0f, 0f, SpawnDistance),
                new Vector3(0f, SpawnDistance + 0.5f, 0f),
            };
            int mask = ~((1 << Players.PlayerContact.BodyLayer) | (1 << ENT_Player.playerObject.gameObject.layer));
            foreach (Vector3 offset in tries)
            {
                Vector3 c = you + offset;
                if (!Physics.CheckCapsule(c - Vector3.up * 0.55f, c + Vector3.up * 0.55f, Players.RemoteBody.Radius + 0.05f, mask, QueryTriggerInteraction.Ignore))
                {
                    return offset;
                }
            }
            return tries[0];
        }

        /// <summary>True if a body at this shared position would touch you or another bot.</summary>
        private bool Crowded(BotPlayer bot, Vector3 shared, Vector3 you, bool youAlive)
        {
            if (youAlive && Vector3.Distance(shared, you) < KeepDistance)
            {
                return true;
            }
            foreach (BotPlayer other in Bots)
            {
                if (other != bot && other.HasState && !other.Dead && Vector3.Distance(shared, other.State.Position) < MinBotSpacing)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>A teammate punched or hammered this bot: shove it (a following bot walks back after).</summary>
        public void Knock(SteamId id, Vector3 velocity)
        {
            BotPlayer bot = Bots.Find(b => b.Id == id);
            if (bot == null || bot.Dead)
            {
                return;
            }
            bot.KnockVelocity += velocity;
        }

        /// <summary>
        /// Something other than a teammate hurt this bot (an enemy, a trap, a projectile). Bots have their
        /// own health and die like a player, then wait for a revive.
        /// </summary>
        public bool Hurt(SteamId id, float amount, string what)
        {
            BotPlayer bot = Bots.Find(b => b.Id == id);
            if (bot == null || bot.Dead || amount <= 0f)
            {
                return false;
            }
            bot.Health -= amount;
            Plugin.Log.LogInfo($"{bot.Name} took {amount:F1} damage from {what} ({Mathf.Max(0f, bot.Health):F1} left)");
            if (bot.Health <= 0f)
            {
                Kill(bot);
            }
            return true;
        }

        public bool IsBot(SteamId id) => Bots.Exists(b => b.Id == id);

        public void Kill(BotPlayer bot)
        {
            if (!bot.Dead)
            {
                bot.Dead = true;
                bot.State.Dead = true;
                Run.RunFlow.Instance?.ShowNotice(bot.Name + " died");
                Push(bot);
            }
        }

        private void HandleRevived(SteamId target, SteamId reviver, Vector3 sharedSpawn)
        {
            BotPlayer bot = Bots.Find(b => b.Id == target);
            if (bot == null)
            {
                return;
            }
            bot.Dead = false;
            bot.Health = MaxHealth;
            bot.KnockOffset = bot.KnockVelocity = Vector3.zero;
            bot.Blocked = 0f;
            bot.State.Dead = false;
            bot.State.Position = sharedSpawn;
            bot.State.LeftHand = sharedSpawn + new Vector3(-0.3f, 0.2f, 0.3f);
            bot.State.RightHand = sharedSpawn + new Vector3(0.3f, 0.2f, 0.3f);
            bot.HasState = true;
            // Pick up the trail from where you are now instead of replaying the climb it missed.
            bot.Playhead = trail.Count > 0 ? trail[trail.Count - 1].Time : -1f;
            Push(bot);
        }

        // ---------------------------------------------------------------- Simulation

        private void Update()
        {
            if (Input.GetKeyDown(Plugin.BotPovKey.Value))
            {
                pov.Toggle();
            }
            NetSession net = NetSession.Instance;
            if (Bots.Count == 0 || !net.InLobby)
            {
                trail.Clear();
                return;
            }
            if (!WorldSpace.Ready)
            {
                trail.Clear();
                foreach (BotPlayer bot in Bots)
                {
                    bot.HasState = false;
                    bot.Playhead = -1f;
                }
                return;
            }

            // Paused: bots hold still (your camera can wander in menus) but keep reporting where they are.
            bool paused = CL_GameManager.gMan != null && CL_GameManager.gMan.isPaused;
            if (!paused)
            {
                Simulate();
            }

            sendTimer -= Time.unscaledDeltaTime;
            if (sendTimer <= 0f)
            {
                sendTimer = 1f / Mathf.Max(1, Plugin.SendRate.Value);
                foreach (BotPlayer bot in Bots)
                {
                    Push(bot);
                }
            }
        }

        private void Simulate()
        {
            Record();

            Vector3 you = WorldSpace.ToShared(ENT_Player.playerObject.transform.position);
            bool youAlive = !ENT_Player.playerObject.dead;
            for (int i = 0; i < Bots.Count; i++)
            {
                BotPlayer bot = Bots[i];
                if (!bot.HasState)
                {
                    TeleportToYou(bot);
                }
                if (!bot.Dead)
                {
                    // Work on the bot's path position; a shove from a hit rides on top of it.
                    PlayerState path = Offset(bot.State, -bot.KnockOffset);
                    if (bot.Mode == BotMode.Follow)
                    {
                        StepFollow(bot, ref path, you, youAlive);
                    }
                    else
                    {
                        path.LeftHanging = path.RightHanging = bot.Anchored;
                    }
                    UpdateKnock(bot, ref path);
                    bool dead = bot.State.Dead;
                    bot.State = Offset(path, bot.KnockOffset);
                    bot.State.Dead = dead;
                    if (bot.LookAtYou && youAlive)
                    {
                        Face(bot, you);
                    }
                    // A bot copies your moves, not your menus.
                    bot.State.InventoryOpen = false;
                    CheckGoo(bot);
                    CheckHazards(bot);
                    // Slow regeneration, like yours.
                    bot.Health = Mathf.Min(MaxHealth, bot.Health + Time.deltaTime * 0.15f);
                }
            }
        }

        private void Push(BotPlayer bot)
        {
            if (bot.HasState)
            {
                NetSession.Instance.InjectLocalBotState(bot.Id, bot.State);
            }
        }

        private void Record()
        {
            ENT_Player player = ENT_Player.playerObject;
            // While you're dead the game moves your body around for spectating; don't let bots follow that.
            if (player.dead)
            {
                return;
            }
            trailTimer -= Time.deltaTime;
            if (trailTimer > 0f)
            {
                return;
            }
            trailTimer = TrailStep;
            trail.Add(new Sample { Time = Time.time, State = PlayerSync.CaptureLocal() });
            int drop = 0;
            while (drop < trail.Count && Time.time - trail[drop].Time > TrailSeconds)
            {
                drop++;
            }
            if (drop > 0)
            {
                trail.RemoveRange(0, drop);
            }
        }

        private void StepFollow(BotPlayer bot, ref PlayerState path, Vector3 you, bool youAlive)
        {
            int place = Bots.IndexOf(bot) + 1;
            if (trail.Count == 0)
            {
                return;
            }
            float oldest = trail[0].Time;
            float newest = trail[trail.Count - 1].Time;
            if (bot.Playhead == float.MaxValue)
            {
                // Just placed next to you: pick up the trail from here on.
                bot.Playhead = newest;
            }
            else if (bot.Playhead < oldest)
            {
                bot.Playhead = oldest;
            }

            // Walk your path at your pace, a fixed distance behind you. It speeds up the further behind it
            // is (up to 3x) and eases off as it closes in, so it never stops and starts.
            float target = TimeAtPathDistance(FollowGap * place);
            float behind = target - bot.Playhead;
            if (behind <= 0f)
            {
                return;
            }
            float rate = Mathf.Clamp(0.6f + behind * 0.6f, 0.6f, 3f);
            float next = Mathf.Min(target, bot.Playhead + Time.deltaTime * rate);
            PlayerState candidate = SampleAt(next);
            if (Crowded(bot, candidate.Position, you, youAlive))
            {
                // Bodies are solid: wait where it is rather than step into you (that launches you).
                return;
            }
            if (PathBlocked(path.Position, candidate.Position))
            {
                // A blast door (or anything solid) closed after you went through: it's shut out, like a
                // player would be. "Bring to me" fetches it.
                bot.Blocked += Time.deltaTime;
                return;
            }
            bot.Blocked = 0f;
            bot.Playhead = next;
            path = candidate;
        }

        /// <summary>The trail time that lies this far (in meters along your path) behind your newest point.</summary>
        private float TimeAtPathDistance(float meters)
        {
            float walked = 0f;
            for (int i = trail.Count - 1; i > 0; i--)
            {
                float step = Vector3.Distance(trail[i].State.Position, trail[i - 1].State.Position);
                if (step > 3f)
                {
                    // A teleport (revive, elevator, world shift): don't count it as walking.
                    step = 0f;
                }
                walked += step;
                if (walked >= meters)
                {
                    return trail[i - 1].Time;
                }
            }
            return trail[0].Time;
        }

        /// <summary>True if something solid that isn't a player sits between two points on the path.</summary>
        private static bool PathBlocked(Vector3 fromShared, Vector3 toShared)
        {
            Vector3 from = WorldSpace.ToLocal(fromShared);
            Vector3 to = WorldSpace.ToLocal(toShared);
            Vector3 delta = to - from;
            float length = delta.magnitude;
            if (length < 0.01f || length > 3f)
            {
                return false;
            }
            foreach (RaycastHit hit in Physics.RaycastAll(from, delta / length, length, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                Collider c = hit.collider;
                if (c == null || RemoteBody.Of(c) != null || c.GetComponentInParent<ENT_Player>() != null)
                {
                    continue;
                }
                return true;
            }
            return false;
        }

        private static void UpdateKnock(BotPlayer bot, ref PlayerState path)
        {
            if (bot.KnockVelocity.sqrMagnitude < 0.0001f && bot.KnockOffset.sqrMagnitude < 0.0001f)
            {
                bot.KnockOffset = bot.KnockVelocity = Vector3.zero;
                return;
            }
            float dt = Time.deltaTime;
            Vector3 move = bot.KnockVelocity * dt;
            bot.KnockVelocity = Vector3.Lerp(bot.KnockVelocity, Vector3.zero, dt * 6f);
            if (bot.Mode == BotMode.Hold)
            {
                // A holding bot stays where it was pushed.
                path = Offset(path, move);
                bot.KnockOffset = Vector3.zero;
            }
            else
            {
                // A following bot drifts back onto your path after the shove.
                bot.KnockOffset = Vector3.Lerp(bot.KnockOffset + move, Vector3.zero, dt * 1.5f);
            }
        }

        private PlayerState SampleAt(float time)
        {
            int hi = trail.Count - 1;
            if (time >= trail[hi].Time)
            {
                return trail[hi].State;
            }
            int lo = 0;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (trail[mid].Time <= time)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }
            Sample a = trail[lo];
            Sample b = trail[hi];
            float t = b.Time > a.Time ? Mathf.Clamp01((time - a.Time) / (b.Time - a.Time)) : 0f;
            // Big jumps (teleports, world offset shifts) snap instead of sliding through walls.
            if ((b.State.Position - a.State.Position).sqrMagnitude > 9f)
            {
                return t < 0.5f ? a.State : b.State;
            }
            PlayerState s = a.State;
            s.Position = Vector3.Lerp(a.State.Position, b.State.Position, t);
            s.LeftHand = Vector3.Lerp(a.State.LeftHand, b.State.LeftHand, t);
            s.RightHand = Vector3.Lerp(a.State.RightHand, b.State.RightHand, t);
            s.Yaw = Mathf.LerpAngle(a.State.Yaw, b.State.Yaw, t);
            s.Pitch = Mathf.LerpAngle(a.State.Pitch, b.State.Pitch, t);
            return s;
        }

        private static void Face(BotPlayer bot, Vector3 target)
        {
            Vector3 eye = bot.State.Position + Vector3.up * BotPov.EyeHeight;
            Vector3 dir = target - eye;
            if (dir.sqrMagnitude < 0.01f)
            {
                return;
            }
            Quaternion look = Quaternion.LookRotation(dir);
            bot.State.Yaw = look.eulerAngles.y;
            bot.State.Pitch = look.eulerAngles.x;
        }

        private void CheckGoo(BotPlayer bot)
        {
            DEN_DeathFloor goo = DEN_DeathFloor.instance;
            if (goo == null || !goo.IsActive())
            {
                return;
            }
            float gooY = WorldSpace.ToShared(((Component)goo).transform.position).y;
            if (bot.State.Position.y < gooY)
            {
                Kill(bot);
            }
        }

        private static readonly Collider[] overlap = new Collider[48];
        private static readonly HashSet<UT_DamageTrigger> touching = new HashSet<UT_DamageTrigger>();

        /// <summary>
        /// The game's hazard volumes (damage triggers, instant-death zones) only react to the real player, so
        /// check the bot's body against them here and hurt it the same way they'd hurt you.
        /// </summary>
        private void CheckHazards(BotPlayer bot)
        {
            if (bot.Dead || !PlayerSync.Instance.Remotes.TryGetValue(bot.Id, out RemotePlayer remote) || remote == null)
            {
                bot.Hazards.Clear();
                return;
            }
            Vector3 c = remote.transform.position;
            float half = RemoteBody.Height * 0.5f - RemoteBody.Radius;
            int n = Physics.OverlapCapsuleNonAlloc(c - Vector3.up * half, c + Vector3.up * half, RemoteBody.Radius, overlap,
                Physics.AllLayers, QueryTriggerInteraction.Collide);
            touching.Clear();
            for (int i = 0; i < n; i++)
            {
                Collider col = overlap[i];
                if (col == null || !col.isTrigger)
                {
                    continue;
                }
                UT_InstantDeath death = col.GetComponent<UT_InstantDeath>();
                if (death != null && death.isActiveAndEnabled && HitsPlayers(death.triggerTags))
                {
                    Plugin.Log.LogInfo($"{bot.Name} hit an instant-death zone ({col.name})");
                    Kill(bot);
                    bot.Hazards.Clear();
                    return;
                }
                UT_DamageTrigger damage = col.GetComponent<UT_DamageTrigger>();
                if (damage == null || !damage.isActiveAndEnabled || !HitsPlayers(damage.triggerTags) || !touching.Add(damage))
                {
                    continue;
                }
                bool entered = !bot.Hazards.TryGetValue(damage, out float next);
                if (entered || Time.time >= next)
                {
                    bot.Hazards[damage] = damage.damageOverTime ? Time.time + Mathf.Max(0.1f, damage.damageRate) : float.MaxValue;
                    float amount = damage.damageWhenMoving && damage.minimumDamage > 0f ? damage.minimumDamage : damage.damage;
                    Hurt(bot.Id, amount, col.name);
                    if (bot.Dead)
                    {
                        bot.Hazards.Clear();
                        return;
                    }
                }
            }
            if (bot.Hazards.Count > touching.Count)
            {
                var left = new List<UT_DamageTrigger>();
                foreach (UT_DamageTrigger t in bot.Hazards.Keys)
                {
                    if (!touching.Contains(t))
                    {
                        left.Add(t);
                    }
                }
                foreach (UT_DamageTrigger t in left)
                {
                    bot.Hazards.Remove(t);
                }
            }
        }

        private static bool HitsPlayers(string[] tags)
        {
            if (tags == null)
            {
                return false;
            }
            foreach (string t in tags)
            {
                if (t == "Player")
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// A room that waits for the whole team (interludes, the ascent) counts a bot as there once you're
        /// inside: a bot can't ride an elevator or get past a door that shut behind you on its own, so if it's
        /// still outside a few seconds after you, it's brought in next to you.
        /// </summary>
        public void BringAlong(SteamId id, Func<Vector3, bool> insideLocal)
        {
            BotPlayer bot = Bots.Find(b => b.Id == id);
            if (bot == null || bot.Dead || !bot.HasState || !WorldSpace.Ready)
            {
                return;
            }
            if (Time.time - bot.OutsideChecked > 0.5f)
            {
                // First check in a while (a different room, or you just arrived): start the wait over.
                bot.OutsideSince = -1f;
            }
            bot.OutsideChecked = Time.time;
            if (insideLocal(WorldSpace.ToLocal(bot.State.Position)))
            {
                bot.OutsideSince = -1f;
                return;
            }
            if (bot.OutsideSince < 0f)
            {
                bot.OutsideSince = Time.time;
            }
            else if (Time.time - bot.OutsideSince > 3f)
            {
                bot.OutsideSince = -1f;
                TeleportToYou(bot);
            }
        }

        private static PlayerState Offset(PlayerState s, Vector3 by)
        {
            s.Position += by;
            s.LeftHand += by;
            s.RightHand += by;
            return s;
        }

        // ---------------------------------------------------------------- Test client

        /// <summary>Start a second copy of the game that joins this lobby over the local link.</summary>
        public void LaunchTestClient()
        {
            testClientError = null;
            NetSession net = NetSession.Instance;
            if (!net.AllowTestClient())
            {
                testClientError = "Couldn't open the local port for the test client (is another game already using it?).";
                return;
            }
            try
            {
                string exe = BepInEx.Paths.ExecutablePath;
                var start = new System.Diagnostics.ProcessStartInfo(exe,
                    LoopbackLink.CommandLineFlag + " -screen-fullscreen 0 -window-mode windowed -screen-width 960 -screen-height 540")
                {
                    UseShellExecute = false,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(exe),
                };
                System.Diagnostics.Process.Start(start);
                testClientLaunched = Time.unscaledTime;
                Plugin.Log.LogInfo("Started a Belay test client: " + exe);
            }
            catch (System.Exception e)
            {
                testClientError = "Couldn't start the test client: " + e.Message;
                Plugin.Log.LogWarning(testClientError);
            }
        }

        // ---------------------------------------------------------------- Menu tab

        public void DrawTab()
        {
            NetSession net = NetSession.Instance;
            if (net.IsTestClient)
            {
                GUILayout.Label("This window is the test client. It follows the host automatically; press "
                    + Plugin.TestClientAutopilotKey.Value + " to take over and play it yourself.");
                return;
            }
            GUILayout.Label("Test bots are fake teammates that only exist in your game. They retrace your climb "
                + "a few seconds behind you, so you can test the rope, goo, doors, revives and the interlude gate solo.");
            GUILayout.Space(4);

            if (!NetSession.SteamReady)
            {
                GUILayout.Label("Waiting for Steam...");
                return;
            }
            if (!net.InLobby)
            {
                if (GUILayout.Button(spawnWhenHosting ? "Creating a private lobby..." : "Start solo test (private lobby + 1 bot)"))
                {
                    StartSoloTest();
                }
                return;
            }
            if (!net.IsHost)
            {
                GUILayout.Label("Bots can only be added by the host.");
                return;
            }

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("<b>Test client (real second player)</b>");
            GUILayout.Label("Opens a second copy of the game in a window. It joins this lobby as a real player with its "
                + "own world, so anything that looks different in that window is a desync. It flies along your path "
                + "by itself (" + Plugin.TestClientAutopilotKey.Value + " in that window to play it yourself) and never "
                + "touches your saves.");
            if (net.Loopback.PeerAlive)
            {
                GUILayout.Label("Test client connected.");
            }
            else if (testClientLaunched > 0f && Time.unscaledTime - testClientLaunched < 90f)
            {
                GUILayout.Label("Starting the test client... (the second window can take a minute to load)");
            }
            if (!net.Loopback.PeerAlive && GUILayout.Button("Open test client window"))
            {
                LaunchTestClient();
            }
            if (testClientError != null)
            {
                GUILayout.Label(testClientError);
            }
            GUILayout.EndVertical();
            GUILayout.Space(6);
            GUILayout.Label("<b>Simple bots (inside this game)</b>");
            int real = 0;
            foreach (Friend _ in net.Members)
            {
                real++;
            }
            if (real > 1)
            {
                GUILayout.Label("Note: other players can't see your bots.");
            }

            GUILayout.BeginHorizontal();
            GUI.enabled = net.MemberOrder().Count < NetSession.MaxPlayers;
            if (GUILayout.Button("Add bot"))
            {
                Spawn();
            }
            GUI.enabled = Bots.Count > 0;
            if (GUILayout.Button("Remove all"))
            {
                RemoveAll();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(pov.Visible ? "Hide POV window" : "Show POV window", GUILayout.Width(160)))
            {
                pov.Toggle();
            }
            GUILayout.Label($"[{Plugin.BotPovKey.Value}] toggles it. Drag it while this menu is open.");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("POV quality", GUILayout.Width(90));
            int quality = GUILayout.Toolbar(pov.Quality, BotPov.QualityLabels);
            if (quality != pov.Quality)
            {
                pov.Quality = quality;
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Want it on another monitor? Use the test client window above and drag it there.");

            GUILayout.Space(6);
            foreach (BotPlayer bot in new List<BotPlayer>(Bots))
            {
                DrawBot(bot);
            }
        }

        private void DrawBot(BotPlayer bot)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            string status = bot.Dead ? "dead (revive at a station)"
                : bot.Blocked > 1f ? "blocked by a door (Bring to me)"
                : bot.Mode == BotMode.Follow ? "following" : bot.Anchored ? "holding on" : "standing";
            if (!bot.Dead)
            {
                status += $", health {Mathf.CeilToInt(bot.Health / Mathf.Max(0.01f, MaxHealth) * 100f)}%";
            }
            GUILayout.Label($"{bot.Name}: {status}");
            if (pov.Watched != bot && GUILayout.Button("Watch", GUILayout.Width(60)))
            {
                pov.Watch(bot);
                pov.Show();
            }
            if (GUILayout.Button("X", GUILayout.Width(28)))
            {
                Remove(bot);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(bot.Mode == BotMode.Follow, "Follow", GUI.skin.button))
            {
                if (bot.Mode != BotMode.Follow)
                {
                    bot.Mode = BotMode.Follow;
                    bot.Playhead = -1f;
                }
            }
            if (GUILayout.Toggle(bot.Mode == BotMode.Hold, "Hold here", GUI.skin.button))
            {
                bot.Mode = BotMode.Hold;
            }
            GUI.enabled = bot.Mode == BotMode.Hold;
            bot.Anchored = GUILayout.Toggle(bot.Anchored, "Grip wall");
            GUI.enabled = true;
            bot.LookAtYou = GUILayout.Toggle(bot.LookAtYou, "Look at you");
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Bring to me"))
            {
                TeleportToYou(bot);
            }
            GUI.enabled = !bot.Dead;
            if (GUILayout.Button("Kill"))
            {
                Kill(bot);
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }
    }
}
