using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Belay.Rope
{
    /// <summary>
    /// One grabbable piece of a team rope, like CL_Handhold_Rope: a capsule along a segment you can grip, climb
    /// and swing on.
    /// </summary>
    public class RopeHold : CL_Handhold
    {
        public const float GripRadius = 0.07f;

        public TeamRope Rope { get; private set; }
        public int Segment { get; private set; }
        public CapsuleCollider Collider { get; private set; }

        /// <summary>Climbing speed while swinging (the game's ropes use 0: you hang on while swinging).</summary>
        private const float SwingClimb = 0f;

        public static RopeHold Create(TeamRope rope, int segment, int layer)
        {
            var go = new GameObject("Belay_RopeHold_" + segment);
            go.transform.SetParent(rope.transform, false);
            go.layer = layer;

            var col = go.AddComponent<CapsuleCollider>();
            col.radius = GripRadius;
            col.direction = 2;
            col.height = 0.5f;

            var body = go.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            var tagger = go.AddComponent<ObjectTagger>();
            tagger.tags = new List<string> { "Handhold" };

            var hold = go.AddComponent<RopeHold>();
            if (hold.activeEvent == null) hold.activeEvent = new UnityEvent();
            if (hold.stopEvent == null) hold.stopEvent = new UnityEvent();
            if (hold.hammerEvent == null) hold.hammerEvent = new UnityEvent();
            hold.useSharedHandholdMaterial = false;
            hold.canHammer = false;
            hold.secure = true;
            hold.dragMult = 10f;
            hold.Rope = rope;
            hold.Segment = segment;
            hold.Collider = col;
            return hold;
        }

        public override void Interact(Clickable.InteractionInfo info)
        {
            base.Interact(info);
            if (Rope != null && info != null && info.hand != null)
            {
                Rope.OnGrab(this, info.hand);
            }
        }

        public override void StopInteract(ENT_Player p, ENT_Player.Hand dropHand, string s = "")
        {
            if (Rope != null)
            {
                Rope.OnRelease(p, s == "jump");
            }
            base.StopInteract(p, dropHand, s);
        }

        public override float GetClimbMult()
        {
            return Rope != null && Rope.Swinging ? SwingClimb : climbMult;
        }
    }
}
