using HarmonyLib;
using System.Reflection;

namespace Belay.Patches
{
    /// <summary>
    /// The game draws its level, spawn and prop randomness from PcgRandom values instead of UnityEngine.Random.
    /// This builds one from a seed of ours, the same way the game seeds its own (only the increment is set).
    /// </summary>
    internal static class Pcg
    {
        private static readonly FieldInfo Inc = AccessTools.Field(typeof(PcgRandom), "inc");

        internal static PcgRandom From(int seed)
        {
            object boxed = default(PcgRandom);
            Inc?.SetValue(boxed, unchecked((ulong)seed));
            return (PcgRandom)boxed;
        }
    }
}
