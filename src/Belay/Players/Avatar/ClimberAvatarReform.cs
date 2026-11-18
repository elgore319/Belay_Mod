using System.Collections.Generic;
using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>Plays the totem revive (<see cref="Reform"/>) on the climber.</summary>
    public partial class ClimberAvatar
    {
        private class ReformPiece
        {
            public Transform Flesh;
            public Transform Bone;
            public Transform Muscle, Skin;
            public Reform.Part Kind;
            public int Seed;
            public Mesh MuscleMesh;
            public MeshKit MuscleKit;
            public float MuscleBuilt = -1f;
            public SpreadMesh Clothes, Skins;
        }

        /// <summary>
        /// A copy of a part's mesh that grows over the body patch by patch (<see cref="Reform.Spread"/>).
        /// Rebuilt if the part's own mesh changes mid-revive (outfits, grips).
        /// </summary>
        private class SpreadMesh
        {
            public Mesh Source, Copy;
            private Vector3[] vertices, output;
            private int[] triangles;
            private float[] keys;
            private float built = -1f;

            public Mesh For(Mesh source, Reform.Part kind, int seed)
            {
                if (source == Source && Copy != null)
                {
                    return Copy;
                }
                Free();
                Source = source;
                if (source == null || !source.isReadable)
                {
                    return null;
                }
                Copy = Object.Instantiate(source);
                Copy.name = source.name + "_Reform";
                Copy.MarkDynamic();
                vertices = source.vertices;
                triangles = source.triangles;
                keys = Reform.SpreadKeys(vertices, triangles, kind, seed);
                output = (Vector3[])vertices.Clone();
                built = -1f;
                return Copy;
            }

            public void Grow(float progress)
            {
                if (Copy == null || Mathf.Abs(progress - built) < 0.002f)
                {
                    return;
                }
                built = progress;
                Reform.Spread(vertices, triangles, keys, progress, output);
                Copy.vertices = output;
            }

            public void Free()
            {
                if (Copy != null)
                {
                    Object.Destroy(Copy);
                }
                Copy = null;
                Source = null;
            }
        }

        private static readonly Dictionary<int, Mesh> boneMeshes = new Dictionary<int, Mesh>();
        private static Material[] boneMaterials;
        private static Material muscleMaterial, skinMaterial;
        private static Material roachBoneMaterial, roachMeatMaterial, roachShellMaterial;
        private ModelReform modelReform;

        private List<ReformPiece> reformPieces;
        private float reformStart = -1f;

        /// <summary>Whether the climber is being rebuilt by a totem revive right now.</summary>
        public bool Reforming => reformStart >= 0f;

        /// <summary>Starts the inside-out rebuild. Call when the revived teammate's body reappears.</summary>
        public void StartReform()
        {
            try
            {
                SetupReform();
                modelReform?.End();
                modelReform = null;
                if (model != null)
                {
                    // Shown as one of the game's models: the revive grows along that model's own rig.
                    bool roach = ReformModel.Roachy(model.Kind);
                    modelReform = ModelReform.Create(model, root, BoneMesh((int)Reform.Part.UpperLimb),
                        roach ? roachBoneMaterial : boneMaterials[0], roach ? roachMeatMaterial : muscleMaterial, roach ? roachShellMaterial : skinMaterial);
                }
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
                Texture2D meat = MeatTexture();
                var skin = new Texture2D(meat.width, meat.height, TextureFormat.RGBA32, false) { name = "Belay_ReformSkin", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
                skin.SetPixels32(Reform.SkinFrom(meat.GetPixels32()));
                skin.Apply(false, false);
                muscleMaterial = FleshMaterial(meat, Reform.Layer.Muscle);
                skinMaterial = FleshMaterial(skin, Reform.Layer.Skin);
                // The roaches' flesh: waxy bug meat and a chitin shell, and yellowed cartilage for bones.
                roachMeatMaterial = FleshMaterial(Remade(meat, Reform.RoachMeatFrom(meat.GetPixels32()), "Belay_ReformRoachMeat"), Reform.Layer.Skin);
                roachShellMaterial = FleshMaterial(Remade(meat, Reform.RoachShellFrom(meat.GetPixels32()), "Belay_ReformRoachShell"), Reform.Layer.Skin);
                if (roachShellMaterial.HasProperty("_Glossiness"))
                {
                    roachShellMaterial.SetFloat("_Glossiness", 0.75f);
                }
                roachBoneMaterial = new Material(boneMaterials[0]) { name = "Belay_ReformRoachBone", color = Reform.RoachBoneTint };
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
            // A lore look's extras (coat tails, badges, tanks) grow back with the clothes on the torso.
            Add(extra, Reform.Part.Torso, clothesOnly: true);
        }

        private static Texture2D Remade(Texture2D like, Color32[] pixels, string name)
        {
            var texture = new Texture2D(like.width, like.height, TextureFormat.RGBA32, false) { name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private void Add(Transform flesh, Reform.Part kind, bool clothesOnly = false)
        {
            Transform bone = null;
            if (kind != Reform.Part.Gear && !clothesOnly)
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
            if (kind != Reform.Part.Gear && !clothesOnly)
            {
                muscle = LayerObject(flesh, "Muscle", muscleMaterial);
                skin = LayerObject(flesh, "Skin", skinMaterial);
            }
            var piece = new ReformPiece { Flesh = flesh, Bone = bone, Muscle = muscle, Skin = skin, Kind = kind, Seed = 17 + reformPieces.Count * 31 };
            if (muscle != null)
            {
                piece.MuscleMesh = new Mesh { name = flesh.name + "Muscle" };
                piece.MuscleMesh.MarkDynamic();
                piece.MuscleKit = new MeshKit(1) { TileSize = 0.12f };
                muscle.GetComponent<MeshFilter>().sharedMesh = piece.MuscleMesh;
            }
            piece.Clothes = new SpreadMesh();
            if (skin != null)
            {
                piece.Skins = new SpreadMesh();
            }
            reformPieces.Add(piece);
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

        /// <summary>A bone mesh per part kind.</summary>
        private static Mesh BoneMesh(int kind)
        {
            if (boneMeshes.TryGetValue(kind, out Mesh mesh) && mesh != null)
            {
                return mesh;
            }
            var kit = new MeshKit(1) { TileSize = 0.12f };
            Reform.BuildBone(kit, (Reform.Part)kind);
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
            modelReform?.Apply(t, root.gameObject.activeInHierarchy);
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
                ShowMuscle(piece, posed, t, showBones);
                ShowSkin(piece, posed, t, showBones);
                ShowClothes(piece, t);
            }
            SetGlow(Reform.Glow(t));
        }

        private static void Place(Transform layer, Transform flesh, Vector3 scale)
        {
            layer.localPosition = flesh.localPosition;
            layer.localRotation = flesh.localRotation;
            layer.localScale = scale;
        }

        private static bool SetShown(Transform layer, bool show)
        {
            if (layer.gameObject.activeSelf != show)
            {
                layer.gameObject.SetActive(show);
            }
            return show;
        }

        /// <summary>Muscle: strands knitting over the bones, rebuilt as they grow.</summary>
        private static void ShowMuscle(ReformPiece piece, Vector3 posed, float t, bool allowed)
        {
            if (piece.Muscle == null
                || !SetShown(piece.Muscle, allowed && piece.Flesh.gameObject.activeSelf && Reform.LayerShows(piece.Kind, Reform.Layer.Muscle, t)))
            {
                return;
            }
            Place(piece.Muscle, piece.Flesh, posed);
            float progress = Reform.Progress(piece.Kind, Reform.Layer.Muscle, t);
            if (Mathf.Abs(progress - piece.MuscleBuilt) < 0.004f)
            {
                return;
            }
            piece.MuscleBuilt = progress;
            MeshKit kit = piece.MuscleKit;
            kit.Vertices.Clear();
            kit.Normals.Clear();
            kit.UVs.Clear();
            kit.Submeshes[0].Clear();
            Reform.BuildMuscle(kit, piece.Kind, progress, piece.Seed);
            Mesh mesh = piece.MuscleMesh;
            mesh.Clear();
            mesh.SetVertices(kit.Vertices);
            mesh.SetNormals(kit.Normals);
            mesh.SetUVs(0, kit.UVs);
            mesh.SetTriangles(kit.Submeshes[0], 0);
            mesh.RecalculateBounds();
        }

        /// <summary>Skin: the part's own shape, just inside the clothes, spreading over the muscle.</summary>
        private static void ShowSkin(ReformPiece piece, Vector3 posed, float t, bool allowed)
        {
            if (piece.Skin == null
                || !SetShown(piece.Skin, allowed && piece.Flesh.gameObject.activeSelf && Reform.LayerShows(piece.Kind, Reform.Layer.Skin, t)))
            {
                return;
            }
            MeshFilter filter = piece.Skin.GetComponent<MeshFilter>();
            Mesh mesh = piece.Skins.For(FleshMesh(piece), piece.Kind, piece.Seed + 1000);
            if (filter.sharedMesh != mesh)
            {
                filter.sharedMesh = mesh;
                MeshRenderer r = piece.Skin.GetComponent<MeshRenderer>();
                var set = new Material[mesh != null ? Mathf.Max(1, mesh.subMeshCount) : 1];
                for (int i = 0; i < set.Length; i++)
                {
                    set[i] = skinMaterial;
                }
                r.sharedMaterials = set;
            }
            piece.Skins.Grow(Reform.Progress(piece.Kind, Reform.Layer.Skin, t));
            Place(piece.Skin, piece.Flesh, Vector3.Scale(posed, Reform.SkinScale(piece.Kind)));
        }

        /// <summary>Clothes and gear: the part itself, spreading over the skin.</summary>
        private static void ShowClothes(ReformPiece piece, float t)
        {
            MeshFilter filter = piece.Flesh.GetComponent<MeshFilter>();
            if (filter == null)
            {
                return;
            }
            Mesh mesh = piece.Clothes.For(FleshMesh(piece), piece.Kind, piece.Seed);
            if (mesh == null)
            {
                return;
            }
            if (filter.sharedMesh != mesh)
            {
                filter.sharedMesh = mesh;
            }
            piece.Clothes.Grow(Reform.Progress(piece.Kind, Reform.Layer.Clothes, t));
        }

        /// <summary>The part's own mesh (not this revive's growing copy of it).</summary>
        private static Mesh FleshMesh(ReformPiece piece)
        {
            MeshFilter filter = piece.Flesh.GetComponent<MeshFilter>();
            if (filter == null)
            {
                return null;
            }
            Mesh current = filter.sharedMesh;
            return current != null && current == piece.Clothes.Copy ? piece.Clothes.Source : current;
        }

        private void EndReform()
        {
            reformStart = -1f;
            modelReform?.End();
            modelReform = null;
            foreach (ReformPiece piece in reformPieces)
            {
                piece.Bone?.gameObject.SetActive(false);
                piece.Muscle?.gameObject.SetActive(false);
                piece.Skin?.gameObject.SetActive(false);
                MeshFilter filter = piece.Flesh.GetComponent<MeshFilter>();
                if (filter != null && piece.Clothes.Copy != null && filter.sharedMesh == piece.Clothes.Copy)
                {
                    filter.sharedMesh = piece.Clothes.Source;
                }
                piece.Clothes.Free();
                piece.Skins?.Free();
                piece.MuscleBuilt = -1f;
            }
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
