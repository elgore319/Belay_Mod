using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>
    /// Outfits drawn from the people and things of the superstructure: its staff (Delta Labs researchers,
    /// Pipeworks maintenance crews, pier workers), the folk scraping by in Habitation's lower decks,
    /// Chimney expeditions, and what the Nest and the denizens make of a climber. Each look has its own
    /// headgear, an extra piece of clothing worn over the torso and a palette; the player's swatch picks
    /// still recolor it on top.
    ///
    /// Pure geometry and colors (no Unity scene calls), so the preview tool can render them too.
    /// Units and spaces match <see cref="AvatarDesign"/>: torso extras at the pelvis, heads at the neck.
    /// </summary>
    public static class LoreLooks
    {
        public const int Climber = 0, Researcher = 1, Maintenance = 2, PierWorker = 3, Scavenger = 4,
            Expedition = 5, Infested = 6, Denizen = 7, ZombieScientist = 8, ZombieSecurity = 9,
            Shopkeeper = 10, Count = 11;

        public struct Look
        {
            public string Name;
            public string Blurb;
            public Color Jacket, Trim, Pants, Pack, Hat, Accent, Flesh, Boot, Glove;
            /// <summary>How filthy the cloth is, 0..1 (the standard climber is about 0.3).</summary>
            public float Grime;
            /// <summary>Set for looks that wear one of the game's own models (see <see cref="GameModelAvatar"/>).</summary>
            public string Model;
        }

        public static readonly Look[] All =
        {
            new Look { Name = "Climber", Blurb = "Standard climbing gear." },
            new Look
            {
                Name = "Delta Labs researcher",
                Blurb = "Lab coat, ID badge and a sealed respirator hood. Mind the fume hoods.",
                Jacket = new Color(0.74f, 0.73f, 0.69f), Trim = new Color(0.56f, 0.6f, 0.58f),
                Pants = new Color(0.25f, 0.29f, 0.33f), Pack = new Color(0.4f, 0.42f, 0.43f),
                Hat = new Color(0.55f, 0.6f, 0.56f), Accent = new Color(0.16f, 0.45f, 0.48f),
                Boot = new Color(0.2f, 0.2f, 0.22f), Glove = new Color(0.55f, 0.62f, 0.55f), Grime = 0.18f,
            },
            new Look
            {
                Name = "Pipeworks maintenance",
                Blurb = "Hazard coveralls, hard hat and a tool belt. Most maintenance staff are people.",
                Jacket = new Color(0.74f, 0.36f, 0.1f), Trim = new Color(0.5f, 0.24f, 0.08f),
                Pants = new Color(0.66f, 0.32f, 0.1f), Pack = new Color(0.36f, 0.27f, 0.18f),
                Hat = new Color(0.78f, 0.62f, 0.16f), Accent = new Color(0.8f, 0.78f, 0.62f),
                Boot = new Color(0.17f, 0.14f, 0.12f), Glove = new Color(0.45f, 0.35f, 0.22f), Grime = 0.45f,
            },
            new Look
            {
                Name = "Pier worker",
                Blurb = "Oilskin slicker and sou'wester for the fog off the Delta piers.",
                Jacket = new Color(0.68f, 0.55f, 0.16f), Trim = new Color(0.46f, 0.37f, 0.12f),
                Pants = new Color(0.16f, 0.2f, 0.3f), Pack = new Color(0.3f, 0.3f, 0.26f),
                Hat = new Color(0.66f, 0.53f, 0.15f), Accent = new Color(0.22f, 0.22f, 0.2f),
                Boot = new Color(0.2f, 0.24f, 0.2f), Glove = new Color(0.22f, 0.26f, 0.22f), Grime = 0.3f,
            },
            new Look
            {
                Name = "Habitation scavenger",
                Blurb = "Patched poncho and head rags, from the lower decks where nobody comes to fix things.",
                Jacket = new Color(0.45f, 0.36f, 0.26f), Trim = new Color(0.4f, 0.16f, 0.14f),
                Pants = new Color(0.32f, 0.33f, 0.22f), Pack = new Color(0.36f, 0.3f, 0.24f),
                Hat = new Color(0.52f, 0.46f, 0.38f), Accent = new Color(0.3f, 0.36f, 0.4f),
                Boot = new Color(0.22f, 0.17f, 0.14f), Glove = new Color(0.5f, 0.44f, 0.36f), Grime = 0.35f,
            },
            new Look
            {
                Name = "Chimney expedition",
                Blurb = "Padded parka with a fur-ruffed hood, for the Icebox and the Spire.",
                Jacket = new Color(0.16f, 0.36f, 0.4f), Trim = new Color(0.1f, 0.22f, 0.26f),
                Pants = new Color(0.17f, 0.17f, 0.19f), Pack = new Color(0.5f, 0.24f, 0.14f),
                Hat = new Color(0.16f, 0.36f, 0.4f), Accent = new Color(0.72f, 0.66f, 0.56f),
                Boot = new Color(0.14f, 0.13f, 0.14f), Glove = new Color(0.3f, 0.24f, 0.2f), Grime = 0.2f,
            },
            new Look
            {
                Name = "Infested",
                Blurb = "Gear gone soft with Zeta growth. It wants you.",
                Jacket = new Color(0.33f, 0.16f, 0.17f), Trim = new Color(0.23f, 0.1f, 0.12f),
                Pants = new Color(0.24f, 0.19f, 0.21f), Pack = new Color(0.28f, 0.2f, 0.2f),
                Hat = new Color(0.34f, 0.3f, 0.3f), Accent = new Color(0.82f, 0.62f, 0.3f),
                Flesh = new Color(0.82f, 0.46f, 0.44f), Boot = new Color(0.15f, 0.12f, 0.13f),
                Glove = new Color(0.4f, 0.27f, 0.28f), Grime = 0.55f,
            },
            new Look
            {
                Name = "Denizen",
                Blurb = "What's left of a climber who didn't make it. Torn, bloodied, bone showing.",
                Jacket = new Color(0.38f, 0.33f, 0.3f), Trim = new Color(0.3f, 0.12f, 0.12f),
                Pants = new Color(0.25f, 0.22f, 0.22f), Pack = new Color(0.26f, 0.22f, 0.2f),
                Hat = new Color(0.3f, 0.3f, 0.3f), Accent = new Color(0.45f, 0.08f, 0.08f),
                Flesh = new Color(0.74f, 0.68f, 0.6f), Boot = new Color(0.15f, 0.13f, 0.13f),
                Glove = new Color(0.36f, 0.3f, 0.28f), Grime = 0.4f,
            },
            new Look
            {
                Name = "Zombie scientist",
                Blurb = "The game's own Delta Labs casualty: torn lab coat, injector still in the pocket.",
                Model = GameModelAvatar.ZombieScientist,
            },
            new Look
            {
                Name = "Zombie security",
                Blurb = "The game's own security guard, armor plates, radio and all, long past saving.",
                Model = GameModelAvatar.ZombieSecurity,
            },
            new Look
            {
                Name = "Soach shopkeeper",
                Blurb = "The Bazaar's roach trader, goggles, rope and wares strapped on.",
                Model = GameModelAvatar.Soach,
            },
        };

        public static bool Valid(int look) => look > 0 && look < Count;

        /// <summary>
        /// Whether the look wears a headlamp. The lab researcher, pier worker, scavenger and denizen go
        /// without, so each look reads differently (and their teammate light stays off).
        /// </summary>
        public static bool HasLamp(int look) => !(look == Researcher || look == PierWorker || look == Scavenger || look == Denizen);

        /// <summary>
        /// The texture recipes for a look. Swatch picks (null = none) still win over the look's own colors,
        /// so a researcher can wear a red coat if they want.
        /// </summary>
        public static PixelTextures.Recipe[] Recipes(int look, Color player, Color? jacket, Color? hat, Color? pants, Color? pack)
        {
            if (!Valid(look))
            {
                return AvatarDesign.Recipes(player, jacket, hat, pants, pack);
            }
            Look l = All[look];
            PixelTextures.Recipe[] r = AvatarDesign.Recipes(player, jacket ?? l.Jacket, hat ?? l.Hat, pants ?? l.Pants, pack ?? l.Pack);
            PixelTextures.Recipe R(Color b, float grime, float speck, float contrast = 0.45f, Color? speckColor = null)
            {
                return new PixelTextures.Recipe
                {
                    Base = b, Grime = AvatarDesign.GrimeColor, Speck = speckColor ?? Color.Lerp(b * 1.3f, AvatarDesign.BoneColor, 0.3f),
                    GrimeAmount = grime, SpeckAmount = speck, Contrast = contrast,
                };
            }
            r[Slot.Jacket] = R(jacket ?? l.Jacket, l.Grime, 0.06f);
            r[Slot.JacketTrim] = R(jacket.HasValue ? AvatarDesign.Muted(jacket.Value, 0.85f, 0.6f) : l.Trim, l.Grime, 0.05f);
            r[Slot.Pants] = R(pants ?? l.Pants, l.Grime, 0.04f);
            r[Slot.Pack] = R(pack ?? l.Pack, l.Grime + 0.05f, 0.06f);
            r[Slot.Helmet] = R(hat ?? l.Hat, l.Grime + 0.1f, 0.1f, 0.4f, AvatarDesign.RustColor);
            r[Slot.Boot] = R(l.Boot, l.Grime, 0.06f, 0.3f, AvatarDesign.RustColor * 0.7f);
            r[Slot.Glove] = R(l.Glove, l.Grime, 0.05f);
            r[Slot.Accent] = R(l.Accent, look == Denizen ? 0.2f : 0.08f, 0.05f, 0.3f);
            r[Slot.Flesh] = R(l.Flesh.a > 0f ? l.Flesh : AvatarDesign.BoneColor, 0.35f, 0.18f, 0.55f, new Color(0.9f, 0.75f, 0.5f));
            if (look == Scavenger)
            {
                // Soot and damp, not blood.
                Color soot = new Color(0.16f, 0.13f, 0.1f);
                r[Slot.Jacket].Grime = r[Slot.JacketTrim].Grime = r[Slot.Pants].Grime = r[Slot.Helmet].Grime = soot;
            }
            if (look == Infested)
            {
                r[Slot.Flesh].Grime = new Color(0.45f, 0.12f, 0.16f);
                r[Slot.Flesh].GrimeAmount = 0.5f;
            }
            if (look == Denizen)
            {
                // Old blood soaked into everything.
                r[Slot.Jacket].Grime = r[Slot.Pants].Grime = new Color(0.32f, 0.06f, 0.07f);
                r[Slot.Flesh].Grime = new Color(0.5f, 0.42f, 0.32f);
            }
            return r;
        }

        // ---------------------------------------------------------------------------------------------
        // Heads. Origin at the base of the neck, +z where they look. Every head keeps the headlamp in the
        // standard spot (the light source sits there in game).

        public static void BuildHead(MeshKit k, int look)
        {
            switch (look)
            {
                case Researcher: ResearcherHead(k); break;
                case Maintenance: MaintenanceHead(k); break;
                case PierWorker: PierHead(k); break;
                case Scavenger: ScavengerHead(k); break;
                case Expedition: ExpeditionHead(k); break;
                case Infested: InfestedHead(k); break;
                case Denizen: DenizenHead(k); break;
                default: AvatarDesign.BuildHead(k, AvatarDesign.HeadHelmet); break;
            }
        }

        private static void Face(MeshKit k, bool goggles = true)
        {
            k.Use(Slot.Buff);
            k.Tube(new Vector3(0, -0.02f, 0), new Vector3(0, 0.08f, 0.01f), 0.07f, 0.064f, 8);
            CoveredHead(k, Slot.Buff, Slot.Webbing);
            if (goggles)
            {
                Goggles(k, Slot.Pad, Slot.Webbing);
            }
        }

        /// <summary>
        /// A head under a balaclava or wrappings: rounded skull, jaw and chin, the nose and ears showing
        /// through the cloth, and a breathing patch over the mouth.
        /// </summary>
        private static void CoveredHead(MeshKit k, int cloth, int mouth)
        {
            k.Use(cloth);
            k.Loft(new[]
            {
                new Vector4(0.05f, 0.048f, 0.058f, 0.032f),
                new Vector4(0.066f, 0.066f, 0.078f, 0.024f),
                new Vector4(0.095f, 0.079f, 0.09f, 0.017f),
                new Vector4(0.13f, 0.084f, 0.095f, 0.014f),
                new Vector4(0.17f, 0.083f, 0.093f, 0.012f),
                new Vector4(0.205f, 0.071f, 0.081f, 0.01f),
                new Vector4(0.228f, 0.042f, 0.05f, 0.008f),
            }, 12);
            // Nose, chin and cheekbones pushing through the cloth.
            k.Block(new Vector3(0, 0.088f, 0.1f), 0.04f, 0.026f, 0.026f, 0.012f, 0.014f, -0.006f);
            k.Ellipsoid(new Vector3(0, 0.058f, 0.072f), new Vector3(0.03f, 0.016f, 0.022f), 2, 6);
            for (int side = -1; side <= 1; side += 2)
            {
                k.Ellipsoid(new Vector3(side * 0.05f, 0.105f, 0.085f), new Vector3(0.022f, 0.016f, 0.016f), 2, 6);
                k.Ellipsoid(new Vector3(side * 0.083f, 0.125f, 0.008f), new Vector3(0.014f, 0.028f, 0.02f), 2, 6);
            }
            k.Use(mouth).Ellipsoid(new Vector3(0, 0.074f, 0.092f), new Vector3(0.034f, 0.022f, 0.016f), 2, 7);
        }

        /// <summary>Twin round goggle lenses in padded cups, a bridge and a strap round the head.</summary>
        private static void Goggles(MeshKit k, int frame, int strap)
        {
            k.Use(strap).Loft(new[] { new Vector4(0.128f, 0.088f, 0.099f, 0.014f), new Vector4(0.152f, 0.088f, 0.099f, 0.013f) }, 12, false, false);
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 c = new Vector3(side * 0.04f, 0.137f, 0.098f);
                k.Use(frame).Tube(c, c + new Vector3(side * 0.004f, 0f, 0.026f), 0.033f, 0.03f, 10);
                k.Use(Slot.Visor).Tube(c + new Vector3(side * 0.004f, 0f, 0.022f), c + new Vector3(side * 0.004f, 0f, 0.03f), 0.024f, 0.022f, 10);
                k.Use(Slot.Metal).Ellipsoid(c + new Vector3(side * 0.024f, 0.02f, 0.024f), new Vector3(0.005f, 0.005f, 0.005f), 2, 4);
            }
            k.Use(frame).Box(new Vector3(0, 0.137f, 0.118f), new Vector3(0.03f, 0.012f, 0.012f));
        }

        private static void Lamp(MeshKit k, float mount, float depth, float strapZ, float strapRadius)
        {
            if (strapRadius > 0f)
            {
                k.Use(Slot.Webbing).Torus(new Vector3(0, 0.208f, strapZ), Vector3.up, strapRadius, 0.009f, 10, 4);
            }
            k.Use(Slot.Webbing).Box(new Vector3(0, 0.215f, mount), new Vector3(0.075f, 0.05f, depth));
            k.Use(Slot.Lamp).Tube(new Vector3(0, 0.215f, 0.157f), new Vector3(0, 0.215f, 0.166f), 0.019f, 0.019f, 8);
        }

        /// <summary>Sealed hood over the head and shoulders, wide face window, twin-filter respirator.</summary>
        private static void ResearcherHead(MeshKit k)
        {
            k.Use(Slot.Helmet);
            k.Ellipsoid(new Vector3(0, 0.15f, -0.01f), new Vector3(0.118f, 0.13f, 0.128f), 4, 10, true);
            // The hood falls in a smooth bell over the shoulders, with a seam down the back.
            k.Loft(new[]
            {
                new Vector4(-0.04f, 0.15f, 0.125f, -0.02f),
                new Vector4(0.0f, 0.13f, 0.115f, -0.018f),
                new Vector4(0.06f, 0.112f, 0.118f, -0.014f),
                new Vector4(0.15f, 0.118f, 0.128f, -0.01f),
            }, 12, true, false);
            k.Use(Slot.JacketTrim).Tube(new Vector3(0, 0.27f, -0.06f), new Vector3(0, -0.03f, -0.145f), 0.006f, 0.006f, 4);
            k.Torus(new Vector3(0, 0.0f, -0.018f), Vector3.up, 0.135f, 0.01f, 12, 4);
            // A domed face window in a gasket ring held by rivets.
            k.Use(Slot.Visor).Ellipsoid(new Vector3(0, 0.15f, 0.09f), new Vector3(0.082f, 0.072f, 0.04f), 3, 12);
            k.Use(Slot.Pad).Torus(new Vector3(0, 0.15f, 0.105f), Vector3.forward, 0.088f, 0.012f, 12, 4);
            k.Use(Slot.Metal);
            for (int i = 0; i < 8; i++)
            {
                float a = (i + 0.5f) * Mathf.PI * 2f / 8f;
                k.Ellipsoid(new Vector3(Mathf.Cos(a) * 0.088f, 0.15f + Mathf.Sin(a) * 0.088f, 0.116f), new Vector3(0.007f, 0.007f, 0.006f), 2, 4);
            }
            // Respirator: snout and two canisters angled down and out.
            k.Use(Slot.Metal).Ellipsoid(new Vector3(0, 0.055f, 0.12f), new Vector3(0.05f, 0.04f, 0.04f), 3, 8);
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 a = new Vector3(side * 0.04f, 0.045f, 0.14f);
                k.Use(Slot.Accent).Tube(a, a + new Vector3(side * 0.05f, -0.03f, 0.03f), 0.026f, 0.026f, 8);
                k.Use(Slot.Webbing).Tube(a + new Vector3(side * 0.05f, -0.03f, 0.03f), a + new Vector3(side * 0.056f, -0.034f, 0.034f), 0.022f, 0.02f, 8);
                k.Use(Slot.Metal).Torus(a + new Vector3(side * 0.025f, -0.015f, 0.015f), new Vector3(side * 0.05f, -0.03f, 0.03f), 0.027f, 0.005f, 8, 4);
            }
            k.Use(Slot.Webbing).Tube(new Vector3(0, 0.04f, 0.15f), new Vector3(0, 0.07f, 0.155f), 0.014f, 0.012f, 6);
        }

        /// <summary>Hard hat with a full brim and a ridge, ear defenders.</summary>
        private static void MaintenanceHead(MeshKit k)
        {
            Face(k);
            k.Use(Slot.Helmet);
            k.Ellipsoid(new Vector3(0, 0.2f, 0.005f), new Vector3(0.118f, 0.1f, 0.134f), 4, 10, true);
            k.Loft(new[]
            {
                new Vector4(0.19f, 0.15f, 0.17f, 0.025f),
                new Vector4(0.205f, 0.12f, 0.14f, 0.008f),
            }, 12, true, true);
            k.Box(new Vector3(0, 0.29f, 0.0f), new Vector3(0.03f, 0.03f, 0.22f));
            k.Use(Slot.Accent).Box(new Vector3(0, 0.255f, 0.0f), new Vector3(0.236f, 0.02f, 0.02f));
            k.Use(Slot.Pack);
            for (int side = -1; side <= 1; side += 2)
            {
                k.Ellipsoid(new Vector3(side * 0.105f, 0.12f, 0.0f), new Vector3(0.03f, 0.045f, 0.04f), 3, 8);
                k.Use(Slot.Webbing).Torus(new Vector3(side * 0.088f, 0.12f, 0.0f), Vector3.right, 0.036f, 0.008f, 10, 4);
                k.Use(Slot.Metal).Tube(new Vector3(side * 0.112f, 0.16f, 0.0f), new Vector3(side * 0.118f, 0.195f, 0.0f), 0.006f, 0.006f, 4);
                k.Use(Slot.Pack);
            }
            Lamp(k, 0.15f, 0.03f, 0f, 0f);
        }

        /// <summary>Sou'wester: soft crown, brim long at the back to shed rain off the neck.</summary>
        private static void PierHead(MeshKit k)
        {
            Face(k);
            k.Use(Slot.Helmet);
            k.Ellipsoid(new Vector3(0, 0.19f, -0.005f), new Vector3(0.112f, 0.11f, 0.122f), 4, 10, true);
            k.Loft(new[]
            {
                new Vector4(0.165f, 0.17f, 0.2f, -0.05f),
                new Vector4(0.19f, 0.118f, 0.13f, -0.005f),
            }, 12, true, true);
            k.Use(Slot.Accent).Tube(new Vector3(-0.1f, 0.18f, 0.03f), new Vector3(0, 0.03f, 0.08f), 0.006f, 0.006f, 4);
            k.Use(Slot.Accent).Tube(new Vector3(0.1f, 0.18f, 0.03f), new Vector3(0, 0.03f, 0.08f), 0.006f, 0.006f, 4);
        }

        /// <summary>Rags wound round the head and face, a strip trailing at the back, goggles over the top.</summary>
        private static void ScavengerHead(MeshKit k)
        {
            k.Use(Slot.Helmet);
            k.Tube(new Vector3(0, -0.02f, 0), new Vector3(0, 0.08f, 0.01f), 0.078f, 0.07f, 7);
            CoveredHead(k, Slot.Helmet, Slot.JacketTrim);
            k.Ellipsoid(new Vector3(0, 0.18f, 0.01f), new Vector3(0.094f, 0.07f, 0.104f), 3, 9, true);
            // Strips wound round at odd angles, a knot at the back with its ends hanging loose.
            k.Use(Slot.JacketTrim);
            k.Torus(new Vector3(0, 0.1f, 0.016f), new Vector3(0.05f, 1f, 0.12f), 0.094f, 0.013f, 11, 4);
            k.Torus(new Vector3(0, 0.2f, 0.01f), new Vector3(0.15f, 1f, -0.1f), 0.094f, 0.015f, 11, 4);
            k.Torus(new Vector3(0, 0.06f, 0.025f), new Vector3(-0.1f, 1f, 0.25f), 0.085f, 0.012f, 11, 4);
            k.Ellipsoid(new Vector3(0.01f, 0.14f, -0.1f), new Vector3(0.03f, 0.026f, 0.022f), 2, 6);
            k.Block(new Vector3(0.0f, 0.04f, -0.1f), 0.11f, 0.05f, 0.02f, 0.06f, 0.025f, -0.03f, 0.02f);
            k.Block(new Vector3(0.03f, 0.06f, -0.095f), 0.08f, 0.035f, 0.015f, 0.04f, 0.02f, -0.02f, 0.03f);
            Goggles(k, Slot.Accent, Slot.Webbing);
        }

        /// <summary>Parka hood up, cinched round the face with a thick fur ruff.</summary>
        private static void ExpeditionHead(MeshKit k)
        {
            Face(k);
            k.Use(Slot.Jacket);
            k.Ellipsoid(new Vector3(0, 0.15f, -0.03f), new Vector3(0.13f, 0.135f, 0.14f), 4, 10, true);
            k.Loft(new[]
            {
                new Vector4(-0.02f, 0.14f, 0.12f, -0.04f),
                new Vector4(0.05f, 0.122f, 0.12f, -0.035f),
                new Vector4(0.15f, 0.13f, 0.14f, -0.03f),
            }, 12, true, false);
            // A scarf wound up over the chin, its end hanging down the chest.
            k.Use(Slot.Pack);
            k.Torus(new Vector3(0, 0.035f, 0.015f), Vector3.up, 0.072f, 0.022f, 10, 5);
            k.Torus(new Vector3(0, 0.065f, 0.025f), new Vector3(0, 1f, 0.15f), 0.068f, 0.018f, 10, 5);
            k.Block(new Vector3(0.03f, -0.04f, 0.08f), 0.07f, 0.04f, 0.015f, 0.035f, 0.015f, 0.01f, 0.01f);
            k.Use(Slot.Accent);
            for (int i = 0; i < 12; i++)
            {
                float a = i * Mathf.PI * 2f / 12f;
                Vector3 p = new Vector3(Mathf.Cos(a) * 0.112f, 0.13f + Mathf.Sin(a) * 0.118f, 0.085f);
                k.Ellipsoid(p, new Vector3(0.032f, 0.032f, 0.03f), 2, 6);
            }
            Lamp(k, 0.115f, 0.085f, -0.03f, 0.13f);
        }

        /// <summary>A cracked helmet with growth bulging through the crack and over one eye.</summary>
        private static void InfestedHead(MeshKit k)
        {
            Face(k);
            k.Use(Slot.Helmet);
            k.Ellipsoid(new Vector3(0, 0.19f, 0.01f), new Vector3(0.124f, 0.105f, 0.14f), 4, 10, true);
            k.Torus(new Vector3(0, 0.19f, 0.01f), Vector3.up, 0.128f, 0.014f, 10, 4);
            // The crack running over the dome, and the growth that split it.
            k.Use(Slot.Visor);
            Vector3[] crack = { new Vector3(-0.07f, 0.27f, 0.07f), new Vector3(-0.02f, 0.29f, 0.03f), new Vector3(0.01f, 0.295f, -0.02f), new Vector3(0.06f, 0.28f, -0.06f) };
            for (int i = 0; i < crack.Length - 1; i++)
            {
                k.Tube(crack[i], crack[i + 1], 0.007f, 0.007f, 4);
            }
            Growth(k, new Vector3(0.05f, 0.27f, -0.02f), 0.06f, 11);
            Growth(k, new Vector3(0.095f, 0.2f, 0.07f), 0.042f, 12);
            Growth(k, new Vector3(0.06f, 0.13f, 0.112f), 0.036f, 13);   // over one eye
            Growth(k, new Vector3(-0.08f, 0.045f, 0.065f), 0.032f, 14); // the jaw
            // Veins spreading down from the crack across the face and neck.
            k.Use(Slot.Flesh);
            Vector3[][] veins =
            {
                new[] { new Vector3(0.09f, 0.2f, 0.09f), new Vector3(0.07f, 0.17f, 0.11f), new Vector3(0.03f, 0.1f, 0.115f), new Vector3(0.015f, 0.06f, 0.1f) },
                new[] { new Vector3(0.1f, 0.18f, 0.06f), new Vector3(0.09f, 0.11f, 0.07f), new Vector3(0.08f, 0.05f, 0.06f), new Vector3(0.07f, 0.0f, 0.05f) },
                new[] { new Vector3(-0.04f, 0.29f, 0.05f), new Vector3(-0.1f, 0.25f, 0.06f), new Vector3(-0.115f, 0.2f, 0.03f) },
                new[] { new Vector3(-0.08f, 0.06f, 0.07f), new Vector3(-0.07f, 0.0f, 0.06f), new Vector3(-0.065f, -0.02f, 0.04f) },
            };
            foreach (Vector3[] vein in veins)
            {
                for (int i = 0; i < vein.Length - 1; i++)
                {
                    float t = i / (float)(vein.Length - 1);
                    k.Tube(vein[i], vein[i + 1], 0.008f * (1f - t * 0.6f), 0.008f * (1f - (t + 1f / (vein.Length - 1)) * 0.6f), 4);
                }
            }
            Lamp(k, 0.15f, 0.03f, 0f, 0f);
        }

        /// <summary>Hood rotted open on a bare skull: brow, sockets, cheekbones and jaw.</summary>
        private static void DenizenHead(MeshKit k)
        {
            k.Use(Slot.Buff);
            k.Tube(new Vector3(0, -0.02f, 0), new Vector3(0, 0.07f, 0.0f), 0.06f, 0.05f, 7);
            k.Use(Slot.Flesh);
            k.Ellipsoid(new Vector3(0, 0.165f, -0.005f), new Vector3(0.088f, 0.098f, 0.105f), 5, 12);
            // Cheekbones, a narrow upper jaw and a separate lower jaw hanging slightly open.
            k.Loft(new[]
            {
                new Vector4(0.075f, 0.045f, 0.05f, 0.05f),
                new Vector4(0.1f, 0.062f, 0.06f, 0.045f),
                new Vector4(0.13f, 0.078f, 0.07f, 0.03f),
            }, 10);
            k.Loft(new[]
            {
                new Vector4(0.025f, 0.038f, 0.035f, 0.065f),
                new Vector4(0.045f, 0.058f, 0.05f, 0.05f),
                new Vector4(0.062f, 0.066f, 0.06f, 0.04f),
            }, 10);
            for (int side = -1; side <= 1; side += 2)
            {
                k.Ellipsoid(new Vector3(side * 0.068f, 0.12f, 0.065f), new Vector3(0.022f, 0.016f, 0.022f), 2, 6); // cheekbone
                k.Tube(new Vector3(side * 0.07f, 0.11f, 0.04f), new Vector3(side * 0.06f, 0.045f, 0.03f), 0.012f, 0.01f, 5); // jaw hinge
                k.Ellipsoid(new Vector3(side * 0.04f, 0.18f, 0.088f), new Vector3(0.04f, 0.014f, 0.022f), 2, 6); // brow ridge
            }
            k.Use(Slot.Visor);
            for (int side = -1; side <= 1; side += 2)
            {
                k.Ellipsoid(new Vector3(side * 0.038f, 0.145f, 0.09f), new Vector3(0.026f, 0.024f, 0.02f), 2, 7);
                k.Ellipsoid(new Vector3(side * 0.009f, 0.105f, 0.106f), new Vector3(0.008f, 0.016f, 0.008f), 2, 4); // nose holes
            }
            k.Box(new Vector3(0, 0.068f, 0.09f), new Vector3(0.07f, 0.012f, 0.03f)); // gap between the teeth
            k.Use(Slot.Pad);
            for (int i = 0; i < 6; i++)
            {
                float x = (i - 2.5f) * 0.012f;
                float z = 0.1f - Mathf.Abs(x) * 0.35f;
                k.Box(new Vector3(x, 0.08f, z), new Vector3(0.01f, 0.016f, 0.01f));
                k.Box(new Vector3(x, 0.055f, z - 0.004f), new Vector3(0.01f, 0.014f, 0.01f));
            }
            // Torn hood hanging off the back of the skull.
            k.Use(Slot.Jacket);
            k.Ellipsoid(new Vector3(0, 0.15f, -0.05f), new Vector3(0.115f, 0.115f, 0.1f), 3, 9, true);
            k.Block(new Vector3(0, -0.01f, -0.07f), 0.15f, 0.2f, 0.1f, 0.21f, 0.12f, 0.01f);
            k.Use(Slot.Accent).Ellipsoid(new Vector3(0.05f, 0.21f, 0.06f), new Vector3(0.03f, 0.02f, 0.03f), 2, 6);
        }

        // ---------------------------------------------------------------------------------------------
        // Extras worn over the torso. Origin at the pelvis, +y up, +z forward (the torso's space).

        public static void BuildExtra(MeshKit k, int look)
        {
            switch (look)
            {
                case Researcher: ResearcherExtra(k); break;
                case Maintenance: MaintenanceExtra(k); break;
                case PierWorker: PierExtra(k); break;
                case Scavenger: ScavengerExtra(k); break;
                case Expedition: ExpeditionExtra(k); break;
                case Infested: InfestedExtra(k); break;
                case Denizen: DenizenExtra(k); break;
            }
        }

        /// <summary>
        /// The coat skirt a look wears (hem height below the pelvis, how far it flares out, how far the
        /// back hangs behind), or false if it has none. <see cref="CoatSkirt"/> swings it out of the legs' way.
        /// </summary>
        public static bool SkirtShape(int look, out float hem, out float flare, out float back)
        {
            back = 0f;
            switch (look)
            {
                case Researcher: hem = -0.5f; flare = 0.05f; return true;
                case PierWorker: hem = -0.42f; flare = 0.07f; back = 0.02f; return true;
                case Expedition: hem = -0.3f; flare = 0.04f; return true;
                default: hem = 0f; flare = 0f; return false;
            }
        }

        /// <summary>A coat's skirt from the waist down to <paramref name="hem"/> (negative, below the pelvis).</summary>
        private static void Skirt(MeshKit k, float hem, float flare, float back = 0f)
        {
            k.Loft(new[]
            {
                new Vector4(0.06f, 0.178f, 0.118f, 0f),
                new Vector4(-0.05f, 0.19f, 0.13f, -0.006f),
                new Vector4(hem * 0.5f, 0.2f + flare * 0.5f, 0.14f + flare * 0.4f, -0.01f - back * 0.5f),
                new Vector4(hem, 0.21f + flare, 0.15f + flare * 0.7f, -0.015f - back),
            }, 14, false, false);
        }

        private static void ResearcherExtra(MeshKit k)
        {
            // Knee-length coat over everything, wide lapels, a pen in the pocket and the ID badge.
            k.Use(Slot.Jacket);
            Skirt(k, -0.5f, 0.05f);
            k.Loft(new[]
            {
                new Vector4(0.05f, 0.182f, 0.12f, 0f),
                new Vector4(0.22f, 0.198f, 0.126f, 0.01f),
                new Vector4(0.34f, 0.22f, 0.134f, 0.016f),
                new Vector4(0.44f, 0.212f, 0.124f, 0.012f),
                new Vector4(0.5f, 0.17f, 0.106f, 0.004f),
            }, 12, false, false);
            k.Use(Slot.JacketTrim);
            for (int side = -1; side <= 1; side += 2)
            {
                k.Block(new Vector3(side * 0.045f, 0.3f, 0.128f), 0.2f, 0.03f, 0.016f, 0.07f, 0.016f, 0.004f, side * 0.03f);
            }
            k.Box(new Vector3(0, 0.0f, 0.15f), new Vector3(0.012f, 0.5f, 0.008f));
            k.Box(new Vector3(-0.12f, -0.12f, 0.15f), new Vector3(0.09f, 0.1f, 0.012f));
            k.Box(new Vector3(0.12f, -0.12f, 0.15f), new Vector3(0.09f, 0.1f, 0.012f));
            k.Use(Slot.Accent).Box(new Vector3(0.1f, 0.33f, 0.142f), new Vector3(0.055f, 0.075f, 0.008f));
            k.Use(Slot.Pad).Box(new Vector3(0.1f, 0.34f, 0.146f), new Vector3(0.035f, 0.025f, 0.004f));
            k.Use(Slot.Metal).Tube(new Vector3(-0.1f, 0.3f, 0.142f), new Vector3(-0.1f, 0.4f, 0.142f), 0.006f, 0.006f, 5);
        }

        private static void MaintenanceExtra(MeshKit k)
        {
            // Reflective bands round chest and waist, tool belt with pouches, a wrench and a radio.
            k.Use(Slot.Accent);
            k.Loft(new[] { new Vector4(0.28f, 0.21f, 0.132f, 0.016f), new Vector4(0.32f, 0.214f, 0.134f, 0.017f) }, 12, false, false);
            k.Loft(new[] { new Vector4(0.1f, 0.172f, 0.115f, 0.004f), new Vector4(0.13f, 0.178f, 0.118f, 0.005f) }, 12, false, false);
            k.Use(Slot.Pack);
            k.Loft(new[] { new Vector4(-0.035f, 0.185f, 0.125f, 0f), new Vector4(0.015f, 0.185f, 0.125f, 0f) }, 12);
            for (int side = -1; side <= 1; side += 2)
            {
                k.Block(new Vector3(side * 0.17f, -0.12f, 0.03f), 0.13f, 0.06f, 0.09f, 0.065f, 0.095f);
                k.Block(new Vector3(side * 0.12f, -0.1f, 0.11f), 0.1f, 0.07f, 0.04f, 0.075f, 0.045f);
            }
            k.Use(Slot.Metal);
            k.Box(new Vector3(-0.19f, -0.18f, 0.08f), new Vector3(0.016f, 0.16f, 0.012f));
            k.Torus(new Vector3(-0.19f, -0.26f, 0.08f), Vector3.right, 0.02f, 0.008f, 6, 4);
            k.Use(Slot.Webbing).Box(new Vector3(0.14f, 0.36f, 0.135f), new Vector3(0.04f, 0.07f, 0.03f));
            k.Use(Slot.Webbing).Tube(new Vector3(0.155f, 0.4f, 0.135f), new Vector3(0.155f, 0.46f, 0.135f), 0.004f, 0.004f, 4);
        }

        private static void PierExtra(MeshKit k)
        {
            // Long slicker with storm flap and toggles, a coil of mooring line on the shoulder.
            k.Use(Slot.Jacket);
            Skirt(k, -0.42f, 0.07f, 0.02f);
            k.Use(Slot.JacketTrim);
            k.Box(new Vector3(0, 0.15f, 0.142f), new Vector3(0.05f, 0.7f, 0.014f));
            k.Use(Slot.Accent);
            for (int i = 0; i < 5; i++)
            {
                k.Box(new Vector3(0, 0.38f - i * 0.12f, 0.152f), new Vector3(0.06f, 0.016f, 0.012f));
            }
            k.Use(Slot.Rope).Torus(new Vector3(-0.06f, 0.36f, 0.0f), new Vector3(0.6f, 0.2f, 0.1f), 0.17f, 0.022f, 14, 5);
        }

        private static void ScavengerExtra(MeshKit k)
        {
            // Poncho over the shoulders, ragged hem, mismatched patches, a bottle and a pot hanging off it.
            k.Use(Slot.Jacket);
            k.Loft(new[]
            {
                new Vector4(-0.02f, 0.27f, 0.2f, -0.01f),
                new Vector4(0.2f, 0.25f, 0.18f, 0.0f),
                new Vector4(0.42f, 0.235f, 0.15f, 0.005f),
                new Vector4(0.51f, 0.16f, 0.11f, 0.0f),
                new Vector4(0.56f, 0.09f, 0.075f, -0.01f),
            }, 10, false, false);
            for (int i = 0; i < 10; i++)
            {
                float a = (i + 0.5f) * Mathf.PI * 2f / 10f;
                float len = 0.05f + 0.04f * Mathf.Abs(Mathf.Sin(i * 2.7f));
                Vector3 p = new Vector3(Mathf.Sin(a) * 0.26f, -0.02f, Mathf.Cos(a) * 0.19f - 0.01f);
                k.Block(p - new Vector3(0, len, 0), len, 0.03f, 0.02f, 0.12f, 0.03f);
            }
            k.Use(Slot.JacketTrim);
            k.Box(new Vector3(-0.12f, 0.3f, 0.2f), new Vector3(0.08f, 0.08f, 0.012f));
            k.Box(new Vector3(0.15f, 0.12f, -0.19f), new Vector3(0.09f, 0.07f, 0.012f));
            k.Use(Slot.Accent);
            k.Box(new Vector3(0.1f, 0.06f, 0.215f), new Vector3(0.07f, 0.09f, 0.012f));
            k.Tube(new Vector3(0.2f, -0.14f, 0.04f), new Vector3(0.2f, -0.02f, 0.04f), 0.03f, 0.02f, 6);
            k.Use(Slot.Metal).Tube(new Vector3(-0.22f, -0.08f, -0.02f), new Vector3(-0.22f, -0.02f, -0.02f), 0.05f, 0.05f, 7);
        }

        private static void ExpeditionExtra(MeshKit k)
        {
            // Bulky parka body to mid-thigh with a cinched waist cord and big bellows pockets.
            k.Use(Slot.Jacket);
            Skirt(k, -0.3f, 0.04f);
            k.Loft(new[]
            {
                new Vector4(0.0f, 0.2f, 0.135f, 0f),
                new Vector4(0.2f, 0.215f, 0.14f, 0.008f),
                new Vector4(0.36f, 0.232f, 0.145f, 0.014f),
                new Vector4(0.46f, 0.215f, 0.13f, 0.01f),
            }, 12, false, false);
            k.Use(Slot.JacketTrim);
            k.Loft(new[] { new Vector4(0.05f, 0.198f, 0.134f, 0f), new Vector4(0.075f, 0.2f, 0.136f, 0f) }, 12, false, false);
            k.Block(new Vector3(-0.11f, -0.2f, 0.135f), 0.13f, 0.1f, 0.04f, 0.1f, 0.04f);
            k.Block(new Vector3(0.11f, -0.2f, 0.135f), 0.13f, 0.1f, 0.04f, 0.1f, 0.04f);
            k.Use(Slot.Accent).Box(new Vector3(0, -0.29f, 0.14f), new Vector3(0.4f, 0.03f, 0.03f));
        }

        private static void InfestedExtra(MeshKit k)
        {
            // Zeta growth bursting out of the jacket: torn holes with lumpy masses swelling out of them,
            // glowing pustules, little spines, and veins creeping out across the cloth from each one.
            Growth(k, new Vector3(0.17f, 0.46f, -0.03f), 0.11f, 1);   // the big mass on the shoulder
            Growth(k, new Vector3(0.22f, 0.35f, 0.05f), 0.065f, 2);
            Growth(k, new Vector3(0.06f, 0.31f, -0.13f), 0.075f, 3);   // down the back
            Growth(k, new Vector3(-0.08f, 0.17f, -0.12f), 0.055f, 4);
            Growth(k, new Vector3(-0.15f, 0.05f, 0.08f), 0.05f, 5);    // the hip
            Growth(k, new Vector3(0.09f, -0.12f, 0.11f), 0.045f, 6);
            Growth(k, new Vector3(-0.12f, 0.38f, 0.1f), 0.04f, 7);     // starting on the chest
            k.Use(Slot.Flesh);
            Tendril(k, 0.9f, 0.45f, 0.1f, 0.3f, 0.014f, 1);
            Tendril(k, 1.1f, 0.4f, 1.9f, 0.15f, 0.013f, 2);
            Tendril(k, 0.7f, 0.42f, -0.5f, 0.42f, 0.011f, 3);
            Tendril(k, 1.4f, 0.34f, 2.4f, 0.4f, 0.012f, 4);
            Tendril(k, 2.7f, 0.3f, 3.6f, 0.12f, 0.012f, 5);
            Tendril(k, 2.6f, 0.28f, 2.2f, 0.02f, 0.01f, 6);
            Tendril(k, 3.7f, 0.16f, 4.5f, 0.06f, 0.011f, 7);
            Tendril(k, 4.9f, 0.05f, 5.6f, 0.3f, 0.01f, 8);
            Tendril(k, 5.3f, 0.37f, 6.1f, 0.24f, 0.009f, 9);
            Tendril(k, 0.6f, -0.1f, 1.2f, 0.12f, 0.01f, 10);
        }

        // The torso's outside, as rings of (height, x radius, z radius, z shift), for laying growth on it.
        private static readonly Vector4[] TorsoSkin =
        {
            new Vector4(-0.2f, 0.2f, 0.14f, -0.01f),
            new Vector4(0.05f, 0.182f, 0.12f, 0f),
            new Vector4(0.22f, 0.198f, 0.126f, 0.01f),
            new Vector4(0.34f, 0.22f, 0.134f, 0.016f),
            new Vector4(0.44f, 0.212f, 0.124f, 0.012f),
            new Vector4(0.52f, 0.16f, 0.1f, 0.004f),
        };

        /// <summary>A point on the torso's surface at <paramref name="angle"/> (0 = front, toward +x), lifted off it.</summary>
        private static Vector3 OnTorso(float angle, float y, float lift)
        {
            Vector4 ring = TorsoSkin[TorsoSkin.Length - 1];
            for (int r = 0; r < TorsoSkin.Length - 1; r++)
            {
                if (y >= TorsoSkin[r].x && y <= TorsoSkin[r + 1].x)
                {
                    ring = Vector4.Lerp(TorsoSkin[r], TorsoSkin[r + 1], (y - TorsoSkin[r].x) / (TorsoSkin[r + 1].x - TorsoSkin[r].x));
                    break;
                }
            }
            return new Vector3(Mathf.Sin(angle) * (ring.y + lift), y, Mathf.Cos(angle) * (ring.z + lift) + ring.w);
        }

        private static float Noise(int seed, int i) => Mathf.Sin(seed * 12.9898f + i * 78.233f) * 0.5f;

        /// <summary>A vein creeping over the torso from one spot to another, wandering a little and thinning out.</summary>
        private static void Tendril(MeshKit k, float a0, float y0, float a1, float y1, float radius, int seed)
        {
            const int Segments = 7;
            Vector3 prev = OnTorso(a0, y0, 0.006f);
            for (int i = 1; i <= Segments; i++)
            {
                float t = i / (float)Segments;
                float wobble = i < Segments ? Noise(seed, i) * 0.25f : 0f;
                Vector3 next = OnTorso(Mathf.Lerp(a0, a1, t) + wobble, Mathf.Lerp(y0, y1, t) + Noise(seed + 7, i) * 0.03f, 0.006f);
                k.Tube(prev, next, radius * (1f - (t - 1f / Segments) * 0.8f), radius * (1f - t * 0.8f), 4);
                if (i == 3)
                {
                    // One side branch, splitting off and fading quickly.
                    Vector3 branch = OnTorso(Mathf.Lerp(a0, a1, t) + 0.35f * Mathf.Sign(Noise(seed, 9) + 0.01f), Mathf.Lerp(y0, y1, t) - 0.06f, 0.006f);
                    k.Tube(next, branch, radius * 0.6f, radius * 0.2f, 4);
                }
                prev = next;
            }
        }

        /// <summary>
        /// One growth: a dark tear in the cloth, a lumpy mass of nodules swelling out of it, spines and
        /// glowing pustules set in fleshy rims.
        /// </summary>
        private static void Growth(MeshKit k, Vector3 center, float size, int seed)
        {
            Vector3 outward = new Vector3(center.x, 0f, center.z).normalized;
            Vector3 side = Vector3.Cross(Vector3.up, outward).normalized;
            k.Use(Slot.Visor).Ellipsoid(center - outward * (size * 0.35f), new Vector3(size * 1.15f, size * 1.15f, size * 1.15f) * 0.9f, 2, 8);
            k.Use(Slot.Flesh);
            k.Ellipsoid(center, new Vector3(size, size * 0.85f, size) * 0.95f, 4, 10);
            for (int i = 0; i < 6; i++)
            {
                float a = i * Mathf.PI * 2f / 6f + Noise(seed, i);
                Vector3 dir = (side * Mathf.Cos(a) + Vector3.up * Mathf.Sin(a)) * 0.75f + outward * (0.35f + Noise(seed, i + 3) * 0.3f);
                float r = size * (0.38f + Mathf.Abs(Noise(seed, i + 11)) * 0.3f);
                k.Ellipsoid(center + dir * size * 0.85f, new Vector3(r, r * 0.9f, r), 3, 7);
            }
            // Spines pushing out of the mass.
            for (int i = 0; i < 3; i++)
            {
                float a = i * 2.1f + seed;
                Vector3 dir = (outward * 0.8f + side * Mathf.Cos(a) * 0.5f + Vector3.up * Mathf.Sin(a) * 0.5f).normalized;
                Vector3 root = center + dir * size * 0.8f;
                k.Tube(root, root + dir * size * 0.55f, size * 0.12f, 0.002f, 4);
            }
            // Pustules: a fleshy rim round a glowing bead.
            for (int i = 0; i < 4; i++)
            {
                float a = i * 1.7f + seed * 0.9f;
                Vector3 dir = (outward * 0.9f + side * Mathf.Cos(a) * 0.6f + Vector3.up * Mathf.Sin(a) * 0.6f).normalized;
                Vector3 at = center + dir * size * 0.92f;
                float r = size * (0.16f + Mathf.Abs(Noise(seed, i + 20)) * 0.12f);
                k.Use(Slot.Flesh).Torus(at, dir, r * 1.1f, r * 0.45f, 7, 4);
                k.Use(Slot.Accent).Ellipsoid(at + dir * r * 0.2f, new Vector3(r, r, r), 2, 6);
            }
        }

        private static void DenizenExtra(MeshKit k)
        {
            // The jacket torn open over bare ribs, shredded cloth hanging off the hem.
            k.Use(Slot.Visor).Box(new Vector3(0, 0.29f, 0.122f), new Vector3(0.17f, 0.25f, 0.012f));
            k.Use(Slot.Flesh);
            for (int i = 0; i < 4; i++)
            {
                float y = 0.2f + i * 0.06f;
                float w = 0.15f - Mathf.Abs(i - 1.5f) * 0.012f;
                k.Block(new Vector3(0, y, 0.13f), 0.018f, w, 0.016f, w * 0.96f, 0.016f, 0.003f);
            }
            k.Box(new Vector3(0, 0.29f, 0.136f), new Vector3(0.025f, 0.27f, 0.016f));
            k.Use(Slot.Jacket);
            for (int i = 0; i < 9; i++)
            {
                float a = (i + 0.3f) * Mathf.PI * 2f / 9f;
                float len = 0.06f + 0.07f * Mathf.Abs(Mathf.Sin(i * 1.9f));
                Vector3 p = new Vector3(Mathf.Sin(a) * 0.18f, -0.0f, Mathf.Cos(a) * 0.12f);
                k.Block(p - new Vector3(0, len, 0), len, 0.02f, 0.015f, 0.07f, 0.02f);
            }
            k.Use(Slot.Accent);
            k.Ellipsoid(new Vector3(-0.1f, 0.12f, 0.125f), new Vector3(0.06f, 0.08f, 0.012f), 2, 6);
            k.Ellipsoid(new Vector3(0.13f, 0.4f, -0.1f), new Vector3(0.07f, 0.06f, 0.03f), 2, 6);
        }
    }
}
