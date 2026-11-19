using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>
    /// Whether the local player is in Roach Mode (the binding). Shared as part of the outfit so teammates see a
    /// roach.
    /// </summary>
    public static class RoachMode
    {
        public const string PerkId = "Perk_Binding_RoachMode";

        private static bool cached;
        private static float nextCheck;

        public static bool Local
        {
            get
            {
                if (Time.unscaledTime < nextCheck)
                {
                    return cached;
                }
                nextCheck = Time.unscaledTime + 1f;
                try
                {
                    ENT_Player player = ENT_Player.GetPlayer();
                    cached = player != null && CL_GameManager.IsInGame() && player.HasPerk(PerkId);
                }
                catch (System.Exception)
                {
                    cached = false;
                }
                return cached;
            }
        }
    }
}
