using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Steamworks;
using UnityEngine;

namespace Belay.Net
{
    /// <summary>
    /// A local link between two copies of the game on the same PC, used for solo testing. The host listens
    /// on 127.0.0.1; a second game started with -belay-testclient connects to it and plays as a real client
    /// (its own world, its own level generation), without Steam networking. Steam can't put the same
    /// account in a lobby twice, so the test client has its own fake id and gets the lobby data from the
    /// host over this link.
    ///
    /// Datagram format: [kind][...]. kind 0 = game packet [channel][packet]; 1 = host snapshot;
    /// 2 = client heartbeat.
    /// </summary>
    public class LoopbackLink
    {
        public const int Port = 27815;
        public const string CommandLineFlag = "-belay-testclient";
        public static readonly SteamId ClientId = new SteamId { Value = 0x00BE1A70FFFF0001UL };
        public const string ClientName = "Test client";

        private const byte KindGame = 0;
        private const byte KindSnapshot = 1;
        private const byte KindHeartbeat = 2;
        private const float Timeout = 5f;

        private UdpClient udp;
        private IPEndPoint peer;
        private float lastHeard = -100f;
        private float snapshotTimer;
        private float heartbeatTimer;
        private string lastSnapshotKey;

        /// <summary>This game is the test client (started with -belay-testclient).</summary>
        public bool IsClient { get; private set; }

        /// <summary>This game is a host that accepts a local test client.</summary>
        public bool IsListening => !IsClient && udp != null;

        public bool PeerAlive => udp != null && peer != null && Time.unscaledTime - lastHeard < Timeout;

        // Test client: what the host last told us.
        public SteamId HostId { get; private set; }
        public readonly Dictionary<string, string> LobbyData = new Dictionary<string, string>();
        public readonly List<SteamId> Members = new List<SteamId>();
        public readonly Dictionary<SteamId, string> Names = new Dictionary<SteamId, string>();

        public bool ClientConnected => IsClient && PeerAlive && HostId.Value != 0;

        public static bool LaunchedAsClient =>
            Environment.GetCommandLineArgs().Any(a => string.Equals(a, CommandLineFlag, StringComparison.OrdinalIgnoreCase));

        public void StartClient()
        {
            Stop();
            IsClient = true;
            udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            DisableConnReset(udp);
            peer = new IPEndPoint(IPAddress.Loopback, Port);
            Plugin.Log.LogInfo("Belay test client: connecting to the host on 127.0.0.1:" + Port);
        }

