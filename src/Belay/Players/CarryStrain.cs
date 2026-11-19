using Belay.Net;
using UnityEngine;

namespace Belay.Players
{
    /// <summary>
    /// Lifting a teammate drains that hand's grip much faster and slows you on foot; run out and you drop them.
    /// Hanging off a climbing teammate costs nothing extra.
    /// </summary>
    public class CarryStrain : MonoBehaviour
    {
        // ENT_Player moves by vel * fixedDeltaTime * 175, and AddForce(v) adds v / 10 to vel.
        private const float ForceToVel = 0.1f;

        /// <summary>Extra grip lost per second by the hand holding a teammate.</summary>
        private const float GripDrain = 1.2f;
        /// <summary>Share of your walking speed taken away per physics step while carrying.</summary>
        private const float Drag = 0.12f;

        private void FixedUpdate()
        {
            if (NetSession.Instance == null || !NetSession.Instance.InLobby || !WorldSpace.Ready || !Plugin.SolidTeammates.Value)
            {
                return;
            }
            ENT_Player player = ENT_Player.playerObject;
            if (player == null || player.dead || CL_GameManager.gMan == null || CL_GameManager.gMan.isPaused)
            {
                return;
            }
            ENT_Player.Hand holding = null;
            bool otherOnWall = false;
            foreach (ENT_Player.Hand hand in player.hands)
            {
                if (hand == null || hand.interactState != ENT_Player.InteractType.hanging || hand.holdTarget == null)
                {
                    continue;
                }
                RemoteBody body = hand.holdTarget.GetComponent<RemoteBody>();
                if (body != null && body.Owner != null && !body.Owner.Ghost)
                {
                    holding = holding ?? hand;
                }
                else
                {
                    otherOnWall = true;
                }
            }
            if (holding == null)
            {
                return;
            }
            bool grounded = player.IsGrounded(true);
            if (!grounded && !otherOnWall)
            {
                return; // riding a climbing teammate, not lifting them
            }
            holding.DamageGripStrength(GripDrain * Time.fixedDeltaTime);
            if (grounded)
            {
                Vector3 v = player.GetVelocity();
                v.y = 0f;
                player.AddForce(-v * (Drag / ForceToVel));
            }
        }
    }
}
