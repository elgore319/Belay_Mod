using System.Text;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Level buttons are shared: when anyone presses one, it's pressed for everyone, so whatever it
    /// drives (hatches, lifts, lights, animated doors) changes on every PC at once. In interludes the
    /// press is only allowed once the whole team is inside (see InterludeGate).
    /// Buttons that buy things or act on the presser's own player are left alone, because currency
    /// is never shared.
    /// </summary>
    public class ButtonSync : MonoBehaviour
    {
        public static ButtonSync Instance { get; private set; }

        /// <summary>Set while replaying someone else's press, so it isn't sent back or gated.</summary>
        internal static bool Applying;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.ButtonPress, (origin, r) => ApplyPress(r.ReadString()));
        }

        internal static bool Shareable(CL_Button button)
        {
            if (button == null || button.GetComponentInParent<M_Level>() == null || button.activeEvent == null)
            {
                return false;
            }
            for (int i = 0; i < button.activeEvent.GetPersistentEventCount(); i++)
            {
                Object target = button.activeEvent.GetPersistentTarget(i);
                string method = button.activeEvent.GetPersistentMethodName(i) ?? "";
                string type = target != null ? target.GetType().Name : "";
                if (IsPersonal(type) || IsPersonal(method))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsPersonal(string name)
        {
            string n = name.ToLowerInvariant();
            return n.Contains("roach") || n.Contains("trader") || n.Contains("shop") || n.Contains("vend")
                || n.Contains("buy") || n.Contains("purchase") || n.Contains("currency") || n.Contains("inventory")
                || n.Contains("ent_player") || n.Contains("perk") || n.Contains("cosmetic");
        }

        internal void Broadcast(CL_Button button)
        {
            string id = ObjectId(button);
            Plugin.Log.LogInfo("Button pressed: " + id);
            NetSession.Instance.Send(PacketType.ButtonPress, w => w.Write(id));
        }

        private static void ApplyPress(string id)
        {
            foreach (CL_Button button in FindObjectsByType<CL_Button>(FindObjectsSortMode.None))
            {
                if (button != null && ObjectId(button) == id)
                {
                    Applying = true;
                    try
                    {
                        button.Interact();
                    }
                    finally
                    {
                        Applying = false;
                    }
                    return;
                }
            }
            Plugin.Log.LogInfo("Teammate pressed a button that isn't loaded here: " + id);
        }

        /// <summary>Same scheme as doors: level name, the level's shared height, then the sibling path inside it.</summary>
        internal static string ObjectId(Component c)
        {
            Transform t = c.transform;
            M_Level level = t.GetComponentInParent<M_Level>();
            Transform root = level != null ? level.transform : null;
            var path = new StringBuilder();
            for (Transform cursor = t; cursor != null && cursor != root; cursor = cursor.parent)
            {
                path.Insert(0, "/" + cursor.GetSiblingIndex() + cursor.name);
            }
            if (root == null)
            {
                return c.gameObject.scene.name + path;
            }
            return root.name + "@" + Mathf.RoundToInt(WorldSpace.ToShared(root.position).y) + path;
        }
    }

    [HarmonyPatch(typeof(CL_Button), nameof(CL_Button.Interact), new System.Type[0])]
    internal static class ButtonPressPatch
    {
        [HarmonyPrefix]
        private static void Prefix(CL_Button __instance, out int __state)
        {
            __state = Traverse.Create(__instance).Field("presses").GetValue<int>();
        }

        [HarmonyPostfix]
        private static void Postfix(CL_Button __instance, int __state)
        {
            if (ButtonSync.Applying || ButtonSync.Instance == null || NetSession.Instance == null || !NetSession.Instance.InLobby || !WorldSpace.Ready)
            {
                return;
            }
            int now = Traverse.Create(__instance).Field("presses").GetValue<int>();
            if (now > __state && ButtonSync.Shareable(__instance))
            {
                ButtonSync.Instance.Broadcast(__instance);
            }
        }
    }
}
