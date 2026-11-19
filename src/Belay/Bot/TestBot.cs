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
        /// <summary>Climb with you on its own: its own body, holds, grip and pace.</summary>
        Climb,
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
        public BotMode Mode = BotMode.Climb;
        public BotStyle Style = BotStyle.Steady;

        /// <summary>Climb mode: its body, and where it is along the way you went.</summary>
        public BotClimber Climber;
        public int RouteIndex;
        public float Stuck;
        public int ProgressIndex;
        public float ProgressDistance = float.MaxValue;
        public int FailedHops;
        /// <summary>A point further along your way that it can walk straight to, skipping your detours.</summary>
        public int Shortcut = -1;
        public float ShortcutCheck;
        public string Activity = "";

        /// <summary>You're down: where it's heading to bring you back.</summary>
        public bool HasReviveGoal;
        public Vector3 ReviveGoal;
        public bool ReviveAtTerminal;
        public float ReviveSearch;
        public float ReviveRunTime;

        /// <summary>Who it climbs with and revives: you (default) or another teammate.</summary>
        public SteamId FollowId;

        /// <summary>What it carries (see BotItems).</summary>
        public readonly List<Item> Items = new List<Item>();
        public bool PickUpItems = true;
        public float ItemScan;
        public string EatingItem;
        public float EatingTimer;

        /// <summary>The creature it's reacting to, if any (see BotThreats).</summary>
        public string Threat;
        public float StrikeTimer;

        /// <summary>A barnacle has it (see BotThreats).</summary>
        public DEN_Barnacle GrabbedBarnacle;
        public int BarnacleHits;
        public float BiteTimer;
        public float GrabCooldown;
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
    /// Bots: AI teammates that exist only on this PC but count as full teammates. Climb mode moves its own body
    /// (BotClimber) and revives you at terminals; Retrace mode replays your path. Watch it with BotPov.
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

        /// <summary>Where you've been (shared space), a point every CrumbSpacing meters: the way up a climbing bot takes.</summary>
        private readonly Dictionary<ulong, List<Vector3>> routes = new Dictionary<ulong, List<Vector3>>();
        /// <summary>The route of the bot being worked on right now (its leader's crumbs).</summary>
        private List<Vector3> crumbs = new List<Vector3>();
        private const float CrumbSpacing = 0.8f;
        private const int MaxCrumbs = 900;
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
            if (WorldSpace.Ready && !bot.Dead)
            {
                BotItems.DropAll(bot);
            }
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
            PlaceClimber(bot, bot.State.Position, you.Yaw);
            bot.RouteIndex = 0;
            bot.ProgressIndex = 0;
            Push(bot);
        }

        private void PlaceClimber(BotPlayer bot, Vector3 shared, float yaw)
        {
            if (bot.Climber == null)
            {
                bot.Climber = new BotClimber(bot.Id);
                SteamId id = bot.Id;
                bot.Climber.Hurt = (amount, what) => Hurt(id, amount, what, "falling");
                bot.Climber.MakeHold = (shoulder, goal, into) =>
                {
                    BotPlayer b = Bots.Find(x => x.Id == id);
                    return b != null && BotTools.MakeHold(b, shoulder, goal, into);
                };
                bot.Climber.Kill = what =>
                {
                    BotPlayer b = Bots.Find(x => x.Id == id);
                    if (b != null)
                    {
                        Plugin.Log.LogInfo($"{b.Name} died: {what}");
                        Kill(b, what == "a fall" ? "falling" : null);
                    }
                };
            }
            bot.Climber.Place(shared, yaw);
            bot.Stuck = 0f;
            bot.FailedHops = 0;
            bot.ProgressIndex = bot.RouteIndex;
            bot.ProgressDistance = float.MaxValue;
            bot.Shortcut = -1;
        }

        /// <summary>
        /// Teammates are solid, so try spots behind, beside, in front of and above you until one is clear.
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
            foreach (Vector3 offset in tries)
            {
                if (SpotIsClear(you + offset))
                {
                    return offset;
                }
            }
            return tries[0];
        }

        private static readonly Collider[] spotOverlap = new Collider[32];

        /// <summary>A body-sized space here (world space of this PC) touches nothing solid except bots' bodies.</summary>
        private static bool SpotIsClear(Vector3 c)
        {
            int n = Physics.OverlapCapsuleNonAlloc(c - Vector3.up * 0.55f, c + Vector3.up * 0.55f, RemoteBody.Radius + 0.05f, spotOverlap,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                Collider col = spotOverlap[i];
                if (col != null && RemoteBody.Of(col) == null)
                {
                    return false;
                }
            }
            return true;
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
            if (bot.Mode == BotMode.Climb && bot.Climber != null)
            {
                bot.Climber.Knock(velocity);
                return;
            }
            bot.KnockVelocity += velocity;
        }

        /// <summary>
        /// Non-teammate damage (enemy, trap, projectile). Bots die like players and wait for a revive.
        /// </summary>
        public bool Hurt(SteamId id, float amount, string what, string deathType = null)
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
                Kill(bot, deathType);
            }
            return true;
        }

        public bool IsBot(SteamId id) => Bots.Exists(b => b.Id == id);

        public void Kill(BotPlayer bot, string deathType = null)
        {
            if (!bot.Dead)
            {
                bot.Dead = true;
                bot.State.Dead = true;
                UI.DeathFeed.Instance?.Show(bot.Id, deathType);
                BotItems.DropAll(bot);
                bot.EatingItem = null;
                bot.GrabbedBarnacle = null;
                if (bot.Climber != null)
                {
                    bot.Climber.GrabbedBy = null;
                }
                Push(bot);
            }
        }

        private void HandleRevived(SteamId target, SteamId reviver, Vector3 sharedSpawn)
        {
            // Someone's back up: their way on starts again from where they are.
            RouteOf(target).Clear();
            foreach (BotPlayer b in Bots)
            {
                if (LeaderId(b) == target)
                {
                    b.RouteIndex = 0;
                    b.ProgressIndex = 0;
                    b.HasReviveGoal = false;
                }
            }
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
            PlaceClimber(bot, sharedSpawn, bot.State.Yaw);
            bot.RouteIndex = 0;
            bot.ProgressIndex = 0;
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
            RecordTeammates();

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
                    if (bot.Mode == BotMode.Climb)
                    {
                        try
                        {
                            StepClimb(bot, you, youAlive);
                        }
                        catch (Exception e)
                        {
                            // Never let a climbing bug break your game: this bot goes back to retracing your path.
                            Plugin.Log.LogError($"{bot.Name}'s climbing failed, switching it to Retrace: {e}");
                            bot.Mode = BotMode.Follow;
                            bot.Playhead = -1f;
                            continue;
                        }
                        CheckGoo(bot);
                        CheckHazards(bot);
                        bot.Health = Mathf.Min(MaxHealth, bot.Health + Time.deltaTime * 0.15f);
                        continue;
                    }
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
            RecordCrumb(NetSession.LocalId, WorldSpace.ToShared(player.transform.position));
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

        // ---------------------------------------------------------------- Climb mode

        private List<Vector3> RouteOf(SteamId id)
        {
            if (!routes.TryGetValue(id.Value, out List<Vector3> list))
            {
                routes[id.Value] = list = new List<Vector3>();
            }
            return list;
        }

        /// <summary>Who this bot climbs with: its chosen teammate while they're in the game, otherwise you.</summary>
        private SteamId LeaderId(BotPlayer bot)
        {
            if (bot.FollowId.Value == 0 || bot.FollowId == NetSession.LocalId || IsBot(bot.FollowId))
            {
                return NetSession.LocalId;
            }
            if (PlayerSync.Instance.Remotes.TryGetValue(bot.FollowId, out RemotePlayer r) && r != null
                && Time.unscaledTime - r.LastReceived < 5f)
            {
                return bot.FollowId;
            }
            return NetSession.LocalId;
        }

        private bool LeaderState(BotPlayer bot, out SteamId id, out Vector3 shared, out bool alive)
        {
            id = LeaderId(bot);
            if (id == NetSession.LocalId)
            {
                shared = WorldSpace.ToShared(ENT_Player.playerObject.transform.position);
                alive = !ENT_Player.playerObject.dead;
                return true;
            }
            RemotePlayer r = PlayerSync.Instance.Remotes[id];
            shared = r.Latest.Position;
            alive = !r.Latest.Dead;
            return true;
        }

        private void RecordCrumb(SteamId id, Vector3 at)
        {
            List<Vector3> list = RouteOf(id);
            if (list.Count > 0 && Vector3.Distance(list[list.Count - 1], at) < CrumbSpacing)
            {
                return;
            }
            list.Add(at);
            if (list.Count > MaxCrumbs)
            {
                int drop = list.Count - MaxCrumbs;
                list.RemoveRange(0, drop);
                foreach (BotPlayer bot in Bots)
                {
                    if (LeaderId(bot) == id)
                    {
                        bot.RouteIndex = Mathf.Max(0, bot.RouteIndex - drop);
                        bot.ProgressIndex = Mathf.Max(0, bot.ProgressIndex - drop);
                        bot.Shortcut = bot.Shortcut >= drop ? bot.Shortcut - drop : -1;
                    }
                }
            }
        }

        /// <summary>Teammates' ways up, for bots that climb with them.</summary>
        private void RecordTeammates()
        {
            foreach (KeyValuePair<SteamId, RemotePlayer> pair in PlayerSync.Instance.Remotes)
            {
                RemotePlayer r = pair.Value;
                if (r == null || IsBot(pair.Key) || r.Latest.Dead || Time.unscaledTime - r.LastReceived > 2f)
                {
                    continue;
                }
                RecordCrumb(pair.Key, r.Latest.Position);
            }
        }

        private static int GapCrumbs(BotStyle style) => style == BotStyle.Careful ? 5 : style == BotStyle.Bold ? 2 : 3;

        /// <summary>
        /// One frame of a climbing bot: follows your route a few meters behind, or heads for a terminal to revive
        /// you when you're down.
        /// </summary>
        private void StepClimb(BotPlayer bot, Vector3 yourShared, bool youAreAlive)
        {
            BotClimber c = bot.Climber;
            if (c == null)
            {
                PlaceClimber(bot, bot.State.Position, bot.State.Yaw);
                c = bot.Climber;
            }
            // "You" below is whoever it climbs with.
            LeaderState(bot, out SteamId leader, out Vector3 youShared, out bool youAlive);
            crumbs = RouteOf(leader);
            Vector3 me = WorldSpace.ToLocal(c.Shared);
            Vector3 goal;
            bool idle;
            if (youAlive)
            {
                bot.HasReviveGoal = false;
                idle = !RouteGoal(bot, me, youShared, out goal);
            }
            else
            {
                idle = !ReviveGoalFor(bot, me, out goal);
                bot.ReviveRunTime += Time.deltaTime;
                if (!idle && ReachedRevive(bot, leader, me, goal, bot.ReviveRunTime > 75f))
                {
                    idle = true;
                }
            }
            if (youAlive)
            {
                bot.ReviveRunTime = 0f;
            }

            bool alarmed = BotThreats.Step(bot);
            // Something's close: it hurries on, whatever its usual pace.
            c.Step(Time.deltaTime, goal, alarmed ? BotStyle.Bold : bot.Style, idle);
            if (bot.Dead)
            {
                return;
            }
            BotItems.Step(bot, youAlive);
            me = WorldSpace.ToLocal(c.Shared);
            TrackProgress(bot, me, goal, idle, youAlive, leader, youShared);
            string whom = leader == NetSession.LocalId ? "you" : NetSession.NameOf(leader);
            bot.Activity = !youAlive ? (bot.HasReviveGoal ? (bot.ReviveAtTerminal ? "heading for a terminal to revive " + whom : "climbing to the next room to revive " + whom) : "looking for a way to revive " + whom)
                : bot.EatingItem != null ? "eating"
                : c.Grabbed ? "grabbed by a barnacle, fighting it"
                : alarmed ? "hurrying away from " + bot.Threat
                : c.Mantling ? "pulling up"
                : c.Hanging ? (c.GripFraction < 0.3f ? "climbing, grip running out" : "climbing")
                : c.Falling ? "falling"
                : idle ? "waiting for " + whom : "walking";

            // Report it like a player.
            bool dead = bot.State.Dead;
            bot.State.Position = c.Shared;
            bot.State.Yaw = c.Yaw;
            bot.State.Pitch = c.Pitch;
            bot.State.LeftHand = WorldSpace.ToShared(c.Hands[0].Shown);
            bot.State.RightHand = WorldSpace.ToShared(c.Hands[1].Shown);
            bot.State.LeftHanging = c.Hands[0].Holding;
            bot.State.RightHanging = c.Hands[1].Holding;
            bot.State.Crouched = false;
            bot.State.InventoryOpen = false;
            bot.State.LeftItem = null;
            bot.State.RightItem = bot.EatingItem;
            bot.State.HeldTarget = 0;
            bot.State.Dead = dead;
            if (bot.LookAtYou && youAlive && !c.Hanging)
            {
                Face(bot, youShared);
            }
        }

        /// <summary>The next point along your way that it's heading for; false when it's close enough behind you.</summary>
        private bool RouteGoal(BotPlayer bot, Vector3 me, Vector3 youShared, out Vector3 goal)
        {
            goal = me;
            if (crumbs.Count == 0)
            {
                return false;
            }
            Vector3 meShared = WorldSpace.ToShared(me);
            // Where along the way is it? Look a little ahead of where it was; if it's wandered far off, look everywhere.
            int start = Mathf.Clamp(bot.RouteIndex, 0, crumbs.Count - 1);
            int best = start;
            float bestDistance = Vector3.Distance(crumbs[start], meShared);
            // Heading for a shortcut: look as far as that too, so it picks up the way again there.
            int window = Mathf.Max(start + 12, bot.Shortcut + 3);
            for (int i = start; i < Mathf.Min(crumbs.Count, window); i++)
            {
                float d = Vector3.Distance(crumbs[i], meShared);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = i;
                }
            }
            if (bestDistance > 8f)
            {
                for (int i = 0; i < crumbs.Count; i++)
                {
                    float d = Vector3.Distance(crumbs[i], meShared);
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = i;
                    }
                }
                // Your way crosses itself when you walk back and forth: take the latest pass by here, not the first.
                for (int i = crumbs.Count - 1; i > best; i--)
                {
                    if (Vector3.Distance(crumbs[i], meShared) < bestDistance + 1f)
                    {
                        best = i;
                        bestDistance = Vector3.Distance(crumbs[i], meShared);
                        break;
                    }
                }
            }
            bot.RouteIndex = best;

            int place = Bots.IndexOf(bot);
            int stopAt = crumbs.Count - 1 - GapCrumbs(bot.Style) - place * 2;
            if (best >= stopAt || Vector3.Distance(meShared, youShared) < KeepDistance + 0.6f)
            {
                return false;
            }
            int next = Mathf.Min(best + 2, stopAt);
            // You were moved (an elevator, a revive, a shortcut ride): it can't walk that, so it rides along too.
            for (int i = best; i < next; i++)
            {
                if (Vector3.Distance(crumbs[i], crumbs[i + 1]) > 4f)
                {
                    if (i == best && bestDistance < 1.5f)
                    {
                        Vector3 spot = WorldSpace.ToLocal(crumbs[i + 1]);
                        bot.Climber.Place(WorldSpace.ToShared(spot + ClearSpotNear(spot, bot.Climber.Yaw)), bot.Climber.Yaw);
                        bot.RouteIndex = i + 1;
                        return false;
                    }
                    next = i;
                    break;
                }
            }
            int ahead = FindShortcut(bot, me, best, stopAt);
            if (ahead > next)
            {
                next = ahead;
            }
            goal = WorldSpace.ToLocal(crumbs[next]);
            return Vector3.Distance(goal, me) > 0.3f;
        }

        private const int ShortcutLookahead = 30;

        /// <summary>
        /// Furthest point on your path it can walk straight to, so it skips your detours. -1 when none.
        /// </summary>
        private int FindShortcut(BotPlayer bot, Vector3 me, int best, int stopAt)
        {
            BotClimber c = bot.Climber;
            if (!c.Grounded || c.Hanging || c.Mantling)
            {
                bot.Shortcut = -1;
                return -1;
            }
            if (bot.Shortcut > best && bot.Shortcut <= stopAt && Time.time < bot.ShortcutCheck)
            {
                return bot.Shortcut;
            }
            bot.ShortcutCheck = Time.time + 0.3f;
            bot.Shortcut = -1;
            int last = Mathf.Min(stopAt, best + ShortcutLookahead);
            // Not past a ride (an elevator, a revive): that part can't be walked.
            for (int i = best; i < last; i++)
            {
                if (Vector3.Distance(crumbs[i], crumbs[i + 1]) > 4f)
                {
                    last = i;
                    break;
                }
            }
            for (int j = last; j > best + 2; j--)
            {
                if (c.CanWalkStraight(me, WorldSpace.ToLocal(crumbs[j])))
                {
                    bot.Shortcut = j;
                    break;
                }
            }
            return bot.Shortcut;
        }

        /// <summary>While you're down: nearest terminal ahead, else the level exit, else straight up.</summary>
        private bool ReviveGoalFor(BotPlayer bot, Vector3 me, out Vector3 goal)
        {
            bot.ReviveSearch -= Time.deltaTime;
            if (bot.ReviveSearch <= 0f || !bot.HasReviveGoal)
            {
                bot.ReviveSearch = 2f;
                bot.HasReviveGoal = false;
                float best = 160f;
                foreach (OS_Manager os in UnityEngine.Object.FindObjectsByType<OS_Manager>(FindObjectsSortMode.None))
                {
                    if (os == null || !os.gameObject.activeInHierarchy || World.VoteTerminal.Is(os))
                    {
                        continue;
                    }
                    Vector3 at = os.transform.position;
                    float d = Vector3.Distance(at, me);
                    if (at.y > me.y - 4f && d < best)
                    {
                        best = d;
                        bot.ReviveGoal = WorldSpace.ToShared(at);
                        bot.ReviveAtTerminal = true;
                        bot.HasReviveGoal = true;
                    }
                }
                if (!bot.HasReviveGoal)
                {
                    M_Level level = World.WorldCatchUp.LevelAt(me);
                    if (level != null && level.levelExit != null)
                    {
                        bot.ReviveGoal = WorldSpace.ToShared(level.levelExit.position);
                        bot.ReviveAtTerminal = false;
                        bot.HasReviveGoal = true;
                    }
                }
            }
            if (!bot.HasReviveGoal)
            {
                goal = me + Vector3.up * 4f;
                return true;
            }
            goal = WorldSpace.ToLocal(bot.ReviveGoal);
            return true;
        }

        /// <summary>At a terminal, or inside a room that waits for the team: bring you back next to it.</summary>
        private bool ReachedRevive(BotPlayer bot, SteamId target, Vector3 me, Vector3 goal, bool givingUp)
        {
            // A minute and more without finding a way: it brings you back where it is rather than leave you
            // spectating a bot that can't get anywhere.
            bool there = givingUp
                || bot.HasReviveGoal && bot.ReviveAtTerminal && Vector3.Distance(me, goal) < 2.6f
                || World.InterludeGate.InGatedRoom(me);
            if (!there || bot.Climber.Hanging || bot.Climber.Falling)
            {
                return false;
            }
            Vector3 spot = me + ClearSpotNear(me, bot.Climber.Yaw);
            if (ReviveSystem.BotRevive(bot.Id, target, spot))
            {
                Plugin.Log.LogInfo($"{bot.Name} reached {(bot.ReviveAtTerminal ? "a terminal" : "a room")} and is reviving {NetSession.NameOf(target)}");
            }
            return true;
        }

        /// <summary>
        /// Stuck for a while: try a short hop along your path (if nothing solid is in the way); after a few fails,
        /// come to you.
        /// </summary>
        private void TrackProgress(BotPlayer bot, Vector3 me, Vector3 goal, bool idle, bool youAlive, SteamId leader, Vector3 leaderShared)
        {
            Vector3 meShared = WorldSpace.ToShared(me);
            float toGoal = Vector3.Distance(me, goal);
            // Progress is getting further along your way or closer to where it's going, not just moving:
            // running around in circles isn't getting anywhere.
            bool along = youAlive && bot.RouteIndex > bot.ProgressIndex;
            bool closer = toGoal < bot.ProgressDistance - 0.7f;
            if (idle || along || closer || toGoal < 1.2f)
            {
                bot.ProgressIndex = bot.RouteIndex;
                bot.ProgressDistance = toGoal;
                bot.Stuck = 0f;
                bot.Blocked = 0f;
                if (idle || Vector3.Distance(me, goal) < 1.2f)
                {
                    bot.FailedHops = 0;
                }
                return;
            }
            if (bot.Climber.Falling || bot.Climber.Mantling)
            {
                return;
            }
            bot.Stuck += Time.deltaTime;
            if (bot.Stuck < 7f)
            {
                return;
            }
            bot.Stuck = 0f;
            bot.ProgressIndex = bot.RouteIndex;
            bot.ProgressDistance = toGoal;
            if (youAlive)
            {
                int from = Mathf.Clamp(bot.RouteIndex, 0, crumbs.Count - 1);
                int to = Mathf.Min(crumbs.Count - 1, from + 4);
                bool clear = to > from;
                for (int i = from; i < to && clear; i++)
                {
                    if (PathBlocked(crumbs[i], crumbs[i + 1]))
                    {
                        clear = false;
                    }
                }
                Vector3 hop = to > from ? WorldSpace.ToLocal(crumbs[to]) : me;
                if (clear && SpotIsClear(hop))
                {
                    bot.Climber.Place(crumbs[to], bot.Climber.Yaw);
                    bot.RouteIndex = to;
                    bot.ProgressIndex = to;
                    bot.FailedHops = 0;
                    return;
                }
                if (!clear)
                {
                    // Shut out (a blast door): stays put, like you would. "Bring to me" fetches it.
                    bot.Blocked = 10f;
                    return;
                }
                // The way is open but it can't make the move: after a few tries, it comes to you.
                if (++bot.FailedHops >= 3)
                {
                    if (leader == NetSession.LocalId)
                    {
                        TeleportToYou(bot);
                    }
                    else
                    {
                        Vector3 near = WorldSpace.ToLocal(leaderShared);
                        bot.Climber.Place(WorldSpace.ToShared(near + ClearSpotNear(near, bot.Climber.Yaw)), bot.Climber.Yaw);
                        bot.RouteIndex = 0;
                        bot.ProgressIndex = 0;
                    }
                }
                return;
            }
            // Revive run with nobody's way to follow: scramble a couple of meters toward the goal, or up.
            Vector3 toward = goal - me;
            Vector3[] tries = { me + Vector3.ClampMagnitude(toward, 2.2f), me + Vector3.up * 2.2f };
            foreach (Vector3 t in tries)
            {
                if (SpotIsClear(t) && HasFloorUnder(t) && !PathBlocked(meShared, WorldSpace.ToShared(t)))
                {
                    bot.Climber.Place(WorldSpace.ToShared(t), bot.Climber.Yaw);
                    return;
                }
            }
        }

        private static bool HasFloorUnder(Vector3 c)
        {
            foreach (RaycastHit hit in Physics.RaycastAll(c, Vector3.down, 1.6f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider != null && RemoteBody.Of(hit.collider) == null && hit.normal.y > 0.6f)
                {
                    return true;
                }
            }
            return false;
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
            // A hard pipe swing is up to 14 m/s: sweep the body along the shove so walls and floors stop it
            // instead of it passing straight through.
            Vector3 move = SweepKnock(path.Position + bot.KnockOffset, bot.KnockVelocity * dt, ref bot.KnockVelocity);
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

        /// <summary>
        /// How far the body can move before touching the level; the blocked part of the velocity is removed so it
        /// slides.
        /// </summary>
        private static Vector3 SweepKnock(Vector3 fromShared, Vector3 move, ref Vector3 velocity)
        {
            float distance = move.magnitude;
            if (distance < 0.0001f)
            {
                return move;
            }
            const float Skin = 0.05f;
            Vector3 dir = move / distance;
            Vector3 c = WorldSpace.ToLocal(fromShared);
            float half = RemoteBody.Height * 0.5f - RemoteBody.Radius;
            float best = float.MaxValue;
            Vector3 normal = Vector3.zero;
            foreach (RaycastHit hit in Physics.CapsuleCastAll(c - Vector3.up * half, c + Vector3.up * half, RemoteBody.Radius * 0.85f, dir,
                distance + Skin, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                Collider col = hit.collider;
                if (col == null || RemoteBody.Of(col) != null || col.GetComponentInParent<ENT_Player>() != null
                    || col.attachedRigidbody != null && !col.attachedRigidbody.isKinematic)
                {
                    // Players, bots and loose props don't stop a shove.
                    continue;
                }
                if (hit.distance <= 0f && hit.point == Vector3.zero)
                {
                    // Already overlapping at the start (the body hugs walls on your path): no direction to judge.
                    continue;
                }
                if (hit.distance < best)
                {
                    best = hit.distance;
                    normal = hit.normal;
                }
            }
            if (best == float.MaxValue)
            {
                return move;
            }
            float into = Vector3.Dot(velocity, normal);
            if (into < 0f)
            {
                velocity -= normal * into;
            }
            return dir * Mathf.Max(0f, best - Skin);
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
                Kill(bot, "deathfloor");
            }
        }

        private static readonly Collider[] overlap = new Collider[48];
        private static readonly HashSet<UT_DamageTrigger> touching = new HashSet<UT_DamageTrigger>();

        /// <summary>
        /// Hazard volumes only react to the real player, so check the bot against them and apply the same damage.
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
                    Kill(bot, death.type);
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
        /// Rooms that wait for the team count a bot as there once you are: if it's still outside a few seconds
        /// later, it's brought to you.
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
            GUILayout.Label("Bots are teammates that only exist in your game. In Climb mode a bot makes its own way up "
                + "behind you: it grabs real holds, runs out of grip, rests on ledges, jumps gaps and can fall. If you "
                + "go down, it heads for the nearest terminal and revives you. It picks up items it passes (take them from it below), "
                + "eats food when it's hurt or slipping, uses pitons and rebar where there's no hold, drops everything when it dies, "
                + "hurries on (or swings) when a creature gets close, and fights its way out of barnacles.");
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

        private static readonly string[] StyleLabels = { "Careful", "Steady", "Bold" };

        private void DrawBot(BotPlayer bot)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            string status = bot.Dead ? "dead (revive at a station)"
                : bot.Blocked > 1f ? "blocked by a door (Bring to me)"
                : bot.Mode == BotMode.Climb ? bot.Activity
                : bot.Mode == BotMode.Follow ? "retracing your path" : bot.Anchored ? "holding on" : "standing";
            if (!bot.Dead)
            {
                status += $", health {Mathf.CeilToInt(bot.Health / Mathf.Max(0.01f, MaxHealth) * 100f)}%";
                if (bot.Mode == BotMode.Climb && bot.Climber != null)
                {
                    status += $", grip {Mathf.CeilToInt(bot.Climber.GripFraction * 100f)}%";
                }
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
            if (GUILayout.Toggle(bot.Mode == BotMode.Climb, "Climb", GUI.skin.button))
            {
                if (bot.Mode != BotMode.Climb)
                {
                    bot.Mode = BotMode.Climb;
                    PlaceClimber(bot, bot.State.Position, bot.State.Yaw);
                }
            }
            if (GUILayout.Toggle(bot.Mode == BotMode.Follow, "Retrace", GUI.skin.button))
            {
                if (bot.Mode != BotMode.Follow)
                {
                    bot.Mode = BotMode.Follow;
                    bot.Playhead = -1f;
                    bot.KnockOffset = bot.KnockVelocity = Vector3.zero;
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

            if (bot.Mode == BotMode.Climb)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Playstyle", GUILayout.Width(90));
                bot.Style = (BotStyle)GUILayout.Toolbar((int)bot.Style, StyleLabels);
                GUILayout.EndHorizontal();

                // Who it climbs with: you or any other (real) teammate.
                var people = new List<SteamId> { NetSession.LocalId };
                foreach (SteamId id in NetSession.Instance.MemberOrder())
                {
                    if (id != NetSession.LocalId && !IsBot(id))
                    {
                        people.Add(id);
                    }
                }
                if (people.Count > 1)
                {
                    var names = new string[people.Count];
                    for (int i = 0; i < people.Count; i++)
                    {
                        names[i] = i == 0 ? "You" : NetSession.NameOf(people[i]);
                    }
                    int current = Mathf.Max(0, people.IndexOf(LeaderId(bot)));
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Climbs with", GUILayout.Width(90));
                    int picked = GUILayout.Toolbar(current, names);
                    GUILayout.EndHorizontal();
                    if (picked != current)
                    {
                        bot.FollowId = picked == 0 ? default : people[picked];
                        bot.RouteIndex = 0;
                        bot.ProgressIndex = 0;
                        bot.HasReviveGoal = false;
                    }
                }

                GUILayout.BeginHorizontal();
                bot.PickUpItems = GUILayout.Toggle(bot.PickUpItems, $"Picks up items ({bot.Items.Count}/{BotItems.MaxItems})");
                GUILayout.EndHorizontal();
                foreach (Item item in new List<Item>(bot.Items))
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("  " + BotItems.Label(item) + (BotItems.FoodOf(item) != null ? " (eats it when hurt or slipping)" : BotTools.IsTool(item) ? " (uses it where there's no hold)" : ""));
                    if (GUILayout.Button("Take", GUILayout.Width(60)))
                    {
                        BotItems.GiveToYou(bot, item);
                    }
                    GUILayout.EndHorizontal();
                }
            }

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
