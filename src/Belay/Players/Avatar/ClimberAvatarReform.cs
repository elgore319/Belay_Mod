using System.Collections.Generic;
using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>Plays the totem revive (<see cref="Reform"/>) on the climber.</summary>
    public partial class ClimberAvatar
    {
        private struct ReformPiece
        {
            public Transform Flesh;
            public Transform Bone;
            public Reform.Part Kind;
        }

        private static readonly Dictionary<int, Mesh> boneMeshes = new Dictionary<int, Mesh>();
        private static Material[] boneMaterials, coreMaterials;

        private List<ReformPiece> reformPieces;
        private Transform reformCore;
        private float reformStart = -1f;

        /// <summary>Whether the climber is being rebuilt by a totem revive right now.</summary>
        public bool Reforming => reformStart >= 0f;

        /// <summary>Starts the inside-out rebuild. Call when the revived teammate's body reappears.</summary>
        public void StartReform()
        {
            try
            {
                SetupReform();
                reformStart = Time.unscaledTime;
            }
            catch (System.Exception e)
            {
                Plugin.Log?.LogWarning("Can't play the revive effect: " + e.Message);
                reformStart = -1f;
            }
        }

        private void SetupReform()
        {
            if (reformPieces != null)
            {
                return;
            }
            if (boneMaterials == null)
            {
                PixelTextures.Recipe bone = Belay.Items.TotemLook.Recipes()[Belay.Items.TotemLook.BoneSlot];
                boneMaterials = MakeItemMaterials(new[] { bone }, -1);
                PixelTextures.Recipe glow = Belay.Items.TotemLook.Recipes()[Belay.Items.TotemLook.GlowSlot];
                coreMaterials = MakeItemMaterials(new[] { glow }, -1);
                if (coreMaterials[0].HasProperty("_EmissionColor"))
                {
                    coreMaterials[0].SetColor("_EmissionColor", Reform.GlowColor * 2.2f);
                }
            }
            reformPieces = new List<ReformPiece>();
            Add(torso, Reform.Part.Torso);
            Add(head, Reform.Part.Head);
            Add(leftArm.upper, Reform.Part.UpperLimb);
            Add(rightArm.upper, Reform.Part.UpperLimb);
            Add(leftLeg.upper, Reform.Part.UpperLimb);
            Add(rightLeg.upper, Reform.Part.UpperLimb);
            Add(leftArm.lower, Reform.Part.LowerLimb);
            Add(rightArm.lower, Reform.Part.LowerLimb);
            Add(leftLeg.lower, Reform.Part.LowerLimb);
            Add(rightLeg.lower, Reform.Part.LowerLimb);
            Add(leftFist, Reform.Part.Hand);
            Add(rightFist, Reform.Part.Hand);
            Add(leftOpen, Reform.Part.Hand);
            Add(rightOpen, Reform.Part.Hand);
            Add(leftBoot, Reform.Part.Boot);
            Add(rightBoot, Reform.Part.Boot);
            Add(pack, Reform.Part.Gear);
            Add(packLid, Reform.Part.Gear);

            var core = new GameObject("ReformCore");
            core.transform.SetParent(root, false);
            core.AddComponent<MeshFilter>().sharedMesh = BoneMesh(-1);
            core.AddComponent<MeshRenderer>().sharedMaterials = coreMaterials;
            core.SetActive(false);
            reformCore = core.transform;
        }

        private void Add(Transform flesh, Reform.Part kind)
        {
            Transform bone = null;
            if (kind != Reform.Part.Gear)
            {
                var go = new GameObject(flesh.name + "Bone");
                go.transform.SetParent(root, false);
                go.AddComponent<MeshFilter>().sharedMesh = BoneMesh((int)kind);
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = boneMaterials;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                go.SetActive(false);
                bone = go.transform;
            }
            reformPieces.Add(new ReformPiece { Flesh = flesh, Bone = bone, Kind = kind });
        }

        /// <summary>A bone mesh per part kind; -1 is the glowing heart.</summary>
        private static Mesh BoneMesh(int kind)
        {
            if (boneMeshes.TryGetValue(kind, out Mesh mesh) && mesh != null)
            {
                return mesh;
            }
            var kit = new MeshKit(1) { TileSize = 0.12f };
            if (kind < 0)
            {
                kit.Ellipsoid(Vector3.zero, Vector3.one * 0.5f, 4, 8);
            }
            else
            {
                Reform.BuildBone(kit, (Reform.Part)kind);
            }
            mesh = kit.ToMesh("Belay_ReformBone" + kind);
            Object.DontDestroyOnLoad(mesh);
            boneMeshes[kind] = mesh;
            return mesh;
        }

        /// <summary>Applies this frame of the rebuild on top of the pose. Call right after <see cref="Pose"/>.</summary>
        public void ApplyReform()
        {
            if (reformStart < 0f || reformPieces == null)
            {
                return;
            }
            float t = Time.unscaledTime - reformStart;
            if (t >= Reform.Duration)
            {
                EndReform();
                return;
            }
            bool showBones = !climberHidden;
            foreach (ReformPiece piece in reformPieces)
            {
                Transform flesh = piece.Flesh;
                Vector3 posed = flesh.localScale;
                if (piece.Bone != null)
                {
                    bool show = showBones && flesh.gameObject.activeSelf && Reform.BoneShows(piece.Kind, t);
                    if (piece.Bone.gameObject.activeSelf != show)
                    {
                        piece.Bone.gameObject.SetActive(show);
                    }
                    if (show)
                    {
                        piece.Bone.localPosition = flesh.localPosition;
                        piece.Bone.localRotation = flesh.localRotation;
                        piece.Bone.localScale = Vector3.Scale(posed, Reform.BoneScale(piece.Kind, t));
                    }
                }
                flesh.localScale = Vector3.Scale(posed, Reform.FleshScale(piece.Kind, t));
            }
            float core = Reform.Core(t);
            bool coreShows = showBones && core > 0.001f;
            if (reformCore.gameObject.activeSelf != coreShows)
            {
                reformCore.gameObject.SetActive(coreShows);
            }
            if (coreShows)
            {
                // Torso scale is already shrunk this frame; place the heart from its rotation and the plain avatar scale.
                reformCore.localPosition = torso.localPosition + torso.localRotation * (Reform.CorePosition * Scale);
                reformCore.localScale = Vector3.one * (core * Scale);
            }
            SetGlow(Reform.Glow(t));
        }

        private void EndReform()
        {
            reformStart = -1f;
            foreach (ReformPiece piece in reformPieces)
            {
                if (piece.Bone != null)
                {
                    piece.Bone.gameObject.SetActive(false);
                }
            }
            reformCore.gameObject.SetActive(false);
            SetGlow(0f);
        }

        private void SetGlow(float glow)
        {
            Color emission = Color.Lerp(Color.white * SelfLight, Reform.GlowColor * 1.6f, glow * 0.85f);
            // Only the climber's own materials: glove skins may be shared with other teammates.
            Tint(materials, emission);
        }

        private static void Tint(Material[] set, Color emission)
        {
            if (set == null)
            {
                return;
            }
            for (int i = 0; i < set.Length; i++)
            {
                // The headlamp lens keeps its own glow.
                if (i != Slot.Lamp && set[i] != null && set[i].HasProperty("_EmissionColor"))
                {
                    set[i].SetColor("_EmissionColor", emission);
                }
            }
        }
    }
}
