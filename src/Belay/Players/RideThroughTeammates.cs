using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Belay.Players
{
    /// <summary>
    /// The game keeps you on a moving floor with a sphere cast from your feet, and a teammate's body in a crowded
    /// elevator broke it (riders got flung). The cast now ignores teammates.
    /// </summary>
    [HarmonyPatch(typeof(ENT_Player), "Movement")]
    internal static class RideThroughTeammates
    {
        private static readonly MethodInfo SphereCast = AccessTools.Method(typeof(Physics), nameof(Physics.SphereCast),
            new[] { typeof(Vector3), typeof(float), typeof(Vector3), typeof(RaycastHit).MakeByRefType(), typeof(float), typeof(int) });

        private static readonly MethodInfo Replacement = AccessTools.Method(typeof(RideThroughTeammates), nameof(CastIgnoringTeammates));

        private static readonly List<int> savedLayers = new List<int>();

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;
            foreach (CodeInstruction ins in instructions)
            {
                if (SphereCast != null && ins.Calls(SphereCast))
                {
                    ins.opcode = System.Reflection.Emit.OpCodes.Call;
                    ins.operand = Replacement;
                    replaced++;
                }
                yield return ins;
            }
            Plugin.Log.LogInfo($"Platform riding looks through teammates ({replaced} cast(s) patched)");
        }

        private static bool CastIgnoringTeammates(Vector3 origin, float radius, Vector3 direction, out RaycastHit hit, float maxDistance, int layerMask)
        {
            List<RemoteBody> bodies = RemoteBody.All;
            if (bodies.Count == 0)
            {
                return Physics.SphereCast(origin, radius, direction, out hit, maxDistance, layerMask);
            }
            int hidden = FreeLayer(layerMask);
            if (hidden < 0)
            {
                return Physics.SphereCast(origin, radius, direction, out hit, maxDistance, layerMask);
            }
            // Move teammate bodies to a layer the cast skips for the length of the cast only.
            savedLayers.Clear();
            for (int i = 0; i < bodies.Count; i++)
            {
                GameObject go = bodies[i] != null ? bodies[i].gameObject : null;
                savedLayers.Add(go != null ? go.layer : 0);
                if (go != null)
                {
                    go.layer = hidden;
                }
            }
            try
            {
                return Physics.SphereCast(origin, radius, direction, out hit, maxDistance, layerMask);
            }
            finally
            {
                for (int i = 0; i < bodies.Count && i < savedLayers.Count; i++)
                {
                    if (bodies[i] != null)
                    {
                        bodies[i].gameObject.layer = savedLayers[i];
                    }
                }
            }
        }

        private static int FreeLayer(int mask)
        {
            for (int layer = 31; layer >= 0; layer--)
            {
                if ((mask & (1 << layer)) == 0)
                {
                    return layer;
                }
            }
            return -1;
        }
    }
}
