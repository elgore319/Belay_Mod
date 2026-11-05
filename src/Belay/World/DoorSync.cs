using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Shared doors. Opening a door opens it for everyone. Closing one (the first player through its
    /// trigger) starts a host-run countdown that everyone sees, and the door then shuts for all players at
    /// the same moment, so nobody gets locked out just for being a few seconds behind.
    /// </summary>
    public class DoorSync : MonoBehaviour
    {
        public const float DefaultCloseDelay = 15f;
        public const float MinCloseDelay = 10f;
        public const float MaxCloseDelay = 20f;

        public static DoorSync Instance { get; private set; }

        /// <summary>Set while Belay itself drives a door, so the patches let the call through.</summary>
        internal static bool Applying;
        /// <summary>Set while a door runs its own Start/FixedUpdate logic, which every client does by itself.</summary>
        internal static bool InDoorSelfUpdate;

        /// <summary>Door id -> time (Time.unscaledTime) it shuts. Host is authoritative; clients mirror it for the HUD.</summary>
        private readonly Dictionary<string, float> countdowns = new Dictionary<string, float>();
        private readonly Dictionary<string, UT_Door> doorCache = new Dictionary<string, UT_Door>();

        public float CloseDelay
        {
            get
            {
                string raw = NetSession.Instance.GetLobbyData(NetSession.KeyDoorDelay);
                return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float delay)
                    ? Mathf.Clamp(delay, MinCloseDelay, MaxCloseDelay)
                    : DefaultCloseDelay;
            }
        }

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession net = NetSession.Instance;
            net.On(PacketType.DoorOpen, (origin, r) => ApplyOpen(r.ReadString()));
            net.On(PacketType.DoorRequestClose, (origin, r) => HostStartCountdown(r.ReadString()));
            net.On(PacketType.DoorCountdown, HandleCountdown);
            net.On(PacketType.DoorClose, HandleClose);
            net.OnLobbyChanged += () =>
            {
                if (!net.InLobby)
                {
                    countdowns.Clear();
                }
            };
        }

        private void Update()
        {
            NetSession net = NetSession.Instance;
            if (!net.InLobby || countdowns.Count == 0)
            {
                return;
            }
            if (!WorldSpace.Ready)
            {
                countdowns.Clear();
                return;
            }
            if (!net.IsHost)
            {
                return;
            }
            List<string> due = null;
            foreach (KeyValuePair<string, float> entry in countdowns)
            {
                if (Time.unscaledTime >= entry.Value)
                {
                    (due ?? (due = new List<string>())).Add(entry.Key);
                }
            }
            if (due == null)
            {
                return;
            }
            foreach (string id in due)
            {
                countdowns.Remove(id);
                net.Send(PacketType.DoorClose, w => w.Write(id));
                ApplyClose(id);
            }
        }

        /// <summary>Seconds left on the countdown of the nearest closing door, or null.</summary>
        public float? NearestCountdown(out float distance)
        {
            distance = 0f;
            float? best = null;
            float bestDistance = float.MaxValue;
            if (!WorldSpace.Ready)
            {
                return null;
            }
            Vector3 me = ENT_Player.playerObject.transform.position;
            foreach (KeyValuePair<string, float> entry in countdowns)
            {
                // Cache only: this runs from OnGUI several times a frame.
                UT_Door door = Find(entry.Key, allowScan: false);
                float d = door != null ? Vector3.Distance(((Component)door).transform.position, me) : float.MaxValue - 1f;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = Mathf.Max(0f, entry.Value - Time.unscaledTime);
                }
            }
            distance = bestDistance;
            return best;
        }

        // ---------------------------------------------------------------- Local door events

        /// <summary>Returns false when the game's own close must be held back.</summary>
        internal bool OnLocalClose(UT_Door door)
        {
            if (!door.IsOpen())
            {
                return true;
            }
            string id = DoorId(door);
            NetSession net = NetSession.Instance;
            if (net.IsHost)
            {
                HostStartCountdown(id);
            }
            else if (!countdowns.ContainsKey(id))
            {
                net.Send(PacketType.DoorRequestClose, w => w.Write(id));
            }
            return false;
        }

        internal void OnLocalOpen(UT_Door door)
        {
            string id = DoorId(door);
            // Someone reopened it (lever, button): cancel a pending shut.
            countdowns.Remove(id);
            NetSession.Instance.Send(PacketType.DoorOpen, w => w.Write(id));
        }

        // ---------------------------------------------------------------- Host

        private void HostStartCountdown(string id)
        {
            NetSession net = NetSession.Instance;
            if (!net.IsHost || countdowns.ContainsKey(id))
            {
                return;
            }
            float delay = CloseDelay;
            countdowns[id] = Time.unscaledTime + delay;
            Find(id);
            net.Send(PacketType.DoorCountdown, w =>
            {
                w.Write(id);
                w.Write(delay);
            });
        }

        // ---------------------------------------------------------------- Clients

        private void HandleCountdown(SteamId origin, System.IO.BinaryReader reader)
        {
            string id = reader.ReadString();
            float delay = reader.ReadSingle();
            if (origin == NetSession.Instance.HostId)
            {
                countdowns[id] = Time.unscaledTime + delay;
                Find(id);
            }
        }

        private void HandleClose(SteamId origin, System.IO.BinaryReader reader)
        {
            string id = reader.ReadString();
            if (origin == NetSession.Instance.HostId)
            {
                countdowns.Remove(id);
                ApplyClose(id);
            }
        }

        private void ApplyOpen(string id)
        {
            countdowns.Remove(id);
            UT_Door door = Find(id);
            if (door == null)
            {
                return;
            }
            Applying = true;
            try
            {
                door.Open();
            }
            finally
            {
                Applying = false;
            }
        }

        private void ApplyClose(string id)
        {
            UT_Door door = Find(id);
            if (door == null)
            {
                // Not loaded here (far away); it will be closed already by the time this player gets there.
                return;
            }
            Applying = true;
            try
            {
                door.Close();
            }
            finally
            {
                Applying = false;
            }
        }

        // ---------------------------------------------------------------- Ids

        private UT_Door Find(string id, bool allowScan = true)
        {
            if (doorCache.TryGetValue(id, out UT_Door cached) && cached != null)
            {
                return cached;
            }
            if (!allowScan)
            {
                return null;
            }
            foreach (UT_Door door in Object.FindObjectsByType<UT_Door>(FindObjectsSortMode.None))
            {
                string doorId = DoorId(door);
                doorCache[doorId] = door;
                if (doorId == id)
                {
                    return door;
                }
            }
            return null;
        }

        /// <summary>
        /// Same on every client: the owning level's name and (offset-corrected, rounded) height, then the
        /// sibling-index path inside it. Sibling indices above the level aren't used because each client
        /// loads and unloads levels on its own schedule.
        /// </summary>
        public static string DoorId(UT_Door door)
        {
            Transform t = ((Component)door).transform;
            M_Level level = t.GetComponentInParent<M_Level>();
            Transform root = level != null ? ((Component)level).transform : null;
            var path = new StringBuilder();
            Transform cursor = t;
            while (cursor != null && cursor != root)
            {
                path.Insert(0, "/" + cursor.GetSiblingIndex() + cursor.name);
                cursor = cursor.parent;
            }
            if (root == null)
            {
                return ((Component)door).gameObject.scene.name + path;
            }
            int height = Mathf.RoundToInt(WorldSpace.ToShared(root.position).y);
            return root.name + "@" + height + path;
        }
    }

    [HarmonyPatch(typeof(UT_Door))]
    internal static class DoorPatches
    {
        private static bool Synced =>
            !DoorSync.Applying && !DoorSync.InDoorSelfUpdate
            && NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready;

        [HarmonyPatch("Start")]
        [HarmonyPrefix]
        private static void StartPrefix() => DoorSync.InDoorSelfUpdate = true;

        [HarmonyPatch("Start")]
        [HarmonyFinalizer]
        private static void StartFinalizer() => DoorSync.InDoorSelfUpdate = false;

        [HarmonyPatch("FixedUpdate")]
        [HarmonyPrefix]
        private static void FixedUpdatePrefix() => DoorSync.InDoorSelfUpdate = true;

        [HarmonyPatch("FixedUpdate")]
        [HarmonyFinalizer]
        private static void FixedUpdateFinalizer() => DoorSync.InDoorSelfUpdate = false;

        [HarmonyPatch(nameof(UT_Door.Close))]
        [HarmonyPrefix]
        private static bool Close(UT_Door __instance)
        {
            return !Synced || DoorSync.Instance.OnLocalClose(__instance);
        }

        [HarmonyPatch(nameof(UT_Door.Open))]
        [HarmonyPrefix]
        private static void Open(UT_Door __instance, bool ___open)
        {
            if (Synced && !___open)
            {
                DoorSync.Instance.OnLocalOpen(__instance);
            }
        }
    }
}
