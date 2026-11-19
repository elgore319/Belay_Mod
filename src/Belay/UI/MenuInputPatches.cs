using HarmonyLib;
using UnityEngine;

namespace Belay.UI
{
    /// <summary>
    /// While the menu is open in a run, mouse moves don't turn the camera and clicks don't grab. Held grips stay
    /// held. A focused text box also keeps keys from the player.
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
