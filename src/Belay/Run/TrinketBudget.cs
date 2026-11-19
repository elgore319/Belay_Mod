using System.Collections.Generic;
using Belay.Net;
using HarmonyLib;

namespace Belay.Run
{
    /// <summary>
    /// The game only stops over-budget trinket picks by greying out Play, which clients never press. So before
    /// trinkets activate, the pick is cut back to the budget.
    /// </summary>
    [HarmonyPatch(typeof(M_Gamemode), nameof(M_Gamemode.StartFreshGamemode))]
    internal static class TrinketBudget
    {
        [HarmonyPrefix]
        private static void Prefix(M_Gamemode __instance)
        {
            if (NetSession.Instance == null || !NetSession.Instance.InLobby || StatManager.saveData == null)
            {
                return;
            }
            try
            {
                string mode = __instance.GetGamemodeName();
                TrinketRules.StripBanned(mode);
                List<string> picked = StatManager.saveData.GetGamemodeTrinkets(mode);
                if (picked == null || picked.Count == 0)
                {
                    return;
                }
                // Same rule as the picker: trinket cost may not exceed 1 + binding cost.
                var trinkets = new List<(string name, Trinket asset)>();
                int used = 0;
                int budget = 1;
                foreach (string name in picked)
                {
                    Trinket t = CL_AssetManager.GetTrinketAsset(name);
                    if (t == null) continue;
                    if (t.isBinding) budget += t.cost;
                    else used += t.cost;
                    trinkets.Add((name, t));
                }
                if (used <= budget)
                {
                    return;
                }
                var kept = new List<string>();
                var dropped = new List<string>();
                int spent = 0;
                foreach (var (name, t) in trinkets)
                {
                    if (t.isBinding)
                    {
                        kept.Add(name);
                    }
                    else if (spent + t.cost <= budget)
                    {
                        spent += t.cost;
                        kept.Add(name);
                    }
                    else
                    {
                        dropped.Add(string.IsNullOrEmpty(t.title) ? name : t.title);
                    }
                }
                StatManager.saveData.SetGamemodeTrinkets(mode, kept);
                Plugin.Log.LogInfo($"Trinkets over budget ({used}/{budget}); left behind: {string.Join(", ", dropped)}");
                RunFlow.Instance?.ShowNotice("Your trinkets were over budget. Left behind: " + string.Join(", ", dropped), 8f);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't check the trinket budget: " + e.Message);
            }
        }
    }
}
