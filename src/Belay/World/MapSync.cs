using System.Collections.Generic;
using System.Text;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Some places are a separate map: the shop and some artifact rooms are chambers (UT_ChamberLoader) that hide
    /// the whole world while you're inside. Each PC tells the team which map its player is in, so a revive or a
    /// spectator can load the same chamber first (instead of dropping into the hidden world, which is the void),
    /// and teammates in a different map aren't drawn.
    /// </summary>
    public class MapSync : MonoBehaviour
    {
        /// <summary>Where a player is. Loader null = the main map.</summary>
        private struct Where
        {
            public string Loader;
            public int Index;
            /// <summary>The chamber's root, in shared space.</summary>
            public Vector3 Root;
            /// <summary>Where leaving the chamber puts you back, in shared space.</summary>
            public Vector3 Exit;
            public Quaternion ExitRotation;

            public bool InChamber => !string.IsNullOrEmpty(Loader);

            /// <summary>
            /// Both in the main map, or both in the same room. A room sits where its loader is, so two rooms
            /// a few meters apart are the same one even if the ids were worked out differently on each PC.
            /// </summary>
            public bool SameMapAs(Where other) => InChamber == other.InChamber
                && (!InChamber || Loader == other.Loader || (Root - other.Root).sqrMagnitude < SameRoomDistance * SameRoomDistance);
        }

        private const float ResendInterval = 3f;

        /// <summary>Rooms whose roots are closer than this (meters, shared space) are the same room.</summary>
        private const float SameRoomDistance = 6f;

        private static readonly Dictionary<SteamId, Where> others = new Dictionary<SteamId, Where>();
        private static UT_ChamberLoader localLoader;
        private static M_Chamber localChamber;
        private static int localIndex = -1;
        private static bool changed = true;
        private float nextSend;

        /// <summary>While set, the next chamber a loader makes is this one of its list (to match a teammate's).</summary>
        internal static int ForcedIndex = -1;

        private void Start()
        {
            NetSession.Instance.On(PacketType.MapState, Handle);
            NetSession.Instance.OnPlayerLeft += id => others.Remove(id);
        }

        private void Update()
        {
            if (!NetSession.InLobbyRun)
            {
                others.Clear();
                localLoader = null;
                localChamber = null;
                changed = true;
                return;
            }
            if (localChamber == null && localLoader != null)
            {
                // The chamber went away without LeaveChamber (destroyed with its level).
                localLoader = null;
                changed = true;
            }
            if (!changed && Time.unscaledTime < nextSend)
            {
                return;
            }
            changed = false;
            nextSend = Time.unscaledTime + ResendInterval;
            Where me = Mine();
            NetSession.Instance.Send(PacketType.MapState, w =>
            {
                w.Write(me.Loader ?? "");
                w.Write(me.Index);
                w.WriteVector(me.Root);
                w.WriteVector(me.Exit);
                w.Write(me.ExitRotation.x);
                w.Write(me.ExitRotation.y);
                w.Write(me.ExitRotation.z);
                w.Write(me.ExitRotation.w);
            });
        }

        private static void Handle(SteamId origin, System.IO.BinaryReader r)
        {
            var where = new Where
            {
                Loader = r.ReadString(),
                Index = r.ReadInt32(),
                Root = r.ReadVector(),
                Exit = r.ReadVector(),
                ExitRotation = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
            };
            if (origin == NetSession.LocalId)
            {
                return;
            }
            bool was = others.TryGetValue(origin, out Where old);
            others[origin] = where;
            if (!was || !old.SameMapAs(where))
            {
                Plugin.Log.LogInfo(NetSession.NameOf(origin) + (where.InChamber ? " went into " + where.Loader + " #" + where.Index : " is in the main map"));
            }
        }

        private static Where Mine()
        {
            if (localLoader == null || localChamber == null)
            {
                return default;
            }
            Traverse t = Traverse.Create(localLoader);
            return new Where
            {
                Loader = LoaderId(localLoader),
                Index = localIndex,
                Root = WorldSpace.ToShared(localChamber.transform.position),
                Exit = WorldSpace.ToShared(t.Field("exitPlayerPosition").GetValue<Vector3>()),
                ExitRotation = t.Field("exitPlayerRotation").GetValue<Quaternion>(),
            };
        }

        /// <summary>The local player is inside a chamber (the world is hidden).</summary>
        internal static bool LocalInChamber => localLoader != null && localChamber != null;

        /// <summary>
        /// This teammate is in the same map as the local player (or we haven't heard, which counts as the same).
        /// </summary>
        internal static bool SameMap(SteamId id)
        {
            if (!others.TryGetValue(id, out Where theirs))
            {
                return !LocalInChamber;
            }
            return theirs.SameMapAs(Mine());
        }

        /// <summary>This teammate is inside a chamber, as far as we know.</summary>
        internal static bool InChamber(SteamId id) => others.TryGetValue(id, out Where w) && w.InChamber;

        /// <summary>
        /// Puts the local player in the same map as this teammate: into their chamber (the same one of the
        /// loader's list), or back out to the main map. False while their chamber can't be made here yet (its
        /// level isn't built on this PC).
        /// </summary>
        internal static bool Follow(SteamId id)
        {
            Where theirs = others.TryGetValue(id, out Where w) ? w : default;
            if (theirs.SameMapAs(Mine()))
            {
                return true;
            }
            if (LocalInChamber)
            {
                LeaveLocalChamber();
            }
            if (!theirs.InChamber)
            {
                return true;
            }
            UT_ChamberLoader loader = FindLoader(theirs.Loader, WorldSpace.ToLocal(theirs.Root));
            if (loader == null)
            {
                return false;
            }
            try
            {
                Traverse t = Traverse.Create(loader);
                M_Chamber spawned = t.Field("spawnedChamber").GetValue<M_Chamber>();
                if (spawned != null && IndexOf(loader, spawned) != theirs.Index)
                {
                    // Kept from an earlier visit, but a different room than theirs.
                    Destroy(spawned.gameObject);
                    t.Field("spawnedChamber").SetValue(null);
                }
                ForcedIndex = theirs.Index;
                loader.EnterChamber();
                // Leaving puts you back where they came in, not where the spectator camera happened to be.
                t.Field("exitPlayerPosition").SetValue(WorldSpace.ToLocal(theirs.Exit));
                t.Field("exitPlayerRotation").SetValue(theirs.ExitRotation);
                Plugin.Log.LogInfo("Went into " + NetSession.NameOf(id) + "'s room " + theirs.Loader + " #" + theirs.Index);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't load " + NetSession.NameOf(id) + "'s room: " + e.Message);
                return false;
            }
            finally
            {
                ForcedIndex = -1;
            }
            return LocalInChamber;
        }

        /// <summary>
        /// A spot a teammate gave in their own chamber, moved to the same place in ours (the rooms can sit at a
        /// slightly different height on each PC).
        /// </summary>
        internal static Vector3 FromTheirChamber(SteamId id, Vector3 sharedSpot)
        {
            if (!LocalInChamber || !others.TryGetValue(id, out Where theirs) || !theirs.InChamber)
            {
                return WorldSpace.ToLocal(sharedSpot);
            }
            return localChamber.transform.position + (sharedSpot - theirs.Root);
        }

        internal static void LeaveLocalChamber()
        {
            M_Chamber chamber = localChamber;
            if (chamber == null)
            {
                return;
            }
            try
            {
                chamber.LeaveChamber();
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't leave the room: " + e.Message);
                AccessTools.Method(typeof(WorldLoader), "ShowWorld")?.Invoke(null, null);
            }
            localLoader = null;
            localChamber = null;
            changed = true;
        }

        private static readonly Dictionary<string, UT_ChamberLoader> loaderCache = new Dictionary<string, UT_ChamberLoader>();

        private static UT_ChamberLoader FindLoader(string id, Vector3 localRoot)
        {
            if (loaderCache.TryGetValue(id, out UT_ChamberLoader cached) && cached != null)
            {
                return cached;
            }
            UT_ChamberLoader nearest = null;
            float best = SameRoomDistance * SameRoomDistance;
            foreach (UT_ChamberLoader l in FindObjectsByType<UT_ChamberLoader>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (l == null)
                {
                    continue;
                }
                if (LoaderId(l) == id)
                {
                    loaderCache[id] = l;
                    return l;
                }
                // The room is made right where its loader stands.
                float d = (l.transform.position - localRoot).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    nearest = l;
                }
            }
            if (nearest != null)
            {
                loaderCache[id] = nearest;
            }
            return nearest;
        }

        /// <summary>
        /// Same on every PC: the level's name and where the loader stands (shared space, to 0.1 m). No sibling
        /// numbers: things leaving a level (picked-up items, creatures) shift them differently on each PC, which
        /// made teammates in the same shop count as being in different rooms.
        /// </summary>
        internal static string LoaderId(Component c)
        {
            M_Level level = c.GetComponentInParent<M_Level>(true);
            Vector3 p = WorldSpace.ToShared(c.transform.position);
            string at = Mathf.RoundToInt(p.x * 10f) + "," + Mathf.RoundToInt(p.y * 10f) + "," + Mathf.RoundToInt(p.z * 10f);
            return (level != null ? level.name : c.gameObject.scene.name) + "/" + c.name + "@" + at;
        }

        private static int IndexOf(UT_ChamberLoader loader, M_Chamber chamber)
        {
            if (loader.chambers == null || chamber == null)
            {
                return -1;
            }
            string name = chamber.name.Replace("(Clone)", "").Trim();
            for (int i = 0; i < loader.chambers.Count; i++)
            {
                if (loader.chambers[i] != null && loader.chambers[i].name == name)
                {
                    return i;
                }
            }
            return -1;
        }

        internal static void Entered(UT_ChamberLoader loader)
        {
            localLoader = loader;
            localChamber = Traverse.Create(loader).Field("spawnedChamber").GetValue<M_Chamber>();
            localIndex = IndexOf(loader, localChamber);
            changed = true;
        }

        internal static void Left(UT_ChamberLoader loader)
        {
            if (localLoader == loader)
            {
                localLoader = null;
                localChamber = null;
                changed = true;
            }
        }
    }

    [HarmonyPatch(typeof(UT_ChamberLoader))]
    internal static class ChamberLoaderPatches
    {
        /// <summary>Makes the teammate's room instead of a random one, so both PCs have the same room.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(UT_ChamberLoader.LoadChamber))]
        private static bool LoadPrefix(UT_ChamberLoader __instance)
        {
            int index = MapSync.ForcedIndex;
            if (index < 0 || __instance.chambers == null || index >= __instance.chambers.Count || __instance.chambers[index] == null)
            {
                return true;
            }
            M_Chamber spawned = Object.Instantiate(__instance.chambers[index], __instance.transform.position, Quaternion.identity, null);
            spawned.SetParentLoader(__instance);
            try
            {
                // The game seeds each room's contents from its loader's random numbers; keep that going.
                object rng = Traverse.Create(__instance).Field("rng").GetValue();
                if (rng != null)
                {
                    Traverse.Create(spawned).Field("seedSalt").SetValue(Traverse.Create(rng).Method("Next").GetValue<int>());
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't seed the room: " + e.Message);
            }
            spawned.gameObject.SetActive(false);
            WorldLoader.instance?.GetHandholdManager()?.LoadHandholds(spawned.gameObject);
            Traverse.Create(__instance).Field("spawnedChamber").SetValue(spawned);
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(UT_ChamberLoader.EnterChamber))]
        private static void EnterPostfix(UT_ChamberLoader __instance)
        {
            if (NetSession.InLobbyRun)
            {
                MapSync.Entered(__instance);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(UT_ChamberLoader.LeaveChamber))]
        private static void LeavePostfix(UT_ChamberLoader __instance)
        {
            MapSync.Left(__instance);
        }
    }
}
