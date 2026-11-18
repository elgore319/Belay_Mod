using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Belay.Players.Avatar
{
    /// <summary>
    /// Plays the totem revive on a teammate shown as one of the game's own models (<see cref="ReformModel"/>):
    /// bones and muscle along the model's rig, then skin, then the model itself spreading over the body.
    /// Works on copies of the model's meshes and puts the originals back at the end.
    /// </summary>
    internal sealed class ModelReform
    {
        private sealed class Surface
        {
            public SkinnedMeshRenderer Renderer;
            public Mesh Original, Clothes, Skin;
            public SkinnedMeshRenderer SkinRenderer;
            public Vector3[] Vertices, Inset, Output;
            public int[] Triangles;
            public float[] Keys, SkinKeys;
            public float ClothesBuilt = -1f, SkinBuilt = -1f;
            /// <summary>No copy could be made: the model just appears once the clothes are half grown.</summary>
            public bool Pop;
        }

        private sealed class Piece
        {
            public Transform From, To;
            public float Radius, BindLength;
            public int Stage;
            public Transform Bone, Muscle;
        }

        private readonly List<Surface> surfaces = new List<Surface>();
        private readonly List<Piece> pieces = new List<Piece>();
        private readonly Mesh[] stageMuscle = new Mesh[4];
        private readonly float[] stageBuilt = { -1f, -1f, -1f, -1f };
        private readonly MeshKit kit = new MeshKit(1) { TileSize = 0.12f };
        private readonly Transform holder;
        private readonly Material skinMaterial;

        private static readonly HashSet<string> warned = new HashSet<string>();

        /// <summary>Everything this revive draws; the climber leaves these on while it hides itself.</summary>
        public Transform Holder => holder;

        public static ModelReform Create(GameModelAvatar model, Transform parent, Mesh boneMesh, Material bone, Material muscle, Material skin)
        {
            try
            {
                return model != null && model.Model != null ? new ModelReform(model, parent, boneMesh, bone, muscle, skin) : null;
            }
            catch (System.Exception e)
            {
                Plugin.Log?.LogWarning("Can't play the revive effect on the " + model?.Kind + " model: " + e.Message);
                return null;
            }
        }

        private ModelReform(GameModelAvatar model, Transform parent, Mesh boneMesh, Material bone, Material muscle, Material skin)
        {
            skinMaterial = skin;
            holder = new GameObject("ModelReform").transform;
            holder.SetParent(parent, false);

            // The rig: every bone any of the model's renderers is skinned to.
            var bones = new List<Transform>();
            foreach (SkinnedMeshRenderer r in model.Model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!r.enabled || r.sharedMesh == null)
                {
                    continue;
                }
                foreach (Transform b in r.bones)
                {
                    if (b != null && !bones.Contains(b))
                    {
                        bones.Add(b);
                    }
                }
                var surface = new Surface { Renderer = r, Original = r.sharedMesh };
                Mesh copy = ReadableCopy(r.sharedMesh);
                if (copy == null)
                {
                    surface.Pop = true;
                    if (warned.Add(r.sharedMesh.name))
                    {
                        Plugin.Log?.LogWarning("Revive effect: can't read the " + r.sharedMesh.name + " mesh, it'll just appear");
                    }
                }
                else
                {
                    surface.Clothes = copy;
                    surface.Vertices = copy.vertices;
                    surface.Triangles = copy.triangles;
                    surface.Output = (Vector3[])surface.Vertices.Clone();
                }
                surfaces.Add(surface);
            }
            if (bones.Count == 0)
            {
                throw new System.Exception("no rig");
            }
            int[] parentOf = new int[bones.Count];
            var names = new string[bones.Count];
            for (int i = 0; i < bones.Count; i++)
            {
                names[i] = bones[i].name;
                parentOf[i] = -1;
                for (Transform p = bones[i].parent; p != null && p != model.Model; p = p.parent)
                {
                    int found = bones.IndexOf(p);
                    if (found >= 0)
                    {
                        parentOf[i] = found;
                        break;
                    }
                }
            }

            // Bone positions in the model's own space, as it stands now (just built, at rest), and the
            // body's surface in the same space, to measure how thick it is around each bone.
            Matrix4x4 toModel = model.Model.worldToLocalMatrix;
            var bonePositions = new Vector3[bones.Count];
            for (int i = 0; i < bones.Count; i++)
            {
                bonePositions[i] = toModel.MultiplyPoint3x4(bones[i].position);
            }
            var body = new List<Vector3>();
            foreach (Surface s in surfaces)
            {
                if (s.Vertices == null)
                {
                    continue;
                }
                // The copy goes on now (fully grown until the first frame), so it can be baked in its pose.
                s.Renderer.sharedMesh = s.Clothes;
                var baked = new Mesh();
                s.Renderer.BakeMesh(baked, true);
                Matrix4x4 m = toModel * s.Renderer.transform.localToWorldMatrix;
                foreach (Vector3 v in baked.vertices)
                {
                    body.Add(m.MultiplyPoint3x4(v));
                }
                Object.Destroy(baked);
            }
            List<ReformModel.Segment> segments = ReformModel.Segments(bonePositions, parentOf, body, names);
            float inset = ReformModel.InsetFor(segments);
            foreach (ReformModel.Segment seg in segments)
            {
                var piece = new Piece
                {
                    From = bones[seg.From], To = bones[seg.To], Stage = seg.Stage,
                    Radius = seg.Radius, BindLength = (bonePositions[seg.To] - bonePositions[seg.From]).magnitude,
                };
                piece.Bone = Part("Bone", boneMesh, bone);
                piece.Muscle = Part("Muscle", null, muscle);
                pieces.Add(piece);
            }
            for (int i = 0; i < stageMuscle.Length; i++)
            {
                stageMuscle[i] = new Mesh { name = "Belay_ModelMuscle" + i };
                stageMuscle[i].MarkDynamic();
            }
            foreach (Piece p in pieces)
            {
                p.Muscle.GetComponent<MeshFilter>().sharedMesh = stageMuscle[p.Stage];
            }

            // Spread keys and the skin, per surface, in each mesh's own (bind) space.
            int seed = 0;
            foreach (Surface s in surfaces)
            {
                seed++;
                if (s.Clothes == null)
                {
                    continue;
                }
                Matrix4x4[] bind = s.Clothes.bindposes;
                Transform[] rbones = s.Renderer.bones;
                var meshBones = new List<Vector3>();
                var meshParents = new List<int>();
                for (int i = 0; i < rbones.Length && i < bind.Length; i++)
                {
                    meshBones.Add(bind[i].inverse.MultiplyPoint3x4(Vector3.zero));
                    int p = -1;
                    int index = rbones[i] != null ? bones.IndexOf(rbones[i]) : -1;
                    if (index >= 0 && parentOf[index] >= 0)
                    {
                        p = System.Array.IndexOf(rbones, bones[parentOf[index]]);
                    }
                    meshParents.Add(p);
                }
                s.Keys = ReformModel.SpreadKeys(s.Vertices, s.Triangles, meshBones, meshParents.ToArray(), 31 + seed);
                s.SkinKeys = ReformModel.SpreadKeys(s.Vertices, s.Triangles, meshBones, meshParents.ToArray(), 977 + seed);
                // The inset is measured in the model's space; the mesh may be in other units.
                float meshUnits = MeshUnitsPerModelUnit(s, model.Model);
                s.Inset = ReformModel.Inset(s.Vertices, s.Clothes.normals, inset * meshUnits);
                s.Skin = Object.Instantiate(s.Clothes);
                s.Skin.name = s.Original.name + "_ReformSkin";
                s.Skin.MarkDynamic();
                var go = new GameObject(s.Renderer.name + "Skin");
                go.transform.SetParent(s.Renderer.transform.parent, false);
                go.transform.localPosition = s.Renderer.transform.localPosition;
                go.transform.localRotation = s.Renderer.transform.localRotation;
                go.transform.localScale = s.Renderer.transform.localScale;
                SkinnedMeshRenderer skinRenderer = go.AddComponent<SkinnedMeshRenderer>();
                skinRenderer.bones = s.Renderer.bones;
                skinRenderer.rootBone = s.Renderer.rootBone;
                skinRenderer.sharedMesh = s.Skin;
                skinRenderer.updateWhenOffscreen = true;
                skinRenderer.shadowCastingMode = ShadowCastingMode.Off;
                var set = new Material[Mathf.Max(1, s.Skin.subMeshCount)];
                for (int i = 0; i < set.Length; i++)
                {
                    set[i] = skinMaterial;
                }
                skinRenderer.sharedMaterials = set;
                go.SetActive(false);
                s.SkinRenderer = skinRenderer;
            }
        }

        private static float MeshUnitsPerModelUnit(Surface s, Transform model)
        {
            // How long one unit of the model's space is in the mesh's bind space, from the renderer's transform.
            Vector3 scale = (model.worldToLocalMatrix * s.Renderer.transform.localToWorldMatrix).lossyScale;
            return 1f / Mathf.Max(1e-4f, Mathf.Abs(scale.x));
        }

        private Transform Part(string name, Mesh mesh, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(holder, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            go.SetActive(false);
            return go.transform;
        }

        /// <summary>This frame of the revive, <paramref name="t"/> seconds in.</summary>
        public void Apply(float t, bool visible)
        {
            if (holder.gameObject.activeSelf != visible)
            {
                holder.gameObject.SetActive(visible);
            }
            float clothes = Reform.WholeProgress(Reform.Layer.Clothes, t);
            float skin = Reform.WholeProgress(Reform.Layer.Skin, t);
            bool skinShows = ReformModel.SkinShows(t);
            foreach (Surface s in surfaces)
            {
                if (s.Renderer == null)
                {
                    continue;
                }
                if (s.Pop)
                {
                    s.Renderer.enabled = clothes >= 0.5f;
                    continue;
                }
                if (s.Renderer.sharedMesh != s.Clothes)
                {
                    s.Renderer.sharedMesh = s.Clothes;
                }
                Grow(s.Clothes, s.Vertices, s, s.Keys, clothes, ref s.ClothesBuilt);
                if (s.SkinRenderer != null)
                {
                    if (s.SkinRenderer.gameObject.activeSelf != skinShows)
                    {
                        s.SkinRenderer.gameObject.SetActive(skinShows);
                    }
                    if (skinShows)
                    {
                        Grow(s.Skin, s.Inset, s, s.SkinKeys, skin, ref s.SkinBuilt);
                    }
                }
            }
            for (int stage = 0; stage < stageMuscle.Length; stage++)
            {
                float p = Reform.ProgressAt(stage, Reform.Layer.Muscle, t);
                if (ReformModel.MuscleShows(stage, t) && Mathf.Abs(p - stageBuilt[stage]) >= 0.004f)
                {
                    stageBuilt[stage] = p;
                    kit.Vertices.Clear();
                    kit.Normals.Clear();
                    kit.UVs.Clear();
                    kit.Submeshes[0].Clear();
                    Reform.BuildMuscle(kit, Reform.Part.UpperLimb, p, stage * 7 + 3);
                    Mesh mesh = stageMuscle[stage];
                    mesh.Clear();
                    mesh.SetVertices(kit.Vertices);
                    mesh.SetNormals(kit.Normals);
                    mesh.SetUVs(0, kit.UVs);
                    mesh.SetTriangles(kit.Submeshes[0], 0);
                    mesh.RecalculateBounds();
                }
            }
            Vector3 holderScale = holder.lossyScale;
            foreach (Piece p in pieces)
            {
                if (p.From == null || p.To == null)
                {
                    continue;
                }
                Vector3 a = p.From.position, b = p.To.position;
                float length = (b - a).magnitude;
                bool boneShows = visible && length > 1e-4f && ReformModel.BoneShows(p.Stage, t);
                bool muscleShows = visible && length > 1e-4f && ReformModel.MuscleShows(p.Stage, t);
                if (p.Bone.gameObject.activeSelf != boneShows)
                {
                    p.Bone.gameObject.SetActive(boneShows);
                }
                if (p.Muscle.gameObject.activeSelf != muscleShows)
                {
                    p.Muscle.gameObject.SetActive(muscleShows);
                }
                if (!boneShows && !muscleShows)
                {
                    continue;
                }
                // Thickness from the rest pose, scaled with how big the model is in the world now.
                float radius = p.Radius * length / Mathf.Max(1e-4f, p.BindLength);
                Vector3 scale = ReformModel.SegmentScale(radius, length);
                Vector3 dir = (b - a) / length;
                Quaternion rotation = Quaternion.LookRotation(dir, Mathf.Abs(dir.y) > 0.9f ? Vector3.forward : Vector3.up);
                Vector3 local = new Vector3(scale.x / holderScale.x, scale.y / holderScale.y, scale.z / holderScale.z);
                if (boneShows)
                {
                    p.Bone.SetPositionAndRotation(a, rotation);
                    p.Bone.localScale = Vector3.Scale(local, new Vector3(1f, 1f, Reform.BoneAt(p.Stage, t)));
                }
                if (muscleShows)
                {
                    p.Muscle.SetPositionAndRotation(a, rotation);
                    p.Muscle.localScale = local;
                }
            }
        }

        private static void Grow(Mesh mesh, Vector3[] from, Surface s, float[] keys, float progress, ref float built)
        {
            if (Mathf.Abs(progress - built) < 0.002f)
            {
                return;
            }
            built = progress;
            Reform.Spread(from, s.Triangles, keys, progress, s.Output);
            mesh.vertices = s.Output;
        }

        /// <summary>Puts the model's own meshes back and throws the revive's pieces away.</summary>
        public void End()
        {
            foreach (Surface s in surfaces)
            {
                if (s.Renderer != null)
                {
                    s.Renderer.sharedMesh = s.Original;
                    s.Renderer.enabled = true;
                }
                if (s.SkinRenderer != null)
                {
                    Object.Destroy(s.SkinRenderer.gameObject);
                }
                if (s.Clothes != null)
                {
                    Object.Destroy(s.Clothes);
                }
                if (s.Skin != null)
                {
                    Object.Destroy(s.Skin);
                }
            }
            surfaces.Clear();
            foreach (Mesh m in stageMuscle)
            {
                if (m != null)
                {
                    Object.Destroy(m);
                }
            }
            if (holder != null)
            {
                Object.Destroy(holder.gameObject);
            }
        }

        /// <summary>
        /// A copy of a mesh the revive can change. The game's meshes usually can't be read on the CPU, so
        /// their vertex and index buffers are read back from the GPU into a new mesh with the same layout
        /// (bone weights included). Null if that isn't possible.
        /// </summary>
        private static Mesh ReadableCopy(Mesh source)
        {
            if (source.isReadable)
            {
                Mesh plain = Object.Instantiate(source);
                plain.name = source.name + "_Reform";
                plain.MarkDynamic();
                return plain;
            }
            Mesh copy = null;
            try
            {
                VertexAttributeDescriptor[] layout = source.GetVertexAttributes();
                copy = new Mesh { name = source.name + "_Reform" };
                copy.SetVertexBufferParams(source.vertexCount, layout);
                const MeshUpdateFlags quiet = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;
                for (int stream = 0; stream < source.vertexBufferCount; stream++)
                {
                    using (GraphicsBuffer buffer = source.GetVertexBuffer(stream))
                    {
                        var data = new byte[buffer.count * buffer.stride];
                        buffer.GetData(data);
                        copy.SetVertexBufferData(data, 0, 0, data.Length, stream, quiet);
                    }
                }
                using (GraphicsBuffer indices = source.GetIndexBuffer())
                {
                    copy.SetIndexBufferParams(indices.count, source.indexFormat);
                    if (source.indexFormat == IndexFormat.UInt16)
                    {
                        var data = new ushort[indices.count];
                        indices.GetData(data);
                        copy.SetIndexBufferData(data, 0, 0, data.Length, quiet);
                    }
                    else
                    {
                        var data = new int[indices.count];
                        indices.GetData(data);
                        copy.SetIndexBufferData(data, 0, 0, data.Length, quiet);
                    }
                }
                copy.subMeshCount = source.subMeshCount;
                for (int i = 0; i < source.subMeshCount; i++)
                {
                    copy.SetSubMesh(i, source.GetSubMesh(i), quiet);
                }
                copy.bindposes = source.bindposes;
                copy.bounds = source.bounds;
                // Check it came back: the positions should fill the mesh's own bounds.
                Bounds expected = source.bounds;
                Vector3[] v = copy.vertices;
                Bounds got = new Bounds(v.Length > 0 ? v[0] : Vector3.zero, Vector3.zero);
                foreach (Vector3 p in v)
                {
                    if (float.IsNaN(p.x) || float.IsInfinity(p.x))
                    {
                        throw new System.Exception("bad positions");
                    }
                    got.Encapsulate(p);
                }
                if (got.size.magnitude < expected.size.magnitude * 0.5f || got.size.magnitude > expected.size.magnitude * 2f + 1e-3f)
                {
                    throw new System.Exception("positions don't match the mesh");
                }
                copy.MarkDynamic();
                return copy;
            }
            catch (System.Exception e)
            {
                if (warned.Add("copy:" + source.name))
                {
                    Plugin.Log?.LogWarning("Revive effect: couldn't copy " + source.name + ": " + e.Message);
                }
                if (copy != null)
                {
                    Object.Destroy(copy);
                }
                return null;
            }
        }
    }
}
