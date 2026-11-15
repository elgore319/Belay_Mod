using System.Collections.Generic;
using System.IO;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// The game's random world events (weather, blizzards, event spawns, announcements) are rolled by
    /// CL_EventManager on each PC, every time that PC's own player enters a level or region and once a
    /// second, each with its own dice. So teammates got different events.
    ///
    /// In a lobby only the host rolls. Every event the host's roll starts is sent to the others, who start
    /// the same event (seeded the same way, so its own random set-up matches as far as it can). Events
    /// that a level starts on purpose (scripted ones) are left to each PC, since the level is the same.
    /// A PC that hasn't reached a level yet starts the event as soon as it has one.
    /// </summary>
    public class WorldEvents : MonoBehaviour
    {
        public static WorldEvents Instance { get; private set; }

        internal static bool Applying;

        private const float PendingTimeout = 60f;

        private readonly List<(string id, float time)> pending = new List<(string, float)>();
        private readonly Dictionary<string, SessionEvent> byId = new Dictionary<string, SessionEvent>();
        private int runId;

        private static readonly AccessTools.FieldRef<Dictionary<string, SessionEvent>> CurrentDict =
            AccessTools.StaticFieldRefAccess<Dictionary<string, SessionEvent>>(AccessTools.Field(typeof(CL_EventManager), "currentEventDict"));
        private static readonly AccessTools.FieldRef<Dictionary<string, SessionEvent>> PreviousDict =
            AccessTools.StaticFieldRefAccess<Dictionary<string, SessionEvent>>(AccessTools.Field(typeof(CL_EventManager), "previousEventDict"));

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.EventStarted, HandleStarted);
        }

        internal static bool InLobby => NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready;

        internal static int SeedFor(string id)
        {
            unchecked
            {
                int h = NetSession.Instance.Seed * 486187739 + 0x3E7;
                foreach (char c in id ?? "")
                {
                    h = h * 31 + c;
                }
                return h;
            }
        }

        /// <summary>Host: tell everyone which events this roll started.</summary>
        internal void OnHostStarted(List<SessionEvent> started)
        {
            foreach (SessionEvent e in started)
            {
                if (e == null || string.IsNullOrEmpty(e.id))
                {
                    continue;
                }
                string id = e.id;
                NetSession.Instance.Send(PacketType.EventStarted, w => w.Write(id));
                Plugin.Log.LogInfo("World event started for the team: " + id);
            }
        }

        private void HandleStarted(SteamId origin, BinaryReader r)
        {
            string id = r.ReadString();
            if (NetSession.Instance.IsHost)
            {
                return;
            }
            if (!TryStart(id))
            {
                pending.Add((id, Time.unscaledTime));
            }
        }

        private void Update()
        {
            NetSession net = NetSession.Instance;
            if (net != null && net.InLobby && runId != net.CurrentRun.RunId)
            {
                runId = net.CurrentRun.RunId;
                pending.Clear();
                byId.Clear();
            }
            if (pending.Count == 0)
            {
                return;
            }
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (TryStart(pending[i].id) || Time.unscaledTime - pending[i].time > PendingTimeout)
                {
                    pending.RemoveAt(i);
                }
            }
        }

        private bool TryStart(string id)
        {
            if (!InLobby || CL_EventManager.currentLevel == null)
            {
                return false; // not in a level yet: try again soon
            }
            Dictionary<string, SessionEvent> current = CurrentDict();
            Dictionary<string, SessionEvent> previous = PreviousDict();
            if (current != null && current.ContainsKey(id))
            {
                return true; // already running here
            }
            SessionEvent asset = Find(id);
            if (asset == null)
            {
                Plugin.Log.LogWarning("World event from the host not found here: " + id);
                return true;
            }
            if (asset.playOnce && previous != null && previous.ContainsKey(id))
            {
                return true;
            }
            Random.State state = Random.state;
            Random.InitState(SeedFor(id));
            Applying = true;
            try
            {
                CL_EventManager.StartEvent(asset);
                Plugin.Log.LogInfo("Started the host's world event: " + id);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't start world event {id}: {e.Message}");
            }
            finally
            {
                Applying = false;
                Random.state = state;
            }
            return true;
        }

        private SessionEvent Find(string id)
        {
            if (byId.TryGetValue(id, out SessionEvent known) && known != null)
            {
                return known;
            }
            SessionEvent found = null;
            foreach (List<SessionEventList> lists in new[] { CL_EventManager.currentSubregion?.sessionEventLists, CL_EventManager.currentRegion?.sessionEventLists })
            {
                if (lists == null) continue;
                foreach (SessionEventList list in lists)
                {
                    if (list?.events == null) continue;
                    foreach (SessionEvent e in list.events)
                    {
                        if (e != null && e.id == id) { found = e; break; }
                    }
                    if (found != null) break;
                }
                if (found != null) break;
            }
            if (found == null)
            {
                foreach (SessionEvent e in Resources.FindObjectsOfTypeAll<SessionEvent>())
                {
                    if (e != null && e.id == id && !e.name.EndsWith("(Clone)"))
                    {
                        found = e;
                        break;
                    }
                }
            }
            if (found != null)
            {
                byId[id] = found;
            }
            return found;
        }
    }

    [HarmonyPatch(typeof(CL_EventManager), nameof(CL_EventManager.CheckEventList))]
    internal static class WorldEventRollPatch
    {
        /// <summary>Clients don't roll: they get the host's events.</summary>
        [HarmonyPrefix]
        private static bool Prefix(ref List<SessionEvent> __result)
        {
            if (!WorldEvents.InLobby || WorldEvents.Applying || NetSession.Instance.IsHost)
            {
                return true;
            }
            __result = new List<SessionEvent>();
            return false;
        }

        [HarmonyPostfix]
        private static void Postfix(List<SessionEvent> __result)
        {
            if (__result == null || __result.Count == 0 || !WorldEvents.InLobby || WorldEvents.Applying || !NetSession.Instance.IsHost || WorldEvents.Instance == null)
            {
                return;
            }
            try
            {
                WorldEvents.Instance.OnHostStarted(__result);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("World events not shared: " + e.Message);
            }
        }
    }
}
