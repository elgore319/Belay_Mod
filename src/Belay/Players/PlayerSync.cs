using System.Collections.Generic;
using Belay.Net;
using Steamworks;
using UnityEngine;

namespace Belay.Players
{
    /// <summary>Sends the local player's state and keeps one RemotePlayer per teammate.</summary>
    public class PlayerSync : MonoBehaviour
    {
        private static readonly Color[] Palette =
        {
            new Color(1f, 0.45f, 0.2f),
            new Color(0.3f, 0.75f, 1f),
            new Color(0.5f, 1f, 0.4f),
            new Color(1f, 0.85f, 0.25f),
            new Color(0.85f, 0.45f, 1f),
            new Color(1f, 0.4f, 0.6f),
            new Color(0.35f, 1f, 0.85f),
            new Color(0.9f, 0.9f, 0.9f),
        };

        public static PlayerSync Instance { get; private set; }

        public readonly Dictionary<SteamId, RemotePlayer> Remotes = new Dictionary<SteamId, RemotePlayer>();

        private float sendTimer;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.OnPlayerState += HandleState;
            NetSession.Instance.OnPlayerLeft += RemoveRemote;
            NetSession.Instance.OnLobbyChanged += PruneRemotes;
        }

        private void Update()
        {
            NetSession net = NetSession.Instance;
            if (!net.InLobby || !WorldSpace.Ready)
            {
                return;
            }
            sendTimer -= Time.unscaledDeltaTime;
            if (sendTimer <= 0f)
            {
                sendTimer = 1f / Mathf.Max(1, Plugin.SendRate.Value);
                net.BroadcastState(CaptureLocal());
            }
        }

        public static PlayerState CaptureLocal()
        {
            ENT_Player player = ENT_Player.playerObject;
            Transform camTransform = player.cam != null ? player.cam.transform : player.transform;
            Vector3 euler = camTransform.eulerAngles;
            return new PlayerState
            {
                Position = WorldSpace.ToShared(player.transform.position),
                Yaw = euler.y,
                Pitch = euler.x,
                LeftHand = WorldSpace.ToShared(HandPosition(player, 0)),
                RightHand = WorldSpace.ToShared(HandPosition(player, 1)),
                LeftHanging = IsHandHanging(player, 0),
                RightHanging = IsHandHanging(player, 1),
                Dead = player.dead,
                InventoryOpen = Inventory.IsShowingInventory(),
            };
        }

        private static Vector3 HandPosition(ENT_Player player, int index)
        {
            ENT_Player.Hand hand = player.hands[index];
            return hand.handModel != null ? hand.handModel.position : player.transform.position;
        }

        private static bool IsHandHanging(ENT_Player player, int index)
        {
            return player.hands[index].interactState == ENT_Player.InteractType.hanging;
        }

        private void HandleState(SteamId id, PlayerState state)
        {
            if (!Remotes.TryGetValue(id, out RemotePlayer remote) || remote == null)
            {
                remote = RemotePlayer.Create(id, NetSession.NameOf(id), ColorFor(id));
                Remotes[id] = remote;
            }
            remote.Apply(state);
        }

        private Color ColorFor(SteamId id)
        {
            int index = NetSession.Instance.MemberOrder().IndexOf(id);
            return Palette[(index < 0 ? Remotes.Count : index) % Palette.Length];
        }

        private void RemoveRemote(SteamId id)
        {
            if (Remotes.TryGetValue(id, out RemotePlayer remote))
            {
                if (remote != null)
                {
                    Destroy(remote.gameObject);
                }
                Remotes.Remove(id);
            }
        }

        private void PruneRemotes()
        {
            var members = new HashSet<SteamId>(NetSession.Instance.MemberOrder());
            foreach (SteamId id in new List<SteamId>(Remotes.Keys))
            {
                if (!members.Contains(id))
                {
                    RemoveRemote(id);
                }
            }
        }
    }
}
