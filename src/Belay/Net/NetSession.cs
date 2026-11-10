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
        public const string ProtocolVersion = "belay-5";

        // Lobby data keys
        public const string KeyTag = "belay";
        public const string KeyVersion = "belay_ver";
        public const string KeyName = "name";
        public const string KeySeed = "seed";
        public const string KeyRopeLength = "rope_len";
        public const string KeyTether = "tether";
        public const string KeyRun = "belay_run";
        public const string KeyDoorDelay = "belay_door_delay";
        public const string KeyReviveCost = "belay_revive_cost";
        public const string KeyMods = "belay_mods";

        public static NetSession Instance { get; private set; }

        public Lobby? CurrentLobby { get; private set; }
        public bool InLobby => CurrentLobby.HasValue;
        public bool IsHost => InLobby && CurrentLobby.Value.Owner.Id == SteamClient.SteamId;
        public SteamId HostId => InLobby ? CurrentLobby.Value.Owner.Id : default;
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
        }

        private void Update()
        {
            if (!SteamReady)
            {
                return;
            }
            if (!callbacksHooked)
            {
                HookCallbacks();
            }
            if (InLobby)
            {
                Receive(Channels.Reliable);
                Receive(Channels.State);
            }
        }

        private void OnDestroy()
        {
            LeaveLobby();
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
            lobby.SetData(KeyTether, "1");
            lobby.SetData(KeyDoorDelay, "15");
            lobby.SetData(KeyReviveCost, "10");
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
            if (!InLobby)
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
            if (InLobby)
            {
                SteamFriends.OpenGameInviteOverlay(CurrentLobby.Value.Id);
            }
        }

        public IEnumerable<Friend> Members => InLobby ? CurrentLobby.Value.Members : Enumerable.Empty<Friend>();

        /// <summary>Members in join order, used to decide who is roped to whom.</summary>
        public List<SteamId> MemberOrder()
        {
            return Members.Select(m => m.Id).ToList();
        }

        public string GetLobbyData(string key)
        {
            return InLobby ? CurrentLobby.Value.GetData(key) : null;
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
            if (InLobby && lobby.Id == CurrentLobby.Value.Id)
            {
                Status = IsHost ? "Hosting" : "Connected to " + lobby.Owner.Name;
                OnLobbyChanged?.Invoke();
            }
        }

        private void HandleMemberGone(Lobby lobby, Friend friend)
        {
            if (!InLobby || lobby.Id != CurrentLobby.Value.Id)
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
            if (InLobby && CurrentLobby.Value.Members.Any(m => m.Id == from))
            {
                SteamNetworking.AcceptP2PSessionWithUser(from);
            }
        }

        // ---------------------------------------------------------------- Transport
        // Wire format: [PacketType][origin SteamId (ulong)][payload]

        public void BroadcastState(PlayerState state)
        {
            byte[] packet = Build(PacketType.PlayerState, SteamClient.SteamId, state.Write);
            if (IsHost)
            {
                SendToAllExcept(packet, SteamClient.SteamId, Channels.State, P2PSend.UnreliableNoDelay);
            }
            else
            {
                SteamNetworking.SendP2PPacket(HostId, packet, packet.Length, Channels.State, P2PSend.UnreliableNoDelay);
            }
        }

        /// <summary>Packets a client sends that the host passes on to every other client.</summary>
        private static readonly HashSet<PacketType> RelayedByHost = new HashSet<PacketType> { PacketType.DoorOpen, PacketType.ButtonPress, PacketType.HoldInteract, PacketType.LeverAngle, PacketType.HandTouch };

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
            byte[] packet = Build(type, SteamClient.SteamId, payload);
            int channel = reliable ? Channels.Reliable : Channels.State;
            P2PSend sendType = reliable ? P2PSend.Reliable : P2PSend.UnreliableNoDelay;
            if (IsHost)
            {
                SendToAllExcept(packet, SteamClient.SteamId, channel, sendType);
            }
            else
            {
                SteamNetworking.SendP2PPacket(HostId, packet, packet.Length, channel, sendType);
            }
        }

        private void SendReliable(PacketType type, SteamId to, Action<BinaryWriter> payload)
        {
            byte[] packet = Build(type, SteamClient.SteamId, payload);
            SteamNetworking.SendP2PPacket(to, packet, packet.Length, Channels.Reliable, P2PSend.Reliable);
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
            foreach (Friend member in Members)
            {
                if (member.Id != SteamClient.SteamId && member.Id != except)
                {
                    SteamNetworking.SendP2PPacket(member.Id, packet, packet.Length, channel, sendType);
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
                try
                {
                    Handle(packet.Value.SteamId, packet.Value.Data, channel);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Bad packet from {packet.Value.SteamId}: {e.Message}");
                }
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
                        if (origin != SteamClient.SteamId)
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
