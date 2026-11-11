using System;
using System.Collections.Generic;
using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>
    /// Builds a copy of an item's own world model (the one you see lying on the ground) for a teammate's
    /// avatar to hold. Only the meshes and materials are copied, never the item's scripts or colliders,
    /// so the copy can't be picked up, saved or used.
    /// </summary>
    public static class HeldItems
    {
        /// <summary>
        /// One template per item, oriented for a fist: its origin is where the hand closes on the
        /// handle, +X runs along the item toward its heavy end (the hammer head) and +Z is the item's
        /// second-longest direction (the way a hammer head points).
        /// </summary>
        private static readonly Dictionary<string, GameObject> templates = new Dictionary<string, GameObject>();
        private static readonly Dictionary<string, float> failedUntil = new Dictionary<string, float>();
        private static Transform holder;

        /// <summary>A fresh copy of the item's model, or null if it has no model (or isn't loaded).</summary>
        public static GameObject Create(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
            {
                return null;
            }
            if (!templates.TryGetValue(prefabName, out GameObject template) || template == null)
            {
                if (failedUntil.TryGetValue(prefabName, out float until) && Time.unscaledTime < until)
                {
                    return null;
                }
                try
                {
                    template = Build(prefabName);
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning("Can't show held item " + prefabName + ": " + e.Message);
                    template = null;
                }
                if (template == null)
                {
                    failedUntil[prefabName] = Time.unscaledTime + 10f;
                    return null;
                }
                templates[prefabName] = template;
            }
            GameObject copy = UnityEngine.Object.Instantiate(template);
            copy.name = "Belay_Held_" + prefabName;
            copy.SetActive(true);
            return copy;
        }

        private static GameObject FindPrefab(string prefabName)
        {
            Item_Object item = CL_AssetManager.GetItemObjectPrefab(prefabName);
            if (item != null)
            {
                return item.gameObject;
            }
            return CL_AssetManager.GetAssetGameObject(prefabName);
        }

        private struct Piece
        {
            public Mesh Mesh;
            public Sprite Sprite;
            public Material[] Materials;
            public Matrix4x4 Matrix; // piece space to item root space
        }

        private static GameObject Build(string prefabName)
        {
            GameObject prefab = FindPrefab(prefabName);
            if (prefab == null)
            {
                return null;
            }
            Matrix4x4 toRoot = Matrix4x4.Scale(prefab.transform.lossyScale) * prefab.transform.worldToLocalMatrix;
            var pieces = new List<Piece>();
            foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer.enabled || renderer.GetType().Name.Contains("Particle") || renderer is LineRenderer || renderer is TrailRenderer)
                {
                    continue;
                }
                var piece = new Piece { Materials = renderer.sharedMaterials, Matrix = toRoot * renderer.transform.localToWorldMatrix };
                if (renderer is MeshRenderer)
                {
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    piece.Mesh = filter != null ? filter.sharedMesh : null;
                }
                else if (renderer is SkinnedMeshRenderer skinned)
                {
                    piece.Mesh = skinned.sharedMesh;
                }
                else if (renderer is SpriteRenderer sprite)
                {
                    piece.Sprite = sprite.sprite;
                }
                if (piece.Mesh != null || piece.Sprite != null)
                {
                    pieces.Add(piece);
                }
            }
            if (pieces.Count == 0)
            {
                return null;
            }

            // Overall size, and where the bulk of it sits (the heavy end).
            Bounds bounds = default;
            bool any = false;
            Vector3 massSum = Vector3.zero;
            float massTotal = 0f;
            foreach (Piece piece in pieces)
            {
                Bounds local = piece.Mesh != null ? piece.Mesh.bounds : piece.Sprite.bounds;
                Bounds b = Transform(local, piece.Matrix);
                if (!any)
                {
                    bounds = b;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(b);
                }
                if (piece.Mesh != null && piece.Mesh.isReadable && piece.Mesh.vertexCount > 0)
                {
                    foreach (Vector3 v in piece.Mesh.vertices)
                    {
                        massSum += piece.Matrix.MultiplyPoint3x4(v);
                        massTotal += 1f;
                    }
                }
                else
                {
                    float volume = Mathf.Max(1e-6f, b.size.x * b.size.y * b.size.z);
                    massSum += b.center * volume;
                    massTotal += volume;
                }
            }
            Vector3 size = bounds.size;
            int longest = 0;
            for (int i = 1; i < 3; i++)
            {
                if (size[i] > size[longest])
                {
                    longest = i;
                }
            }
            int second = (longest + 1) % 3, third = (longest + 2) % 3;
            if (size[third] > size[second])
            {
                second = third;
            }
            Vector3 axis = Vector3.zero, across = Vector3.zero;
            axis[longest] = 1f;
            across[second] = 1f;
            Vector3 mass = massTotal > 0f ? massSum / massTotal : bounds.center;
            float heavySide = Vector3.Dot(mass - bounds.center, axis);
            if (heavySide < 0f)
            {
                axis = -axis;
            }
            float acrossSide = Vector3.Dot(mass - bounds.center, across);
            if (acrossSide < 0f)
            {
                across = -across;
            }

            // Hold it a fifth of the way up from the light end, or by the middle if it's small.
            float length = size[longest];
            float gripFromEnd = length > 0.25f ? 0.2f : 0.5f;
            Vector3 grip = bounds.center + axis * (length * (gripFromEnd - 0.5f));
            Quaternion orient = Quaternion.Inverse(Quaternion.LookRotation(across, Vector3.Cross(across, axis)));
            // LookRotation(forward=across, up=across x axis) maps +Z to across and +X to axis; invert it so
            // the item's axis lands on +X and its across direction on +Z.

            if (holder == null)
            {
                var go = new GameObject("Belay_HeldItemTemplates");
                go.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(go);
                holder = go.transform;
            }
            var root = new GameObject("Belay_HeldTemplate_" + prefabName);
            root.transform.SetParent(holder, false);
            var model = new GameObject("Model");
            model.transform.SetParent(root.transform, false);
            model.transform.localRotation = orient;
            model.transform.localPosition = orient * -grip;
            foreach (Piece piece in pieces)
            {
                var part = new GameObject(piece.Mesh != null ? piece.Mesh.name : piece.Sprite.name);
                part.transform.SetParent(model.transform, false);
                part.transform.localPosition = piece.Matrix.GetColumn(3);
                part.transform.localRotation = piece.Matrix.rotation;
                part.transform.localScale = piece.Matrix.lossyScale;
                if (piece.Mesh != null)
                {
                    part.AddComponent<MeshFilter>().sharedMesh = piece.Mesh;
                    MeshRenderer r = part.AddComponent<MeshRenderer>();
                    r.sharedMaterials = piece.Materials;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
                else
                {
                    SpriteRenderer r = part.AddComponent<SpriteRenderer>();
                    r.sprite = piece.Sprite;
                    if (piece.Materials != null && piece.Materials.Length > 0)
                    {
                        r.sharedMaterial = piece.Materials[0];
                    }
                }
            }
            root.SetActive(false);
            return root;
        }

        private static Bounds Transform(Bounds b, Matrix4x4 m)
        {
            Vector3 c = m.MultiplyPoint3x4(b.center);
            var result = new Bounds(c, Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                result.Encapsulate(m.MultiplyPoint3x4(corner));
            }
            return result;
        }

        /// <summary>What the local player holds in a hand (its prefab name), or "" when empty.</summary>
        public static string LocalHeld(int hand)
        {
            try
            {
                ENT_Player player = ENT_Player.playerObject;
                if (player == null || player.hands == null || player.hands.Length <= hand)
                {
                    return "";
                }
                Item item = player.hands[hand].inventoryHand?.currentItem;
                return item?.prefabName ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
