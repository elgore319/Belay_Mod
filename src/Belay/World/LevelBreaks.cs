using System.IO;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Bits of a level that change for good when one player touches them, and used to change only on
    /// that player's PC:
    ///  - Crumbling handholds (CL_Handhold_Breakable) that give way: gone for everyone, and anyone
    ///    hanging on it there drops too.
    ///  - Glass and other shatter props (UT_ShatterProp): shatter for everyone.
    ///  - Item slots (UT_InsertableItem), where you put an item in to make something happen: the slot
    ///    goes off for everyone. Slots on computer terminals stay personal, like the terminals.
    /// </summary>
    public class LevelBreaks : MonoBehaviour
    {
        public static LevelBreaks Instance { get; private set; }

        internal static bool Applying;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.HandholdBroke, (o, r) => Apply<CL_Handhold_Breakable>(o, r.ReadString(), h => h.Break()));
            NetSession.Instance.On(PacketType.PropShattered, (o, r) => Apply<UT_ShatterProp>(o, r.ReadString(), p => p.Shatter()));
            NetSession.Instance.On(PacketType.ItemInserted, HandleInserted);
        }

        internal static bool Live => Instance != null && !Applying && NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready;

        internal static void Share(PacketType type, Component c, string extra = null)
        {
            string id = ButtonSync.ObjectId(c);
            NetSession.Instance.Send(type, w =>
            {
                w.Write(id);
                if (extra != null) w.Write(extra);
            });
        }

        private static T FindById<T>(string id) where T : Component
        {
            foreach (T c in FindObjectsByType<T>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (c != null && ButtonSync.ObjectId(c) == id)
                {
                    return c;
                }
            }
            return null;
        }

        private static void Apply<T>(SteamId origin, string id, System.Action<T> act) where T : Component
        {
            if (origin == NetSession.LocalId || !WorldSpace.Ready)
            {
                return;
            }
            T c = FindById<T>(id);
            if (c == null)
            {
                return; // already gone here, or not built yet
            }
            Applying = true;
            try
            {
                act(c);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't apply a teammate's {typeof(T).Name} ({id}): {e.Message}");
            }
            finally
            {
                Applying = false;
            }
        }

        private void HandleInserted(SteamId origin, BinaryReader r)
        {
            string id = r.ReadString();
            string text = r.ReadString();
            Apply<UT_InsertableItem>(origin, id, slot =>
            {
                if (!slot.active || slot.hasBeenPressed)
                {
                    return;
                }
                slot.activeEvent?.Invoke();
                slot.interactEvent?.Invoke(text);
                slot.hasBeenPressed = true;
            });
        }

        internal static bool OnTerminal(Component c) => c.GetComponentInParent<OS_Computer_Interface>() != null;
    }

    [HarmonyPatch]
    internal static class LevelBreakPatches
    {
        [HarmonyPatch(typeof(CL_Handhold_Breakable), nameof(CL_Handhold_Breakable.Break))]
        [HarmonyPrefix]
        private static void Break(CL_Handhold_Breakable __instance)
        {
            if (LevelBreaks.Live && __instance.gameObject.activeInHierarchy)
            {
                LevelBreaks.Share(PacketType.HandholdBroke, __instance);
            }
        }

        [HarmonyPatch(typeof(UT_ShatterProp), nameof(UT_ShatterProp.Shatter))]
        [HarmonyPrefix]
        private static void Shatter(UT_ShatterProp __instance, bool ___shattered)
        {
            if (LevelBreaks.Live && !___shattered)
            {
                LevelBreaks.Share(PacketType.PropShattered, __instance);
            }
        }

        [HarmonyPatch(typeof(UT_InsertableItem), nameof(UT_InsertableItem.Interact), new[] { typeof(Clickable.InteractionInfo) })]
        [HarmonyPrefix]
        private static void Insert(UT_InsertableItem __instance, Clickable.InteractionInfo info)
        {
            if (LevelBreaks.Live && !__instance.hasBeenPressed && !LevelBreaks.OnTerminal(__instance))
            {
                LevelBreaks.Share(PacketType.ItemInserted, __instance, info?.interactionString ?? "");
            }
        }

        [HarmonyPatch(typeof(UT_InsertableItem), nameof(UT_InsertableItem.Interact), new System.Type[0])]
        [HarmonyPrefix]
        private static void InsertPlain(UT_InsertableItem __instance)
        {
            if (LevelBreaks.Live && !__instance.hasBeenPressed && !LevelBreaks.OnTerminal(__instance))
            {
                LevelBreaks.Share(PacketType.ItemInserted, __instance, "");
            }
        }
    }
}
