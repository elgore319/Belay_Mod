using System.Collections.Generic;
using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>
    /// The totem revive (<see cref="Reform"/>) on a game model (zombies, the Soach, the roach), built from the
    /// model's rig: bones along every rig bone, muscle, skin (shell on roaches), then the model itself. Plain math
    /// on bind-pose positions, shared with the preview tool.
    /// </summary>
    public static class ReformModel
    {
        /// <summary>A bone of the rig, from its parent's joint to its own.</summary>
        public struct Segment
        {
            public int From, To;
            /// <summary>How thick the body is around this bone, in the mesh's units.</summary>
            public float Radius;
            public int Stage;
        }

        /// <summary>
        /// The stage (0..3) each bone forms in, by depth from the root, so the core forms first. <paramref
        /// name="parent"/> is each bone's parent, or -1.
        /// </summary>
        public static int[] Stages(int[] parent)
        {
            int n = parent.Length;
            var depth = new int[n];
            int deepest = 1;
            for (int i = 0; i < n; i++)
            {
                int d = 0;
                for (int p = parent[i]; p >= 0 && d < n; p = parent[p])
                {
                    d++;
                }
                depth[i] = d;
                deepest = Mathf.Max(deepest, d);
            }
            var stage = new int[n];
            for (int i = 0; i < n; i++)
            {
                stage[i] = Mathf.Clamp(Mathf.RoundToInt(3f * depth[i] / deepest), 0, 3);
            }
            return stage;
        }

        /// <summary>
        /// Rig bones as segments with the body thickness around each, in bind-pose space. Tiny bones are skipped.
        /// </summary>
        public static List<Segment> Segments(IList<Vector3> bones, int[] parent, IList<Vector3> vertices, IList<string> names = null)
        {
            var segments = new List<Segment>();
            if (bones.Count == 0)
            {
                return segments;
            }
            Vector3 min = Vector3.one * 1e9f, max = Vector3.one * -1e9f;
            foreach (Vector3 v in vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            float size = Mathf.Max(1e-4f, (max - min).magnitude);
            int[] stage = Stages(parent);
            var candidates = new List<int>();
            for (int i = 0; i < bones.Count; i++)
            {
                // Not from the rig's root on the floor, and not to the animation helpers (IK targets and poles).
                if (parent[i] >= 0 && parent[parent[i]] >= 0 && !Helper(names, i) && !Helper(names, parent[i])
                    && (bones[i] - bones[parent[i]]).magnitude > size * 0.025f)
                {
                    candidates.Add(i);
                }
            }
            // Each vertex belongs to the segment it's nearest; a segment's thickness is how far its vertices sit.
            var distances = new Dictionary<int, List<float>>();
            foreach (int c in candidates)
            {
                distances[c] = new List<float>();
            }
            int step = Mathf.Max(1, vertices.Count / 1500);
            for (int v = 0; v < vertices.Count; v += step)
            {
                float best = float.MaxValue;
                int owner = -1;
                foreach (int c in candidates)
                {
                    float d = DistanceToSegment(vertices[v], bones[parent[c]], bones[c]);
                    if (d < best)
                    {
                        best = d;
                        owner = c;
                    }
                }
                if (owner >= 0)
                {
                    distances[owner].Add(best);
                }
            }
            int sampled = (vertices.Count + step - 1) / step;
            foreach (int c in candidates)
            {
                // Bones no part of the body hangs off (IK targets, a root on the floor) are left out.
                List<float> list = distances[c];
                if (list.Count < Mathf.Max(3, sampled / 200))
                {
                    continue;
                }
                list.Sort();
                float length = (bones[c] - bones[parent[c]]).magnitude;
                float radius = Mathf.Clamp(list[list.Count / 2], size * 0.008f, Mathf.Min(size * 0.07f, Mathf.Max(size * 0.008f, length * 0.9f)));
                segments.Add(new Segment { From = parent[c], To = c, Radius = radius, Stage = stage[c] });
            }
            return segments;
        }

        private static bool Helper(IList<string> names, int i)
        {
            if (names == null || i >= names.Count || names[i] == null)
            {
                return false;
            }
            string n = names[i];
            return n.Contains("IK") || n.Contains("Pole") || n.Contains("_PNT") || n.Contains("Motion_offset");
        }

        private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude));
            return (p - (a + ab * t)).magnitude;
        }

        /// <summary>
        /// Axis scale for the climber's unit-long limb bone and muscle shapes so they fill one segment.
        /// </summary>
        public static Vector3 SegmentScale(float radius, float length)
        {
            float s = radius * 1.0f / 0.07f;
            return new Vector3(s, s, length);
        }

        /// <summary>The skin sits this far inside the model's own surface, as a share of its average thickness.</summary>
        public static float InsetFor(List<Segment> segments)
        {
            if (segments.Count == 0)
            {
                return 0f;
            }
            float sum = 0f;
            foreach (Segment s in segments)
            {
                sum += s.Radius;
            }
            return 0.12f * sum / segments.Count;
        }

        /// <summary>The surface pushed in along its normals.</summary>
        public static Vector3[] Inset(IList<Vector3> vertices, IList<Vector3> normals, float amount)
        {
            var result = new Vector3[vertices.Count];
            for (int i = 0; i < result.Length; i++)
            {
                Vector3 n = normals != null && i < normals.Count ? normals[i] : Vector3.zero;
                result[i] = vertices[i] - n.normalized * amount;
            }
            return result;
        }

        /// <summary>When each triangle of the model starts to grow: out from the bones that form first.</summary>
        public static float[] SpreadKeys(IList<Vector3> vertices, IList<int> triangles, IList<Vector3> bones, int[] parent, int seed)
        {
            // From the middle of the bones that form first (not the rig's root, which sits on the floor).
            int[] stage = Stages(parent);
            var seeds = new List<Vector3>();
            for (int want = 0; want <= 3 && seeds.Count < 2; want++)
            {
                for (int i = 0; i < bones.Count; i++)
                {
                    int p = parent[i];
                    if (stage[i] == want && p >= 0 && parent[p] >= 0)
                    {
                        seeds.Add((bones[i] + bones[p]) * 0.5f);
                    }
                }
            }
            if (seeds.Count == 0)
            {
                seeds.AddRange(bones);
            }
            return Reform.SpreadKeys(vertices, triangles, seeds, Vector3.one, seed);
        }

        /// <summary>
        /// A model's surface cut into separate small triangles: each gets its own vertices and big ones split in
        /// four until no edge exceeds <paramref name="maxEdge"/>. Each new vertex blends one original triangle's
        /// corners (<paramref name="corners"/>, <paramref name="weights"/>) so any vertex data can be carried
        /// over.
        /// </summary>
        public static void Split(IList<Vector3> vertices, IList<int> triangles, float maxEdge, out int[] corners, out Vector3[] weights, int maxDepth = 3)
        {
            var c = new List<int>();
            var w = new List<Vector3>();
            for (int t = 0; t + 2 < triangles.Count; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], d = triangles[t + 2];
                SplitOne(vertices[a], vertices[b], vertices[d], new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1), maxEdge, maxDepth, a, b, d, c, w);
            }
            corners = c.ToArray();
            weights = w.ToArray();
        }

        private static void SplitOne(Vector3 pa, Vector3 pb, Vector3 pc, Vector3 wa, Vector3 wb, Vector3 wc, float maxEdge, int depth,
            int a, int b, int d, List<int> corners, List<Vector3> weights)
        {
            float longest = Mathf.Max((pa - pb).sqrMagnitude, Mathf.Max((pb - pc).sqrMagnitude, (pc - pa).sqrMagnitude));
            if (depth <= 0 || longest <= maxEdge * maxEdge)
            {
                foreach (Vector3 x in new[] { wa, wb, wc })
                {
                    corners.Add(a);
                    corners.Add(b);
                    corners.Add(d);
                    weights.Add(x);
                }
                return;
            }
            Vector3 ab = (pa + pb) * 0.5f, bc = (pb + pc) * 0.5f, ca = (pc + pa) * 0.5f;
            Vector3 wab = (wa + wb) * 0.5f, wbc = (wb + wc) * 0.5f, wca = (wc + wa) * 0.5f;
            SplitOne(pa, ab, ca, wa, wab, wca, maxEdge, depth - 1, a, b, d, corners, weights);
            SplitOne(ab, pb, bc, wab, wb, wbc, maxEdge, depth - 1, a, b, d, corners, weights);
            SplitOne(ca, bc, pc, wca, wbc, wc, maxEdge, depth - 1, a, b, d, corners, weights);
            SplitOne(ab, bc, ca, wab, wbc, wca, maxEdge, depth - 1, a, b, d, corners, weights);
        }

        /// <summary>Carries vertex data (positions, normals, UVs) over to the split surface.</summary>
        public static Vector3[] Blend(IList<Vector3> data, int[] corners, Vector3[] weights)
        {
            var result = new Vector3[weights.Length];
            for (int i = 0; i < result.Length; i++)
            {
                Vector3 w = weights[i];
                result[i] = data[corners[i * 3]] * w.x + data[corners[i * 3 + 1]] * w.y + data[corners[i * 3 + 2]] * w.z;
            }
            return result;
        }

        public static Vector2[] Blend(IList<Vector2> data, int[] corners, Vector3[] weights)
        {
            var result = new Vector2[weights.Length];
            for (int i = 0; i < result.Length; i++)
            {
                Vector3 w = weights[i];
                result[i] = data[corners[i * 3]] * w.x + data[corners[i * 3 + 1]] * w.y + data[corners[i * 3 + 2]] * w.z;
            }
            return result;
        }

        /// <summary>How long a triangle's edge may be on the split surface: small patches, a few cm on a person.</summary>
        public static float PatchSize(IList<Vector3> vertices)
        {
            Vector3 min = Vector3.one * 1e9f, max = Vector3.one * -1e9f;
            foreach (Vector3 v in vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            return Mathf.Max(1e-4f, (max - min).magnitude) * 0.035f;
        }

        /// <summary>The roaches' bones are thinner: cartilage under a shell, not a skeleton holding a body up.</summary>
        public static float BoneThickness(bool roachy) => roachy ? 0.55f : 1f;

        /// <summary>Whether a segment's bone shows (grown, and the skin hasn't closed over the body).</summary>
        public static bool BoneShows(int stage, float t)
        {
            return Reform.BoneAt(stage, t) > 0.001f && Reform.WholeProgress(Reform.Layer.Skin, t) < 0.7f;
        }

        public static bool MuscleShows(int stage, float t)
        {
            return Reform.ProgressAt(stage, Reform.Layer.Muscle, t) > 0f && Reform.WholeProgress(Reform.Layer.Skin, t) < 1f;
        }

        public static bool SkinShows(float t)
        {
            return Reform.WholeProgress(Reform.Layer.Skin, t) > 0f && Reform.WholeProgress(Reform.Layer.Clothes, t) < 1f;
        }

        /// <summary>Whether the model is one of the roaches, whose flesh is a bug's.</summary>
        public static bool Roachy(string kind)
        {
            return kind == "roach" || kind == "soach"; // GameModelAvatar.GiantRoach, GameModelAvatar.Soach
        }
    }
}
