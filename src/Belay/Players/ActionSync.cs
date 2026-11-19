using System;
using Belay.Net;
using Belay.Players.Avatar;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.Players
{
    /// <summary>
    /// Shares item actions (swings, pitons, throws, shots, eating) so teammates' avatars act them out. Hooks the
    /// game's own item code.
    /// </summary>
    public static class ActionSync
    {
        private static readonly AccessTools.FieldRef<HandItem_Melee, bool> IsCharging =
            AccessTools.FieldRefAccess<HandItem_Melee, bool>("isCharging");

        private static readonly float[] lastSent = { -1f, -1f };
        private static readonly PlayerAction[] lastAction = new PlayerAction[2];

        public static void Register()
        {
            NetSession.Instance.On(PacketType.PlayerAction, Receive);
        }

        private static void Receive(SteamId origin, System.IO.BinaryReader r)
        {
            var action = (PlayerAction)r.ReadByte();
            int hand = r.ReadByte();
            if (origin == NetSession.LocalId || PlayerSync.Instance == null)
            {
                return;
            }
            if (PlayerSync.Instance.Remotes.TryGetValue(origin, out RemotePlayer remote) && remote != null)
            {
                remote.PlayAction(hand, action);
            }
        }

        /// <summary>Tells the team the local player did <paramref name="action"/> with the item in <paramref name="hand"/>.</summary>
        public static void Send(int hand, PlayerAction action)
        {
            NetSession net = NetSession.Instance;
            if (net == null || !net.InLobby || !WorldSpace.Ready || hand < 0 || hand > 1)
            {
                return;
            }
            // Automatic weapons can fire every frame; a few motions a second is all an arm can show.
            float now = Time.unscaledTime;
            if (action == lastAction[hand] && now - lastSent[hand] < 0.12f)
            {
                return;
            }
            lastAction[hand] = action;
            lastSent[hand] = now;
            net.Send(PacketType.PlayerAction, w =>
            {
                w.Write((byte)action);
                w.Write((byte)hand);
            });
        }

        private static int HandOf(HandItem item)
        {
            return item != null && item.hand != null ? item.hand.id : -1;
        }

        private static bool IsLocal(HandItem item)
        {
            return item != null && item.hand != null && item.hand.GetPlayer() == ENT_Player.playerObject;
        }

        private static void Safe(Action send)
        {
            try
            {
                send();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Couldn't share an item action: " + e.Message);
            }
        }

        [HarmonyPatch]
        internal static class Patches
        {
            /// <summary>Every item use starts here (the fire button on the hand holding it).</summary>
            [HarmonyPatch(typeof(HandItem), nameof(HandItem.TryUse))]
            [HarmonyPrefix]
            private static void TryUsePrefix(HandItem __instance, out bool __state)
            {
                __state = __instance != null && __instance.used;
            }

            [HarmonyPatch(typeof(HandItem), nameof(HandItem.TryUse))]
            [HarmonyPostfix]
            private static void TryUsePostfix(HandItem __instance, bool __state)
            {
                Safe(() =>
                {
                    if (!IsLocal(__instance))
                    {
                        return;
                    }
                    int hand = HandOf(__instance);
                    bool wasUsed = __state;
                    switch (__instance)
                    {
                        case HandItem_Melee melee:
                            // A plain swing is sent from StartUse; this only catches the start of a charge.
                            if (IsCharging(melee))
                            {
                                Send(hand, PlayerAction.Charge);
                            }
                            break;
                        case HandItem_Shoot _:
                            // Sent from Shoot, when it actually fires.
                            break;
                        case HandItem_Piton _:
                            if (!wasUsed)
                            {
                                Send(hand, PlayerAction.Place);
                            }
                            break;
                        case HandItem_Food _:
                            if (!wasUsed)
                            {
                                Send(hand, PlayerAction.Eat);
                            }
                            break;
                        case HandItem_Buff _:
                            Send(hand, PlayerAction.Eat);
                            break;
                        default:
                            Send(hand, PlayerAction.Use);
                            break;
                    }
                });
            }

            [HarmonyPatch(typeof(HandItem_Melee), nameof(HandItem_Melee.StartUse))]
            [HarmonyPostfix]
            private static void StartUse(HandItem_Melee __instance, bool charged)
            {
                Safe(() =>
                {
                    if (IsLocal(__instance))
                    {
                        Send(HandOf(__instance), charged ? PlayerAction.HeavySwing : PlayerAction.Swing);
                    }
                });
            }

            [HarmonyPatch(typeof(HandItem_Shoot), nameof(HandItem_Shoot.Shoot))]
            [HarmonyPostfix]
            private static void Shoot(HandItem_Shoot __instance)
            {
                Safe(() =>
                {
                    if (IsLocal(__instance))
                    {
                        Send(HandOf(__instance), PlayerAction.Shoot);
                    }
                });
            }

            /// <summary>Q: the item is thrown, or set down where the player aims.</summary>
            [HarmonyPatch(typeof(Inventory), nameof(Inventory.DropItemFromHand))]
            [HarmonyPrefix]
            private static void DropPrefix(Inventory __instance, int h, out bool __state)
            {
                __state = false;
                try
                {
                    __state = __instance.itemHands != null && h >= 0 && h < __instance.itemHands.Length
                        && __instance.itemHands[h].currentItem != null;
                }
                catch (Exception)
                {
                }
            }

            [HarmonyPatch(typeof(Inventory), nameof(Inventory.DropItemFromHand))]
            [HarmonyPostfix]
            private static void DropPostfix(bool __result, int h, bool addtoInventory, bool pocket, bool __state)
            {
                // Only a real drop into the world: not into the backpack or a pocket, and not with the
                // inventory screen open (that moves it into the bag too).
                if (__result && __state && !addtoInventory && !pocket && !Inventory.IsShowingInventory())
                {
                    Safe(() => Send(h, PlayerAction.Toss));
                }
            }
        }
    }
}
