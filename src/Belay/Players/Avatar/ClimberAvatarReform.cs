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
            public Transform Muscle, Skin;
            public Reform.Part Kind;
        }

        private static readonly Dictionary<int, Mesh> boneMeshes = new Dictionary<int, Mesh>();
        private static Material[] boneMaterials, coreMaterials;
        private static Material muscleMaterial, skinMaterial;

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
                Texture2D meat = MeatTexture();
                var skin = new Texture2D(meat.width, meat.height, TextureFormat.RGBA32, false) { name = "Belay_ReformSkin", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
                skin.SetPixels32(Reform.SkinFrom(meat.GetPixels32()));
                skin.Apply(false, false);
                muscleMaterial = FleshMaterial(meat, Reform.Layer.Muscle);
                skinMaterial = FleshMaterial(skin, Reform.Layer.Skin);
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
            Transform muscle = null, skin = null;
            if (kind != Reform.Part.Gear)
            {
                muscle = LayerObject(flesh, "Muscle", muscleMaterial);
                skin = LayerObject(flesh, "Skin", skinMaterial);
            }
            reformPieces.Add(new ReformPiece { Flesh = flesh, Bone = bone, Muscle = muscle, Skin = skin, Kind = kind });
        }

        private Transform LayerObject(Transform flesh, string layer, Material material)
        {
            var go = new GameObject(flesh.name + layer);
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>();
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.SetActive(false);
            return go.transform;
        }

        /// <summary>
        /// The flesh texture: the meat from the game's own food item, cut out of its sheet so it tiles,
        /// or a stand-in if the item isn't there.
        /// </summary>
        private static Texture2D MeatTexture()
        {
            try
            {
                Item_Object item = CL_AssetManager.GetItemObjectPrefab(Reform.MeatItem);
                GameObject prefab = item != null ? item.gameObject : CL_AssetManager.GetAssetGameObject(Reform.MeatItem);
                Renderer renderer = prefab != null ? prefab.GetComponentInChildren<Renderer>(true) : null;
                Texture source = renderer != null && renderer.sharedMaterial != null ? renderer.sharedMaterial.mainTexture : null;
                if (source != null)
                {
                    Vector4 r = Reform.MeatRegion;
                    int w = Mathf.Max(8, Mathf.RoundToInt(source.width * r.z));
                    int h = Mathf.Max(8, Mathf.RoundToInt(source.height * r.w));
                    // The game's texture isn't readable: copy the region through the GPU.
                    RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(source, rt, new Vector2(r.z, r.w), new Vector2(r.x, r.y));
                    RenderTexture previous = RenderTexture.active;
                    RenderTexture.active = rt;
                    var texture = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "Belay_ReformMeat", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
                    texture.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                    texture.Apply(false, false); // stays readable: the skin is made from it
                    RenderTexture.active = previous;
                    RenderTexture.ReleaseTemporary(rt);
                    return texture;
                }
                Plugin.Log?.LogWarning("Revive effect: the game's meat texture wasn't found, using a stand-in");
            }
            catch (System.Exception e)
            {
                Plugin.Log?.LogWarning("Revive effect: couldn't copy the game's meat texture: " + e.Message);
            }
            var fallback = new Texture2D(PixelTextures.Size, PixelTextures.Size, TextureFormat.RGBA32, false) { name = "Belay_ReformMeat", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            fallback.SetPixels32(PixelTextures.Generate(Reform.FallbackMeat, 611));
            fallback.Apply(false, false);
            return fallback;
        }

        private static Material FleshMaterial(Texture2D meat, Reform.Layer layer)
        {
            Material m = MakeItemMaterials(new[] { Reform.FallbackMeat }, -1)[0];
            m.name = "Belay_Reform" + layer;
            m.mainTexture = meat;
            m.color = Reform.LayerTint(layer);
            if (m.HasProperty("_Glossiness"))
            {
                m.SetFloat("_Glossiness", layer == Reform.Layer.Muscle ? 0.55f : 0.3f); // wet
            }
            if (m.HasProperty("_EmissionColor"))
            {
                m.SetTexture("_EmissionMap", meat);
                m.SetColor("_EmissionColor", Reform.LayerTint(layer) * SelfLight);
            }
            return m;
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
                ShowLayer(piece.Muscle, flesh, posed, piece.Kind, Reform.Layer.Muscle, t, showBones);
                ShowLayer(piece.Skin, flesh, posed, piece.Kind, Reform.Layer.Skin, t, showBones);
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

        /// <summary>A flesh layer: the part's own shape, smaller, in muscle or skin.</summary>
        private static void ShowLayer(Transform layer, Transform flesh, Vector3 posed, Reform.Part kind, Reform.Layer which, float t, bool allowed)
        {
            if (layer == null)
            {
                return;
            }
            bool show = allowed && flesh.gameObject.activeSelf && Reform.LayerShows(kind, which, t);
            if (layer.gameObject.activeSelf != show)
            {
                layer.gameObject.SetActive(show);
            }
            if (!show)
            {
                return;
            }
            MeshFilter filter = layer.GetComponent<MeshFilter>();
            Mesh mesh = flesh.GetComponent<MeshFilter>().sharedMesh;
            if (filter.sharedMesh != mesh)
            {
                filter.sharedMesh = mesh; // the head and hands change with outfits and grips
                MeshRenderer r = layer.GetComponent<MeshRenderer>();
                Material m = r.sharedMaterials.Length > 0 ? r.sharedMaterials[0] : null;
                var set = new Material[mesh != null ? Mathf.Max(1, mesh.subMeshCount) : 1];
                for (int i = 0; i < set.Length; i++)
                {
                    set[i] = m;
                }
                r.sharedMaterials = set;
            }
            layer.localPosition = flesh.localPosition;
            layer.localRotation = flesh.localRotation;
            layer.localScale = Vector3.Scale(posed, Reform.LayerScale(kind, which, t));
        }

        private void EndReform()
        {
            reformStart = -1f;
            foreach (ReformPiece piece in reformPieces)
            {
                piece.Bone?.gameObject.SetActive(false);
                piece.Muscle?.gameObject.SetActive(false);
                piece.Skin?.gameObject.SetActive(false);
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
