using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Belay.UI
{
    /// <summary>
    /// Puts a "NOT ALONE" entry into the game's own main menu and pause menu, so the mod menu opens from
    /// the game's menus like any other option. The entry is a copy of one of the menu's own buttons
    /// (same look, font, hover and select effects), with the copy's game behaviour stripped and its
    /// click wired to the Belay window. Only the copy is created; the game's buttons keep their
    /// listeners. When no button column can be found the old corner button in <see cref="BelayMenu"/>
    /// is drawn instead, and the menu key works either way.
    /// </summary>
    public class GameMenuEntry : MonoBehaviour
    {
        private const string EntryName = "Belay Menu Entry";

        // Game components a menu button may carry that only change how it looks when hovered or
        // selected. Every other game component on the copy is removed, so it can't open a game screen,
        // grab the first selection, or react to a hotkey.
        private static readonly HashSet<string> LookOnly = new HashSet<string>
        {
            "UI_AnimateOnSelect", "UI_OutlineOnSelect", "UI_CopyColor", "UI_CopySelectedColor",
            "UI_SelectEventTrigger", "UI_HideIfTargetUnselected", "UI_HideTargetIfUnselected",
            "UI_ElementScaler", "UI_FlexScaler", "UI_DMTextAnim", "UI_SizeToFit", "UI_UIScaleToSettings",
        };

        private static readonly string[] AfterWords = { "SETTING", "OPTION" };
        private static readonly string[] BeforeWords = { "QUIT", "EXIT", "MAIN MENU", "MENU", "ABANDON", "GIVE UP" };

        private static GameObject mainEntry;
        private static GameObject pauseEntry;

        private MenuManager menuManager;
        private float nextMainTry;
        private float nextPauseTry;
        private int mainFails;
        private int pauseFails;
        private bool loggedMainFail;
        private bool loggedPauseFail;

        /// <summary>True while one of our entries is on screen, so the corner button isn't needed.</summary>
        internal static bool Showing =>
            (mainEntry != null && mainEntry.activeInHierarchy) || (pauseEntry != null && pauseEntry.activeInHierarchy);

        /// <summary>True once the menu that's open now has failed to take an entry, so the corner button stays.</summary>
        internal static bool GaveUp { get; private set; }

        private void Update()
        {
            if (!CL_GameManager.IsInGame())
            {
                TickMainMenu();
            }
            else
            {
                TickPauseMenu();
            }
        }

        private void TickMainMenu()
        {
            if (mainEntry != null)
            {
                return;
            }
            if (menuManager == null)
            {
                if (Time.unscaledTime < nextMainTry)
                {
                    return;
                }
                nextMainTry = Time.unscaledTime + 0.5f;
                menuManager = FindFirstObjectByType<MenuManager>();
                mainFails = 0;
                loggedMainFail = false;
                if (menuManager == null)
                {
                    return;
                }
            }
            if (Time.unscaledTime < nextMainTry)
            {
                return;
            }
            nextMainTry = Time.unscaledTime + 0.5f;
            Transform root = menuManager.menu != null ? menuManager.menu.transform : menuManager.transform;
            mainEntry = TryAdd(root, "main menu", "NOT ALONE");
            if (mainEntry == null && ++mainFails >= 6)
            {
                GaveUp = true;
                if (!loggedMainFail)
                {
                    loggedMainFail = true;
                    LogButtons(root, "main menu");
                }
            }
            else if (mainEntry != null)
            {
                GaveUp = false;
            }
        }

        private void TickPauseMenu()
        {
            CL_GameManager gMan = CL_GameManager.gMan;
            GameObject pause = gMan != null ? gMan.pauseMenu : null;
            if (pause == null || !pause.activeInHierarchy)
            {
                pauseFails = 0;
                return;
            }
            if (pauseEntry != null && pauseEntry.transform.IsChildOf(pause.transform))
            {
                return;
            }
            if (Time.unscaledTime < nextPauseTry)
            {
                return;
            }
            nextPauseTry = Time.unscaledTime + 0.25f;
            pauseEntry = TryAdd(pause.transform, "pause menu", "GAME LOBBY");
            if (pauseEntry == null && ++pauseFails >= 6)
            {
                GaveUp = true;
                if (!loggedPauseFail)
                {
                    loggedPauseFail = true;
                    LogButtons(pause.transform, "pause menu");
                }
            }
            else if (pauseEntry != null)
            {
                GaveUp = false;
            }
        }

        /// <summary>Finds the menu's column of buttons and adds a copy of one of them as the Belay entry.</summary>
        private static GameObject TryAdd(Transform root, string where, string name)
        {
            try
            {
                Transform existing = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == EntryName);
                if (existing != null)
                {
                    return existing.gameObject;
                }

                List<Button> column = FindColumn(root);
                if (column == null)
                {
                    return null;
                }
                Transform parent = column[0].transform.parent;
                bool laidOut = parent.GetComponent<LayoutGroup>() != null;

                Button template;
                int index;
                Button after = column.FirstOrDefault(b => LabelHas(b, AfterWords));
                Button before = column.FirstOrDefault(b => LabelHas(b, BeforeWords));
                if (!laidOut)
                {
                    // No layout group: the buttons were placed by hand, so the entry goes under the lowest one.
                    template = column.OrderBy(b => ScreenY(b)).First();
                    index = template.transform.GetSiblingIndex() + 1;
                }
                else if (after != null)
                {
                    template = after;
                    index = after.transform.GetSiblingIndex() + 1;
                }
                else if (before != null)
                {
                    template = before;
                    index = before.transform.GetSiblingIndex();
                }
                else
                {
                    template = column[column.Count - 1];
                    index = template.transform.GetSiblingIndex();
                }

                // Copied under an inactive holder, so none of the copy's game scripts wake up (one of
                // them would otherwise grab the menu's first selection) before they're stripped.
                var holder = new GameObject("Belay Entry Holder");
                holder.SetActive(false);
                GameObject entry = Instantiate(template.gameObject, holder.transform, false);
                entry.name = EntryName;
                entry.SetActive(false);
                StripGameBehaviour(entry);
                entry.transform.SetParent(parent, false);
                Destroy(holder);
                Relabel(entry, Label(template), name);

                Button button = entry.GetComponent<Button>();
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener(() => BelayMenu.OpenFromGameMenu());
                button.interactable = true;

                entry.transform.SetSiblingIndex(Mathf.Clamp(index, 0, parent.childCount - 1));
                entry.SetActive(true);
                if (!laidOut)
                {
                    PlaceBelow(entry, template, column);
                }
                WireNavigation(button, template, column, laidOut ? after != null : true);

                Plugin.Log.LogInfo($"Belay entry added to the {where} ({(laidOut ? "layout" : "placed")}, copied from '{Label(template)}')");
                return entry;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't add the Belay entry to the {where}: {e.Message}");
                return null;
            }
        }

        /// <summary>The group of sibling buttons with the most members (the menu's list of options).</summary>
        private static List<Button> FindColumn(Transform root)
        {
            var groups = root.GetComponentsInChildren<Button>(false)
                .Where(b => b.gameObject.activeInHierarchy && b.name != EntryName && !string.IsNullOrEmpty(Label(b)))
                .GroupBy(b => b.transform.parent)
                .Where(g => g.Key != null && g.Count() >= 2)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Any(b => LabelHas(b, AfterWords) || LabelHas(b, BeforeWords)) ? 1 : 0)
                .ToList();
            if (groups.Count == 0)
            {
                return null;
            }
            return groups[0].OrderBy(b => b.transform.GetSiblingIndex()).ToList();
        }

        private static void StripGameBehaviour(GameObject entry)
        {
            foreach (MonoBehaviour mb in entry.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null)
                {
                    continue;
                }
                System.Type type = mb.GetType();
                if (type.Assembly.GetName().Name == "Assembly-CSharp" && !LookOnly.Contains(type.Name))
                {
                    DestroyImmediate(mb);
                }
            }
            foreach (EventTrigger trigger in entry.GetComponentsInChildren<EventTrigger>(true))
            {
                trigger.triggers.RemoveAll(e => e.eventID == EventTriggerType.PointerClick || e.eventID == EventTriggerType.Submit);
            }
            foreach (UI_SelectEventTrigger trigger in entry.GetComponentsInChildren<UI_SelectEventTrigger>(true))
            {
                trigger.triggers?.RemoveAll(e => e.eventID == EventTriggerType.PointerClick || e.eventID == EventTriggerType.Submit);
            }
        }

        /// <summary>Names the copy ("NOT ALONE" on the main menu, "GAME LOBBY" in the pause menu), in the same letter case as the button it was copied from.</summary>
        private static void Relabel(GameObject entry, string templateLabel, string name)
        {
            bool upper = templateLabel == templateLabel.ToUpperInvariant();
            string text = upper ? name : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
            bool first = true;
            foreach (TMP_Text tmp in entry.GetComponentsInChildren<TMP_Text>(true))
            {
                if (string.IsNullOrEmpty(tmp.text))
                {
                    continue;
                }
                tmp.text = first ? text : "";
                first = false;
            }
            foreach (Text legacy in entry.GetComponentsInChildren<Text>(true))
            {
                if (string.IsNullOrEmpty(legacy.text))
                {
                    continue;
                }
                legacy.text = first ? text : "";
                first = false;
            }
        }

        /// <summary>Without a layout group, the entry goes one button-spacing under the lowest button.</summary>
        private static void PlaceBelow(GameObject entry, Button lowest, List<Button> column)
        {
            var rect = entry.GetComponent<RectTransform>();
            var lowRect = lowest.GetComponent<RectTransform>();
            if (rect == null || lowRect == null)
            {
                return;
            }
            Vector2 step = new Vector2(0f, -lowRect.rect.height * 1.25f);
            var ordered = column.OrderByDescending(b => ScreenY(b)).Select(b => b.GetComponent<RectTransform>()).Where(r => r != null).ToList();
            if (ordered.Count >= 2)
            {
                Vector2 gap = ordered[ordered.Count - 1].anchoredPosition - ordered[ordered.Count - 2].anchoredPosition;
                if (gap.sqrMagnitude > 1f)
                {
                    step = gap;
                }
            }
            rect.anchoredPosition = lowRect.anchoredPosition + step;
        }

        /// <summary>Keeps gamepad/keyboard navigation reaching the entry when the menu uses explicit links.</summary>
        private static void WireNavigation(Button entry, Button template, List<Button> column, bool entryIsBelowTemplate)
        {
            Navigation nav = template.navigation;
            if (nav.mode != Navigation.Mode.Explicit)
            {
                return;
            }
            Selectable above = entryIsBelowTemplate ? template : nav.selectOnUp;
            Selectable below = entryIsBelowTemplate ? nav.selectOnDown : template;
            Navigation mine = nav;
            mine.selectOnUp = above;
            mine.selectOnDown = below;
            entry.navigation = mine;
            if (above != null)
            {
                Navigation n = above.navigation;
                n.selectOnDown = entry;
                above.navigation = n;
            }
            if (below != null)
            {
                Navigation n = below.navigation;
                n.selectOnUp = entry;
                below.navigation = n;
            }
        }

        private static float ScreenY(Button b) => b.transform.position.y;

        private static string Label(Button b)
        {
            TMP_Text tmp = b.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(t => !string.IsNullOrEmpty(t.text));
            if (tmp != null)
            {
                return tmp.text.Trim();
            }
            Text legacy = b.GetComponentsInChildren<Text>(true).FirstOrDefault(t => !string.IsNullOrEmpty(t.text));
            return legacy != null ? legacy.text.Trim() : "";
        }

        private static bool LabelHas(Button b, string[] words)
        {
            string label = Label(b).ToUpperInvariant();
            return words.Any(w => label.Contains(w));
        }

        private static void LogButtons(Transform root, string where)
        {
            var labels = root.GetComponentsInChildren<Button>(false)
                .Select(b => $"'{Label(b)}' under {(b.transform.parent != null ? b.transform.parent.name : "-")}");
            Plugin.Log.LogInfo($"No button column found in the {where}; using the corner button. Buttons: {string.Join(", ", labels)}");
        }
    }
}
