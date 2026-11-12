using System.Collections.Generic;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Scripted sequences in interludes and breakrooms (the habitation elevator ride, for one) start
    /// when a player walks into a one-shot trigger zone. Those used to start only on that player's PC.
    /// Now, in gated rooms:
    ///  - a one-shot zone waits until every living teammate is in the room (like the room's buttons),
    ///    so nobody is left behind when an elevator leaves, and
    ///  - when it fires, it fires on every PC.
    /// Listeners that act on the presser (camera, teleport, terminal) are left out on other PCs.
    /// </summary>
    public class ZoneSync : MonoBehaviour
    {
        public static ZoneSync Instance { get; private set; }

        private readonly Dictionary<UT_TriggerZone, Transform> waiting = new Dictionary<UT_TriggerZone, Transform>();
        private float lastNotice = -10f;

        internal static bool Applying;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.ZoneEntered, (origin, r) => Apply(r.ReadString()));
        }

        internal static bool Live => NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready && !Applying;

        internal static bool Shared(UT_TriggerZone zone, out M_Level level)
        {
            level = zone != null ? zone.GetComponentInParent<M_Level>() : null;
            return zone != null && zone.runOnce && InterludeGate.IsGatedRoom(level);
        }

        /// <summary>The local player walked in. Returns false to hold the zone until the team is here.</summary>
        internal bool LocalEnter(UT_TriggerZone zone, Transform who, M_Level level)
        {
            List<string> missing = MissingFor(zone, level, out string where);
            if (missing.Count > 0)
            {
                waiting[zone] = who;
                // Waits quietly: no text on screen.
                return false;
            }
            waiting.Remove(zone);
            Broadcast(zone);
            return true;
        }

        /// <summary>
        /// Who the zone is still waiting for. A door zone waits for nobody; an elevator's zone waits until
        /// everyone is standing in it (the elevator is the wait); anything else waits for the whole room.
        /// </summary>
        private static List<string> MissingFor(UT_TriggerZone zone, M_Level level, out string where)
        {
            InterludeGate.GateKind kind = InterludeGate.Classify(zone, level, zone.onEnter);
            if (kind == InterludeGate.GateKind.Free)
            {
                where = "the door";
                return new List<string>();
            }
            if (kind == InterludeGate.GateKind.Elevator)
            {
                where = "the elevator";
                Collider c = zone.GetComponent<Collider>();
                if (c != null)
                {
                    Bounds b = c.bounds;
                    b.Expand(3f);
                    return InterludeGate.Missing(p => b.Contains(p));
                }
            }
            where = "the room";
            return InterludeGate.Missing(level);
        }

        private void Broadcast(UT_TriggerZone zone)
        {
            string id = ButtonSync.ObjectId(zone);
            Plugin.Log.LogInfo("Room sequence started: " + id);
            NetSession.Instance.Send(PacketType.ZoneEntered, w => w.Write(id));
        }

        private void Update()
        {
            if (waiting.Count == 0 || !Live)
            {
                if (!Live) waiting.Clear();
                return;
            }
            foreach (var pair in new List<KeyValuePair<UT_TriggerZone, Transform>>(waiting))
            {
                UT_TriggerZone zone = pair.Key;
                if (zone == null || !Shared(zone, out M_Level level) || Traverse.Create(zone).Field("hasRun").GetValue<bool>())
                {
                    waiting.Remove(zone);
                    continue;
                }
                if (!LocalStillInside(zone))
                {
                    waiting.Remove(zone);
                    continue;
                }
                if (MissingFor(zone, level, out _).Count == 0)
                {
                    waiting.Remove(zone);
                    Broadcast(zone);
                    Fire(zone, pair.Value);
                }
            }
        }

        private static bool LocalStillInside(UT_TriggerZone zone)
        {
            Collider c = zone.GetComponent<Collider>();
            ENT_Player player = ENT_Player.playerObject;
            if (c == null || player == null)
            {
                return false;
            }
            Bounds b = c.bounds;
            b.Expand(2f);
            return b.Contains(player.transform.position);
        }

        private static void Fire(UT_TriggerZone zone, Transform who)
        {
            Traverse.Create(zone).Field("hasRun").SetValue(true);
            zone.onEnter?.Invoke(who != null ? who : zone.transform);
        }

        private void Apply(string id)
        {
            foreach (UT_TriggerZone zone in FindObjectsByType<UT_TriggerZone>(FindObjectsSortMode.None))
            {
                if (zone == null || ButtonSync.ObjectId(zone) != id)
                {
                    continue;
                }
                var hasRun = Traverse.Create(zone).Field("hasRun");
                if (hasRun.GetValue<bool>())
                {
                    return;
                }
                waiting.Remove(zone);
                // Pass our own player only if we're standing in the zone too; otherwise the zone itself,
                // so nothing grabs a player who isn't there.
                Transform who = LocalStillInside(zone) ? ENT_Player.playerObject.transform : zone.transform;
                Applying = true;
                ButtonSync.Applying = true;
                try
                {
                    hasRun.SetValue(true);
                    ButtonSync.RunShared(() => zone.onEnter?.Invoke(who), zone.onEnter);
                    Plugin.Log.LogInfo("Teammate started a room sequence: " + id);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"Teammate's room sequence failed here ({id}): {e.Message}");
                }
                finally
                {
                    Applying = false;
                    ButtonSync.Applying = false;
                }
                return;
            }
            Plugin.Log.LogInfo("Teammate started a room sequence that isn't loaded here: " + id);
        }
    }

    [HarmonyPatch(typeof(UT_TriggerZone), "OnTriggerEnter")]
    internal static class ZoneSyncPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(UT_TriggerZone __instance, Collider other)
        {
            if (!ZoneSync.Live || ZoneSync.Instance == null || other == null || DoorSync.Applying)
            {
                return true;
            }
            bool closeZone = DoorSync.Instance != null && DoorSync.IsCloseZone(__instance);
            M_Level level = null;
            if (!closeZone && !ZoneSync.Shared(__instance, out level))
            {
                return true;
            }
            ENT_Player player = ENT_Player.playerObject;
            if (player == null || other.GetComponentInParent<ENT_Player>() != player)
            {
                return true; // not our player (items, creatures): leave it to the game
            }
            if (__instance.runOnce && Traverse.Create(__instance).Field("hasRun").GetValue<bool>())
            {
                return true;
            }
            // Same test the zone itself makes, so we only act when it would really fire.
            ObjectTagger tagger = other.GetComponent<ObjectTagger>();
            if (tagger == null || !tagger.HasTagInList(__instance.entityTags)
                || (__instance.ignoreTags != null && __instance.ignoreTags.Length > 0 && tagger.HasTagInList(__instance.ignoreTags)))
            {
                return true;
            }
            if (closeZone)
            {
                // A door's close trigger: start the team's countdown instead of shutting it now.
                return DoorSync.Instance.OnLocalCloseZone(__instance);
            }
            return ZoneSync.Instance.LocalEnter(__instance, other.transform, level);
        }
    }
}
