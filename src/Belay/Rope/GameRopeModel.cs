using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace Belay.Rope
{
    /// <summary>
    /// The game's own rope segment model, taken from an ENV_Rope (the swinging ropes in levels and the one the
    /// rebar rope leaves behind). ENV_Rope draws one copy of its segment model per node, placed at the node,
    /// turned toward the next node and stretched along Z to the gap between them; team ropes draw the same way.
    /// </summary>
    public static class GameRopeModel
    {
        public struct Part
        {
            public Mesh Mesh;
            public Material[] Materials;
            /// <summary>The part's transform relative to the segment root, with the root's own scale left out.</summary>
            public Matrix4x4 Local;
            public int Layer;
            public ShadowCastingMode Shadows;
            public bool ReceiveShadows;
        }

        /// <summary>Rope item prefabs that may hold an ENV_Rope somewhere in what they spawn.</summary>
        private static readonly string[] SourceAssets = { "Item_RebarRope" };
        private const float RetrySeconds = 5f;
        private const int SearchDepth = 4;

        private static Part[] parts;
        private static Vector3 rootScale = Vector3.one;
        private static float nextTry;
        private static bool logged;

        /// <summary>The game's rope parts, or false until a rope has been found (looks again every few seconds).</summary>
        public static bool TryGet(out Part[] model, out Vector3 scale)
        {
            if (parts == null && Time.unscaledTime >= nextTry)
            {
                nextTry = Time.unscaledTime + RetrySeconds;
                try
                {
                    Find();
                }
                catch (Exception e)
                {
                    if (!logged)
                    {
                        logged = true;
                        Plugin.Log.LogWarning("Game rope model lookup failed: " + e.Message);
                    }
                }
            }
            model = parts;
            scale = rootScale;
            return parts != null;
        }

        /// <summary>Draws one segment model from <paramref name="from"/> to <paramref name="to"/>, like ENV_Rope.RenderRope.</summary>
        public static void DrawSegment(Part[] model, Vector3 scale, Vector3 from, Vector3 to, Vector3 up)
        {
            Vector3 dir = to - from;
            float length = dir.magnitude;
            if (length < 1e-4f || float.IsNaN(length) || float.IsInfinity(length))
            {
                return;
            }
            if (Mathf.Abs(Vector3.Dot(dir / length, up)) > 0.999f)
            {
                up = Vector3.forward;
            }
            Matrix4x4 root = Matrix4x4.TRS(from, Quaternion.LookRotation(dir, up), new Vector3(scale.x, scale.y, scale.z * length));
            foreach (Part part in model)
            {
                Matrix4x4 m = root * part.Local;
                for (int s = 0; s < part.Materials.Length && s < part.Mesh.subMeshCount; s++)
                {
                    if (part.Materials[s] != null)
                    {
                        Graphics.DrawMesh(part.Mesh, m, part.Materials[s], part.Layer, null, s, null, part.Shadows, part.ReceiveShadows);
                    }
                }
            }
        }

        private static void Find()
        {
            foreach (ENV_Rope rope in Resources.FindObjectsOfTypeAll<ENV_Rope>())
            {
                if (TryTake(rope))
                {
                    return;
                }
            }
            var seen = new HashSet<int>();
            foreach (string name in SourceAssets)
            {
                GameObject asset = null;
                try
                {
                    asset = CL_AssetManager.GetAssetGameObject(name);
                }
                catch (Exception)
                {
                }
                if (asset != null && Search(asset, SearchDepth, seen))
                {
                    return;
                }
            }
        }

        /// <summary>Looks for an ENV_Rope in a prefab and in the prefabs its scripts point at (projectiles, hit objects).</summary>
        private static bool Search(GameObject go, int depth, HashSet<int> seen)
        {
            if (go == null || !seen.Add(go.GetInstanceID()))
            {
                return false;
            }
            foreach (ENV_Rope rope in go.GetComponentsInChildren<ENV_Rope>(true))
            {
                if (TryTake(rope))
                {
                    return true;
                }
            }
            if (depth <= 0)
            {
                return false;
            }
            foreach (MonoBehaviour script in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (script == null)
                {
                    continue;
                }
                foreach (GameObject next in Referenced(script, 1))
                {
                    if (Search(next, depth - 1, seen))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static IEnumerable<GameObject> Referenced(object owner, int nesting)
        {
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (FieldInfo field in owner.GetType().GetFields(Flags))
            {
                object value;
                try
                {
                    value = field.GetValue(owner);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (GameObject go in AsGameObjects(value, nesting))
                {
                    yield return go;
                }
            }
        }

        private static IEnumerable<GameObject> AsGameObjects(object value, int nesting)
        {
            switch (value)
            {
                case null:
                    yield break;
                case GameObject go:
                    yield return go;
                    yield break;
                case Component c:
                    if (c != null)
                    {
                        yield return c.gameObject;
                    }
                    yield break;
                case string _:
                    yield break;
                case UnityEngine.Object _:
                    yield break;
                case IEnumerable list:
                    foreach (object item in list)
                    {
                        if (item is GameObject g)
                        {
                            yield return g;
                        }
                        else if (item is Component ic && ic != null)
                        {
                            yield return ic.gameObject;
                        }
                        else if (item != null && nesting > 0 && IsDataClass(item.GetType()))
                        {
                            foreach (GameObject inner in Referenced(item, nesting - 1))
                            {
                                yield return inner;
                            }
                        }
                    }
                    yield break;
            }
            if (nesting > 0 && IsDataClass(value.GetType()))
            {
                foreach (GameObject inner in Referenced(value, nesting - 1))
                {
                    yield return inner;
                }
            }
        }

        /// <summary>Serialized plain classes (like an item's data) that can hold prefab links of their own.</summary>
        private static bool IsDataClass(Type t) =>
            t.IsClass && !t.IsPrimitive && t != typeof(string) && !typeof(Delegate).IsAssignableFrom(t)
            && !typeof(UnityEngine.Object).IsAssignableFrom(t) && t.Namespace?.StartsWith("System") != true
            && t.Namespace?.StartsWith("UnityEngine") != true;

        private static bool TryTake(ENV_Rope rope)
        {
            if (rope == null || rope.meshRoot == null || rope.meshRoot.transform.childCount == 0)
            {
                return false;
            }
            Transform segment = rope.meshRoot.transform.GetChild(0);
            var found = new List<Part>();
            foreach (MeshRenderer renderer in segment.GetComponentsInChildren<MeshRenderer>(true))
            {
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null || renderer.sharedMaterials == null || renderer.sharedMaterials.Length == 0)
                {
                    continue;
                }
                Matrix4x4 local = Matrix4x4.identity;
                for (Transform t = renderer.transform; t != segment && t != null; t = t.parent)
                {
                    local = Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale) * local;
                }
                found.Add(new Part
                {
                    Mesh = filter.sharedMesh,
                    Materials = renderer.sharedMaterials,
                    Local = local,
                    Layer = renderer.gameObject.layer,
                    Shadows = renderer.shadowCastingMode,
                    ReceiveShadows = renderer.receiveShadows,
                });
            }
            if (found.Count == 0)
            {
                return false;
            }
            Vector3 s = rope.meshRoot.transform.lossyScale;
            bool sane = s.x > 0.05f && s.y > 0.05f && s.z > 0.05f && s.x < 20f && s.y < 20f && s.z < 20f;
            rootScale = sane ? s : Vector3.one;
            parts = found.ToArray();
            var names = new List<string>();
            foreach (Part p in parts)
            {
                names.Add(p.Mesh.name + "/" + (p.Materials[0] != null ? p.Materials[0].name : "?"));
            }
            Plugin.Log.LogInfo($"Game rope model: {string.Join(", ", names)} from '{rope.name}', scale {rootScale}");
            return true;
        }
    }
}
