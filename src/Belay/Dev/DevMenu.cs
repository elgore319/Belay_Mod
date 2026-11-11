using System.Collections.Generic;
using Belay.Players;
using Steamworks;
using UnityEngine;

namespace Belay.Dev
{
    /// <summary>
    /// The Dev tab of the Belay menu: teleporting around the map for testing. It only moves your own
    /// player (teammates see you arrive through normal position sync) and adds no keys of its own.
    /// </summary>
    internal static class DevMenu
    {
        private static Vector2 levelScroll;
        private static bool moveGoo = true;
        private static bool invincible;
        private static bool hasSaved;
        private static Vector3 saved;
        private static string lastAction = "";

        /// <summary>
        /// Keeps invincibility on while the box is ticked. The game turns its god mode off on a new
        /// player object and in some scripted events, so it's re-applied every frame.
        /// </summary>
        internal static void Tick()
        {
            ENT_Player player = ENT_Player.playerObject;
            if (invincible && player != null && !player.GetGodMode())
            {
                player.SetGodMode(true);
            }
        }

        public static void DrawTab()
        {
            ENT_Player player = ENT_Player.playerObject;
            WorldLoader loader = WorldLoader.instance;
            if (!CL_GameManager.IsInGame() || player == null || loader == null || loader.GetCurrentBranch() == null)
            {
                GUILayout.Label("Start a run to use the teleports.");
                return;
            }
            if (player.dead)
            {
                GUILayout.Label("You're dead. Teleports work again once you're revived.");
                return;
            }

            Vector3 pos = player.transform.position;
            WorldLoader.LevelInfo current = loader.GetCurrentLevel();
            GUILayout.Label($"Height {pos.y:0} m, in {LevelName(current?.GetLevel())}");
            bool want = GUILayout.Toggle(invincible, " Invincible (nothing can hurt or kill you)");
            if (want != invincible)
            {
                invincible = want;
                player.SetGodMode(want);
                Plugin.Log.LogInfo("[Dev] Invincible " + (want ? "on" : "off"));
            }
            moveGoo = GUILayout.Toggle(moveGoo, " Push the mass 20 m below me when I teleport");
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Next level"))
            {
                // The game's own console command: it generates the next level first if it isn't there yet.
                try
                {
                    loader.TeleportPlayerToNextLevel(new string[0]);
                    Done("Teleported to the next level");
                }
                catch (System.Exception e)
                {
                    lastAction = "Couldn't go to the next level: " + e.Message;
                }
            }
            if (GUILayout.Button("Where I'm looking"))
            {
                TeleportToLook(player);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save this spot"))
            {
                saved = pos;
                hasSaved = true;
                lastAction = $"Saved spot at {pos.y:0} m";
            }
            GUI.enabled = hasSaved;
            if (GUILayout.Button(hasSaved ? $"Back to saved spot ({saved.y:0} m)" : "Back to saved spot"))
            {
                Teleport(player, saved, "Back at the saved spot");
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            DrawTeammates(player);

            GUILayout.Space(4);
            GUILayout.Label("Levels on this path (top is highest)");
            List<WorldLoader.LevelInfo> levels = loader.GetCurrentBranch().levelTracker;
            levelScroll = GUILayout.BeginScrollView(levelScroll, GUILayout.Height(200));
            for (int i = levels.Count - 1; i >= 0; i--)
            {
                M_Level level = levels[i]?.GetLevel();
                if (level == null)
                {
                    continue;
                }
                string name = LevelName(level);
                bool here = levels[i] == current;
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{i + 1}. {name}{(here ? "  (you)" : "")}");
                if (GUILayout.Button("Go", GUILayout.Width(50)))
                {
                    Teleport(player, LevelStart(level), "Teleported to " + name);
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            if (lastAction.Length > 0)
            {
                GUILayout.Label(lastAction);
            }
        }

        private static void DrawTeammates(ENT_Player player)
        {
            PlayerSync sync = PlayerSync.Instance;
            if (sync == null || sync.Remotes.Count == 0)
            {
                return;
            }
            GUILayout.Space(4);
            GUILayout.Label("Teammates");
            foreach (KeyValuePair<SteamId, RemotePlayer> pair in sync.Remotes)
            {
                RemotePlayer remote = pair.Value;
                if (remote == null || remote.Ghost)
                {
                    continue;
                }
                GUILayout.BeginHorizontal();
                bool dead = remote.Latest.Dead;
                GUILayout.Label($"{remote.DisplayName}{(dead ? " (dead)" : $"  {remote.LocalPosition.y:0} m")}");
                GUI.enabled = !dead;
                if (GUILayout.Button("Go", GUILayout.Width(50)))
                {
                    Teleport(player, remote.LocalPosition + Vector3.up * 0.5f, "Teleported to " + remote.DisplayName);
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
        }

        private static void TeleportToLook(ENT_Player player)
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                return;
            }
            Ray ray = new Ray(cam.transform.position, cam.transform.forward);
            if (!Physics.Raycast(ray, out RaycastHit hit, 500f, ~0, QueryTriggerInteraction.Ignore))
            {
                lastAction = "Nothing within 500 m in that direction";
                return;
            }
            // Step back off the surface so you don't end up inside a wall.
            Vector3 target = hit.point + hit.normal * 0.8f;
            if (hit.normal.y > 0.5f)
            {
                target = hit.point + Vector3.up * 1f;
            }
            Teleport(player, target, $"Teleported {Vector3.Distance(player.transform.position, target):0} m");
        }

        private static void Teleport(ENT_Player player, Vector3 target, string what)
        {
            player.Teleport(target);
            player.ResetFallingHeight();
            player.AddIgnoreFallDamageTime(5f);
            Done(what);
        }

        private static void Done(string what)
        {
            lastAction = what;
            Plugin.Log.LogInfo("[Dev] " + what);
            ENT_Player player = ENT_Player.playerObject;
            if (moveGoo && player != null && DEN_DeathFloor.instance != null
                && DEN_DeathFloor.instance.transform.position.y > player.transform.position.y - 20f)
            {
                DEN_DeathFloor.instance.Teleport(player.transform.position - Vector3.up * 20f);
            }
        }

        private static Vector3 LevelStart(M_Level level)
        {
            if (level.spawnPosition != null)
            {
                return level.GetSpawnPosition();
            }
            Transform entrance = level.GetLevelEntrance();
            return entrance != null ? entrance.position + Vector3.up * 5f : level.GetSpawnPosition();
        }

        private static string LevelName(M_Level level)
        {
            if (level == null)
            {
                return "unknown level";
            }
            return string.IsNullOrEmpty(level.levelName) ? level.name.Replace("(Clone)", "") : level.levelName;
        }
    }

    /// <summary>Runs the Dev tab's per-frame upkeep while the menu is closed.</summary>
    internal class DevTools : MonoBehaviour
    {
        private void Update()
        {
            DevMenu.Tick();
        }
    }
}
