using System.Linq;
using System.Globalization;
using Belay.Net;
using Belay.Revive;
using Belay.Rope;
using Belay.Run;
using Belay.World;
using BepInEx.Configuration;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace Belay.UI
{
    /// <summary>
    /// The Belay mod menu. It's drawn with IMGUI on its own (no game UI objects are created, moved or
    /// patched, so other mods' menus are untouched), opens from a small "Belay" button on the main and
    /// pause menus or the menu key, and only captures input while it is open.
    /// </summary>
    public class BelayMenu : MonoBehaviour
    {
        private enum Tab
        {
            Lobby,
            Players,
            Rules,
            Mods,
            Bot,
            Dev,
            Settings,
        }

        private static readonly string[] TabLabels = { "Lobby", "Players", "Rules", "Mods", "Bot", "Dev", "Settings" };

        private bool open;

        /// <summary>True while the menu is open in a run: mouse look and new grabs are ignored (see MenuInputPatches).</summary>
        internal static bool CapturingInput { get; private set; }

        /// <summary>True while a text box in the menu has the keyboard, so typing doesn't move the player.</summary>
        internal static bool Typing => CapturingInput && GUIUtility.keyboardControl != 0;
        private Tab tab;
        private Rect window = new Rect(40, 40, 440, 520);
        private Vector2 modScroll;
        private ConfigEntry<KeyCode> rebinding;
        private bool lockedCamera;
        private Vector2 scroll;
        private string lobbyName = "";
        private LobbyPrivacy privacy = LobbyPrivacy.FriendsOnly;
        private Lobby[] found = new Lobby[0];
        private bool searching;
        private CursorLockMode previousLock;
        private bool previousVisible;
        private float catchFlash;

        private static readonly string[] PrivacyLabels = { "Public", "Friends", "Private" };

        private void Update()
        {
            if (Input.GetKeyDown(Plugin.MenuKey.Value))
            {
                SetOpen(!open);
            }
            CapturingInput = open && CL_GameManager.IsInGame();
            if (open)
            {
                // In a run the cursor stays inside the game window, so a click can't land on another
                // monitor and pull focus away from the game.
                Cursor.lockState = CL_GameManager.IsInGame() ? CursorLockMode.Confined : CursorLockMode.None;
                Cursor.visible = true;
            }
            else if (ShouldBeLocked())
            {
                // Playing with a free, hidden cursor lets it drift onto a second monitor, and the next
                // click there tabs you out of the game. The game only locks it on spawn and unpause.
                Cursor.lockState = CursorLockMode.Locked;
            }
            if (Tether.Instance != null && Tether.Instance.CaughtThisFrame)
            {
                catchFlash = 1f;
            }
            catchFlash = Mathf.Max(0f, catchFlash - Time.unscaledDeltaTime);
        }

        private void SetOpen(bool value)
        {
            if (value == open)
            {
                return;
            }
            open = value;
            rebinding = null;
            if (open)
            {
                previousLock = Cursor.lockState;
                previousVisible = Cursor.visible;
                ModManager.Refresh();
                // Stop mouse-look while clicking around, unless the game is paused anyway.
                ENT_Player player = ENT_Player.playerObject;
                if (player != null && CL_GameManager.gMan != null && !CL_GameManager.gMan.isPaused && !player.dead)
                {
                    player.LockCamera();
                    lockedCamera = true;
                }
            }
            else
            {
                // Closing the menu used to put back the cursor state from when it was opened. Opened from
                // the pause menu, that was "free", so you went on playing with an unlocked cursor.
                if (ShouldBeLocked())
                {
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                }
                else
                {
                    Cursor.lockState = previousLock;
                    Cursor.visible = previousVisible;
                }
                if (lockedCamera && ENT_Player.playerObject != null)
                {
                    ENT_Player.playerObject.UnlockCamera();
                }
                lockedCamera = false;
            }
        }

        /// <summary>True when the game itself wants a locked, hidden cursor: playing, not in a menu.</summary>
        private static bool ShouldBeLocked()
        {
            ENT_Player player = ENT_Player.playerObject;
            return Application.isFocused
                && CL_GameManager.IsInGame()
                && CL_GameManager.gMan != null && !CL_GameManager.gMan.isPaused
                && !CL_GameManager.showCursor
                && player != null && !player.dead
                && !OS_Manager.inUse
                && !CommandConsole.IsConsoleVisible()
                && Cursor.lockState != CursorLockMode.Locked;
        }

        private void OnGUI()
        {
            if (rebinding != null && Event.current.type == EventType.KeyDown && Event.current.keyCode != KeyCode.None)
            {
                if (Event.current.keyCode != KeyCode.Escape)
                {
                    rebinding.Value = Event.current.keyCode;
                }
                rebinding = null;
                Event.current.Use();
            }
            DrawHud();
            DrawMenuButton();
            if (open)
            {
                window = GUILayout.Window(0x8E1A7, window, DrawWindow, "Belay " + Plugin.Version);
            }
        }

        /// <summary>A small button in the corner of the main menu and the pause menu.</summary>
        private void DrawMenuButton()
        {
            bool onMainMenu = !CL_GameManager.IsInGame();
            bool paused = CL_GameManager.gMan != null && CL_GameManager.gMan.isPaused;
            if (open || (!onMainMenu && !paused))
            {
                return;
            }
            if (GUI.Button(new Rect(Screen.width - 130, Screen.height - 50, 120, 36), "Belay (" + Plugin.MenuKey.Value + ")"))
            {
                SetOpen(true);
            }
            if (onMainMenu && BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("shenxl.MultiPlayerMod"))
            {
                GUI.color = new UnityEngine.Color(1f, 0.45f, 0.4f);
                GUI.Label(new Rect(Screen.width - 520, Screen.height - 90, 510, 40),
                    "Another multiplayer mod (MultiPlayer Mod) is on and conflicts with Belay. Turn it off in Belay > Mods, then restart.");
                GUI.color = UnityEngine.Color.white;
            }
        }

        private void DrawHud()
        {
            string notice = RunFlow.Instance != null ? RunFlow.Instance.Notice : null;
            if (notice != null)
            {
                GUI.Label(new Rect(10, Screen.height - 55, 600, 25), notice);
            }
            NetSession net = NetSession.Instance;
            if (net == null || !net.InLobby)
            {
                return;
            }
            string text = $"Belay: {net.MemberOrder().Count}/{NetSession.MaxPlayers} roped in";
            string revivePrompt = ReviveSystem.Instance != null ? ReviveSystem.Instance.Prompt : null;
            if (revivePrompt != null)
            {
                GUI.Label(new Rect(Screen.width / 2f - 250f, Screen.height * 0.7f, 500f, 30f), revivePrompt);
            }
            float? doorSeconds = DoorSync.Instance != null ? DoorSync.Instance.NearestCountdown(out float doorDistance) : null;
            if (doorSeconds.HasValue)
            {
                GUI.Label(new Rect(Screen.width / 2f - 150f, 40f, 300f, 30f), $"DOOR CLOSING IN {Mathf.CeilToInt(doorSeconds.Value)}");
            }
            if (Tether.Instance != null && !string.IsNullOrEmpty(Tether.Instance.PullStatus))
            {
                text += "   " + Tether.Instance.PullStatus;
            }
            if (catchFlash > 0f)
            {
                text += "   CAUGHT!";
            }
            if (Players.WorldSpace.Ready && DEN_DeathFloor.instance != null && DEN_DeathFloor.instance.IsActive())
            {
                float below = ENT_Player.playerObject.transform.position.y - ((Component)DEN_DeathFloor.instance).transform.position.y;
                text += below > 0f ? $"   Mass: {Mathf.RoundToInt(below)} m below" : "   Mass: HERE";
            }
            LevelCheck check = LevelCheck.Instance;
            if (check != null && check.Checked > 0)
            {
                text += check.Mismatches == 0 ? $"   Levels synced: {check.Checked}" : $"   LEVELS DIFFER: {check.Mismatches} of {check.Checked}";
            }
            int waiting = InterludeGate.MissingInCurrentRoom;
            if (waiting > 0)
            {
                GUI.Label(new Rect(Screen.width / 2f - 200f, 70f, 400f, 30f), $"Waiting for {waiting} teammate{(waiting == 1 ? "" : "s")} to reach the room");
            }
            GUI.Label(new Rect(10, Screen.height - 30, 400, 25), text);
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginHorizontal();
            tab = (Tab)GUILayout.Toolbar((int)tab, TabLabels);
            if (GUILayout.Button("X", GUILayout.Width(28)))
            {
                SetOpen(false);
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            NetSession net = NetSession.Instance;
            switch (tab)
            {
                case Tab.Lobby:
                    GUILayout.Label("Status: " + net.Status);
                    if (net.IsTestClient)
                    {
                        GUILayout.Label("This window is a Belay test client. It joins the game running on this PC by itself; close it to stop.");
                        if (net.InLobby)
                        {
                            GUILayout.Label($"Lobby: {net.GetLobbyData(NetSession.KeyName)}");
                        }
                    }
                    else if (!NetSession.SteamReady)
                    {
                        GUILayout.Label("Waiting for Steam...");
                    }
                    else if (!net.InLobby)
                    {
                        DrawHostSection(net);
                        GUILayout.Space(10);
                        DrawBrowseSection(net);
                    }
                    else
                    {
                        DrawLobbySection(net);
                    }
                    break;
                case Tab.Players:
                    DrawPlayersTab(net);
                    break;
                case Tab.Rules:
                    DrawRulesTab(net);
                    break;
                case Tab.Mods:
                    DrawModsTab();
                    break;
                case Tab.Bot:
                    Bot.TestBot.Instance?.DrawTab();
                    break;
                case Tab.Dev:
                    Dev.DevMenu.DrawTab();
                    break;
                case Tab.Settings:
                    DrawSettingsTab();
                    break;
            }

            GUI.DragWindow();
        }

        private void DrawHostSection(NetSession net)
        {
            GUILayout.Label("Host a climb");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(50));
            lobbyName = GUILayout.TextField(lobbyName, 40);
            GUILayout.EndHorizontal();
            privacy = (LobbyPrivacy)GUILayout.Toolbar((int)privacy, PrivacyLabels);
            if (GUILayout.Button("Host lobby"))
            {
                net.HostLobby(lobbyName, privacy);
            }
        }

        private async void Search(NetSession net)
        {
            searching = true;
            found = await net.FindPublicLobbies();
            searching = false;
        }

        private void DrawBrowseSection(NetSession net)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Public lobbies");
            if (GUILayout.Button(searching ? "Searching..." : "Refresh", GUILayout.Width(100)) && !searching)
            {
                Search(net);
            }
            GUILayout.EndHorizontal();

            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(200));
            if (found.Length == 0)
            {
                GUILayout.Label(searching ? "" : "None found. Friends-only and private lobbies are joined through Steam invites.");
            }
            foreach (Lobby lobby in found)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{lobby.GetData(NetSession.KeyName)}  ({lobby.MemberCount}/{lobby.MaxMembers})");
                if (GUILayout.Button("Join", GUILayout.Width(60)))
                {
                    net.JoinLobby(lobby.Id);
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        private void DrawLobbySection(NetSession net)
        {
            GUILayout.Label($"Lobby: {net.GetLobbyData(NetSession.KeyName)}");
            RunInfo run = net.CurrentRun;
            GUILayout.Label(run.IsValid
                ? $"Run {run.RunId}: {run.Gamemode}  (seed {run.Seed})"
                : (net.IsHost ? "Pick a gamemode from the menu to start. Everyone follows you in." : "Waiting for the host to start a run..."));

            GUILayout.Label($"{net.MemberOrder().Count}/{NetSession.MaxPlayers} players. See Players and Rules tabs.");
            GUILayout.Space(6);
            if (GUILayout.Button("Invite friends"))
            {
                net.OpenInviteOverlay();
            }
            if (GUILayout.Button("Leave lobby"))
            {
                net.LeaveLobby();
            }
        }

        private void DrawPlayersTab(NetSession net)
        {
            if (!net.InLobby)
            {
                GUILayout.Label("Not in a lobby.");
                return;
            }
            GUILayout.Label("Rope order (each player is roped to the ones next to them):");
            Lobby? lobby = net.CurrentLobby;
            string hostMods = lobby.HasValue ? lobby.Value.GetMemberData(lobby.Value.Owner, NetSession.KeyMods) : null;
            int index = 1;
            foreach (SteamId member in net.MemberOrder())
            {
                string tag = member == net.HostId ? " (host)" : "";
                string you = member == NetSession.LocalId ? " (you)" : "";
                string mods = lobby.HasValue && !net.IsLocalBot(member) && member != LoopbackLink.ClientId
                    ? lobby.Value.GetMemberData(new Friend(member), NetSession.KeyMods)
                    : null;
                string mismatch = !string.IsNullOrEmpty(hostMods) && !string.IsNullOrEmpty(mods) && mods != hostMods
                    ? "  [different mods than host]"
                    : "";
                GUILayout.Label($"  {index++}. {NetSession.NameOf(member)}{tag}{you}{mismatch}");
            }
        }

        private void DrawRulesTab(NetSession net)
        {
            if (!net.InLobby)
            {
                GUILayout.Label("Host or join a lobby to see its rules. The host sets them.");
                return;
            }
            bool tether = net.GetLobbyData(NetSession.KeyTether) == "1";
            float length = Tether.Instance.RopeLength;
            if (net.IsHost)
            {
                bool newTether = GUILayout.Toggle(tether, "Rope on");
                if (newTether != tether)
                {
                    net.SetLobbyData(NetSession.KeyTether, newTether ? "1" : "0");
                }
                bool pull = net.GetLobbyData(NetSession.KeyRopePull) != "0";
                bool newPull = GUILayout.Toggle(pull, "Pull teammates up (hold " + Plugin.RopePullKey.Value + " while gripping)");
                if (newPull != pull)
                {
                    net.SetLobbyData(NetSession.KeyRopePull, newPull ? "1" : "0");
                }
                GUILayout.Label($"Rope length: {length:0} m");
                float newLength = Mathf.Round(GUILayout.HorizontalSlider(length, 4f, 40f));
                if (!Mathf.Approximately(newLength, length))
                {
                    net.SetLobbyData(NetSession.KeyRopeLength, newLength.ToString(CultureInfo.InvariantCulture));
                }
                float doorDelay = DoorSync.Instance.CloseDelay;
                GUILayout.Label($"Door close countdown: {doorDelay:0} s");
                float newDelay = Mathf.Round(GUILayout.HorizontalSlider(doorDelay, DoorSync.MinCloseDelay, DoorSync.MaxCloseDelay));
                if (!Mathf.Approximately(newDelay, doorDelay))
                {
                    net.SetLobbyData(NetSession.KeyDoorDelay, newDelay.ToString(CultureInfo.InvariantCulture));
                }
                int reviveCost = ReviveSystem.Instance.Cost;
                GUILayout.Label($"Revive cost: {reviveCost} roaches (paid by the reviver)");
                int newCost = Mathf.RoundToInt(GUILayout.HorizontalSlider(reviveCost, 0, ReviveSystem.MaxCost));
                if (newCost != reviveCost)
                {
                    net.SetLobbyData(NetSession.KeyReviveCost, newCost.ToString(CultureInfo.InvariantCulture));
                }
            }
            else
            {
                GUILayout.Label($"Rope: {(tether ? "on" : "off")}, {length:0} m");
                GUILayout.Label($"Door close countdown: {DoorSync.Instance.CloseDelay:0} s");
                GUILayout.Label($"Revive cost: {ReviveSystem.Instance.Cost} roaches (paid by the reviver)");
            }

        }

        private void DrawModsTab()
        {
            GUILayout.Label("Installed mods. Switching one changes what loads next time you start the game.");
            if (ModManager.RestartRequired)
            {
                GUILayout.Label("Restart required for changes to take effect.");
                if (GUILayout.Button("Quit game"))
                {
                    Application.Quit();
                }
            }
            modScroll = GUILayout.BeginScrollView(modScroll, GUILayout.Height(330));
            foreach (ModManager.ModEntry mod in ModManager.Mods)
            {
                GUILayout.BeginHorizontal();
                string version = string.IsNullOrEmpty(mod.Version) ? "" : " " + mod.Version;
                string state = mod.LoadedNow == mod.EnabledOnDisk ? "" : (mod.EnabledOnDisk ? "  (on after restart)" : "  (off after restart)");
                GUILayout.Label($"{mod.DisplayName}{version}{state}\n{mod.FileName}");
                if (mod.Protected)
                {
                    GUILayout.Label("always on", GUILayout.Width(80));
                }
                else
                {
                    bool on = GUILayout.Toggle(mod.EnabledOnDisk, mod.EnabledOnDisk ? "On" : "Off", GUILayout.Width(80));
                    if (on != mod.EnabledOnDisk)
                    {
                        ModManager.SetEnabled(mod, on);
                    }
                }
                GUILayout.EndHorizontal();
                if (mod.Error != null)
                {
                    GUILayout.Label(mod.Error);
                }
            }
            GUILayout.EndScrollView();
            if (GUILayout.Button("Refresh list"))
            {
                ModManager.Refresh();
            }
        }

        private void DrawSettingsTab()
        {
            DrawKeyBinding("Open Belay menu", Plugin.MenuKey);
            DrawKeyBinding("Next spectated player", Plugin.SpectateNextKey);
            DrawKeyBinding("Pull teammate up (rope)", Plugin.RopePullKey);
            DrawKeyBinding("Vote: take the shortcut", Plugin.VoteYesKey);
            DrawKeyBinding("Vote: normal path", Plugin.VoteNoKey);
            GUILayout.Space(6);
            Plugin.ShowRope.Value = GUILayout.Toggle(Plugin.ShowRope.Value, "Show rope");
            GUILayout.Label($"Network updates per second: {Plugin.SendRate.Value}");
            Plugin.SendRate.Value = Mathf.RoundToInt(GUILayout.HorizontalSlider(Plugin.SendRate.Value, 10, 30));
            GUILayout.Space(6);
            DrawUpdates();
            GUILayout.Space(6);
            GUILayout.Label("Settings are saved in BepInEx/config/" + Plugin.Guid + ".cfg");
        }

        private void DrawUpdates()
        {
            var up = Updater.Instance;
            GUILayout.Label("Updates (you have Belay " + Plugin.Version + ")");
            Plugin.AutoUpdate.Value = GUILayout.Toggle(Plugin.AutoUpdate.Value, "Update automatically when the game starts");
            if (up == null) return;
            if (!string.IsNullOrEmpty(up.Message)) GUILayout.Label(up.Message);
            GUILayout.BeginHorizontal();
            bool busy = up.State == Updater.UpdateState.Checking || up.State == Updater.UpdateState.Downloading;
            if (!busy && up.State != Updater.UpdateState.Ready && GUILayout.Button("Check for updates", GUILayout.Width(160)))
            {
                up.CheckNow();
            }
            if (up.State == Updater.UpdateState.Available && GUILayout.Button("Update to " + up.LatestVersion, GUILayout.Width(160)))
            {
                up.InstallNow();
            }
            if (up.State == Updater.UpdateState.Ready && GUILayout.Button("Quit to apply", GUILayout.Width(160)))
            {
                Application.Quit();
            }
            GUILayout.EndHorizontal();
        }

        private void DrawKeyBinding(string label, ConfigEntry<KeyCode> entry)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(200));
            string text = rebinding == entry ? "Press a key (Esc cancels)" : entry.Value.ToString();
            if (GUILayout.Button(text))
            {
                rebinding = entry;
            }
            GUILayout.EndHorizontal();
        }
    }
}
