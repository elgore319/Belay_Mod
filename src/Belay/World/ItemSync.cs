using System.Collections.Generic;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Items in the world are shared:
    ///  - Every item lying in a level gets the same id on every PC (level, height and place in the
    ///    level, taken when it spawns). When someone picks one up, it disappears for everyone else.
    ///  - When someone drops or throws an item, a copy appears for everyone at the same spot with the
    ///    same contents (charge, uses, notes...), and whoever grabs it first takes it from the others.
    /// What you carry stays yours: inventories and wallets are never shared.
    /// </summary>
    public class ItemSync : MonoBehaviour
    {
        public static ItemSync Instance { get; private set; }

        /// <summary>World items we know about, by network id, and back.</summary>
        private readonly Dictionary<string, Item_Object> byId = new Dictionary<string, Item_Object>();
        private readonly Dictionary<Item_Object, string> idOf = new Dictionary<Item_Object, string>();
        /// <summary>Copies we made of teammates' drops: they appear even if the game's spawn rules would hide them.</summary>
        internal readonly HashSet<Item_Object> RemoteCopies = new HashSet<Item_Object>();
        private int dropCounter;
        private int runId;

        internal static bool Applying;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.ItemPickup, (origin, r) => ApplyPickup(r.ReadString()));
            NetSession.Instance.On(PacketType.ItemDrop, HandleDrop);
        }

        internal static bool Live => Instance != null && NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready && !Applying;

        private void Update()
        {
            NetSession net = NetSession.Instance;
            if (net != null && net.InLobby && runId != net.CurrentRun.RunId)
            {
                runId = net.CurrentRun.RunId;
                byId.Clear();
                idOf.Clear();
                RemoteCopies.Clear();
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
            if (obj == null || idOf.ContainsKey(obj) || obj.GetComponentInParent<M_Level>() == null)
            {
                return;
            }
            Register("lvl:" + ButtonSync.ObjectId(obj), obj);
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
            Forget(obj);
            RemoteCopies.Remove(obj);
            Plugin.Log.LogInfo($"Picked up {item.prefabName} ({id})");
            NetSession.Instance.Send(PacketType.ItemPickup, w => w.Write(id));
        }

        private void ApplyPickup(string id)
        {
            if (!byId.TryGetValue(id, out Item_Object obj) || obj == null)
            {
                Plugin.Log.LogInfo("Teammate picked up an item that isn't here: " + id);
                return;
            }
            Forget(obj);
            RemoteCopies.Remove(obj);
            Item item = obj.itemData;
            if (item != null && item.inventory != null)
            {
                // Somehow it's already in our hands (we grabbed it at the same moment): keep ours.
                return;
            }
            Plugin.Log.LogInfo("Teammate picked up " + (item?.prefabName ?? obj.name) + " (" + id + ")");
            Destroy(obj.gameObject);
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
            if (ItemSync.Instance != null && NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready)
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
            if (ItemSync.Live && __instance == Inventory.instance && item != null)
            {
                ItemSync.Instance.OnLocalDrop(item);
            }
        }
    }
}
