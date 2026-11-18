using System.Collections.Generic;
using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>
    /// The totem revive (<see cref="Reform"/>) on one of the game's own models (zombies, the Soach, the giant
    /// roach), worked out from the model's own rig instead of the climber's parts: a bone grows along every
    /// bone of the rig (from the hips out to the fingers, toes, legs and antennae), muscle strands knit over
    /// them, then skin (a shell, on the roaches) and finally the model itself spread over the body patch by
    /// patch. Plain math on bind-pose positions, so the avatar preview draws the same thing the game does.
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
        /// The stage each bone forms in (0..3), by how far down the rig it is from the root, so the middle
        /// of the body forms first and the ends of the limbs last. <paramref name="parent"/> is each bone's
        /// parent bone, or -1.
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
        /// The rig's bones as segments, with how thick the body is around each (from the vertices nearest
        /// it). Positions are in one space (the mesh's bind pose); tiny bones (fingers of a mouth, tips)
        /// are left out.
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
        /// Scale on a segment's axes for the climber's limb bone and muscle shapes (made one unit long, about
        /// 7 cm thick with the muscle on): thick enough to fill out the body around it, one segment long.
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
            int[] stage = Stages(parent);
            var seeds = new List<Vector3>();
            for (int i = 0; i < bones.Count; i++)
            {
                if (stage[i] == 0 || (stage[i] == 1 && seeds.Count < 2))
                {
                    seeds.Add(bones[i]);
                }
            }
            return Reform.SpreadKeys(vertices, triangles, seeds, Vector3.one, seed);
        }

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
