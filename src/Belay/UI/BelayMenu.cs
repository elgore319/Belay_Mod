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
    /// The mod menu. Drawn with its own IMGUI (no game UI is patched, so other mods are untouched), opened from
    /// the NOT ALONE entry <see cref="GameMenuEntry"/> adds to the main and pause menus, and only captures input
    /// while open.
    /// </summary>
    public partial class BelayMenu : MonoBehaviour
    {
        private enum Tab
        {
            Lobby,
            Players,
            Rules,
            Mods,
            Bot,
            Outfit,
            Settings,
        }

        private static readonly string[] TabLabels = { "Lobby", "Players", "Rules", "Mods", "Bot", "Outfit", "Settings" };

        private static BelayMenu instance;

        private bool open;

        /// <summary>True while the menu is open in a run: mouse look and new grabs are ignored (see MenuInputPatches).</summary>
        internal static bool CapturingInput { get; private set; }

        /// <summary>True while a text box in the menu has the keyboard, so typing doesn't move the player.</summary>
        internal static bool Typing => CapturingInput && GUIUtility.keyboardControl != 0;
        private Tab tab;
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

        private void Awake()
        {
            instance = this;
        }

        /// <summary>Opens the window from the BELAY entry in the game's main or pause menu.</summary>
        internal static void OpenFromGameMenu()
        {
            if (instance != null)
            {
                instance.SetOpen(true);
            }
        }

        private void Update()
        {
            GameFont.Tick();
            if (open && (CL_GameManager.IsInGame() != pageInRun || (pageInRun && !World.CoopPause.InPauseMenu)))
            {
                // The page belongs to the menu it was opened from: a run starting, the run ending, or the
                // pause menu closing closes it too.
                SetOpen(false);
            }
            bool anyOpen = open || Dev.DevTools.Open;
            CapturingInput = anyOpen && CL_GameManager.IsInGame();
            if (anyOpen)
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
                pageMode = true;
                pageInRun = CL_GameManager.IsInGame();
                if (pageInRun)
                {
                    outfitPage = false;
                }
                if (!outfitPage && tab == Tab.Outfit)
                {
                    // The outfit only opens from the game's Cosmetics screen, never mid-run.
                    tab = Tab.Lobby;
                }
                ShowPage();
                previousLock = Cursor.lockState;
                previousVisible = Cursor.visible;
                ModManager.Refresh();
                // Stop mouse-look while clicking around, unless the game is paused anyway.
                ENT_Player player = ENT_Player.playerObject;
                if (player != null && !World.CoopPause.InPauseMenu && !player.dead)
                {
                    player.LockCamera();
                    lockedCamera = true;
                }
            }
            else
            {
                if (pageMode)
                {
                    HidePage();
                }
                pageMode = false;
                outfitPage = false;
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
                && !World.CoopPause.InPauseMenu
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
            // The menu and its button use Belay's own skin (the game's font and colours); the HUD and other mods don't.
            GUISkin previousSkin = GUI.skin;
            GUISkin theme = BelayTheme.Skin;
            if (theme != null)
            {
                GUI.skin = theme;
            }
            try
            {
                DrawMenuButton();
                DrawUpdatePopup();
                if (open)
                {
                    DrawPage();
                }
            }
            finally
            {
                GUI.skin = previousSkin;
            }
        }

        /// <summary>
        /// The conflict warning on the main menu, plus a corner button fallback for when the menu entry couldn't
        /// be added.
        /// </summary>
        private void DrawMenuButton()
        {
            bool onMainMenu = !CL_GameManager.IsInGame();
            bool paused = World.CoopPause.InPauseMenu;
            if (open || (!onMainMenu && !paused))
            {
                return;
            }
            if (onMainMenu && CosmeticsScreen.Showing && GUI.Button(CosmeticsScreen.ButtonRect, "CLIMBER OUTFIT"))
            {
                // Outfits live with the game's own cosmetics: the outfit page opens over the Cosmetics
                // screen, and BACK returns to it.
                tab = Tab.Outfit;
                outfitPage = true;
                SetOpen(true);
                return;
            }
            if (!GameMenuEntry.Showing && GameMenuEntry.GaveUp && GUI.Button(new Rect(Screen.width - 150, Screen.height - 54, 140, 40), "NOT ALONE"))
            {
                SetOpen(true);
            }
            if (onMainMenu && BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("shenxl.MultiPlayerMod"))
            {
                GUI.color = new UnityEngine.Color(1f, 0.45f, 0.4f);
                GUI.Label(new Rect(Screen.width - 520, Screen.height - 90, 510, 40),
                    "Another multiplayer mod (MultiPlayer Mod) is on and conflicts with Not Alone. Turn it off in Not Alone > Mods, then restart.");
                GUI.color = UnityEngine.Color.white;
            }
        }

        /// <summary>Update-available popup on the main and pause menus (in play the updater shows a notice).</summary>
        private void DrawUpdatePopup()
        {
            Updater up = Updater.Instance;
            if (up == null || up.PopupDismissed) return;
            bool ready = up.State == Updater.UpdateState.Ready;
            if (!ready && up.State != Updater.UpdateState.Available) return;
            bool onMainMenu = !CL_GameManager.IsInGame();
            bool paused = World.CoopPause.InPauseMenu;
            if (!onMainMenu && !paused) return;

            var box = new Rect(Screen.width / 2f - 230f, 40f, 460f, 112f);
            GUI.Box(box, "");
            GUI.Label(new Rect(box.x + 16f, box.y + 12f, box.width - 32f, 50f), ready
                ? "Not Alone " + up.LatestVersion + " is downloaded (you're on " + Plugin.Version + "). Restart the game to use it."
                : "Not Alone " + up.LatestVersion + " is out (you're on " + Plugin.Version + ").");
            if (GUI.Button(new Rect(box.x + 16f, box.y + 64f, 200f, 36f), ready ? "Restart now" : "Update"))
            {
                if (ready) Updater.RestartGame();
                else up.InstallNow();
            }
            if (GUI.Button(new Rect(box.xMax - 136f, box.y + 64f, 120f, 36f), "Later"))
            {
                up.PopupDismissed = true;
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
            string text = $"Not Alone: {net.MemberOrder().Count}/{NetSession.MaxPlayers} roped in";
            string revivePrompt = ReviveSystem.Instance != null ? ReviveSystem.Instance.Prompt : null;
            if (revivePrompt != null)
            {
                GUI.Label(new Rect(Screen.width / 2f - 250f, Screen.height * 0.7f, 500f, 30f), revivePrompt);
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
            // No "waiting for teammates" text, but the check still brings bots along.
            _ = InterludeGate.MissingInCurrentRoom;
            GUI.Label(new Rect(10, Screen.height - 30, 400, 25), text);
        }

        /// <summary>The selected tab's controls.</summary>
        private void DrawTabContent()
        {
            NetSession net = NetSession.Instance;
            switch (tab)
            {
                case Tab.Lobby:
                    GUILayout.Label("Status: " + net.Status);
                    if (net.IsTestClient)
                    {
                        GUILayout.Label("This window is a Not Alone test client. It joins the game running on this PC by itself; close it to stop.");
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
                case Tab.Outfit:
                    if (pageMode)
                    {
                        DrawOutfitWithPreview();
                    }
                    else
                    {
                        DrawOutfitTab();
                    }
                    break;
                case Tab.Settings:
                    DrawSettingsTab();
                    break;
            }
        }

        private void DrawHostSection(NetSession net)
        {
            GUILayout.Label("HOST A CLIMB");
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
            GUILayout.Label("PUBLIC LOBBIES");
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
                GUILayout.Label($"Door shuts {doorDelay:0} s after the last teammate is through");
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
                GUILayout.Label($"Door shuts {DoorSync.Instance.CloseDelay:0} s after the last teammate is through");
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

        /// <summary>How teammates see the local player's climber: headgear and clothing colors.</summary>
        private void DrawOutfitTab()
        {
            Players.Avatar.Outfit outfit = Players.Avatar.Outfits.Local;
            Players.Avatar.Outfit before = outfit;
            GUILayout.Label("How teammates see your climber. Your gloves follow the hand skins you equip in the game.");
            GUILayout.Space(8);

            GUILayout.Label("Look");
            outfit.Look = GUILayout.SelectionGrid(outfit.Look, Players.Avatar.Outfits.LookNames, 4, BelayTheme.Tab ?? GUI.skin.button);
            GUILayout.Label(Players.Avatar.LoreLooks.All[Mathf.Clamp(outfit.Look, 0, Players.Avatar.LoreLooks.Count - 1)].Blurb,
                BelayTheme.Hint ?? GUI.skin.label);
            GUILayout.Space(8);
            if (!Players.Avatar.LoreLooks.Valid(outfit.Look))
            {
                GUILayout.Label("Headgear");
                outfit.Headgear = GUILayout.Toolbar(outfit.Headgear, Players.Avatar.Outfits.HeadgearNames, BelayTheme.Tab ?? GUI.skin.button);
            }
            GUILayout.Space(8);
            outfit.Jacket = DrawOutfitColor("Jacket", outfit.Jacket, "Lobby color");
            outfit.Hat = DrawOutfitColor("Helmet / beanie", outfit.Hat, "Lobby color");
            outfit.Pants = DrawOutfitColor("Trousers", outfit.Pants, "Standard");
            outfit.Pack = DrawOutfitColor("Backpack", outfit.Pack, "Standard");
            GUILayout.Space(8);
            if (GUILayout.Button("Back to the standard look", GUILayout.Width(240)))
            {
                outfit = default;
            }
            if (!outfit.Same(before))
            {
                Players.Avatar.Outfits.Local = outfit;
            }
            GUILayout.Space(6);
            GUILayout.Label("Teammates need Not Alone 0.7.21 or newer to see it. Test bots wear your outfit, so you can check it on one.",
                BelayTheme.Hint ?? GUI.skin.label);
        }

        /// <summary>One clothing color: a row of swatches to click, with the standard look first.</summary>
        private static int DrawOutfitColor(string label, int chosen, string standardName)
        {
            Players.Avatar.Outfits.Swatch[] colors = Players.Avatar.Outfits.Colors;
            string name = chosen == 0 ? standardName : colors[Mathf.Clamp(chosen, 0, colors.Length - 1)].Name;
            GUILayout.Label(label + ": " + name);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(chosen == 0 ? "[ " + standardName + " ]" : standardName, GUILayout.Width(130), GUILayout.Height(26)))
            {
                chosen = 0;
            }
            UnityEngine.Color old = GUI.color;
            for (int i = 1; i < colors.Length; i++)
            {
                Rect box = GUILayoutUtility.GetRect(26, 26, GUILayout.Width(26), GUILayout.Height(26));
                if (i == chosen)
                {
                    GUI.color = UnityEngine.Color.white;
                    GUI.DrawTexture(box, Texture2D.whiteTexture);
                }
                GUI.color = colors[i].Color;
                Rect inner = i == chosen ? new Rect(box.x + 3, box.y + 3, box.width - 6, box.height - 6) : new Rect(box.x + 1, box.y + 1, box.width - 2, box.height - 2);
                GUI.DrawTexture(inner, Texture2D.whiteTexture);
                GUI.color = old;
                if (GUI.Button(box, GUIContent.none, GUIStyle.none))
                {
                    chosen = i;
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
            return chosen;
        }

        private void DrawSettingsTab()
        {
            DrawKeyBinding("Next spectated player", Plugin.SpectateNextKey);
            DrawKeyBinding("Pull teammate up (rope)", Plugin.RopePullKey);
            GUILayout.Space(6);
            Plugin.ShowRope.Value = GUILayout.Toggle(Plugin.ShowRope.Value, "Show rope");
            GUILayout.Label($"Network updates per second: {Plugin.SendRate.Value}");
            Plugin.SendRate.Value = Mathf.RoundToInt(GUILayout.HorizontalSlider(Plugin.SendRate.Value, 10, 30));
            GUILayout.Space(6);
            DrawUpdates();
            GUILayout.Space(6);
            GUILayout.Label("Settings are saved in BepInEx/config/" + Plugin.Guid + ".cfg", BelayTheme.Hint ?? GUI.skin.label);
        }

        private void DrawUpdates()
        {
            var up = Updater.Instance;
            GUILayout.Label("Updates (you have Not Alone " + Plugin.Version + ")");
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
            if (up.State == Updater.UpdateState.Ready && GUILayout.Button("Restart now", GUILayout.Width(160)))
            {
                Updater.RestartGame();
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
