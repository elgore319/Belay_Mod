using System.Collections.Generic;
using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>Swings a long coat (<see cref="CoatSkirt"/>) out of the climber's legs' way.</summary>
    public partial class ClimberAvatar
    {
        private CoatSkirt coat;
        private Mesh coatMesh, coatSource;
        private Vector3[] coatRest, coatBent;
        private Vector3 coatLastPelvis, coatLastVelocity;
        private bool coatHasLast;
        private readonly List<CoatSkirt.Capsule> coatLegs = new List<CoatSkirt.Capsule>(4);

        private void UpdateCoat(float dt)
        {
            if (Reforming || climberHidden)
            {
                coatHasLast = false; // the revive borrows the coat's mesh; pick up again after
                return;
            }
            MeshFilter filter = extra.GetComponent<MeshFilter>();
            if (!extra.gameObject.activeSelf || filter == null)
            {
                return;
            }
            if (filter.sharedMesh != coatMesh || coatMesh == null)
            {
                // New outfit: a coat with a skirt gets its own copy of the mesh to bend.
                SetupCoat(filter);
                if (coat == null)
                {
                    return;
                }
            }

            Vector3 pelvis = torso.position;
            Vector3 acceleration = Vector3.zero;
            if (coatHasLast && dt > 1e-4f)
            {
                Vector3 v = (pelvis - coatLastPelvis) / dt;
                if (v.sqrMagnitude > 400f)
                {
                    // A teleport, not a swing.
                    coat.Reset();
                    v = Vector3.zero;
                    coatLastVelocity = Vector3.zero;
                }
                acceleration = Vector3.ClampMagnitude((v - coatLastVelocity) / dt, 25f);
                coatLastVelocity = v;
            }
            coatLastPelvis = pelvis;
            coatHasLast = true;

            coatLegs.Clear();
            AddLeg(leftLeg);
            AddLeg(rightLeg);
            Vector3 down = torso.InverseTransformDirection(Vector3.down);
            // The coat works in design units; the climber is scaled down from them.
            Vector3 localAcceleration = torso.InverseTransformDirection(acceleration) / Scale;
            coat.Step(dt, down, localAcceleration, coatLegs);
            coat.Deform(coatRest, coatBent);
            coatMesh.vertices = coatBent;
        }

        private void AddLeg(Limb leg)
        {
            Vector3 hip = torso.InverseTransformPoint(leg.Root);
            Vector3 knee = torso.InverseTransformPoint(leg.Bend);
            Vector3 ankle = torso.InverseTransformPoint(leg.Tip);
            coatLegs.Add(new CoatSkirt.Capsule { A = hip, B = knee, Radius = CoatSkirt.ThighRadius });
            coatLegs.Add(new CoatSkirt.Capsule { A = knee, B = ankle, Radius = CoatSkirt.ShinRadius });
        }

        private void SetupCoat(MeshFilter filter)
        {
            Mesh source = filter.sharedMesh;
            if (coatMesh != null && source == coatSource)
            {
                filter.sharedMesh = coatMesh;
                return;
            }
            if (coatMesh != null)
            {
                Object.Destroy(coatMesh);
            }
            coatMesh = null;
            coat = null;
            coatSource = source;
            coatHasLast = false;
            if (source == null || !source.isReadable || !LoreLooks.SkirtShape(outfit.Look, out float hem, out float flare, out float back))
            {
                return;
            }
            coat = new CoatSkirt(hem, flare, back);
            coatMesh = Object.Instantiate(source);
            coatMesh.name = source.name + "_Coat";
            coatMesh.MarkDynamic();
            Bounds bounds = coatMesh.bounds;
            bounds.Expand(0.5f); // room for the hem to swing out
            coatMesh.bounds = bounds;
            coatRest = source.vertices;
            coatBent = (Vector3[])coatRest.Clone();
            filter.sharedMesh = coatMesh;
        }
    }
}
