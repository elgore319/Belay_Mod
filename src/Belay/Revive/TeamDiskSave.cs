using System;
using System.Collections.Generic;
using Belay.Net;
using Steamworks;
using UnityEngine;

namespace Belay.Revive
{
    /// <summary>
    /// In co-op a disk save belongs to the whole team. Whoever reformats disks at a save terminal makes
    /// the game's own disk save, and the save (the level it was made in, the levels after it and the
    /// mass) is sent to everyone, so every PC holds the same save under the same id. It's never used
    /// while anyone is still up: only when the whole team is down, instead of the game over, the host
    /// tells everyone to come back from it, and each PC runs the game's own disk revive. Everybody lands
    /// back at that terminal's level together, so nobody is brought back alone behind the mass.
    /// </summary>
    internal static class TeamDiskSave
    {
        private const CL_SaveManager.SaveState.SaveType Disk = CL_SaveManager.SaveState.SaveType.disk;

        /// <summary>What a disk save looks like on the wire. The player part stays on each PC.</summary>
        [Serializable]
        private class Wire
        {
            public string id;
            public string flag;
            public int amount;
            public string gamemode;
            public List<M_Level.SaveData> levelData;
            public List<WorldLoader.BranchInfo.LevelId> levelGenerationList;
            public DEN_DeathFloor.SaveData gooData;
            public float playerTravelDistance;
            public float playerBestTravelDistance;
            public Vector3 playerLastTravelCheckpointOffset;
        }

        internal static void Listen()
        {
            NetSession.Instance.On(PacketType.TeamDiskSave, (origin, r) => Receive(origin, r.ReadString()));
        }

        /// <summary>The local player just made (or added to) the disk save with this id: send it to the team.</summary>
        internal static void Share(string id)
        {
            CL_SaveManager.SaveState save = Find(id);
            if (save == null)
            {
                Plugin.Log.LogWarning("Made a disk save but couldn't find it to share: " + id);
                return;
            }
            var wire = new Wire
            {
                id = save.id,
                flag = save.flag,
                amount = save.amount,
                gamemode = save.gamemode,
                levelData = new List<M_Level.SaveData>(),
                levelGenerationList = save.levelGenerationList,
                gooData = save.gooData,
                playerTravelDistance = save.playerTravelDistance,
                playerBestTravelDistance = save.playerBestTravelDistance,
                playerLastTravelCheckpointOffset = save.playerLastTravelCheckpointOffset,
            };
            foreach (M_Level.SaveData level in save.levelData ?? new List<M_Level.SaveData>())
            {
                // A disk revive rebuilds the level fresh, without its saved creatures and loose items.
                var copy = JsonUtility.FromJson<M_Level.SaveData>(JsonUtility.ToJson(level));
                copy.baseEntitySaves = new List<GameEntity.BaseEntitySaveData>();
                wire.levelData.Add(copy);
            }
            string json = JsonUtility.ToJson(wire);
            NetSession.Instance.Send(PacketType.TeamDiskSave, w => w.Write(json));
            Plugin.Log.LogInfo($"Shared the team disk save {save.id} ({save.amount} lives, {json.Length} chars)");
        }

        private static void Receive(SteamId origin, string json)
        {
            if (origin == NetSession.LocalId)
            {
                return;
            }
            Wire wire = JsonUtility.FromJson<Wire>(json);
            if (wire == null || string.IsNullOrEmpty(wire.id) || wire.levelData == null || wire.levelData.Count == 0)
            {
                Plugin.Log.LogWarning("Got an empty team disk save from " + NetSession.NameOf(origin));
                return;
            }
            CL_SaveManager.SaveState save = Find(wire.id);
            if (save == null)
            {
                save = new CL_SaveManager.SaveState { id = wire.id, type = Disk };
            }
            save.flag = string.IsNullOrEmpty(wire.flag) ? "disk" : wire.flag;
            save.amount = Mathf.Max(wire.amount, 0);
            save.gamemode = wire.gamemode;
            save.levelData = wire.levelData;
            save.levelGenerationList = wire.levelGenerationList;
            save.gooData = wire.gooData;
            save.playerTravelDistance = wire.playerTravelDistance;
            save.playerBestTravelDistance = wire.playerBestTravelDistance;
            save.playerLastTravelCheckpointOffset = wire.playerLastTravelCheckpointOffset;
            save.clearItems = false;
            try
            {
                // Refreshed from the player again right before it's used; this just keeps it whole.
                if (ENT_Player.playerObject != null)
                {
                    save.UpdatePlayerData(ENT_Player.playerObject);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Team disk save: couldn't record the player: " + e.Message);
            }
            MakeMostRecent(save);
            Plugin.Log.LogInfo($"{NetSession.NameOf(origin)} made a team disk save at {wire.id} in {wire.levelData[0].levelName} ({save.amount} lives)");
        }

        private static CL_SaveManager.SaveState Find(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            foreach (CL_SaveManager.SaveState s in CL_SaveManager.saveStates)
            {
                if (s != null && s.type == Disk && s.id == id)
                {
                    return s;
                }
            }
            return null;
        }

        /// <summary>The game revives from the newest disk save, so put this one last.</summary>
        private static void MakeMostRecent(CL_SaveManager.SaveState save)
        {
            CL_SaveManager.saveStates.Remove(save);
            CL_SaveManager.saveStates.Add(save);
        }

        /// <summary>The disk save the team would come back from now, if there's one with a life left.</summary>
        internal static bool Available(out string id)
        {
            CL_SaveManager.SaveState save = CL_SaveManager.GetMostRecentSaveStateByType(Disk);
            id = save != null && save.amount > 0 ? save.id : null;
            return id != null;
        }

        /// <summary>
        /// Make the save with this id the one the game's disk revive uses. False if this PC has no disk
        /// save at all. Without that exact one (its message never came), the newest one here is used.
        /// </summary>
        internal static bool Prepare(string id)
        {
            CL_SaveManager.SaveState save = Find(id);
            if (save != null && save.amount > 0)
            {
                MakeMostRecent(save);
                return true;
            }
            if (Available(out string other))
            {
                Plugin.Log.LogWarning($"Team disk save {id} isn't here; coming back from {other} instead");
                return true;
            }
            return false;
        }
    }
}
