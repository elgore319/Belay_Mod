using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace Belay.Net
{
    public enum LobbyPrivacy
    {
        Public,
        FriendsOnly,
        Private,
    }

    /// <summary>
    /// Owns the Steam lobby and the P2P transport. The lobby owner is the host; every packet goes
    /// client -> host -> other clients (star topology), so the host can validate and later own
    /// authoritative state such as enemies.
    /// </summary>
    public class NetSession : MonoBehaviour
    {
        public const int MaxPlayers = 8;
        public const string ProtocolVersion = "belay-15";

        // Lobby data keys
        public const string KeyTag = "belay";
        public const string KeyVersion = "belay_ver";
        public const string KeyName = "name";
        public const string KeySeed = "seed";
        public const string KeyRopeLength = "rope_len";
        public const string KeyTether = "tether";
        public const string KeyRopePull = "rope_pull";
        public const string KeyRun = "belay_run";
        public const string KeyDoorDelay = "belay_door_delay";
        public const string KeyReviveCost = "belay_revive_cost";
        public const string KeyMods = "belay_mods";

        public static NetSession Instance { get; private set; }

        public Lobby? CurrentLobby { get; private set; }
        public bool InLobby => CurrentLobby.HasValue || Loopback.ClientConnected;
        public bool IsHost => CurrentLobby.HasValue && CurrentLobby.Value.Owner.Id == SteamClient.SteamId;
        public SteamId HostId => Loopback.IsClient ? Loopback.HostId : CurrentLobby.HasValue ? CurrentLobby.Value.Owner.Id : default;

        /// <summary>
        /// Who this game is in the session. Use this instead of SteamClient.SteamId: a local test client runs
        /// on the same Steam account as the host, so it has its own id (LoopbackLink.ClientId).
        /// </summary>
        public static SteamId LocalId => Instance != null && Instance.Loopback.IsClient ? LoopbackLink.ClientId : SteamClient.SteamId;

        /// <summary>Second copy of the game on this PC, for solo testing (see LoopbackLink).</summary>
        public readonly LoopbackLink Loopback = new LoopbackLink();
        public bool IsTestClient => Loopback.IsClient;
        public string Status { get; private set; } = "Not connected";

        public event Action<SteamId, PlayerState> OnPlayerState;
        public event Action<SteamId> OnPlayerLeft;
        public event Action OnLobbyChanged;

        private readonly MemoryStream sendBuffer = new MemoryStream(256);
        private BinaryWriter sendWriter;
        private bool callbacksHooked;

        public static bool SteamReady => SteamClient.IsValid;

        private void Awake()
        {
            Instance = this;
            sendWriter = new BinaryWriter(sendBuffer);
            if (LoopbackLink.LaunchedAsClient)
            {
                Loopback.StartClient();
                Status = "Test client: waiting for the host";
            }
        }

        private void Update()
        {
            if (Loopback.IsClient)
            {
                UpdateTestClient();
                return;
            }
            if (!SteamReady)
            {
                return;
            }
            if (!callbacksHooked)
            {
                HookCallbacks();
            }
            if (Loopback.IsListening)
            {
                bool wasAlive = Loopback.PeerAlive;
                Loopback.Poll((channel, packet) => HandleSafe(LoopbackLink.ClientId, packet, channel));
                if (wasAlive != Loopback.PeerAlive)
                {
                    if (!Loopback.PeerAlive)
                    {
                        OnPlayerLeft?.Invoke(LoopbackLink.ClientId);
                    }
                    OnLobbyChanged?.Invoke();
                }
                if (IsHost)
                {
                    Loopback.HostTick(HostId, MemberIds(), NameOf, CurrentLobby.Value.Data);
                }
            }
            if (InLobby)
            {
                Receive(Channels.Reliable);
                Receive(Channels.State);
            }
        }

        private void UpdateTestClient()
        {
            bool wasConnected = Loopback.ClientConnected;
            bool changed = Loopback.Poll((channel, packet) => HandleSafe(Loopback.HostId, packet, channel));
            Loopback.ClientTick();
            if (Loopback.ClientConnected && !wasConnected)
            {
                Status = "Test client: connected to " + NameOf(Loopback.HostId);
                SendReliable(PacketType.Hello, HostId, w => w.Write(ProtocolVersion));
            }
            else if (!Loopback.ClientConnected && wasConnected)
            {
                Status = "Test client: lost the host";
            }
            if (changed)
            {
                OnLobbyChanged?.Invoke();
            }
        }

        /// <summary>Host: accept a second copy of the game on this PC as a client.</summary>
        public bool AllowTestClient()
        {
            return !Loopback.IsClient && Loopback.StartListening();
        }

        private void OnDestroy()
        {
            LeaveLobby();
            Loopback.Stop();
        }

        private void HookCallbacks()
        {
            callbacksHooked = true;
            SteamMatchmaking.OnLobbyEntered += HandleLobbyEntered;
            SteamMatchmaking.OnLobbyMemberJoined += (lobby, friend) => HandleMembersChanged(lobby);
            SteamMatchmaking.OnLobbyMemberLeave += (lobby, friend) => HandleMemberGone(lobby, friend);
            SteamMatchmaking.OnLobbyMemberDisconnected += (lobby, friend) => HandleMemberGone(lobby, friend);
            SteamMatchmaking.OnLobbyDataChanged += lobby => HandleMembersChanged(lobby);
            SteamFriends.OnGameLobbyJoinRequested += (lobby, friend) => JoinLobby(lobby.Id);
            SteamNetworking.OnP2PSessionRequest += HandleSessionRequest;
            SteamNetworking.AllowP2PPacketRelay(true);
        }

        // ---------------------------------------------------------------- Lobby

        public async void HostLobby(string lobbyName, LobbyPrivacy privacy)
        {
            if (!SteamReady)
            {
                Status = "Steam is not running";
                return;
            }
            LeaveLobby();
            Status = "Creating lobby...";
            Lobby? created = await SteamMatchmaking.CreateLobbyAsync(MaxPlayers);
            if (!created.HasValue)
            {
                Status = "Failed to create lobby";
                return;
            }
            Lobby lobby = created.Value;
            switch (privacy)
            {
                case LobbyPrivacy.Public: lobby.SetPublic(); break;
                case LobbyPrivacy.FriendsOnly: lobby.SetFriendsOnly(); break;
                default: lobby.SetPrivate(); break;
            }
            lobby.SetJoinable(true);
            lobby.SetData(KeyTag, "1");
            lobby.SetData(KeyVersion, ProtocolVersion);
            lobby.SetData(KeyName, string.IsNullOrEmpty(lobbyName) ? SteamClient.Name + "'s climb" : lobbyName);
            lobby.SetData(KeyRopeLength, Plugin.DefaultRopeLength.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lobby.SetData(KeyTether, Plugin.RopeOnByDefault.Value ? "1" : "0");
            lobby.SetData(KeyRopePull, "1");
            lobby.SetData(KeyDoorDelay, "15");
            lobby.SetData(KeyReviveCost, Revive.ReviveSystem.DefaultCost.ToString(System.Globalization.CultureInfo.InvariantCulture));
            RerollSeed(lobby);
            // OnLobbyEntered fires for the creator as well, which sets CurrentLobby.
        }

        public async void JoinLobby(SteamId lobbyId)
        {
            if (!SteamReady)
            {
                return;
            }
            LeaveLobby();
            Status = "Joining...";
            Lobby? joined = await SteamMatchmaking.JoinLobbyAsync(lobbyId);
            if (!joined.HasValue)
            {
                Status = "Failed to join lobby";
            }
        }

        public void LeaveLobby()
        {
            if (!CurrentLobby.HasValue)
            {
                return;
            }
            Lobby lobby = CurrentLobby.Value;
            foreach (Friend member in lobby.Members)
            {
                if (member.Id != SteamClient.SteamId)
                {
                    SteamNetworking.CloseP2PSessionWithUser(member.Id);
                }
            }
            lobby.Leave();
            CurrentLobby = null;
            Status = "Not connected";
            OnLobbyChanged?.Invoke();
        }

        public async Task<Lobby[]> FindPublicLobbies()
        {
            if (!SteamReady)
            {
                return Array.Empty<Lobby>();
            }
            Lobby[] lobbies = await SteamMatchmaking.LobbyList
                .WithKeyValue(KeyTag, "1")
                .WithKeyValue(KeyVersion, ProtocolVersion)
                .WithSlotsAvailable(1)
                .FilterDistanceWorldwide()
                .WithMaxResults(50)
                .RequestAsync();
            return lobbies ?? Array.Empty<Lobby>();
        }

        public void OpenInviteOverlay()
        {
            if (CurrentLobby.HasValue)
            {
                SteamFriends.OpenGameInviteOverlay(CurrentLobby.Value.Id);
            }
        }

        /// <summary>Steam lobby members (real Steam accounts only). For gameplay, use MemberIds or MemberOrder.</summary>
        public IEnumerable<Friend> Members => CurrentLobby.HasValue ? CurrentLobby.Value.Members : Enumerable.Empty<Friend>();

        /// <summary>Everyone connected, in join order: Steam members, then a local test client.</summary>
        public List<SteamId> MemberIds()
        {
            if (Loopback.IsClient)
            {
                return Loopback.ClientConnected ? new List<SteamId>(Loopback.Members) : new List<SteamId>();
            }
            List<SteamId> ids = Members.Select(m => m.Id).ToList();
            if (IsHost && Loopback.PeerAlive)
            {
                ids.Add(LoopbackLink.ClientId);
            }
            return ids;
        }

        /// <summary>Members in join order, used to decide who is roped to whom. Local test bots come last.</summary>
        public List<SteamId> MemberOrder()
        {
            List<SteamId> order = MemberIds();
            if (InLobby)
            {
                order.AddRange(LocalBots);
            }
            return order;
        }

        // ---------------------------------------------------------------- Local test bots
        // Bot/TestBot.cs adds fake teammates that live only on this machine. They count as members for
        // gameplay (rope, goo, revive, interlude gate) but have no Steam connection, so nothing is sent to them.

        public readonly List<SteamId> LocalBots = new List<SteamId>();
        public readonly Dictionary<SteamId, string> LocalBotNames = new Dictionary<SteamId, string>();

        public bool IsLocalBot(SteamId id) => LocalBotNames.ContainsKey(id);

        /// <summary>Display name for a member, including local bots.</summary>
        public static string NameOf(SteamId id)
        {
            if (Instance != null && Instance.LocalBotNames.TryGetValue(id, out string name))
            {
                return name;
            }
            if (id == LoopbackLink.ClientId)
            {
                return LoopbackLink.ClientName;
            }
            if (Instance != null && Instance.Loopback.IsClient && Instance.Loopback.Names.TryGetValue(id, out string hostName) && !string.IsNullOrEmpty(hostName))
            {
                return hostName;
            }
            return new Friend(id).Name;
        }

        public void AddLocalBot(SteamId id, string name)
        {
            if (!LocalBotNames.ContainsKey(id))
            {
                LocalBots.Add(id);
                LocalBotNames[id] = name;
                OnLobbyChanged?.Invoke();
            }
        }

        public void RemoveLocalBot(SteamId id)
        {
            if (LocalBotNames.Remove(id))
            {
                LocalBots.Remove(id);
                OnPlayerLeft?.Invoke(id);
                OnLobbyChanged?.Invoke();
            }
        }

        /// <summary>Feed a local bot's state through the same path as a network snapshot.</summary>
        public void InjectLocalBotState(SteamId id, PlayerState state)
        {
            if (InLobby && IsLocalBot(id))
            {
                OnPlayerState?.Invoke(id, state);
            }
        }

        public string GetLobbyData(string key)
        {
            if (Loopback.IsClient)
            {
                return Loopback.LobbyData.TryGetValue(key, out string value) ? value : null;
            }
            return CurrentLobby.HasValue ? CurrentLobby.Value.GetData(key) : null;
        }

        public void SetLobbyData(string key, string value)
        {
            if (IsHost)
            {
                CurrentLobby.Value.SetData(key, value);
            }
        }

        public int Seed
        {
            get
            {
                RunInfo run = CurrentRun;
                if (run.IsValid && run.Seed != 0)
                {
                    return run.Seed;
                }
                int.TryParse(GetLobbyData(KeySeed), out int seed);
                return seed;
            }
        }

        /// <summary>The run the host last started, or an invalid RunInfo if none.</summary>
        public RunInfo CurrentRun => RunInfo.Parse(GetLobbyData(KeyRun));

        /// <summary>Host only: publish a new run with a fresh seed. Clients follow it from lobby data.</summary>
        public RunInfo StartRun(string gamemode, List<string> settings)
        {
            var run = new RunInfo
            {
                RunId = Math.Max(1, CurrentRun.RunId + 1),
                Seed = UnityEngine.Random.Range(1, 10000000),
                Gamemode = gamemode,
                Settings = settings ?? new List<string>(),
            };
            if (IsHost)
            {
                CurrentLobby.Value.SetData(KeySeed, run.Seed.ToString());
                CurrentLobby.Value.SetData(KeyRun, run.Serialize());
            }
            return run;
        }

        private static void RerollSeed(Lobby lobby)
        {
            // 0 means "no preset" to WorldLoader, so never pick it.
            lobby.SetData(KeySeed, UnityEngine.Random.Range(1, 10000000).ToString());
        }

        private void HandleLobbyEntered(Lobby lobby)
        {
            if (lobby.GetData(KeyTag) != "1")
            {
                // Not a Belay lobby (vanilla invite or another mod's lobby).
                return;
            }
            if (lobby.GetData(KeyVersion) != ProtocolVersion)
            {
                Status = "Version mismatch: host runs " + lobby.GetData(KeyVersion);
                lobby.Leave();
                return;
            }
            CurrentLobby = lobby;
            lobby.SetMemberData(KeyMods, UI.ModManager.LoadedModList());
            Status = IsHost ? "Hosting" : "Connected to " + lobby.Owner.Name;
            if (!IsHost)
            {
                SendReliable(PacketType.Hello, HostId, w => w.Write(ProtocolVersion));
            }
            Plugin.Log.LogInfo($"Entered lobby {lobby.Id} ({lobby.GetData(KeyName)}), host={lobby.Owner.Name}, seed={lobby.GetData(KeySeed)}");
            OnLobbyChanged?.Invoke();
        }

        private void HandleMembersChanged(Lobby lobby)
        {
            if (CurrentLobby.HasValue && lobby.Id == CurrentLobby.Value.Id)
            {
                Status = IsHost ? "Hosting" : "Connected to " + lobby.Owner.Name;
                OnLobbyChanged?.Invoke();
            }
        }

        private void HandleMemberGone(Lobby lobby, Friend friend)
        {
            if (!CurrentLobby.HasValue || lobby.Id != CurrentLobby.Value.Id)
            {
                return;
            }
            SteamNetworking.CloseP2PSessionWithUser(friend.Id);
            OnPlayerLeft?.Invoke(friend.Id);
            HandleMembersChanged(lobby);
        }

        private void HandleSessionRequest(SteamId from)
        {
            // Only talk to people in our lobby.
            if (CurrentLobby.HasValue && CurrentLobby.Value.Members.Any(m => m.Id == from))
            {
                SteamNetworking.AcceptP2PSessionWithUser(from);
            }
        }

        // ---------------------------------------------------------------- Transport
        // Wire format: [PacketType][origin SteamId (ulong)][payload]

        public void BroadcastState(PlayerState state)
        {
            byte[] packet = Build(PacketType.PlayerState, LocalId, state.Write);
            if (IsHost)
            {
                SendToAllExcept(packet, LocalId, Channels.State, P2PSend.UnreliableNoDelay);
            }
            else
            {
                SendRaw(HostId, packet, Channels.State, P2PSend.UnreliableNoDelay);
            }
        }

        /// <summary>Packets a client sends that the host passes on to every other client.</summary>
        private static readonly HashSet<PacketType> RelayedByHost = new HashSet<PacketType> { PacketType.DoorOpen, PacketType.ButtonPress, PacketType.HoldInteract, PacketType.LeverAngle, PacketType.HandTouch, PacketType.ItemPickup, PacketType.ItemDrop, PacketType.RopePull, PacketType.ZoneEntered, PacketType.Knockback, PacketType.PropKill, PacketType.EnemyHurt, PacketType.EnemyKilled };
        /// <summary>Frequent state the host passes on without delivery guarantees (a newer copy follows soon).</summary>
        private static readonly HashSet<PacketType> RelayedUnreliable = new HashSet<PacketType> { PacketType.PropState };

        private readonly Dictionary<PacketType, Action<SteamId, BinaryReader>> handlers =
            new Dictionary<PacketType, Action<SteamId, BinaryReader>>();

        /// <summary>Register the handler for a gameplay packet type. The origin is the player who sent it.</summary>
        public void On(PacketType type, Action<SteamId, BinaryReader> handler)
        {
            handlers[type] = handler;
        }

        /// <summary>
        /// Send a gameplay packet. From a client it goes to the host (which may relay it); from the host it
        /// goes to every client.
        /// </summary>
        public void Send(PacketType type, Action<BinaryWriter> payload, bool reliable = true)
        {
            if (!InLobby)
            {
                return;
            }
            byte[] packet = Build(type, LocalId, payload);
            int channel = reliable ? Channels.Reliable : Channels.State;
            P2PSend sendType = reliable ? P2PSend.Reliable : P2PSend.UnreliableNoDelay;
            if (IsHost)
            {
                SendToAllExcept(packet, LocalId, channel, sendType);
            }
            else
            {
                SendRaw(HostId, packet, channel, sendType);
            }
        }

        private void SendReliable(PacketType type, SteamId to, Action<BinaryWriter> payload)
        {
            byte[] packet = Build(type, LocalId, payload);
            SendRaw(to, packet, Channels.Reliable, P2PSend.Reliable);
        }

        /// <summary>Send one built packet to one member, over Steam or the local test-client link.</summary>
        private void SendRaw(SteamId to, byte[] packet, int channel, P2PSend sendType)
        {
            if (Loopback.IsClient)
            {
                // A test client only ever talks to the host, over the local link.
                Loopback.SendGame(channel, packet);
            }
            else if (to == LoopbackLink.ClientId)
            {
                if (Loopback.PeerAlive)
                {
                    Loopback.SendGame(channel, packet);
                }
            }
            else if (!IsLocalBot(to))
            {
                SteamNetworking.SendP2PPacket(to, packet, packet.Length, channel, sendType);
            }
        }

        private byte[] Build(PacketType type, SteamId origin, Action<BinaryWriter> payload)
        {
            sendBuffer.SetLength(0);
            sendWriter.Write((byte)type);
            sendWriter.Write(origin.Value);
            payload?.Invoke(sendWriter);
            sendWriter.Flush();
            return sendBuffer.ToArray();
        }

        private void SendToAllExcept(byte[] packet, SteamId except, int channel, P2PSend sendType)
        {
            foreach (SteamId member in MemberIds())
            {
                if (member != LocalId && member != except)
                {
                    SendRaw(member, packet, channel, sendType);
                }
            }
        }

        private void Receive(int channel)
        {
            while (SteamNetworking.IsP2PPacketAvailable(channel))
            {
                P2Packet? packet = SteamNetworking.ReadP2PPacket(channel);
                if (!packet.HasValue || packet.Value.Data == null || packet.Value.Data.Length < 9)
                {
                    continue;
                }
                HandleSafe(packet.Value.SteamId, packet.Value.Data, channel);
            }
        }

        private void HandleSafe(SteamId sender, byte[] data, int channel)
        {
            if (data == null || data.Length < 9)
            {
                return;
            }
            try
            {
                Handle(sender, data, channel);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Bad packet from {sender}: {e.Message}");
            }
        }

        private void Handle(SteamId sender, byte[] data, int channel)
        {
            using (var reader = new BinaryReader(new MemoryStream(data)))
            {
                var type = (PacketType)reader.ReadByte();
                SteamId origin = reader.ReadUInt64();

                if (IsHost)
                {
                    // Clients may only speak for themselves.
                    if (origin != sender)
                    {
                        return;
                    }
                    if (type == PacketType.PlayerState)
                    {
                        SendToAllExcept(data, sender, channel, P2PSend.UnreliableNoDelay);
                    }
                    else if (RelayedUnreliable.Contains(type))
                    {
                        // Passed on the way it came: most updates unreliable, the final one reliable.
                        SendToAllExcept(data, sender, channel, channel == Channels.Reliable ? P2PSend.Reliable : P2PSend.UnreliableNoDelay);
                    }
                    else if (RelayedByHost.Contains(type))
                    {
                        SendToAllExcept(data, sender, channel, P2PSend.Reliable);
                    }
                }
                else if (sender != HostId)
                {
                    // Clients only accept traffic relayed by the host.
                    return;
                }

                switch (type)
                {
                    case PacketType.Hello:
                        string version = reader.ReadString();
                        Plugin.Log.LogInfo($"Hello from {origin} ({version})");
                        break;
                    case PacketType.PlayerState:
                        if (origin != LocalId)
                        {
                            OnPlayerState?.Invoke(origin, PlayerState.Read(reader));
                        }
                        break;
                    case PacketType.PlayerLeft:
                        OnPlayerLeft?.Invoke(origin);
                        break;
                    default:
                        if (handlers.TryGetValue(type, out Action<SteamId, BinaryReader> handler))
                        {
                            handler(origin, reader);
                        }
                        break;
                }
            }
        }
    }
}
