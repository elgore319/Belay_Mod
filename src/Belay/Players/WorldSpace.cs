using UnityEngine;

namespace Belay.Players
{
    /// <summary>
    /// White Knuckle periodically shifts the whole world down (CL_GameManager.SetWorldOffset) once the
    /// player climbs past y=10000, and each client does this on its own schedule. Everything we send is
    /// converted to "corrected" space (offset removed) and back on arrival.
    /// </summary>
    public static class WorldSpace
    {
        public static bool Ready => CL_GameManager.IsInGame() && ENT_Player.playerObject != null;

        /// <summary>The local world's current vertical offset.</summary>
        public static float Offset
        {
            get
            {
                if (!Ready)
                {
                    return 0f;
                }
                // GetCorrectedPosition(p) == p - up * currentWorldOffset
                return -CL_GameManager.gMan.GetCorrectedPosition(Vector3.zero).y;
            }
        }

        public static Vector3 ToShared(Vector3 local)
        {
            return local - Vector3.up * Offset;
        }

        public static Vector3 ToLocal(Vector3 shared)
        {
            return shared + Vector3.up * Offset;
        }
    }
}
