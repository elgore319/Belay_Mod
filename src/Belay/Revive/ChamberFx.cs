using Belay.Players.Avatar;
using UnityEngine;

namespace Belay.Revive
{
    /// <summary>
    /// The biochamber's revive effects: growth medium filling and draining the glass, bubbles, sparks off the
    /// control box, steam venting round the base, and synthesized sounds (ReviveSounds). Built the first time a chamber revives somebody. Every PC plays the revive itself
    /// when it hears about it, so everyone sees the same thing.
    /// </summary>
    internal class ChamberFx : MonoBehaviour
    {
        private static Material puffMaterial, fluidMaterial;

        private Transform fluid;
        private ParticleSystem bubbles, sparks, steam;
        private float bubbleDebt;

        internal static ChamberFx For(BioChamberMarker m)
        {
            ChamberFx fx = m.GetComponent<ChamberFx>();
            if (fx == null)
            {
                fx = m.gameObject.AddComponent<ChamberFx>();
                fx.Build();
            }
            return fx;
        }

        private void Build()
        {
            Materials();
            // Growth medium: an amber-green column inside the glass, scaled up from the floor.
            var kit = new MeshKit(1);
            float height = BioChamberLook.GlassTop - BioChamberLook.FloorHeight - 0.12f;
            kit.Loft(new[] { new Vector4(0f, BioChamberLook.GlassRadius - 0.03f, BioChamberLook.GlassRadius - 0.03f, 0f),
                new Vector4(height, BioChamberLook.GlassRadius - 0.03f, BioChamberLook.GlassRadius - 0.03f, 0f) }, 16);
            var go = new GameObject("Fluid");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.up * BioChamberLook.FloorHeight;
            go.AddComponent<MeshFilter>().sharedMesh = kit.ToMesh("Belay_BioChamberFluid");
            go.AddComponent<MeshRenderer>().sharedMaterial = fluidMaterial;
            fluid = go.transform;
            SetFluid(0f);

            bubbles = Particles("Bubbles", new Vector3(0, BioChamberLook.FloorHeight + 0.1f, 0), Quaternion.Euler(-90, 0, 0),
                new Color(0.85f, 0.95f, 0.75f, 0.7f), 0.02f, 0.06f, 0.4f, 0.9f, 2.2f, -0.15f, ParticleSystemShapeType.Cone, 0.45f, 4f);
            sparks = Particles("Sparks", new Vector3(0.82f, 1.12f, 0.08f), Quaternion.Euler(0, 90, 0),
                new Color(1f, 0.75f, 0.3f, 1f), 0.015f, 0.035f, 1.5f, 3.5f, 0.5f, 1.2f, ParticleSystemShapeType.Cone, 0.02f, 35f);
            steam = Particles("Steam", new Vector3(0, BioChamberLook.FloorHeight + 0.05f, 0), Quaternion.Euler(-90, 0, 0),
                new Color(0.82f, 0.84f, 0.82f, 0.45f), 0.25f, 0.6f, 0.5f, 1.4f, 1.8f, -0.08f, ParticleSystemShapeType.Circle, 0.62f);
        }

        private ParticleSystem Particles(string name, Vector3 position, Quaternion rotation, Color color, float minSize, float maxSize,
            float minSpeed, float maxSpeed, float life, float gravity, ParticleSystemShapeType shape, float radius, float angle = 0f)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = position;
            go.transform.localRotation = rotation;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.6f, life);
            main.startSpeed = new ParticleSystem.MinMaxCurve(minSpeed, maxSpeed);
            main.startSize = new ParticleSystem.MinMaxCurve(minSize, maxSize);
            main.startColor = color;
            main.gravityModifier = gravity;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 120;
            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = 0f;
            ParticleSystem.ShapeModule s = ps.shape;
            s.shapeType = shape;
            s.radius = radius;
            s.angle = angle;
            ParticleSystem.ColorOverLifetimeModule fade = ps.colorOverLifetime;
            fade.enabled = true;
            var gradient = new Gradient();
            gradient.colorKeys = new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) };
            gradient.alphaKeys = new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.4f), new GradientAlphaKey(0f, 1f) };
            fade.color = gradient;
            ParticleSystem.SizeOverLifetimeModule grow = ps.sizeOverLifetime;
            grow.enabled = true;
            grow.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.7f, 1f, 1.4f));
            go.GetComponent<ParticleSystemRenderer>().sharedMaterial = puffMaterial;
            ps.Play();
            return ps;
        }

        internal void SetFluid(float level)
        {
            if (fluid == null)
            {
                return;
            }
            fluid.gameObject.SetActive(level > 0.01f);
            fluid.localScale = new Vector3(1f, Mathf.Max(0.01f, level), 1f);
        }

        internal void Bubbles(float perFrame)
        {
            bubbleDebt += perFrame * 60f * Time.deltaTime * 3f;
            int n = Mathf.FloorToInt(bubbleDebt);
            if (n > 0 && bubbles != null)
            {
                bubbles.Emit(n);
                bubbleDebt -= n;
            }
        }

        internal void Sparks(int count) => sparks?.Emit(count);

        internal void Steam(int count) => steam?.Emit(count);

        internal void Sound(AudioClip clip, float volume) => ReviveSounds.Play(clip, transform, volume);

        private static void Materials()
        {
            if (puffMaterial != null)
            {
                return;
            }
            Shader sprite = Shader.Find("Sprites/Default");
            // A soft round puff, used for bubbles, sparks and steam alike (tinted per particle).
            const int n = 32;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float d = new Vector2(x + 0.5f - n / 2f, y + 0.5f - n / 2f).magnitude / (n / 2f);
                    byte a = (byte)(255 * Mathf.Clamp01(1f - d * d));
                    px[y * n + x] = new Color32(255, 255, 255, a);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            puffMaterial = new Material(sprite) { name = "Belay_BioChamberPuff", mainTexture = tex };
            fluidMaterial = new Material(sprite) { name = "Belay_BioChamberFluid", color = new Color(0.62f, 0.55f, 0.18f, 0.5f) };
            DontDestroyOnLoad(tex);
            DontDestroyOnLoad(puffMaterial);
            DontDestroyOnLoad(fluidMaterial);
        }
    }
}
