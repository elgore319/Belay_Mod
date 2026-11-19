using UnityEngine;

namespace Belay.UI
{
    /// <summary>
    /// Whether the game's own Cosmetics screen is on show in the main menu, where the climber outfit
    /// button sits. Looked up at most twice a second.
    /// </summary>
    internal static class CosmeticsScreen
    {
        private static UI_CosmeticsMenu menu;
        private static float nextLook;
        private static bool showing;
        private static int frame = -1;

        /// <summary>Bottom right of the screen, above the corner where the game keeps nothing.</summary>
        internal static Rect ButtonRect => new Rect(Screen.width - 300f, Screen.height - 150f, 260f, 52f);

        internal static bool Showing
        {
            get
            {
                if (frame == Time.frameCount)
                {
                    return showing;
                }
                frame = Time.frameCount;
                if (menu == null && Time.unscaledTime >= nextLook)
                {
                    nextLook = Time.unscaledTime + 0.5f;
                    menu = Object.FindFirstObjectByType<UI_CosmeticsMenu>();
                }
                showing = menu != null && Visible(menu);
                return showing;
            }
        }

        private static bool Visible(UI_CosmeticsMenu m)
        {
            if (!m.isActiveAndEnabled)
            {
                return false;
            }
            if (m.cosmeticInfoPanel != null && m.cosmeticInfoPanel.gameObject.activeInHierarchy)
            {
                return false;
            }
            // Menu screens fade and lock with canvas groups; hidden ones are see-through or not interactable.
            UI_MenuScreen screen = m.GetComponentInParent<UI_MenuScreen>();
            CanvasGroup own = screen != null ? screen.GetComponent<CanvasGroup>() : null;
            if (own != null && !own.interactable)
            {
                return false;
            }
            foreach (CanvasGroup g in m.GetComponentsInParent<CanvasGroup>())
            {
                if (g.alpha < 0.5f)
                {
                    return false;
                }
            }
            Canvas canvas = m.GetComponentInParent<Canvas>();
            return canvas == null || canvas.enabled;
        }
    }
}
