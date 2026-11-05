using System.Linq;
using System.Globalization;
using Belay.Net;
using Belay.Rope;
using Belay.Run;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace Belay.UI
{
    /// <summary>Minimal IMGUI lobby window: host, browse public lobbies, invite, rope settings.</summary>
    public class LobbyWindow : MonoBehaviour
    {
        private bool open;
        private Rect window = new Rect(40, 40, 380, 460);
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
            if (open)
            {
                // The game re-locks the cursor every frame while playing.
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
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
            if (open)
            {
                previousLock = Cursor.lockState;
                previousVisible = Cursor.visible;
            }
            else
            {
                Cursor.lockState = previousLock;
                Cursor.visible = previousVisible;
            }
        }

        private void OnGUI()
        {
            DrawHud();
            if (open)
            {
                window = GUILayout.Window(0x8E1A7, window, DrawWindow, "Belay  (" + Plugin.MenuKey.Value + " to close)");
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
            string text = $"Belay: {net.Members.Count()}/{NetSession.MaxPlayers} roped in";
            if (catchFlash > 0f)
            {
                text += "   CAUGHT!";
            }
            GUI.Label(new Rect(10, Screen.height - 30, 400, 25), text);
        }

        private void DrawWindow(int id)
        {
            NetSession net = NetSession.Instance;
            GUILayout.Label("Status: " + net.Status);

            if (!NetSession.SteamReady)
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

            GUILayout.Label("Rope order:");
            int index = 1;
            foreach (Friend member in net.Members)
            {
                string tag = member.Id == net.HostId ? " (host)" : "";
                string you = member.Id == SteamClient.SteamId ? " (you)" : "";
                GUILayout.Label($"  {index++}. {member.Name}{tag}{you}");
            }

            GUILayout.Space(6);
            bool tether = net.GetLobbyData(NetSession.KeyTether) == "1";
            float length = Tether.Instance.RopeLength;
            if (net.IsHost)
            {
                bool newTether = GUILayout.Toggle(tether, "Rope on");
                if (newTether != tether)
                {
                    net.SetLobbyData(NetSession.KeyTether, newTether ? "1" : "0");
                }
                GUILayout.Label($"Rope length: {length:0} m");
                float newLength = Mathf.Round(GUILayout.HorizontalSlider(length, 4f, 40f));
                if (!Mathf.Approximately(newLength, length))
                {
                    net.SetLobbyData(NetSession.KeyRopeLength, newLength.ToString(CultureInfo.InvariantCulture));
                }
            }
            else
            {
                GUILayout.Label($"Rope: {(tether ? "on" : "off")}, {length:0} m");
            }

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
    }
}
