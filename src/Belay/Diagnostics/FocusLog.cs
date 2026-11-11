using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace Belay.Diagnostics
{
    /// <summary>
    /// Writes a line to the BepInEx log whenever the game window loses or gets focus, or the cursor
    /// lock changes, with the keys pressed just before and (on Windows) the window that took focus.
    /// Used to track down being tabbed out of the game mid-run.
    /// </summary>
    public class FocusLog : MonoBehaviour
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        private static readonly KeyCode[] AllKeys = (KeyCode[])Enum.GetValues(typeof(KeyCode));
        private readonly Queue<KeyValuePair<float, KeyCode>> recentKeys = new Queue<KeyValuePair<float, KeyCode>>();
        private bool lastFocused = true;
        private CursorLockMode lastLock;
        private bool lastVisible;
        private int lastDisplays;

        private void Start()
        {
            lastFocused = Application.isFocused;
            lastLock = Cursor.lockState;
            lastVisible = Cursor.visible;
            lastDisplays = ActiveDisplays();
            Plugin.Log.LogInfo($"[Focus] watching. displays connected={Display.displays.Length} active={lastDisplays} mode={Screen.fullScreenMode} res={Screen.width}x{Screen.height}");
        }

        private static int ActiveDisplays()
        {
            int n = 0;
            foreach (Display d in Display.displays) if (d.active) n++;
            return n;
        }

        private void Update()
        {
            float now = Time.unscaledTime;
            if (Input.anyKeyDown)
            {
                foreach (KeyCode k in AllKeys)
                {
                    if (Input.GetKeyDown(k)) recentKeys.Enqueue(new KeyValuePair<float, KeyCode>(now, k));
                }
            }
            while (recentKeys.Count > 0 && now - recentKeys.Peek().Key > 2f) recentKeys.Dequeue();

            if (Cursor.lockState != lastLock || Cursor.visible != lastVisible)
            {
                Plugin.Log.LogInfo($"[Focus] cursor {lastLock}/{(lastVisible ? "shown" : "hidden")} -> {Cursor.lockState}/{(Cursor.visible ? "shown" : "hidden")} | {Context()}");
                lastLock = Cursor.lockState;
                lastVisible = Cursor.visible;
            }
            int displays = ActiveDisplays();
            if (displays != lastDisplays)
            {
                Plugin.Log.LogInfo($"[Focus] active displays {lastDisplays} -> {displays}");
                lastDisplays = displays;
            }
        }

        private void OnApplicationFocus(bool focused)
        {
            if (focused == lastFocused) return;
            lastFocused = focused;
            Plugin.Log.LogInfo($"[Focus] game {(focused ? "GOT" : "LOST")} focus | {Context()}{(focused ? "" : " | now in front: " + Foreground())}");
        }

        private string Context()
        {
            var keys = new StringBuilder();
            float now = Time.unscaledTime;
            foreach (var k in recentKeys) keys.Append($"{k.Value}({now - k.Key:0.00}s) ");
            bool paused = CL_GameManager.gMan != null && CL_GameManager.gMan.isPaused;
            return $"keys in last 2s: [{keys.ToString().Trim()}] mouse={Input.mousePosition.x:0},{Input.mousePosition.y:0} lock={Cursor.lockState} inGame={CL_GameManager.IsInGame()} paused={paused} terminal={OS_Manager.inUse} mode={Screen.fullScreenMode}";
        }

        private static string Foreground()
        {
            try
            {
                IntPtr h = GetForegroundWindow();
                var sb = new StringBuilder(256);
                GetWindowText(h, sb, sb.Capacity);
                GetWindowThreadProcessId(h, out uint pid);
                string proc = "?";
                try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
                return $"\"{sb}\" ({proc})";
            }
            catch (Exception e)
            {
                return "unknown (" + e.GetType().Name + ")";
            }
        }
    }
}
