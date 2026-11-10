using HarmonyLib;
using UnityEngine;

namespace Belay.UI
{
    /// <summary>
    /// While the Belay menu is open in a run, moving the mouse doesn't turn the camera and clicking
    /// doesn't grab, so using the menu never tugs your character. Grips you're already holding stay
    /// held (only new presses are dropped), so you don't fall off a wall by opening the menu.
    /// While a menu text box has focus, keyboard presses don't reach the player either.
    /// </summary>
    [HarmonyPatch]
    internal static class MenuInputPatches
    {
        private static readonly InputManager.VectorInput NoVector = new InputManager.VectorInput { vector = Vector2.zero };

        [HarmonyPatch(typeof(InputManager), nameof(InputManager.GetVector))]
        [HarmonyPostfix]
        private static void GetVector(string name, ref InputManager.VectorInput __result)
        {
            if (__result == null || !BelayMenu.CapturingInput)
            {
                return;
            }
            if (name == "Look" || BelayMenu.Typing)
            {
                NoVector.name = __result.name;
                __result = NoVector;
            }
        }

        [HarmonyPatch(typeof(InputManager), nameof(InputManager.GetButton))]
        [HarmonyPostfix]
        private static void GetButton(string name, ref InputManager.ButtonInput __result)
        {
            if (__result == null || !BelayMenu.CapturingInput || !__result.Down)
            {
                return;
            }
            if (BelayMenu.Typing || (name != null && name.StartsWith("Fire")))
            {
                __result = new InputManager.ButtonInput
                {
                    name = __result.name,
                    Pressed = __result.Pressed,
                    Down = false,
                    Up = __result.Up,
                };
            }
        }
    }
}