        /// <summary>Host: start accepting a local test client. Returns false if the port is taken.</summary>
        public bool StartListening()
        {
            if (udp != null)
            {
                return true;
            }
            try
            {
                udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port));
                DisableConnReset(udp);
                peer = null;
                Plugin.Log.LogInfo("Belay: listening for a local test client on 127.0.0.1:" + Port);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Belay: can't listen for a test client: " + e.Message);
                udp = null;
                return false;
            }
        }

        public void Stop()
        {
            udp?.Close();
            udp = null;
            peer = null;
            lastHeard = -100f;
            lastSnapshotKey = null;
            HostId = default;
            LobbyData.Clear();
            Members.Clear();
            Names.Clear();
        }

        private static void DisableConnReset(UdpClient client)
        {
            // Windows reports "connection reset" on the next receive after sending to a closed port. Turn that off.
            try
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                client.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
            }
            catch
            {
                // Not supported here; Poll catches the exception instead.
            }
        }

        public void SendGame(int channel, byte[] packet)
        {
            if (udp == null || peer == null)
            {
                return;
            }
            var data = new byte[packet.Length + 2];
            data[0] = KindGame;
            data[1] = (byte)channel;
            Buffer.BlockCopy(packet, 0, data, 2, packet.Length);
            SendRaw(data);
        }

        private void SendRaw(byte[] data)
        {
            try
            {
                udp.Send(data, data.Length, peer);
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("Belay loopback send failed: " + e.Message);
            }
        }

        /// <summary>
        /// Read everything waiting. Game packets go to onGame(channel, packet); returns true when the member
        /// list or lobby data changed (or the link came up or went down).
        /// </summary>
        public bool Poll(Action<int, byte[]> onGame)
        {
            if (udp == null)
            {
                return false;
            }
            bool wasAlive = PeerAlive;
            bool changed = false;
            while (true)
            {
                byte[] data;
                IPEndPoint from = null;
                try
                {
                    if (udp.Available <= 0)
                    {
                        break;
                    }
                    data = udp.Receive(ref from);
                }
                catch (SocketException)
                {
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
                if (data == null || data.Length < 1 || !IPAddress.IsLoopback(from.Address))
                {
                    continue;
                }
                if (IsClient)
                {
                    if (from.Port != Port)
                    {
                        continue;
                    }
                }
                else
                {
                    // Host: the newest local client is the peer.
                    if (peer == null || !peer.Equals(from))
                    {
                        peer = from;
                        Plugin.Log.LogInfo("Belay: local test client connected from port " + from.Port);
                    }
                }
                lastHeard = Time.unscaledTime;
                switch (data[0])
                {
                    case KindGame:
                        if (data.Length >= 2 + 9)
                        {
                            var packet = new byte[data.Length - 2];
                            Buffer.BlockCopy(data, 2, packet, 0, packet.Length);
                            onGame(data[1], packet);
                        }
                        break;
                    case KindSnapshot:
                        if (IsClient)
                        {
                            changed |= ReadSnapshot(data);
                        }
                        break;
                }
            }
            if (wasAlive != PeerAlive)
            {
                changed = true;
                if (IsClient && !PeerAlive)
                {
                    HostId = default;
                    Members.Clear();
                    LobbyData.Clear();
                    lastSnapshotKey = null;
                }
            }
            return changed;
        }

        /// <summary>Client: say we're here, once a second.</summary>
        public void ClientTick()
        {
            if (!IsClient || udp == null)
            {
                return;
            }
            heartbeatTimer -= Time.unscaledDeltaTime;
            if (heartbeatTimer <= 0f)
            {
                heartbeatTimer = 1f;
                SendRaw(new[] { KindHeartbeat });
            }
        }

        /// <summary>Host: send the lobby (members, names, data) to the test client twice a second.</summary>
        public void HostTick(SteamId hostId, List<SteamId> members, Func<SteamId, string> nameOf, IEnumerable<KeyValuePair<string, string>> lobbyData)
        {
            if (!IsListening || !PeerAlive)
            {
                return;
            }
            snapshotTimer -= Time.unscaledDeltaTime;
            if (snapshotTimer > 0f)
            {
                return;
            }
            snapshotTimer = 0.5f;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(KindSnapshot);
                w.Write(hostId.Value);
                w.Write(members.Count);
                foreach (SteamId id in members)
                {
                    w.Write(id.Value);
                    w.Write(nameOf(id) ?? "");
                }
                var pairs = lobbyData.ToList();
                w.Write(pairs.Count);
                foreach (KeyValuePair<string, string> pair in pairs)
                {
                    w.Write(pair.Key ?? "");
                    w.Write(pair.Value ?? "");
                }
                w.Flush();
                SendRaw(ms.ToArray());
            }
        }

        private bool ReadSnapshot(byte[] data)
        {
            using (var r = new BinaryReader(new MemoryStream(data, 1, data.Length - 1)))
            {
                var hostId = new SteamId { Value = r.ReadUInt64() };
                int count = r.ReadInt32();
                var members = new List<SteamId>(count);
                var names = new Dictionary<SteamId, string>();
                for (int i = 0; i < count; i++)
                {
                    var id = new SteamId { Value = r.ReadUInt64() };
                    members.Add(id);
                    names[id] = r.ReadString();
                }
                int pairs = r.ReadInt32();
                var lobby = new Dictionary<string, string>();
                for (int i = 0; i < pairs; i++)
                {
                    string key = r.ReadString();
                    lobby[key] = r.ReadString();
                }

                string snapshotKey = hostId.Value + "|" + string.Join(",", members.Select(m => m.Value.ToString()))
                    + "|" + string.Join(";", lobby.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value));
                if (snapshotKey == lastSnapshotKey)
                {
                    return false;
                }
                lastSnapshotKey = snapshotKey;
                HostId = hostId;
                Members.Clear();
                Members.AddRange(members);
                Names.Clear();
                foreach (KeyValuePair<SteamId, string> pair in names)
                {
                    Names[pair.Key] = pair.Value;
                }
                LobbyData.Clear();
                foreach (KeyValuePair<string, string> pair in lobby)
                {
                    LobbyData[pair.Key] = pair.Value;
                }
                return true;
            }
        }
    }
}
