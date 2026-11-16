using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Belay.UI
{
    /// <summary>
    /// The full-screen multiplayer page shown on the main menu in place of the floating window. The game's
    /// menu canvas is switched off while it's open (IMGUI doesn't block uGUI clicks, so the game's buttons
    /// would otherwise still react underneath), and switched back on when the page closes. In a run the
    /// menu key and the pause-menu entry still open the floating window.
    /// </summary>
    public partial class BelayMenu
    {
        /// <summary>
        /// Draws the live 3D climber preview on the Outfit tab of the page. Set by the outfit preview
        /// component; called for every IMGUI event with the rect reserved for it, so it can handle drags too.
        /// </summary>
        internal static System.Action<Rect> OutfitPreviewDrawer;

        // The page is laid out on a 1920x1080 canvas and scaled to the screen height.
        private const float RefHeight = 1080f;
        private const float Margin = 80f;
        private const float NavWidth = 280f;
        private const float HeaderHeight = 120f;

        private static readonly Tab[] PageTabs = { Tab.Lobby, Tab.Players, Tab.Rules, Tab.Outfit, Tab.Mods, Tab.Settings, Tab.Bot };

        private bool pageMode;
        private Vector2 pageScroll;
        private readonly List<Canvas> hiddenCanvases = new List<Canvas>();
        private GUIStyle pageTitle;
        private GUIStyle pageSubtitle;
        private GUIStyle navButton;
        private GUIStyle navSelected;
        private GUIStyle bigButton;
        private GUIStyle campaignButton;
        private GUISkin stylesFor;

        private void ShowPage()
        {
            hiddenCanvases.Clear();
            MenuManager manager = FindObjectOfType<MenuManager>();
            if (manager == null)
            {
                return;
            }
            // Every root canvas of the main menu scene (buttons, version text, popups) goes dark; the 3D
            // background keeps running behind the page.
            foreach (Canvas canvas in FindObjectsOfType<Canvas>())
            {
                if (canvas.isRootCanvas && canvas.enabled && canvas.gameObject.scene == manager.gameObject.scene)
                {
                    canvas.enabled = false;
                    hiddenCanvases.Add(canvas);
                }
            }
        }

        private void HidePage()
        {
            foreach (Canvas canvas in hiddenCanvases)
            {
                if (canvas != null)
                {
                    canvas.enabled = true;
                }
            }
            hiddenCanvases.Clear();
        }

        private void BuildPageStyles()
        {
            if (stylesFor == GUI.skin && pageTitle != null)
            {
                return;
            }
            stylesFor = GUI.skin;
            pageTitle = new GUIStyle(GUI.skin.label) { fontSize = 64, alignment = TextAnchor.LowerLeft, wordWrap = false };
            pageTitle.normal.textColor = BelayTheme.Text;
            pageSubtitle = new GUIStyle(GUI.skin.label) { fontSize = 24, alignment = TextAnchor.LowerLeft, wordWrap = false };
            pageSubtitle.normal.textColor = BelayTheme.Dim;

            GUIStyle baseTab = BelayTheme.Tab ?? GUI.skin.button;
            navButton = new GUIStyle(GUI.skin.button) { fontSize = 26, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(22, 12, 6, 6) };
            navSelected = new GUIStyle(navButton);
            // The selected entry is drawn inverted, like the selected tab in the window.
            navSelected.normal = baseTab.onNormal;
            navSelected.hover = baseTab.onHover;
            navSelected.active = baseTab.onActive;

            bigButton = new GUIStyle(GUI.skin.button) { fontSize = 26, alignment = TextAnchor.MiddleCenter };
            campaignButton = new GUIStyle(bigButton) { fontSize = 30 };
            campaignButton.normal.textColor = BelayTheme.Red;
            campaignButton.hover.textColor = BelayTheme.Text;
        }

        private void DrawPage()
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                Event.current.Use();
                SetOpen(false);
                return;
            }
            BuildPageStyles();
            float scale = Screen.height / RefHeight;
            float width = Screen.width / scale;
            Matrix4x4 oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            try
            {
                // Dark veil over the menu's 3D background.
                Fill(new Rect(0, 0, width, RefHeight), new Color(0f, 0f, 0f, 0.82f));

                // Header: red marker, title, subtitle, version, and a rule underneath.
                Fill(new Rect(Margin, Margin - 8f, 8f, 72f), BelayTheme.Red);
                GUI.Label(new Rect(Margin + 26f, Margin - 20f, 600f, 80f), "TETHERED", pageTitle);
                GUI.Label(new Rect(Margin + 380f, Margin + 4f, 400f, 40f), "MULTIPLAYER", pageSubtitle);
                var version = new GUIStyle(pageSubtitle) { alignment = TextAnchor.LowerRight, fontSize = 20 };
                GUI.Label(new Rect(width - Margin - 300f, Margin + 4f, 300f, 40f), "v" + Plugin.Version, version);
                Fill(new Rect(Margin, Margin + HeaderHeight - 60f, width - Margin * 2f, 2f), BelayTheme.Line);

                float top = Margin + HeaderHeight - 20f;
                float bottom = RefHeight - Margin;

                // Left column: page sections, then CAMPAIGN and BACK at the bottom.
                float y = top;
                foreach (Tab t in PageTabs)
                {
                    if (t == Tab.Bot && Bot.TestBot.Instance == null)
                    {
                        continue;
                    }
                    string label = TabLabels[(int)t].ToUpperInvariant();
                    if (GUI.Button(new Rect(Margin, y, NavWidth, 54f), label, tab == t ? navSelected : navButton))
                    {
                        tab = t;
                        pageScroll = Vector2.zero;
                    }
                    y += 62f;
                }
                if (GUI.Button(new Rect(Margin, bottom - 136f, NavWidth, 72f), "CAMPAIGN", campaignButton))
                {
                    GoToCampaign();
                    return;
                }
                if (GUI.Button(new Rect(Margin, bottom - 54f, NavWidth, 54f), "BACK", bigButton))
                {
                    SetOpen(false);
                    return;
                }

                // Right: the section's content in a framed panel.
                float left = Margin + NavWidth + 40f;
                var panel = new Rect(left, top, width - left - Margin, bottom - top);
                GUI.Box(panel, GUIContent.none);
                Fill(new Rect(panel.x, panel.y, panel.width, 2f), BelayTheme.Frame);
                var inner = new Rect(panel.x + 30f, panel.y + 26f, panel.width - 60f, panel.height - 52f);
                GUILayout.BeginArea(inner);
                GUILayout.Label(TabLabels[(int)tab].ToUpperInvariant(), pageSubtitle);
                GUILayout.Space(14);
                pageScroll = GUILayout.BeginScrollView(pageScroll);
                // Keep text lines a readable length on wide screens.
                GUILayout.BeginVertical(GUILayout.MaxWidth(tab == Tab.Outfit ? inner.width - 20f : 1000f));
                DrawTabContent();
                GUILayout.EndVertical();
                GUILayout.EndScrollView();
                GUILayout.EndArea();
            }
            finally
            {
                GUI.matrix = oldMatrix;
            }
        }

        /// <summary>Outfit controls on the left, the live climber preview on the right.</summary>
        private void DrawOutfitWithPreview()
        {
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.MaxWidth(820f));
            DrawOutfitTab();
            GUILayout.EndVertical();
            GUILayout.Space(30);
            Rect previewRect = GUILayoutUtility.GetRect(360f, 520f, GUILayout.Width(360f), GUILayout.Height(520f));
            if (OutfitPreviewDrawer != null)
            {
                try
                {
                    OutfitPreviewDrawer(previewRect);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning("Outfit preview failed: " + e.Message);
                    OutfitPreviewDrawer = null;
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// Leaves the page for the game's own campaign screen (where the run is started as usual; in a lobby
        /// only the host's start counts, see RunFlow).
        /// </summary>
        private void GoToCampaign()
        {
            M_Gamemode campaign = null;
            UI_Gamemode_Button campaignButton = null;
            foreach (UI_Gamemode_Button b in Resources.FindObjectsOfTypeAll<UI_Gamemode_Button>())
            {
                if (!b.gameObject.scene.IsValid() || b.gamemode == null)
                {
                    continue;
                }
                bool named = (b.gamemode.gamemodeName ?? "").ToLowerInvariant().Contains("campaign");
                bool bank = b.gamemode.GetRoachBankID() == "campaign";
                if (named || (bank && campaign == null))
                {
                    campaign = b.gamemode;
                    campaignButton = b;
                    if (named)
                    {
                        break;
                    }
                }
            }
            SetOpen(false);
            if (campaign == null)
            {
                Plugin.Log.LogWarning("Campaign gamemode not found in the main menu");
                return;
            }
            try
            {
                // Open the menu screen the gamemode buttons live on (the game's PLAY screen) first.
                foreach (UI_MenuButton menuButton in Resources.FindObjectsOfTypeAll<UI_MenuButton>())
                {
                    if (menuButton.gameObject.scene.IsValid() && menuButton.screen != null
                        && campaignButton.transform.IsChildOf(menuButton.screen.transform))
                    {
                        menuButton.OpenScreen();
                        break;
                    }
                }
                UI_GamemodeScreen screen = UI_GamemodeScreen.instance;
                if (screen == null)
                {
                    screen = Resources.FindObjectsOfTypeAll<UI_GamemodeScreen>().FirstOrDefault(s => s.gameObject.scene.IsValid());
                    if (screen != null)
                    {
                        // Waking it runs its Awake (which registers it and hides it again) before it's opened.
                        screen.gameObject.SetActive(true);
                        screen = UI_GamemodeScreen.instance ?? screen;
                    }
                }
                if (screen == null)
                {
                    Plugin.Log.LogWarning("Campaign screen not found in the main menu");
                    return;
                }
                screen.Initialize(campaign);
                Plugin.Log.LogInfo("Opened the campaign screen (" + campaign.gamemodeName + ") from the Tethered page");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't open the campaign screen: " + e.Message);
            }
        }

        private static void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }
    }
}
