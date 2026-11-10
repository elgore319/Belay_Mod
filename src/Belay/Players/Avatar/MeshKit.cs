using System.Collections.Generic;
using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>
    /// Tiny procedural mesh builder for the climber avatar. Every triangle gets its own vertices and a
    /// face normal, which gives the hard-edged low-poly look. Each shape knows its own center and turns
    /// faces outward from it, so callers never have to think about winding order.
    ///
    /// Only plain Vector3 math is used here (no Quaternion or Matrix calls), so the same code also runs
    /// outside Unity in the preview tool under tools/avatar-preview.
    /// </summary>
    public class MeshKit
    {
        public readonly List<Vector3> Vertices = new List<Vector3>();
        public readonly List<Vector3> Normals = new List<Vector3>();
        public readonly List<int>[] Submeshes;

        private int current;

        public MeshKit(int submeshCount)
        {
            Submeshes = new List<int>[submeshCount];
            for (int i = 0; i < submeshCount; i++)
            {
                Submeshes[i] = new List<int>();
            }
        }

        /// <summary>Selects which material slot the next shapes are added to.</summary>
        public MeshKit Use(int submesh)
        {
            current = submesh;
            return this;
        }

        /// <summary>World-ish size of one texture repeat, in meters. Pixel textures tile at this size.</summary>
        public float TileSize = 0.75f;

        /// <summary>
        /// Real-world length of each local axis per mesh unit, for UVs. Limbs are built one unit long and
        /// stretched in game, so they set z to their nominal bone length to keep texels square.
        /// </summary>
        public Vector3 UvAxisScale = Vector3.one;

        /// <summary>
        /// 0 gives hard faceted edges; 1 gives soft rounded shading like the game's characters. Rounded
        /// shapes (tubes, ellipsoids) always shade smooth along their sides.
        /// </summary>
        public float Softness = 0.6f;

        public readonly List<Vector2> UVs = new List<Vector2>();

        /// <summary>
        /// When set, gives every vertex's UV directly from its position and its face's normal (e.g. a
        /// picture projected from above onto the top faces and another from below onto the bottom).
        /// </summary>
        public System.Func<Vector3, Vector3, Vector2> UvProjection;

        public void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 inside)
        {
            Tri(a, b, c, inside, null);
        }

        /// <summary>
        /// Adds a triangle. Normals come from <paramref name="smooth"/> (a per-point outward direction)
        /// when given, otherwise from the face bent toward the shape's center by <see cref="Softness"/>.
        /// </summary>
        public void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 inside, System.Func<Vector3, Vector3> smooth)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a);
            float length = normal.magnitude;
            if (length < 1e-9f)
            {
                return;
            }
            normal /= length;
            Vector3 center = (a + b + c) / 3f;
            if (Vector3.Dot(normal, center - inside) < 0f)
            {
                Vector3 swap = b;
                b = c;
                c = swap;
                normal = -normal;
            }

            // Project UVs on the plane the face mostly points along, so texels stay square.
            Vector3 n = new Vector3(Mathf.Abs(normal.x), Mathf.Abs(normal.y), Mathf.Abs(normal.z));
            int drop = n.x >= n.y && n.x >= n.z ? 0 : n.y >= n.z ? 1 : 2;

            List<int> tris = Submeshes[current];
            foreach (Vector3 p in new[] { a, b, c })
            {
                tris.Add(Vertices.Count);
                Vertices.Add(p);
                Vector3 vertexNormal;
                if (smooth != null)
                {
                    vertexNormal = smooth(p);
                }
                else
                {
                    Vector3 outward = p - inside;
                    vertexNormal = outward.sqrMagnitude > 1e-8f
                        ? (normal + outward.normalized * Softness).normalized
                        : normal;
                }
                Normals.Add(vertexNormal);
                Vector3 m = new Vector3(p.x * UvAxisScale.x, p.y * UvAxisScale.y, p.z * UvAxisScale.z) / TileSize;
                UVs.Add(UvProjection != null ? UvProjection(p, normal)
                    : drop == 0 ? new Vector2(m.z, m.y) : drop == 1 ? new Vector2(m.x, m.z) : new Vector2(m.x, m.y));
            }
        }

        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 inside)
        {
            Tri(a, b, c, inside);
            Tri(a, c, d, inside);
        }

        /// <summary>
        /// A box whose top can be a different size from its bottom and shifted forward/back, which covers
        /// torsos, packs, boots and most blocky parts. Sizes are full widths (x) and depths (z).
        /// </summary>
        public void Block(Vector3 bottomCenter, float height, float bottomWidth, float bottomDepth,
            float topWidth, float topDepth, float topShiftZ = 0f, float topShiftX = 0f)
        {
            float bx = bottomWidth * 0.5f, bz = bottomDepth * 0.5f;
            float tx = topWidth * 0.5f, tz = topDepth * 0.5f;
            Vector3 b = bottomCenter;
            Vector3 t = bottomCenter + new Vector3(topShiftX, height, topShiftZ);
            var c = new[]
            {
                b + new Vector3(-bx, 0, -bz), b + new Vector3(bx, 0, -bz), b + new Vector3(bx, 0, bz), b + new Vector3(-bx, 0, bz),
                t + new Vector3(-tx, 0, -tz), t + new Vector3(tx, 0, -tz), t + new Vector3(tx, 0, tz), t + new Vector3(-tx, 0, tz),
            };
            Vector3 inside = (b + t) * 0.5f;
            Quad(c[0], c[1], c[2], c[3], inside);
            Quad(c[4], c[5], c[6], c[7], inside);
            Quad(c[0], c[1], c[5], c[4], inside);
            Quad(c[1], c[2], c[6], c[5], inside);
            Quad(c[2], c[3], c[7], c[6], inside);
            Quad(c[3], c[0], c[4], c[7], inside);
        }

        /// <summary>A plain axis-aligned box given its center and full size.</summary>
        public void Box(Vector3 center, Vector3 size)
        {
            Block(center - new Vector3(0, size.y * 0.5f, 0), size.y, size.x, size.z, size.x, size.z);
        }

        /// <summary>A tapered n-sided tube from a to b, with optional end caps.</summary>
        public void Tube(Vector3 a, Vector3 b, float radiusA, float radiusB, int sides, bool caps = true, float twist = 0f)
        {
            Vector3 axis = b - a;
            float length = axis.magnitude;
            if (length < 1e-6f)
            {
                return;
            }
            axis /= length;
            Basis(axis, out Vector3 u, out Vector3 v);
            var ringA = new Vector3[sides];
            var ringB = new Vector3[sides];
            for (int i = 0; i < sides; i++)
            {
                float angle = twist + i * Mathf.PI * 2f / sides;
                Vector3 radial = u * Mathf.Cos(angle) + v * Mathf.Sin(angle);
                ringA[i] = a + radial * radiusA;
                ringB[i] = b + radial * radiusB;
            }
            Vector3 inside = (a + b) * 0.5f;
            Vector3 start = a;
            Vector3 Radial(Vector3 p)
            {
                Vector3 off = p - start;
                Vector3 r = off - axis * Vector3.Dot(off, axis);
                return r.sqrMagnitude > 1e-10f ? r.normalized : axis;
            }
            for (int i = 0; i < sides; i++)
            {
                int j = (i + 1) % sides;
                Tri(ringA[i], ringA[j], ringB[j], inside, Radial);
                Tri(ringA[i], ringB[j], ringB[i], inside, Radial);
            }
            if (caps)
            {
                for (int i = 0; i < sides; i++)
                {
                    int j = (i + 1) % sides;
                    Tri(a, ringA[i], ringA[j], b);
                    Tri(b, ringB[j], ringB[i], a);
                }
            }
        }

        /// <summary>
        /// A rounded body swept up through elliptical rings: each ring is (height, half width, half depth,
        /// forward shift). Shaded smooth around, so torsos read as soft, not boxy. Ends are capped.
        /// </summary>
        public void Loft(Vector4[] rings, int sides = 12, bool capBottom = true, bool capTop = true)
        {
            var points = new Vector3[rings.Length, sides];
            for (int r = 0; r < rings.Length; r++)
            {
                for (int i = 0; i < sides; i++)
                {
                    float angle = (i + 0.5f) * Mathf.PI * 2f / sides;
                    points[r, i] = new Vector3(Mathf.Sin(angle) * rings[r].y, rings[r].x, Mathf.Cos(angle) * rings[r].z + rings[r].w);
                }
            }
            for (int r = 0; r < rings.Length - 1; r++)
            {
                Vector4 lo = rings[r], hi = rings[r + 1];
                Vector3 inside = new Vector3(0, (lo.x + hi.x) * 0.5f, (lo.w + hi.w) * 0.5f);
                Vector3 Around(Vector3 p)
                {
                    float t = Mathf.Approximately(hi.x, lo.x) ? 0f : Mathf.Clamp01((p.y - lo.x) / (hi.x - lo.x));
                    float rx = Mathf.Lerp(lo.y, hi.y, t), rz = Mathf.Lerp(lo.z, hi.z, t), shift = Mathf.Lerp(lo.w, hi.w, t);
                    var n = new Vector3(p.x / (rx * rx), 0, (p.z - shift) / (rz * rz)).normalized;
                    // Lean the normal up or down where the body narrows or widens.
                    float slope = (lo.y + lo.z - hi.y - hi.z) * 0.5f / Mathf.Max(hi.x - lo.x, 0.01f);
                    return (n + Vector3.up * Mathf.Clamp(slope, -2f, 2f)).normalized;
                }
                for (int i = 0; i < sides; i++)
                {
                    int j = (i + 1) % sides;
                    Tri(points[r, i], points[r, j], points[r + 1, j], inside, Around);
                    Tri(points[r, i], points[r + 1, j], points[r + 1, i], inside, Around);
                }
            }
            int last = rings.Length - 1;
            Vector3 bottom = new Vector3(0, rings[0].x, rings[0].w);
            Vector3 top = new Vector3(0, rings[last].x, rings[last].w);
            for (int i = 0; i < sides; i++)
            {
                int j = (i + 1) % sides;
                if (capBottom)
                {
                    Tri(bottom, points[0, i], points[0, j], bottom + Vector3.up, p => Vector3.down);
                }
                if (capTop)
                {
                    Tri(top, points[last, i], points[last, j], top + Vector3.down, p => Vector3.up);
                }
            }
        }

        /// <summary>A faceted ellipsoid, or its upper half when <paramref name="dome"/> is set.</summary>
        public void Ellipsoid(Vector3 center, Vector3 radii, int rings, int segments, bool dome = false)
        {
            float startLat = dome ? 0f : -Mathf.PI * 0.5f;
            Vector3 Point(int ring, int seg)
            {
                float lat = startLat + (Mathf.PI * 0.5f - startLat) * ring / rings;
                float lon = seg * Mathf.PI * 2f / segments;
                return center + new Vector3(
                    Mathf.Cos(lat) * Mathf.Sin(lon) * radii.x,
                    Mathf.Sin(lat) * radii.y,
                    Mathf.Cos(lat) * Mathf.Cos(lon) * radii.z);
            }
            Vector3 inside = dome ? center + Vector3.up * radii.y * 0.4f : center;
            Vector3 Outward(Vector3 p)
            {
                Vector3 d = p - center;
                return new Vector3(d.x / (radii.x * radii.x), d.y / (radii.y * radii.y), d.z / (radii.z * radii.z)).normalized;
            }
            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    Vector3 a = Point(r, s), b = Point(r, s + 1), c = Point(r + 1, s + 1), d = Point(r + 1, s);
                    if (r == rings - 1)
                    {
                        Tri(a, b, c, inside, Outward);
                    }
                    else if (!dome && r == 0)
                    {
                        Tri(a, c, d, inside, Outward);
                    }
                    else
                    {
                        Tri(a, b, c, inside, Outward);
                        Tri(a, c, d, inside, Outward);
                    }
                }
            }
            if (dome)
            {
                // Close the bottom so the dome reads as a solid shell from below.
                for (int s = 0; s < segments; s++)
                {
                    Tri(center, Point(0, s), Point(0, s + 1), center + Vector3.up);
                }
            }
        }

        /// <summary>A ring of short tubes (rope coils, carabiners, helmet rims).</summary>
        public void Torus(Vector3 center, Vector3 normal, float radius, float thickness, int segments, int sides = 4)
        {
            Basis(normal.normalized, out Vector3 u, out Vector3 v);
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * Mathf.PI * 2f / segments;
                float a1 = (i + 1) * Mathf.PI * 2f / segments;
                Vector3 p0 = center + (u * Mathf.Cos(a0) + v * Mathf.Sin(a0)) * radius;
                Vector3 p1 = center + (u * Mathf.Cos(a1) + v * Mathf.Sin(a1)) * radius;
                // Stretch each piece a little so neighbors overlap and the ring has no gaps.
                Vector3 pad = (p1 - p0) * 0.08f;
                Tube(p0 - pad, p1 + pad, thickness, thickness, sides, false);
            }
        }

        /// <summary>Two unit vectors perpendicular to <paramref name="axis"/> and to each other.</summary>
        public static void Basis(Vector3 axis, out Vector3 u, out Vector3 v)
        {
            Vector3 helper = Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right;
            u = Vector3.Cross(helper, axis).normalized;
            v = Vector3.Cross(axis, u);
        }

#if !AVATAR_PREVIEW
        public Mesh ToMesh(string name)
        {
            var mesh = new Mesh { name = name };
            mesh.SetVertices(Vertices);
            mesh.SetNormals(Normals);
            mesh.SetUVs(0, UVs);
            mesh.subMeshCount = Submeshes.Length;
            for (int i = 0; i < Submeshes.Length; i++)
            {
                mesh.SetTriangles(Submeshes[i], i);
            }
            mesh.RecalculateBounds();
            return mesh;
        }
#endif
    }
}
