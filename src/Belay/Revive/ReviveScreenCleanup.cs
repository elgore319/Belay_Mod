using HarmonyLib;
using UnityEngine;

namespace Belay.Revive
{
    /// <summary>
    /// Some kills leave the screen covered when you come back. The Face darkens the screen as it gets close and
    /// only fades that out while it keeps running, so a Face that stops (removed, switched off, or broken) leaves
    /// it dark. The vent creature's grab darkens the screen and puts its hand in front of the camera until it lets
    /// go, which it never does once you're dead. On revive, clear both.
    /// </summary>
    internal static class ReviveScreenCleanup
    {
        private static readonly string[] Vignettes = { "fade", "bloodied", "hurt" };

        internal static void Run(ENT_Player player)
        {
            Safe("the Face's dark screen", () =>
            {
                var FaceSmooth = AccessTools.FieldRefAccess<DEN_Face, float>("screenEffectSmooth");
                DEN_Face.screenEffectAmount = 0f;
                foreach (DEN_Face face in Object.FindObjectsByType<DEN_Face>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (face == null) continue;
                    FaceSmooth(face) = 0f;
                    if (face.screenEffectMaterial != null) face.screenEffectMaterial.SetFloat("_AlphaMult", 0f);
                    if (face.screenEffectParticles != null) face.screenEffectParticles.Stop();
                }
            });
            Safe("the vent creature's grab", () =>
            {
                var VentGrabbed = AccessTools.FieldRefAccess<DEN_VentThing, bool>("hasGrabbedTarget");
                var VentTarget = AccessTools.FieldRefAccess<DEN_VentThing, GameEntity>("targetEntity");
                var VentHand = AccessTools.FieldRefAccess<DEN_VentThing, SpriteRenderer>("handSpriteRenderer");
                foreach (DEN_VentThing vent in Object.FindObjectsByType<DEN_VentThing>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (vent == null) continue;
                    if (VentGrabbed(vent) && VentTarget(vent) == player) vent.DropTarget(false);
                    SpriteRenderer hand = VentHand(vent);
                    if (hand != null) hand.gameObject.SetActive(false);
                }
                AccessTools.Field(typeof(DEN_VentThing), "playerIsGrabbed")?.SetValue(null, false);
                AccessTools.Field(typeof(DEN_VentThing), "grabberVentThing")?.SetValue(null, null);
                player.SetGravity(true);
                player.SetGravityMult(1f);
            });
            Safe("the screen vignettes", () =>
            {
                CL_UIManager ui = CL_UIManager.instance;
                if (ui == null) return;
                foreach (string v in Vignettes)
                {
                    ui.SetVignetteTarget(v, 0f);
                    ui.SetVignette(v, 0f);
                }
            });
        }

        private static void Safe(string what, System.Action action)
        {
            try
            {
                action();
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't clear {what} on revive: {e.Message}");
            }
        }
    }
}
