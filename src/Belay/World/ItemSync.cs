using System.Collections.Generic;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Shared world items: each gets the same id on every PC (level, height, place). Pickups vanish for everyone;
    /// drops and throws appear for everyone with the same contents, first grab wins. A level's only item of its
    /// kind is one per player. Inventories and wallets are never shared.
    /// </summary>
    public class ItemSync : MonoBehaviour
    {
        public static ItemSync Instance { get; private set; }

        /// <summary>World items we know about, by network id, and back.</summary>
        private readonly Dictionary<string, Item_Object> byId = new Dictionary<string, Item_Object>();
        private readonly Dictionary<Item_Object, string> idOf = new Dictionary<Item_Object, string>();
        /// <summary>Copies we made of teammates' drops: they appear even if the game's spawn rules would hide them.</summary>
        internal readonly HashSet<Item_Object> RemoteCopies = new HashSet<Item_Object>();
        /// <summary>Our own drops still flying or rolling: once they stop, teammates' copies snap to the same spot.</summary>
        private readonly List<(string id, Item_Object obj, float time)> settling = new List<(string, Item_Object, float)>();
        /// <summary>
        /// Count of each item kind per level (or loose, with spots). A level's only item of its kind is a one-off:
        /// everyone gets their own copy.
        /// </summary>
        private readonly Dictionary<string, int> levelCounts = new Dictionary<string, int>();
        private readonly Dictionary<Item_Object, string> levelKeyOf = new Dictionary<Item_Object, string>();
        private readonly List<(string kind, Vector3 shared)> looseSpawns = new List<(string, Vector3)>();
        private const float LooseSingleRadius = 30f;
        private int dropCounter;
        private int runId;

        /// <summary>
        /// Who got each shared item, as the host decided. If two players grab at once, the first grab to reach the
        /// host wins and the other copy goes away. Kept on every PC for host handover.
        /// </summary>
        private readonly Dictionary<string, ulong> claims = new Dictionary<string, ulong>();
        /// <summary>What the local player took, by id, until the host has decided who keeps it.</summary>
        private readonly Dictionary<string, Item> takenHere = new Dictionary<string, Item>();

        internal static bool Applying;

        /// <summary>A save disk is going into a terminal: it's used up, not dropped for the team to see.</summary>
        internal static bool Inserting;

        /// <summary>Items a personal machine (vendor, disk vendor) made for the local player only: never shared.</summary>
        internal readonly HashSet<Item_Object> Personal = new HashSet<Item_Object>();

        /// <summary>
        /// Shared items a teammate took while their level wasn't built here yet. When the level builds here, they
        /// must not show up again: grabbing one would only have it taken back out of your hand.
        /// </summary>
        private readonly HashSet<string> takenAway = new HashSet<string>();
        private readonly List<(string kind, Vector3 shared)> takenAwayLoose = new List<(string, Vector3)>();

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.ItemPickup, (origin, r) =>
            {
                string id = r.ReadString();
                bool oneOff = r.ReadBoolean();
                if (NetSession.Instance.IsHost)
                {
                    HostClaim(id, origin, oneOff);
                }
            });
            NetSession.Instance.On(PacketType.ItemTaken, HandleTaken);
            NetSession.Instance.On(PacketType.ItemDenied, HandleDenied);
            NetSession.Instance.On(PacketType.ItemDrop, HandleDrop);
            NetSession.Instance.On(PacketType.ItemSettled, HandleSettled);
        }

        internal static bool Live => Instance != null && NetSession.InLobbyRun && !Applying;

        private void Update()
        {
            NetSession net = NetSession.Instance;
            if (net != null && net.InLobby && runId != net.CurrentRun.RunId)
            {
                runId = net.CurrentRun.RunId;
                byId.Clear();
                idOf.Clear();
                RemoteCopies.Clear();
                settling.Clear();
                levelCounts.Clear();
                levelKeyOf.Clear();
                looseSpawns.Clear();
                claims.Clear();
                takenHere.Clear();
                Personal.Clear();
                ForgetTakenAway();
            }
            UpdateSettling();
        }

        private void UpdateSettling()
        {
            for (int i = settling.Count - 1; i >= 0; i--)
            {
                var (id, obj, time) = settling[i];
                if (obj == null || !idOf.TryGetValue(obj, out string current) || current != id || !obj.gameObject.activeInHierarchy)
                {
                    settling.RemoveAt(i);
                    continue;
                }
                float age = Time.time - time;
                if (age < 0.4f)
                {
                    continue;
                }
                Rigidbody rb = obj.GetComponent<Rigidbody>();
                bool resting = rb == null || rb.isKinematic || rb.IsSleeping() || rb.linearVelocity.sqrMagnitude < 0.01f;
                if (!resting && age < 6f)
                {
                    continue;
                }
                settling.RemoveAt(i);
                if (!Live)
                {
                    continue;
                }
                Vector3 pos = WorldSpace.ToShared(obj.transform.position);
                Quaternion rot = obj.transform.rotation;
                NetSession.Instance.Send(PacketType.ItemSettled, w =>
                {
                    w.Write(id);
                    w.WriteVector(pos);
                    w.Write(rot.x); w.Write(rot.y); w.Write(rot.z); w.Write(rot.w);
                });
            }
        }

        private void HandleSettled(SteamId origin, System.IO.BinaryReader r)
        {
            string id = r.ReadString();
            Vector3 pos = WorldSpace.ToLocal(r.ReadVector());
            var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (!WorldSpace.Ready || !byId.TryGetValue(id, out Item_Object obj) || obj == null)
            {
                return;
            }
            if (obj.itemData != null && obj.itemData.inventory != null)
            {
                return;
            }
            // Our copy rolled somewhere slightly different: put it where the dropper sees it.
            obj.transform.SetPositionAndRotation(pos, rot);
            Rigidbody rb = obj.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.position = pos;
                rb.rotation = rot;
            }
        }

        private void Register(string id, Item_Object obj)
        {
            if (byId.TryGetValue(id, out Item_Object old) && old != null && old != obj)
            {
                idOf.Remove(old);
            }
            if (idOf.TryGetValue(obj, out string oldId))
            {
                byId.Remove(oldId);
            }
            byId[id] = obj;
            idOf[obj] = id;
        }

        private void Forget(Item_Object obj)
        {
            if (obj != null && idOf.TryGetValue(obj, out string id))
            {
                idOf.Remove(obj);
                byId.Remove(id);
            }
        }

        // ---------------------------------------------------------------- Level items

        /// <summary>Called as an item spawns, while it still sits where the level put it.</summary>
        internal void OnItemSpawned(Item_Object obj)
        {
            if (obj == null || idOf.ContainsKey(obj) || Personal.Contains(obj))
            {
                return;
            }
            if (obj.GetComponentInParent<M_Level>() != null)
            {
                Register("lvl:" + ButtonSync.ObjectId(obj), obj);
                return;
            }
            // Spawned loose in the world (by a spawner or a creature, outside any level's hierarchy):
            // the seeded spawn puts it at the same spot on every PC, so its kind and spot are its id.
            Register(PosId(obj), obj);
        }

        private static string Kind(Item_Object obj)
        {
            string name = obj.itemData != null && !string.IsNullOrEmpty(obj.itemData.prefabName) ? obj.itemData.prefabName : obj.name;
            return name.Replace("(Clone)", "").Trim();
        }

        private static string PosId(Item_Object obj)
        {
            Vector3 p = WorldSpace.ToShared(obj.transform.position);
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "pos:{0}:{1:F1}:{2:F1}:{3:F1}", Kind(obj), p.x, p.y, p.z);
        }

        /// <summary>A loose item's id can differ by a rounding step between PCs: find the same kind nearby.</summary>
        private Item_Object FindLoose(string id)
        {
            string[] parts = id.Split(':');
            if (parts.Length < 5)
            {
                return null;
            }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string kind = string.Join(":", parts, 1, parts.Length - 4);
            if (!float.TryParse(parts[parts.Length - 3], System.Globalization.NumberStyles.Float, inv, out float x)
                || !float.TryParse(parts[parts.Length - 2], System.Globalization.NumberStyles.Float, inv, out float y)
                || !float.TryParse(parts[parts.Length - 1], System.Globalization.NumberStyles.Float, inv, out float z))
            {
                return null;
            }
            Vector3 target = new Vector3(x, y, z);
            Item_Object best = null;
            float bestDist = 1.5f;
            foreach (var pair in byId)
            {
                if (pair.Value == null || !pair.Key.StartsWith("pos:", System.StringComparison.Ordinal) || Kind(pair.Value) != kind)
                {
                    continue;
                }
                float d = Vector3.Distance(WorldSpace.ToShared(pair.Value.transform.position), target);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = pair.Value;
                }
            }
            return best;
        }

        /// <summary>
        /// A teammate took this item before its level was built here: it goes away as it appears. True if it went.
        /// </summary>
        internal bool RemoveIfTakenAway(Item_Object obj)
        {
            if (obj == null || RemoteCopies.Contains(obj) || !idOf.TryGetValue(obj, out string id)
                || (obj.itemData != null && obj.itemData.inventory != null))
            {
                return false;
            }
            bool gone = takenAway.Remove(id);
            if (!gone && id.StartsWith("pos:", System.StringComparison.Ordinal))
            {
                string kind = Kind(obj);
                Vector3 here = WorldSpace.ToShared(obj.transform.position);
                for (int i = 0; i < takenAwayLoose.Count; i++)
                {
                    if (takenAwayLoose[i].kind == kind && Vector3.Distance(takenAwayLoose[i].shared, here) < 1.5f)
                    {
                        takenAwayLoose.RemoveAt(i);
                        gone = true;
                        break;
                    }
                }
            }
            if (!gone)
            {
                return false;
            }
            Forget(obj);
            Plugin.Log.LogInfo($"A teammate already took {Kind(obj)} ({id}) before it was built here: removed");
            obj.gameObject.SetActive(false);
            Destroy(obj.gameObject);
            return true;
        }

        /// <summary>The world was rebuilt from a save (team disk revive): earlier pickups no longer apply.</summary>
        internal void ForgetTakenAway()
        {
            takenAway.Clear();
            takenAwayLoose.Clear();
        }

        /// <summary>Called once an item has really appeared in the world (the game's spawn rules let it show).</summary>
        internal void OnItemShown(Item_Object obj)
        {
            if (obj == null || RemoteCopies.Contains(obj) || levelKeyOf.ContainsKey(obj) || !idOf.ContainsKey(obj))
            {
                return;
            }
            string kind = Kind(obj);
            M_Level level = obj.GetComponentInParent<M_Level>();
            if (level != null)
            {
                string key = level.GetInstanceID() + "|" + kind;
                levelKeyOf[obj] = key;
                levelCounts.TryGetValue(key, out int n);
                levelCounts[key] = n + 1;
            }
            else
            {
                levelKeyOf[obj] = "";
                looseSpawns.Add((kind, WorldSpace.ToShared(obj.transform.position)));
            }
        }

        /// <summary>The flare gun and its ammo are needed by everyone: each player always has their own to pick up.</summary>
        private static bool AlwaysOwn(string kind) => kind == "Item_Flaregun" || kind == "Item_Flaregun_Ammo";

        /// <summary>
        /// Finds (disks, totems, artifacts, the handgun and its ammo) are one for the whole team even when
        /// they're the only one around: whoever picks one up takes it away for everyone.
        /// </summary>
        private static bool AlwaysShared(string kind)
        {
            return kind.StartsWith("Item_Floppy", System.StringComparison.Ordinal)
                || kind.StartsWith("Item_Artifact", System.StringComparison.Ordinal)
                || kind.StartsWith("Item_Handgun", System.StringComparison.Ordinal)
                || kind == "Item_10mm_Ammo"
                || kind.IndexOf("Totem", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Was this the only item of its kind where it spawned?</summary>
        private bool IsOneOff(Item_Object obj, string id)
        {
            if (id.StartsWith("drop:", System.StringComparison.Ordinal))
            {
                return false;
            }
            string what = Kind(obj);
            if (AlwaysOwn(what))
            {
                return true;
            }
            if (AlwaysShared(what) || !levelKeyOf.TryGetValue(obj, out string key))
            {
                return false;
            }
            if (key.Length > 0)
            {
                return levelCounts.TryGetValue(key, out int n) && n == 1;
            }
            string kind = Kind(obj);
            Vector3 here = WorldSpace.ToShared(obj.transform.position);
            int near = 0;
            foreach (var (k, p) in looseSpawns)
            {
                if (k == kind && Vector3.Distance(p, here) < LooseSingleRadius)
                {
                    near++;
                }
            }
            return near <= 1;
        }

        // ---------------------------------------------------------------- Picking up

        /// <summary>The local player is taking this item out of the world.</summary>
        internal void OnLocalTake(Item item)
        {
            Item_Object obj = item?.GetDropObject();
            if (obj == null || !idOf.TryGetValue(obj, out string id))
            {
                return;
            }
            bool oneOff = IsOneOff(obj, id);
            Forget(obj);
            RemoteCopies.Remove(obj);
            Plugin.Log.LogInfo($"Picked up {item.prefabName} ({id})");
            if (!oneOff)
            {
                // A one-off is everyone's own copy, so nobody else's grab can take it from us.
                takenHere[id] = item;
            }
            if (NetSession.Instance.IsHost)
            {
                HostClaim(id, NetSession.LocalId, oneOff);
            }
            else
            {
                NetSession.Instance.Send(PacketType.ItemPickup, w =>
                {
                    w.Write(id);
                    w.Write(oneOff);
                });
            }
        }

        /// <summary>
        /// A bot (host only) picks up a world item: gone for everyone, like a player pickup. One-offs are left for
        /// you. The object is kept switched off so the bot can drop or hand it over.
        /// </summary>
        internal bool BotTake(Item_Object obj, SteamId bot)
        {
            if (!Live || !NetSession.Instance.IsHost || obj == null || !idOf.TryGetValue(obj, out string id)
                || IsOneOff(obj, id) || claims.ContainsKey(id))
            {
                return false;
            }
            Forget(obj);
            RemoteCopies.Remove(obj);
            claims[id] = bot.Value;
            ulong winner = bot.Value;
            NetSession.Instance.Send(PacketType.ItemTaken, w =>
            {
                w.Write(id);
                w.Write(winner);
                w.Write(false);
            });
            obj.gameObject.SetActive(false);
            // It travels with the bot now, so the level it came from unloading mustn't take it.
            obj.transform.SetParent(null, true);
            DontDestroyOnLoad(obj.gameObject);
            Plugin.Log.LogInfo($"{NetSession.NameOf(bot)} picked up {obj.itemData?.prefabName} ({id})");
            return true;
        }

        /// <summary>A bot drops an item it carried (it died, or you asked): it shows up for everyone, like your drops.</summary>
        internal void BotDrop(Item item, Vector3 localPos, Vector3 velocity)
        {
            Item_Object obj = item?.GetDropObject();
            if (obj == null)
            {
                return;
            }
            obj.transform.position = localPos;
            obj.gameObject.SetActive(true);
            Rigidbody rb = obj.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic)
            {
                rb.linearVelocity = velocity;
            }
            if (Live)
            {
                OnLocalDrop(item);
            }
        }

        /// <summary>Host: the first grab of a shared item keeps it; a later grab by someone else is undone.</summary>
        private void HostClaim(string id, SteamId taker, bool oneOff)
        {
            if (!oneOff && claims.TryGetValue(id, out ulong owner) && owner != taker.Value)
            {
                string first = NetSession.NameOf(new SteamId { Value = owner });
                Plugin.Log.LogInfo($"{NetSession.NameOf(taker)} grabbed {id} after {first}: theirs goes");
                ulong loser = taker.Value;
                NetSession.Instance.Send(PacketType.ItemDenied, w =>
                {
                    w.Write(id);
                    w.Write(loser);
                });
                ApplyDenied(id, loser);
                return;
            }
            ulong winner = taker.Value;
            NetSession.Instance.Send(PacketType.ItemTaken, w =>
            {
                w.Write(id);
                w.Write(winner);
                w.Write(oneOff);
            });
            ApplyTaken(id, winner, oneOff);
        }

        private void HandleTaken(SteamId origin, System.IO.BinaryReader r)
        {
            string id = r.ReadString();
            ulong winner = r.ReadUInt64();
            bool oneOff = r.ReadBoolean();
            if (origin == NetSession.Instance.HostId && !NetSession.Instance.IsHost)
            {
                ApplyTaken(id, winner, oneOff);
            }
        }

        private void HandleDenied(SteamId origin, System.IO.BinaryReader r)
        {
            string id = r.ReadString();
            ulong loser = r.ReadUInt64();
            if (origin == NetSession.Instance.HostId && !NetSession.Instance.IsHost)
            {
                ApplyDenied(id, loser);
            }
        }

        private void ApplyTaken(string id, ulong winner, bool oneOff)
        {
            if (!oneOff)
            {
                claims[id] = winner;
            }
            if (winner == NetSession.LocalId.Value)
            {
                takenHere.Remove(id);
                return;
            }
            if (takenHere.ContainsKey(id))
            {
                // We grabbed it too, but later: the host sends us a denial for ours.
                return;
            }
            ApplyPickup(id, oneOff);
        }

        private void ApplyDenied(string id, ulong loser)
        {
            if (loser != NetSession.LocalId.Value)
            {
                return;
            }
            if (!takenHere.TryGetValue(id, out Item item))
            {
                return;
            }
            takenHere.Remove(id);
            if (item != null && item.inventory != null && !item.hasBeenDestroyed)
            {
                Plugin.Log.LogInfo($"A teammate grabbed {item.prefabName} ({id}) first: it's theirs");
                item.Destroy();
            }
        }

        private void ApplyPickup(string id, bool oneOff)
        {
            if ((!byId.TryGetValue(id, out Item_Object obj) || obj == null) && id.StartsWith("pos:", System.StringComparison.Ordinal))
            {
                obj = FindLoose(id);
            }
            if (obj == null)
            {
                Plugin.Log.LogInfo("Teammate picked up an item that isn't here: " + id);
                if (!oneOff && !id.StartsWith("drop:", System.StringComparison.Ordinal))
                {
                    // Its level isn't built here yet: once it is, the item mustn't be there to grab.
                    if (id.StartsWith("pos:", System.StringComparison.Ordinal))
                    {
                        RememberLooseTaken(id);
                    }
                    else
                    {
                        takenAway.Add(id);
                    }
                }
                return;
            }
            Item item = obj.itemData;
            if (IsOneOff(obj, id))
            {
                // One per player: the teammate took theirs, this one is still yours.
                Plugin.Log.LogInfo("Teammate took their copy of " + (item?.prefabName ?? obj.name) + " (" + id + "), yours stays");
                return;
            }
            Forget(obj);
            RemoteCopies.Remove(obj);
            if (item != null && item.inventory != null)
            {
                // Somehow it's already in our hands (we grabbed it at the same moment): keep ours.
                return;
            }
            Plugin.Log.LogInfo("Teammate picked up " + (item?.prefabName ?? obj.name) + " (" + id + ")");
            Destroy(obj.gameObject);
        }

        private void RememberLooseTaken(string id)
        {
            string[] parts = id.Split(':');
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (parts.Length >= 5
                && float.TryParse(parts[parts.Length - 3], System.Globalization.NumberStyles.Float, inv, out float x)
                && float.TryParse(parts[parts.Length - 2], System.Globalization.NumberStyles.Float, inv, out float y)
                && float.TryParse(parts[parts.Length - 1], System.Globalization.NumberStyles.Float, inv, out float z))
            {
                takenAwayLoose.Add((string.Join(":", parts, 1, parts.Length - 4), new Vector3(x, y, z)));
            }
        }

        // ---------------------------------------------------------------- Dropping

        internal void OnLocalDrop(Item item)
        {
            Item_Object obj = item?.GetDropObject();
            if (obj == null || string.IsNullOrEmpty(item.prefabName))
            {
                return;
            }
            string id = "drop:" + NetSession.LocalId.Value + ":" + (++dropCounter);
            Register(id, obj);
            SaveableInfo info;
            try
            {
                info = obj.GetSaveInfo();
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't read dropped {item.prefabName}: {e.Message}");
                info = new SaveableInfo { id = item.prefabName, data = "" };
            }
            Vector3 pos = WorldSpace.ToShared(obj.transform.position);
            Quaternion rot = obj.transform.rotation;
            Vector3 vel = Vector3.zero;
            Rigidbody rb = obj.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic)
            {
                vel = rb.linearVelocity;
                if (vel.sqrMagnitude < 0.01f && Camera.main != null)
                {
                    // The throw force applies on the next physics step; rebuild it.
                    vel = Camera.main.transform.forward * (item.dropVel + 2f);
                }
            }
            settling.Add((id, obj, Time.time));
            Plugin.Log.LogInfo($"Dropped {item.prefabName} ({id})");
            NetSession.Instance.Send(PacketType.ItemDrop, w =>
            {
                w.Write(id);
                w.Write(item.prefabName);
                w.Write(info.data ?? "");
                w.WriteVector(pos);
                w.Write(rot.x); w.Write(rot.y); w.Write(rot.z); w.Write(rot.w);
                w.WriteVector(vel);
            });
        }

        private void HandleDrop(SteamId origin, System.IO.BinaryReader r)
        {
            string id = r.ReadString();
            string prefabName = r.ReadString();
            string data = r.ReadString();
            Vector3 pos = WorldSpace.ToLocal(r.ReadVector());
            var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            Vector3 vel = r.ReadVector();
            if (!WorldSpace.Ready || byId.ContainsKey(id))
            {
                return;
            }
            Item_Object prefab = CL_AssetManager.GetItemObjectPrefab(prefabName);
            if (prefab == null)
            {
                GameObject asset = CL_AssetManager.GetAssetGameObject(prefabName);
                prefab = asset != null ? asset.GetComponent<Item_Object>() : null;
            }
            if (prefab == null)
            {
                Plugin.Log.LogWarning("Teammate dropped an item this game doesn't know: " + prefabName);
                return;
            }
            Applying = true;
            try
            {
                Item_Object obj = Instantiate(prefab, pos, rot, WorldLoader.GetCurrentLevelParentRoot());
                obj.gameObject.SetActive(true);
                if (!string.IsNullOrEmpty(data))
                {
                    try
                    {
                        obj.SetSaveInfo(new SaveableInfo { id = prefabName, data = data }, new List<string>());
                    }
                    catch (System.Exception e)
                    {
                        Plugin.Log.LogWarning($"Couldn't copy the dropped {prefabName}'s contents: {e.Message}");
                    }
                }
                Rigidbody rb = obj.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = false;
                    rb.linearVelocity = vel;
                }
                RemoteCopies.Add(obj);
                Register(id, obj);
                Plugin.Log.LogInfo($"Teammate dropped {prefabName} ({id})");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't place teammate's {prefabName}: {e.Message}");
            }
            finally
            {
                Applying = false;
            }
        }
    }

    [HarmonyPatch]
    internal static class ItemSyncPatches
    {
        // Record level items' ids before anything moves them.
        [HarmonyPatch(typeof(Item_Object), nameof(Item_Object.Start))]
        [HarmonyPrefix]
        private static void ItemStart(Item_Object __instance)
        {
            if (ItemSync.Instance != null && NetSession.InLobbyRun)
            {
                ItemSync.Instance.OnItemSpawned(__instance);
            }
        }

        // A teammate's drop always shows up, even where the spawn rules would hide that item.
        [HarmonyPatch(typeof(Item_Object), nameof(Item_Object.Start))]
        [HarmonyPostfix]
        private static void ItemStartPostfix(Item_Object __instance)
        {
            if (ItemSync.Instance != null && !__instance.gameObject.activeSelf && ItemSync.Instance.RemoteCopies.Contains(__instance))
            {
                __instance.gameObject.SetActive(true);
            }
            if (ItemSync.Instance != null && NetSession.InLobbyRun && ItemSync.Instance.RemoveIfTakenAway(__instance))
            {
                return;
            }
            if (ItemSync.Instance != null && __instance.gameObject.activeSelf && NetSession.InLobbyRun)
            {
                ItemSync.Instance.OnItemShown(__instance);
            }
        }

        // Taking an item from the world, into a hand or straight into the bag.
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItemToHand), new[] { typeof(Item), typeof(ENT_Player.Hand) })]
        [HarmonyPrefix]
        private static void AddToHand(Inventory __instance, Item i)
        {
            if (ItemSync.Live && __instance == Inventory.instance && i != null)
            {
                ItemSync.Instance.OnLocalTake(i);
            }
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItemToInventoryScreen))]
        [HarmonyPrefix]
        private static void AddToBag(Inventory __instance, Item item)
        {
            if (ItemSync.Live && __instance == Inventory.instance && item != null)
            {
                ItemSync.Instance.OnLocalTake(item);
            }
        }

        // Dropping or throwing into the world.
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.DropItemIntoWorld))]
        [HarmonyPostfix]
        private static void Drop(Inventory __instance, Item item)
        {
            if (ItemSync.Live && __instance == Inventory.instance && item != null && !ItemSync.Inserting)
            {
                ItemSync.Instance.OnLocalDrop(item);
            }
        }

        // A disk going into a terminal or reader leaves the hand through a drop, then is used up: it was never
        // put down, so teammates mustn't get a copy of it lying there.
        [HarmonyPatch(typeof(Item_InteractionModule_Floppy), nameof(Item_InteractionModule_Floppy.Interact))]
        [HarmonyPrefix]
        private static void FloppyInsertStart() => ItemSync.Inserting = true;

        [HarmonyPatch(typeof(Item_InteractionModule_Floppy), nameof(Item_InteractionModule_Floppy.Interact))]
        [HarmonyFinalizer]
        private static void FloppyInsertEnd() => ItemSync.Inserting = false;

        // What a personal machine hands out (a bought disk, a vended item) is the buyer's alone. Each PC only buys
        // for its own player, so these never get a shared id (two players' purchases would share one).
        [HarmonyPatch(typeof(ENV_Vendor_Disk), nameof(ENV_Vendor_Disk.SpawnDisk))]
        [HarmonyPrefix]
        private static void DiskVendorBefore(ENV_Vendor_Disk __instance, out HashSet<Item_Object> __state) => __state = ItemsNear(__instance);

        [HarmonyPatch(typeof(ENV_Vendor_Disk), nameof(ENV_Vendor_Disk.SpawnDisk))]
        [HarmonyPostfix]
        private static void DiskVendorAfter(ENV_Vendor_Disk __instance, HashSet<Item_Object> __state) => MarkPersonal(__instance, __state);

        [HarmonyPatch(typeof(ENV_VendingMachine), nameof(ENV_VendingMachine.Buy), new[] { typeof(int), typeof(bool), typeof(bool) })]
        [HarmonyPrefix]
        private static void VendingBefore(ENV_VendingMachine __instance, out HashSet<Item_Object> __state) => __state = ItemsNear(__instance);

        [HarmonyPatch(typeof(ENV_VendingMachine), nameof(ENV_VendingMachine.Buy), new[] { typeof(int), typeof(bool), typeof(bool) })]
        [HarmonyPostfix]
        private static void VendingAfter(ENV_VendingMachine __instance, HashSet<Item_Object> __state) => MarkPersonal(__instance, __state);

        private static HashSet<Item_Object> ItemsNear(Component machine)
        {
            var set = new HashSet<Item_Object>();
            Transform parent = machine != null ? machine.transform.parent : null;
            if (parent != null && ItemSync.Instance != null && NetSession.InLobbyRun)
            {
                set.UnionWith(parent.GetComponentsInChildren<Item_Object>(true));
            }
            return set;
        }

        private static void MarkPersonal(Component machine, HashSet<Item_Object> before)
        {
            Transform parent = machine != null ? machine.transform.parent : null;
            if (parent == null || ItemSync.Instance == null || !NetSession.InLobbyRun)
            {
                return;
            }
            foreach (Item_Object obj in parent.GetComponentsInChildren<Item_Object>(true))
            {
                if (obj != null && !before.Contains(obj))
                {
                    ItemSync.Instance.Personal.Add(obj);
                }
            }
        }
    }
}
