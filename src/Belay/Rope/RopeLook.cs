using UnityEngine;

namespace Belay.Rope
{
    /// <summary>
    /// Team rope material: kernmantle with a woven sheath and a dark tracer, in the climbers' pixel style.
    /// </summary>
    public static class RopeLook
    {
        private const int Around = 8;
        private const int Along = 32;
        private const float SelfLight = 0.45f;

        private static readonly Color Sheath = new Color(0.86f, 0.36f, 0.13f);
        private static readonly Color Weave = new Color(0.98f, 0.62f, 0.25f);
        private static readonly Color Tracer = new Color(0.13f, 0.17f, 0.32f);

        private static Material material;

        public static Material Material
        {
            get
            {
                if (material == null)
                {
                    material = Make();
                }
                return material;
            }
        }

        private static Material Make()
        {
            // The default primitive material is guaranteed to render in this game's pipeline.
            GameObject probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Shader shader = probe.GetComponent<Renderer>().sharedMaterial.shader;
            Object.Destroy(probe);

            var texture = new Texture2D(Around, Along, TextureFormat.RGBA32, false)
            {
                name = "Belay_Rope",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
            };
            texture.SetPixels32(Pixels());
            texture.Apply(false, true);

            var m = new Material(shader) { name = "Belay_Rope", color = Color.white, mainTexture = texture };
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                m.SetTexture("_EmissionMap", texture);
                m.SetColor("_EmissionColor", Color.white * SelfLight);
            }
            if (m.HasProperty("_Glossiness"))
            {
                m.SetFloat("_Glossiness", 0.05f);
            }
            return m;
        }

        public static Color32[] Pixels()
        {
            var pixels = new Color32[Around * Along];
            for (int y = 0; y < Along; y++)
            {
                for (int x = 0; x < Around; x++)
                {
                    // Sheath strands cross in a herringbone: two diagonals, alternating bands.
                    bool left = ((x + y) & 3) < 2;
                    bool right = ((x - y + 64) & 3) < 2;
                    bool band = ((y >> 1) & 1) == 0;
                    Color c = (band ? left : right) ? Weave : Sheath;
                    // Shadow in the grooves between strands.
                    if (((x + y) & 3) == 3 && band || ((x - y + 64) & 3) == 3 && !band)
                    {
                        c *= 0.72f;
                    }
                    // One tracer thread spiralling round the rope, as on real dynamic ropes.
                    if (((x * 4 + y) & 31) < 2)
                    {
                        c = Tracer;
                    }
                    float grime = Hash(x, y) * 0.16f;
                    c = Color.Lerp(c, c * 0.6f, grime);
                    c.a = 1f;
                    pixels[y * Around + x] = c;
                }
            }
            return pixels;
        }

        private static float Hash(int x, int y)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + 17);
                h = (h ^ (h >> 13)) * 1274126177u;
                return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
            }
        }
    }
}
