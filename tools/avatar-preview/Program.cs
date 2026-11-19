using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Belay.Players.Avatar;
using UnityEngine;

/// Poses the climber in a few stances with the same proportions and IK idea the mod uses, and dumps the
/// triangles (with their material colors) for render.py. Only managed Vector3 math is used.
internal static class Program
{
    private struct Frame
    {
        public Vector3 Origin, Right, Up, Forward;
        public float ScaleZ;

        public Vector3 Apply(Vector3 p) => Origin + Right * p.x + Up * p.y + Forward * (p.z * ScaleZ);

        public static Frame Look(Vector3 origin, Vector3 forward, Vector3 up, float scaleZ = 1f)
        {
            Vector3 f = forward.normalized;
            Vector3 r = Vector3.Cross(up, f).normalized;
            if (r.sqrMagnitude < 1e-6f)
            {
                r = Vector3.right;
            }
            Vector3 u = Vector3.Cross(f, r);
            return new Frame { Origin = origin, Right = r, Up = u, Forward = f, ScaleZ = scaleZ };
        }

        public Vector3 Dir(Vector3 d) => Right * d.x + Up * d.y + Forward * d.z;
    }

    private static readonly List<string> Figures = new List<string>();
    private static StringBuilder tris;

    private static void Main(string[] args)
    {
        // Optional outfit colors: PREVIEW_COLORS="r,g,b;r,g,b;r,g,b;r,g,b" (jacket;headgear;trousers;pack, "-" to skip).
        Color?[] outfit = new Color?[4];
        string[] chosen = (Environment.GetEnvironmentVariable("PREVIEW_COLORS") ?? "").Split(';');
        for (int i = 0; i < 4 && i < chosen.Length; i++)
        {
            string[] c = chosen[i].Split(',');
            if (c.Length == 3)
            {
                outfit[i] = new Color(float.Parse(c[0], System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(c[1], System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(c[2], System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        // Optional lore look: LOOK=<index into LoreLooks.All>.
        int.TryParse(Environment.GetEnvironmentVariable("LOOK") ?? "0", out look);
        PixelTextures.Recipe[] recipes = LoreLooks.Recipes(look, new Color(1f, 0.45f, 0.2f), outfit[0], outfit[1], outfit[2], outfit[3]);
        var textures = new List<string>();
        for (int i = 0; i < recipes.Length; i++)
        {
            textures.Add(Tex(PixelTextures.Generate(recipes[i], 101 + i * 7), PixelTextures.Size, PixelTextures.Size));
        }

        // Optional hand skins: skins/manifest.txt lines "name|w|h|back.raw|palm.raw|palette.raw:width or -",
        // optionally followed by "|material|secondaryName|secondary.raw:w:h or -" for shader-only skins,
        // raw files being RGBA rows bottom-up (see README).
        var skinNames = new List<string>();
        string manifest = Environment.GetEnvironmentVariable("SKINS_DIR");
        if (manifest != null && File.Exists(Path.Combine(manifest, "manifest.txt")))
        {
            foreach (string line in File.ReadAllLines(Path.Combine(manifest, "manifest.txt")))
            {
                string[] f = line.Split('|');
                int w = int.Parse(f[1]), h = int.Parse(f[2]);
                Color32[] palette = null;
                if (f[5] != "-")
                {
                    string[] pf = f[5].Split(':');
                    palette = Raw(Path.Combine(manifest, pf[0]));
                    Array.Resize(ref palette, int.Parse(pf[1]));
                }
                // Optional shader effect: "|material|secondaryName|secondary.raw:w:h" (or "-" for no texture).
                SkinBaker.Effect effect = null;
                if (f.Length > 8)
                {
                    Color32[] sec = null;
                    int sw = 0, sh = 0;
                    if (f[8] != "-")
                    {
                        string[] sf = f[8].Split(':');
                        sec = Raw(Path.Combine(manifest, sf[0]));
                        sw = int.Parse(sf[1]);
                        sh = int.Parse(sf[2]);
                    }
                    effect = SkinBaker.Effect.For(f[6], f[7], sec, sw, sh);
                }
                SkinBaker.Result back = SkinBaker.Bake(Raw(Path.Combine(manifest, f[3])), w, h, palette, null, effect);
                SkinBaker.Result palm = SkinBaker.Bake(Raw(Path.Combine(manifest, f[4])), w, h, palette, null, effect);
                textures.Add(Tex(SkinBaker.Atlas(back, palm, false, 64, 96), 192, 96));
                textures.Add(Tex(SkinBaker.Atlas(back, palm, true, 64, 96), 192, 96));
                skinNames.Add(f[0]);
            }
        }

        // Optional held items: ITEMS_JSON from scripts/export_items.py (game models, never committed).
        string itemsJson = Environment.GetEnvironmentVariable("ITEMS_JSON");
        var itemFigures = new List<string>();
        if (itemsJson != null && File.Exists(itemsJson))
        {
            modelTexBase = textures.Count;
            PixelTextures.Recipe[] rapier = ItemModels.RapierRecipes();
            for (int i = 0; i < rapier.Length; i++)
            {
                textures.Add(Tex(PixelTextures.Generate(rapier[i], 301 + i * 7), PixelTextures.Size, PixelTextures.Size));
            }
            itemFigures = LoadItems(itemsJson, textures);
        }

        // TOTEM=1: the co-op disk totem on its own (world size), held by a climber, and its hand sprite.
        bool totem = Environment.GetEnvironmentVariable("TOTEM") == "1";
        if (totem)
        {
            totemTexBase = textures.Count;
            foreach (PixelTextures.Recipe r in Belay.Items.TotemLook.Recipes())
            {
                textures.Add(Tex(PixelTextures.Generate(r, 401 + (textures.Count - totemTexBase) * 7), PixelTextures.Size, PixelTextures.Size));
            }
            itemFigures.Add(TotemItem);
            Color32[] sprite = Belay.Items.TotemLook.DrawSprite();
            var raw = new byte[sprite.Length * 4];
            for (int i = 0; i < sprite.Length; i++)
            {
                raw[i * 4] = sprite[i].r; raw[i * 4 + 1] = sprite[i].g; raw[i * 4 + 2] = sprite[i].b; raw[i * 4 + 3] = sprite[i].a;
            }
            File.WriteAllBytes("totem-sprite.raw", raw);
        }

        boneTex = textures.Count;
        textures.Add(Tex(PixelTextures.Generate(Belay.Items.TotemLook.Recipes()[Belay.Items.TotemLook.BoneSlot], 501), PixelTextures.Size, PixelTextures.Size));
        coreTex = textures.Count;
        textures.Add(Tex(PixelTextures.Generate(Belay.Items.TotemLook.Recipes()[Belay.Items.TotemLook.GlowSlot], 502), PixelTextures.Size, PixelTextures.Size));
        // FLESH_RAW: the game's meat texture region (32x32 RGBA, rows bottom-up) cut from your own
        // install (never committed); otherwise the stand-in the game also falls back to.
        string fleshRaw = Environment.GetEnvironmentVariable("FLESH_RAW");
        Color32[] meat = fleshRaw != null && File.Exists(fleshRaw) ? Raw(fleshRaw) : PixelTextures.Generate(Reform.FallbackMeat, 611);
        int meatSize = (int)Math.Round(Math.Sqrt(meat.Length));
        Color32[] skinPixels = Reform.SkinFrom(meat);
        foreach (Reform.Layer layer in new[] { Reform.Layer.Muscle, Reform.Layer.Skin })
        {
            Color tint = Reform.LayerTint(layer);
            Color32[] source = layer == Reform.Layer.Skin ? skinPixels : meat;
            var tinted = new Color32[meat.Length];
            for (int i = 0; i < meat.Length; i++)
            {
                tinted[i] = new Color32((byte)(source[i].r * tint.r), (byte)(source[i].g * tint.g), (byte)(source[i].b * tint.b), 255);
            }
            if (layer == Reform.Layer.Muscle) muscleTex = textures.Count; else skinTex = textures.Count;
            textures.Add(Tex(tinted, meatSize, meatSize));
        }

        // MODEL: a game model exported for the revive preview (see README), with REFORM times.
        string modelPath = Environment.GetEnvironmentVariable("MODEL");
        if (!string.IsNullOrEmpty(modelPath))
        {
            LoadModel(modelPath, textures, meat, meatSize);
        }

        var sb = new StringBuilder("{\"size\":" + PixelTextures.Size + ",\"textures\":[");
        sb.Append(string.Join(",", textures)).Append("],\"skins\":[");
        sb.Append(string.Join(",", skinNames.ConvertAll(n => "\"" + n + "\""))).Append("],");
        sb.Append("\"emissive\":[" + coreTex + (totem ? "," + (totemTexBase + Belay.Items.TotemLook.GlowSlot) : "") + "],");
        sb.Append("\"figures\":{");

        Figure("standing", standing: true, leftGrip: false, rightGrip: false,
            new Vector3(-0.22f, 1.25f, 0.45f), new Vector3(0.22f, 1.25f, 0.45f));
        Figure("hanging", standing: false, leftGrip: true, rightGrip: false,
            new Vector3(-0.2f, 1.95f, 0.32f), new Vector3(0.38f, 1.55f, 0.4f));
        Figure("crouching", standing: true, leftGrip: false, rightGrip: false,
            new Vector3(-0.22f, 0.55f, 0.6f), new Vector3(0.22f, 0.55f, 0.6f), crouch: true);
        string[] headNames = { "helmet", "beanie", "hood", "balaclava" };
        for (int style = 1; style < AvatarDesign.HeadStyles; style++)
        {
            headStyle = style;
            Figure(headNames[style], standing: true, leftGrip: false, rightGrip: false,
                new Vector3(-0.22f, 1.25f, 0.45f), new Vector3(0.22f, 1.25f, 0.45f));
        }
        headStyle = 0;
        Figure("inventory", standing: true, leftGrip: true, rightGrip: true, Vector3.zero, Vector3.zero, inventory: true);

        foreach (string item in itemFigures)
        {
            // Left hand held out in front (as when using it), right hand relaxed at the side.
            Figure("hold:" + item, standing: true, leftGrip: false, rightGrip: false,
                new Vector3(-0.21f, 1.38f, 0.5f), new Vector3(0.07f + AvatarDesign.ShoulderWidth, AvatarDesign.PelvisHeight + AvatarDesign.ShoulderHeight - 0.52f, 0.1f),
                leftItem: item, rightItem: item);
        }

        foreach (string item in itemFigures)
        {
            // Close-up: a right fist, forearm pointing forward (+z), back of the hand out to the right.
            tris = new StringBuilder();
            Frame palm = Frame.Look(Vector3.zero, Vector3.forward, Vector3.right);
            Emit(k => AvatarDesign.BuildForearm(k), Frame.Look(new Vector3(0, 0, -AvatarDesign.Forearm), Vector3.forward, Vector3.right, AvatarDesign.Forearm));
            Emit(k => AvatarDesign.BuildFist(k, false), palm);
            EmitItem(item, palm, false);
            Figures.Add("\"grip:" + item + "\":[" + tris.ToString().TrimEnd(',') + "]");
        }

        // STEP: a hanging climber bringing the left knee up, at these amounts (0 leg down .. 1 high step).
        string steps = Environment.GetEnvironmentVariable("STEP");
        if (!string.IsNullOrEmpty(steps))
        {
            foreach (string part in steps.Split(','))
            {
                Figure("step:" + part, standing: false, leftGrip: true, rightGrip: true,
                    new Vector3(-0.2f, 1.95f, 0.32f), new Vector3(0.22f, 1.9f, 0.34f), step: float.Parse(part, CultureInfo.InvariantCulture));
            }
        }

        string reform = Environment.GetEnvironmentVariable("REFORM");
        if (!string.IsNullOrEmpty(reform))
        {
            foreach (string part in reform.Split(','))
            {
                reformT = float.Parse(part, CultureInfo.InvariantCulture);
                reformSeed = 0;
                Figure("reform:" + part, standing: true, leftGrip: false, rightGrip: false,
                    new Vector3(-0.22f, 1.25f, 0.45f), new Vector3(0.22f, 1.25f, 0.45f));
                if (gameModel != null)
                {
                    ModelFigure("model:" + part, reformT);
                }
            }
            reformT = -1f;
        }

        if (totem)
        {
            // Standing on the floor at world size.
            tris = new StringBuilder();
            var kit = new MeshKit(Belay.Items.TotemLook.SlotCount);
            Belay.Items.TotemLook.Build(kit, Vector3.zero, new Vector3(0, 1, 0), new Vector3(0, 0, 1));
            EmitKit(kit, totemTexBase);
            Figures.Add("\"totem\":[" + tris.ToString().TrimEnd(',') + "]");
        }

        if (skinNames.Count > 0)
        {
            SkinFigure(skinNames.Count);
        }
        sb.Append(string.Join(",", Figures)).Append("}}");
        File.WriteAllText(args.Length > 0 ? args[0] : "preview.json", sb.ToString());
        Console.WriteLine("wrote preview.json");
    }

    private static void Figure(string name, bool standing, bool leftGrip, bool rightGrip, Vector3 leftHand, Vector3 rightHand,
        bool inventory = false, bool crouch = false, string leftItem = null, string rightItem = null, float step = -1f)
    {
        tris = new StringBuilder();
        // Crouched: same offsets as ClimberAvatar, in the design's unscaled units.
        float c = crouch ? 1f : 0f;
        Vector3 pelvis = new Vector3(0, AvatarDesign.PelvisHeight - 0.5f / 0.83f * c, -0.12f / 0.83f * c);
        float lean = (standing ? 4f : -6f) + 60f * c;
        float rad = lean * Mathf.Deg2Rad;
        Frame torso = Frame.Look(pelvis, new Vector3(0, -Mathf.Sin(rad), Mathf.Cos(rad)), Vector3.up);
        Emit(AvatarDesign.BuildTorso, torso, Reform.Part.Torso);

        // Backpack on the back, or held open in front (same placement as ClimberAvatar).
        Frame pack;
        if (inventory)
        {
            float tilt = -30f * Mathf.Deg2Rad;
            pack = Frame.Look(torso.Apply(new Vector3(0, 0.26f, 0.44f)),
                torso.Dir(new Vector3(0, Mathf.Sin(-tilt), Mathf.Cos(tilt))), torso.Dir(new Vector3(0, Mathf.Cos(tilt), -Mathf.Sin(-tilt))));
            leftHand = pack.Apply(new Vector3(0.17f, -0.03f, 0.07f));
            rightHand = pack.Apply(new Vector3(-0.17f, -0.03f, 0.07f));
        }
        else
        {
            pack = Frame.Look(torso.Apply(AvatarDesign.PackOnBack), -torso.Forward, torso.Up);
        }
        Emit(AvatarDesign.BuildPack, pack, Reform.Part.Gear);
        float open = (inventory ? 115f : 0f) * Mathf.Deg2Rad;
        Emit(AvatarDesign.BuildPackLid, Frame.Look(pack.Apply(AvatarDesign.PackLidHinge),
            pack.Dir(new Vector3(0, Mathf.Sin(open), Mathf.Cos(open))), pack.Dir(new Vector3(0, Mathf.Cos(open), -Mathf.Sin(open)))), Reform.Part.Gear);

        Vector3 neck = torso.Apply(new Vector3(0, AvatarDesign.NeckHeight, 0.01f));
        float pitch = (inventory ? 40f : standing ? 10f : -25f) * Mathf.Deg2Rad;
        Emit(k => { if (LoreLooks.Valid(look)) LoreLooks.BuildHead(k, look); else AvatarDesign.BuildHead(k, headStyle); }, Frame.Look(neck, new Vector3(0, -Mathf.Sin(pitch), Mathf.Cos(pitch)), Vector3.up), Reform.Part.Head);

        Arm(torso, -1, leftHand, leftGrip, leftItem);
        Arm(torso, 1, rightHand, rightGrip, rightItem);

        float legLength = AvatarDesign.Thigh + AvatarDesign.Shin;
        var legs = new List<CoatSkirt.Capsule>();
        Vector3 InTorso(Vector3 p) => new Vector3(Vector3.Dot(p - torso.Origin, torso.Right), Vector3.Dot(p - torso.Origin, torso.Up), Vector3.Dot(p - torso.Origin, torso.Forward));
        for (int side = -1; side <= 1; side += 2)
        {
            Vector3 hip = pelvis + new Vector3(side * AvatarDesign.HipWidth, -0.07f, 0);
            Vector3 foot = standing
                ? new Vector3(hip.x, AvatarDesign.AnkleHeight, (side < 0 ? 0.12f : -0.1f) * (1f - c) + hip.z - 0.3f / 0.83f * c)
                : step >= 0f && side < 0 ? hip + Vector3.down * (legLength * Mathf.Lerp(0.95f, 0.55f, step)) + new Vector3(0.04f, 0, Mathf.Lerp(0.05f, 0.36f, step))
                : (side < 0 ? hip + Vector3.down * (legLength * 0.78f) + new Vector3(0.04f, 0, 0.22f)
                            : hip + Vector3.down * (legLength * 0.95f) + new Vector3(0.03f, 0, 0.05f));
            Vector3 knee = hip;
            Vector3 ankle = Limb(hip, foot, hip + new Vector3(side * 0.1f, -0.4f * c, 0.6f), AvatarDesign.Thigh, AvatarDesign.Shin,
                AvatarDesign.BuildThigh, AvatarDesign.BuildShin, false, m => knee = m);
            legs.Add(new CoatSkirt.Capsule { A = InTorso(hip), B = InTorso(knee), Radius = CoatSkirt.ThighRadius });
            legs.Add(new CoatSkirt.Capsule { A = InTorso(knee), B = InTorso(ankle), Radius = CoatSkirt.ShinRadius });
            float toe = standing ? 0f : 35f * Mathf.Deg2Rad;
            Emit(AvatarDesign.BuildBoot, Frame.Look(ankle, new Vector3(0, -Mathf.Sin(toe), Mathf.Cos(toe)), Vector3.up), Reform.Part.Boot);
        }
        if (LoreLooks.Valid(look))
        {
            // The look's extras; a coat skirt settles out of the legs' way (COAT=0 shows it unbent, for comparison).
            CoatSkirt coat = LoreLooks.SkirtShape(look, out float hem, out float flare, out float back) && Environment.GetEnvironmentVariable("COAT") != "0"
                ? new CoatSkirt(hem, flare, back) : null;
            coat?.Settle(legs);
            Emit(k =>
            {
                LoreLooks.BuildExtra(k, look);
                if (coat != null)
                {
                    var bent = new Vector3[k.Vertices.Count];
                    coat.Deform(k.Vertices, bent);
                    for (int i = 0; i < bent.Length; i++) k.Vertices[i] = bent[i];
                }
            }, torso, Reform.Part.Torso, clothesOnly: true);
        }
        Figures.Add("\"" + name + "\":[" + tris.ToString().TrimEnd(',') + "]");
    }

    private static void Arm(Frame torso, int side, Vector3 hand, bool grip, string item = null)
    {
        Vector3 shoulder = torso.Apply(new Vector3(side * AvatarDesign.ShoulderWidth, AvatarDesign.ShoulderHeight - 0.02f, 0));
        Vector3 dir = (hand - shoulder).normalized;
        Vector3 wrist = hand - dir * (grip ? 0.09f : 0.05f);
        Vector3 pole = shoulder + torso.Dir(new Vector3(side * 0.6f, -0.6f, -0.35f));
        Vector3 elbow = Vector3.zero;
        Vector3 end = Limb(shoulder, wrist, pole, AvatarDesign.UpperArm, AvatarDesign.Forearm,
            AvatarDesign.BuildUpperArm, AvatarDesign.BuildForearm, true, e => elbow = e);
        Vector3 forearm = (end - elbow).normalized;
        Vector3 up = torso.Up;
        if (item != null && !OldGrip)
        {
            // Held out in front (not hanging at the side): guns point where the player looks.
            forearm = HeldGrip.HandForward(forearm, new Vector3(0, 0, 1), item, hand.y > 1.1f ? 1f : 0f);
        }
        Vector3 back = HeldGrip.HandBack(forearm, up, torso.Forward, torso.Right, side, item != null && !OldGrip);
        bool left = side < 0;
        bool fist = grip || item != null;
        Frame palm = Frame.Look(wrist, forearm, back);
        Emit(k => { if (fist) AvatarDesign.BuildFist(k, left); else AvatarDesign.BuildOpenHand(k, left); }, palm, Reform.Part.Hand);
        if (item != null)
        {
            EmitItem(item, palm, left);
        }
    }

    private static Vector3 Limb(Vector3 start, Vector3 target, Vector3 pole, float a, float b,
        Action<MeshKit> upper, Action<MeshKit> lower, bool stretch, Action<Vector3> joint = null)
    {
        Vector3 to = target - start;
        float d = to.magnitude;
        Vector3 dir = to / d;
        float reach = (a + b) * 0.999f, scale = 1f;
        if (d > reach)
        {
            if (stretch) scale = Mathf.Min(d / reach, 3f);
            d = reach;
        }
        float cos = Mathf.Clamp((a * a + d * d - b * b) / (2 * a * d), -1, 1);
        float sin = Mathf.Sqrt(1 - cos * cos);
        Vector3 bend = Vector3.ProjectOnPlane(pole - start, dir).normalized;
        Vector3 mid = start + (dir * cos + bend * sin) * a * scale;
        Vector3 end = start + dir * d * scale;
        joint?.Invoke(mid);
        Emit(upper, Frame.Look(start, mid - start, bend, (mid - start).magnitude), Reform.Part.UpperLimb);
        Emit(lower, Frame.Look(mid, end - mid, bend, (end - mid).magnitude), Reform.Part.LowerLimb);
        return end;
    }

    private static int headStyle;
    private static int look;
    private static int gloveSlot = Slot.Glove;

    /// <summary>Each skin on a left open hand, left fist, right fist and right open hand, in a row.</summary>
    private static void SkinFigure(int count)
    {
        tris = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            // Three skins per row.
            int columns = Math.Min(3, count), rows = (count + columns - 1) / columns;
            float x = (i % columns - (columns - 1) * 0.5f) * 1.0f;
            float y = ((rows - 1) * 0.5f - i / columns) * 0.42f;
            // Fingers up, back of the hand toward the camera (which looks along +z from -z).
            for (int j = 0; j < 4; j++)
            {
                bool left = j < 2;
                bool fist = j == 1 || j == 2;
                gloveSlot = Slot.Count + i * 2 + (left ? 0 : 1);
                Frame f = Frame.Look(new Vector3(x + (j - 1.5f) * 0.23f, y + (fist ? 0.04f : 0f), 0), Vector3.up, new Vector3(0, 0, -1));
                Emit(k => { if (fist) AvatarDesign.BuildFist(k, left); else AvatarDesign.BuildOpenHand(k, left); }, f);
            }
        }
        gloveSlot = Slot.Glove;
        Figures.Add("\"skins\":[" + tris.ToString().TrimEnd(',') + "]");
    }

    private static Color32[] Raw(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        var px = new Color32[b.Length / 4];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Color32(b[i * 4], b[i * 4 + 1], b[i * 4 + 2], b[i * 4 + 3]);
        }
        return px;
    }

    private static string Tex(Color32[] px, int w, int h)
    {
        var bytes = new byte[px.Length * 4];
        for (int j = 0; j < px.Length; j++)
        {
            bytes[j * 4] = px[j].r; bytes[j * 4 + 1] = px[j].g; bytes[j * 4 + 2] = px[j].b; bytes[j * 4 + 3] = 255;
        }
        return "{\"w\":" + w + ",\"h\":" + h + ",\"data\":\"" + Convert.ToBase64String(bytes) + "\"}";
    }

    /// <summary>REFORM: seconds into the totem revive for the figure being built (-1 off).</summary>
    private static float reformT = -1f;
    private static int boneTex, coreTex, muscleTex, skinTex, forceTex = -1;

    private static void Emit(Action<MeshKit> build, Frame frame, Reform.Part? part, bool clothesOnly = false)
    {
        if (reformT < 0f || part == null)
        {
            Emit(build, frame);
            return;
        }
        Reform.Part kind = part.Value;
        int seed = ++reformSeed;
        // Clothes and gear spread over the skin, patch by patch.
        float clothes = Reform.Progress(kind, Reform.Layer.Clothes, reformT);
        if (clothes > 0f)
        {
            Emit(k => { build(k); Grow(k, kind, clothes, seed); }, frame);
        }
        if (clothesOnly)
        {
            return;
        }
        // Skin: the part's own shape, just inside the clothes, spreading the same way.
        if (Reform.LayerShows(kind, Reform.Layer.Skin, reformT))
        {
            float skin = Reform.Progress(kind, Reform.Layer.Skin, reformT);
            forceTex = skinTex;
            Emit(k => { build(k); Grow(k, kind, skin, seed + 1000); Squash(k, Reform.SkinScale(kind)); }, frame);
            forceTex = -1;
        }
        // Muscle: strands knitting over the bones.
        if (Reform.LayerShows(kind, Reform.Layer.Muscle, reformT))
        {
            float muscle = Reform.Progress(kind, Reform.Layer.Muscle, reformT);
            forceTex = muscleTex;
            Emit(k => { var tmp = new MeshKit(1) { TileSize = 0.12f }; Reform.BuildMuscle(tmp, kind, muscle, seed); Copy(tmp, k, 0); }, frame);
            forceTex = -1;
        }
        if (kind != Reform.Part.Gear && Reform.BoneShows(kind, reformT))
        {
            Vector3 bs = Reform.BoneScale(kind, reformT);
            gloveSlot = boneTex; // reuse the glove remap to point slot 0 at the bone texture
            Emit(k => { k.Use(Slot.Glove); var tmp = new MeshKit(1); Reform.BuildBone(tmp, kind); Copy(tmp, k, Slot.Glove); Squash(k, bs); }, frame);
            gloveSlot = Slot.Glove;
        }
    }

    // ------------------------------------------------------------------ Game models (MODEL)

    private class ModelPart
    {
        public Vector3[] V, N;
        public Vector2[] UV;
        public List<int> Tris = new List<int>();
        public List<int> TriTex = new List<int>();
        public float[] Keys, SkinKeys;
        public Vector3[] Inset;
    }

    private class GameModel
    {
        public List<ModelPart> Parts = new List<ModelPart>();
        public Vector3[] Bones;
        public int[] Parent;
        public List<ReformModel.Segment> Segments;
        public bool Roachy;
        public int MuscleTex, SkinTex, BoneTex;
    }

    private static GameModel gameModel;

    private static void LoadModel(string path, List<string> textures, Color32[] meat, int meatSize)
    {
        var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        var model = new GameModel { Roachy = ReformModel.Roachy(Environment.GetEnvironmentVariable("MODEL_KIND")) };
        var texIndex = new Dictionary<string, int>();
        foreach (var t in doc.GetProperty("textures").EnumerateObject())
        {
            texIndex[t.Name] = textures.Count;
            textures.Add("{\"w\":" + t.Value.GetProperty("w").GetInt32() + ",\"h\":" + t.Value.GetProperty("h").GetInt32() + ",\"data\":\"" + t.Value.GetProperty("data").GetString() + "\"}");
        }
        if (model.Roachy)
        {
            model.MuscleTex = textures.Count;
            textures.Add(Tex(Reform.RoachMeatFrom(meat), meatSize, meatSize));
            model.SkinTex = textures.Count;
            textures.Add(Tex(Reform.RoachShellFrom(meat), meatSize, meatSize));
            Color32[] bone = PixelTextures.Generate(Belay.Items.TotemLook.Recipes()[Belay.Items.TotemLook.BoneSlot], 501);
            Color tint = Reform.RoachBoneTint;
            for (int i = 0; i < bone.Length; i++)
            {
                bone[i] = new Color32((byte)(bone[i].r * tint.r), (byte)(bone[i].g * tint.g), (byte)(bone[i].b * tint.b), 255);
            }
            model.BoneTex = textures.Count;
            textures.Add(Tex(bone, PixelTextures.Size, PixelTextures.Size));
        }
        else
        {
            model.MuscleTex = muscleTex;
            model.SkinTex = skinTex;
            model.BoneTex = boneTex;
        }
        // Bones of every renderer, joined by id.
        var ids = new List<string>();
        var parentIds = new List<string>();
        var positions = new List<Vector3>();
        var boneNames = new List<string>();
        foreach (var r in doc.GetProperty("renderers").EnumerateArray())
        {
            foreach (var b in r.GetProperty("bones").EnumerateArray())
            {
                if (b.ValueKind == System.Text.Json.JsonValueKind.Null) continue;
                string id = b.GetProperty("id").GetString();
                if (ids.Contains(id)) continue;
                ids.Add(id);
                parentIds.Add(b.GetProperty("parent").GetString());
                boneNames.Add(b.GetProperty("name").GetString());
                var p = b.GetProperty("pos");
                positions.Add(new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle()));
            }
        }
        model.Bones = positions.ToArray();
        model.Parent = new int[ids.Count];
        for (int i = 0; i < ids.Count; i++)
        {
            // A bone's parent may be a helper outside the rig: walk to the nearest one that's in it.
            model.Parent[i] = ids.IndexOf(parentIds[i]);
        }
        var all = new List<Vector3>();
        int seed = 0;
        foreach (var r in doc.GetProperty("renderers").EnumerateArray())
        {
            var part = new ModelPart();
            float[] v = Floats(r.GetProperty("v")), n = Floats(r.GetProperty("n")), uv = Floats(r.GetProperty("uv"));
            int count = v.Length / 3;
            part.V = new Vector3[count];
            part.N = new Vector3[count];
            part.UV = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                part.V[i] = new Vector3(v[i * 3], v[i * 3 + 1], v[i * 3 + 2]);
                part.N[i] = i * 3 + 2 < n.Length ? new Vector3(n[i * 3], n[i * 3 + 1], n[i * 3 + 2]) : Vector3.up;
                part.UV[i] = i * 2 + 1 < uv.Length ? new Vector2(uv[i * 2], uv[i * 2 + 1]) : Vector2.zero;
            }
            foreach (var sub in r.GetProperty("subs").EnumerateArray())
            {
                string tex = sub.GetProperty("tex").ValueKind == System.Text.Json.JsonValueKind.String ? sub.GetProperty("tex").GetString() : null;
                int ti = tex != null && texIndex.TryGetValue(tex, out int found) ? found : model.SkinTex;
                foreach (var x in sub.GetProperty("tris").EnumerateArray())
                {
                    part.Tris.Add(x.GetInt32());
                }
                while (part.TriTex.Count < part.Tris.Count / 3) part.TriTex.Add(ti);
            }
            all.AddRange(part.V);
            model.Parts.Add(part);
        }
        model.Segments = ReformModel.Segments(model.Bones, model.Parent, all, boneNames);
        float inset = ReformModel.InsetFor(model.Segments);
        foreach (ModelPart part in model.Parts)
        {
            part.Keys = ReformModel.SpreadKeys(part.V, part.Tris, model.Bones, model.Parent, 31 + seed);
            part.SkinKeys = ReformModel.SpreadKeys(part.V, part.Tris, model.Bones, model.Parent, 977 + seed);
            part.Inset = ReformModel.Inset(part.V, part.N, inset);
            seed++;
        }
        Console.WriteLine("model: " + ids.Count + " bones, " + model.Segments.Count + " segments, inset " + inset);
        gameModel = model;
    }

    private static float[] Floats(System.Text.Json.JsonElement e)
    {
        var list = new List<float>();
        foreach (var x in e.EnumerateArray()) list.Add(x.GetSingle());
        return list.ToArray();
    }

    private static void ModelFigure(string name, float t)
    {
        tris = new StringBuilder();
        GameModel m = gameModel;
        float clothes = Reform.WholeProgress(Reform.Layer.Clothes, t);
        float skin = Reform.WholeProgress(Reform.Layer.Skin, t);
        foreach (ModelPart part in m.Parts)
        {
            if (clothes > 0f)
            {
                var grown = new Vector3[part.V.Length];
                Reform.Spread(part.V, part.Tris, part.Keys, clothes, grown);
                EmitTris(grown, part, -1);
            }
            if (ReformModel.SkinShows(t))
            {
                var grown = new Vector3[part.V.Length];
                Reform.Spread(part.Inset, part.Tris, part.SkinKeys, skin, grown);
                EmitTris(grown, part, m.SkinTex);
            }
        }
        foreach (ReformModel.Segment seg in m.Segments)
        {
            Vector3 a = m.Bones[seg.From], b = m.Bones[seg.To];
            Vector3 scale = ReformModel.SegmentScale(seg.Radius, (b - a).magnitude);
            Vector3 up = Mathf.Abs(Vector3.Dot((b - a).normalized, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up;
            Frame frame = Frame.Look(a, b - a, up, scale.z);
            if (ReformModel.BoneShows(seg.Stage, t))
            {
                var kit = new MeshKit(1) { TileSize = 0.12f };
                Reform.BuildBone(kit, Reform.Part.UpperLimb);
                EmitSegment(kit, frame, scale, m.BoneTex, Reform.BoneAt(seg.Stage, t));
            }
            if (ReformModel.MuscleShows(seg.Stage, t))
            {
                var kit = new MeshKit(1) { TileSize = 0.12f };
                Reform.BuildMuscle(kit, Reform.Part.UpperLimb, Reform.ProgressAt(seg.Stage, Reform.Layer.Muscle, t), seg.Stage * 7 + 3);
                EmitSegment(kit, frame, scale, m.MuscleTex, 1f);
            }
        }
        Figures.Add("\"" + name + "\":[" + tris.ToString().TrimEnd(',') + "]");
    }

    private static void EmitTris(Vector3[] positions, ModelPart part, int tex)
    {
        for (int i = 0; i < part.Tris.Count; i += 3)
        {
            tris.Append('[').Append(tex >= 0 ? tex : part.TriTex[i / 3]);
            for (int j = 0; j < 3; j++)
            {
                int v = part.Tris[i + j];
                Vector3 p = positions[v], n = part.N[v];
                Vector2 uv = part.UV[v];
                foreach (float f in new[] { p.x, p.y, p.z, n.x, n.y, n.z, uv.x, uv.y })
                {
                    tris.Append(',').Append(F(f));
                }
            }
            tris.Append("],");
        }
    }

    private static void EmitSegment(MeshKit kit, Frame frame, Vector3 scale, int tex, float grow)
    {
        List<int> list = kit.Submeshes[0];
        for (int i = 0; i < list.Count; i += 3)
        {
            tris.Append('[').Append(tex);
            for (int j = 0; j < 3; j++)
            {
                int v = list[i + j];
                Vector3 q = kit.Vertices[v];
                Vector3 p = frame.Apply(new Vector3(q.x * scale.x, q.y * scale.y, q.z * grow));
                Vector3 n = frame.Dir(kit.Normals[v]).normalized;
                Vector2 uv = kit.UVs[v];
                foreach (float f in new[] { p.x, p.y, p.z, n.x, n.y, n.z, uv.x, uv.y })
                {
                    tris.Append(',').Append(F(f));
                }
            }
            tris.Append("],");
        }
    }

    /// <summary>Counts the parts of each figure, so each one spreads in its own pattern.</summary>
    private static int reformSeed;

    private static void Grow(MeshKit k, Reform.Part kind, float progress, int seed)
    {
        var tris = new List<int>();
        foreach (List<int> sub in k.Submeshes) tris.AddRange(sub);
        float[] keys = Reform.SpreadKeys(k.Vertices, tris, kind, seed);
        var output = k.Vertices.ToArray();
        Reform.Spread(k.Vertices, tris, keys, progress, output);
        for (int i = 0; i < output.Length; i++) k.Vertices[i] = output[i];
    }

    private static void Squash(MeshKit k, Vector3 s)
    {
        for (int i = 0; i < k.Vertices.Count; i++)
        {
            k.Vertices[i] = Vector3.Scale(k.Vertices[i], s);
        }
    }

    private static void Copy(MeshKit from, MeshKit to, int slot)
    {
        int b = to.Vertices.Count;
        to.Vertices.AddRange(from.Vertices);
        to.Normals.AddRange(from.Normals);
        to.UVs.AddRange(from.UVs);
        foreach (int i in from.Submeshes[0]) to.Submeshes[slot].Add(b + i);
    }

    private static void Emit(Action<MeshKit> build, Frame frame)
    {
        var kit = new MeshKit(Slot.Count);
        build(kit);
        for (int s = 0; s < kit.Submeshes.Length; s++)
        {
            List<int> list = kit.Submeshes[s];
            for (int i = 0; i < list.Count; i += 3)
            {
                tris.Append('[').Append(forceTex >= 0 ? forceTex : s == Slot.Glove ? gloveSlot : s);
                for (int j = 0; j < 3; j++)
                {
                    int v = list[i + j];
                    // Unity is left-handed (x right, y up, z forward); render.js mirrors z.
                    Vector3 p = frame.Apply(kit.Vertices[v]);
                    Vector3 n = kit.Normals[v];
                    n = frame.Dir(new Vector3(n.x, n.y, n.z / frame.ScaleZ)).normalized;
                    Vector2 uv = kit.UVs[v];
                    foreach (float f in new[] { p.x, p.y, p.z, n.x, n.y, n.z, uv.x, uv.y })
                    {
                        tris.Append(',').Append(F(f));
                    }
                }
                tris.Append("],");
            }
        }
    }

    private class ItemMesh
    {
        public List<int> Tex = new List<int>();
        public List<float[]> Tris = new List<float[]>();
        public Matrix4x4 Fit;
        public float Length;
    }

    private static readonly Dictionary<string, ItemMesh> Items = new Dictionary<string, ItemMesh>();

    /// <summary>Reads exported item models (triangles in the item's root space) and fits each like HeldItems does.</summary>
    private static List<string> LoadItems(string path, List<string> textures)
    {
        var names = new List<string>();
        var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        int texBase = textures.Count;
        foreach (var t in doc.RootElement.GetProperty("textures").EnumerateArray())
        {
            textures.Add("{\"w\":" + t.GetProperty("w").GetInt32() + ",\"h\":" + t.GetProperty("h").GetInt32() + ",\"data\":\"" + t.GetProperty("data").GetString() + "\"}");
        }
        string only = Environment.GetEnvironmentVariable("ITEMS");
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateObject())
        {
            if (only != null && Array.IndexOf(only.Split(','), item.Name) < 0)
            {
                continue;
            }
            var mesh = new ItemMesh();
            Vector3 min = Vector3.one * 1e9f, max = Vector3.one * -1e9f, sum = Vector3.zero;
            int count = 0;
            foreach (var sub in item.Value.EnumerateArray())
            {
                int tex = texBase + sub.GetProperty("tex").GetInt32();
                var f = new List<float>();
                foreach (var x in sub.GetProperty("tris").EnumerateArray()) f.Add(x.GetSingle());
                for (int i = 0; i + 15 <= f.Count; i += 15)
                {
                    mesh.Tex.Add(tex);
                    mesh.Tris.Add(f.GetRange(i, 15).ToArray());
                    for (int j = 0; j < 3; j++)
                    {
                        var p = new Vector3(f[i + j * 5], f[i + j * 5 + 1], f[i + j * 5 + 2]);
                        min = Vector3.Min(min, p); max = Vector3.Max(max, p); sum += p; count++;
                    }
                }
            }
            if (count == 0) continue;
            Vector3 size = max - min;
            mesh.Length = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            mesh.Fit = OldGrip ? HeldGrip.FitByShape(min, max, sum / count) : HeldGrip.Fit(min, max, sum / count, item.Name);
            Items[item.Name] = mesh;
            names.Add(item.Name);
        }
        return names;
    }

    private static int modelTexBase;

    /// <summary>OLD=1: the grip before 0.7.35 (real world size, no tilt, back of the hand up), for comparisons.</summary>
    private static readonly bool OldGrip = Environment.GetEnvironmentVariable("OLD") == "1";

    private static Matrix4x4 OldInFist(bool left)
    {
        var m = Matrix4x4.identity;
        float s = 1f / 0.83f, t = left ? 1f : -1f;
        m.SetColumn(0, new Vector4(t * s, 0, 0, 0));
        m.SetColumn(1, new Vector4(0, t * s, 0, 0));
        m.SetColumn(2, new Vector4(0, 0, s, 0));
        m.SetColumn(3, new Vector4(0, -0.035f, 0.11f, 1));
        return m;
    }

    private const string TotemItem = "Belay_Totem";
    private static int totemTexBase;

    /// <summary>Triangles of a kit built in place (no frame), with texture slots from <paramref name="texBase"/>.</summary>
    private static void EmitKit(MeshKit kit, int texBase)
    {
        for (int s = 0; s < kit.Submeshes.Length; s++)
        {
            List<int> list = kit.Submeshes[s];
            for (int i = 0; i < list.Count; i += 3)
            {
                tris.Append('[').Append(texBase + s);
                for (int j = 0; j < 3; j++)
                {
                    int v = list[i + j];
                    Vector3 p = kit.Vertices[v], n = kit.Normals[v];
                    Vector2 uv = kit.UVs[v];
                    foreach (float f in new[] { p.x, p.y, p.z, n.x, n.y, n.z, uv.x, uv.y })
                    {
                        tris.Append(',').Append(F(f));
                    }
                }
                tris.Append("],");
            }
        }
    }

    private static void EmitItem(string name, Frame hand, bool left)
    {
        Items.TryGetValue(name, out ItemMesh mesh);
        if (ItemModels.HasModel(name) && !OldGrip)
        {
            bool isTotem = ItemModels.IsTotem(name);
            var kit = new MeshKit(isTotem ? Belay.Items.TotemLook.SlotCount : ItemModels.SlotCount);
            if (isTotem) ItemModels.BuildTotem(kit); else ItemModels.BuildRapier(kit);
            int texBase = isTotem ? totemTexBase : modelTexBase;
            mesh = new ItemMesh { Fit = Matrix4x4.identity, Length = isTotem ? ItemModels.HeldTotemLength : 1.05f };
            for (int sub = 0; sub < kit.Submeshes.Length; sub++)
            {
                List<int> list = kit.Submeshes[sub];
                for (int i = 0; i < list.Count; i += 3)
                {
                    var f = new float[15];
                    for (int j = 0; j < 3; j++)
                    {
                        Vector3 v = kit.Vertices[list[i + j]];
                        Vector2 uv = kit.UVs[list[i + j]];
                        f[j * 5] = v.x; f[j * 5 + 1] = v.y; f[j * 5 + 2] = v.z; f[j * 5 + 3] = uv.x; f[j * 5 + 4] = uv.y;
                    }
                    mesh.Tex.Add(texBase + sub);
                    mesh.Tris.Add(f);
                }
            }
        }
        if (mesh == null) return;
        var fist = Matrix4x4.identity;
        fist.SetColumn(0, new Vector4(hand.Right.x, hand.Right.y, hand.Right.z, 0));
        fist.SetColumn(1, new Vector4(hand.Up.x, hand.Up.y, hand.Up.z, 0));
        fist.SetColumn(2, new Vector4(hand.Forward.x, hand.Forward.y, hand.Forward.z, 0));
        fist.SetColumn(3, new Vector4(hand.Origin.x, hand.Origin.y, hand.Origin.z, 1));
        Matrix4x4 m = fist * (OldGrip ? OldInFist(left) : HeldGrip.InFist(left, name, mesh.Length, 0.83f)) * mesh.Fit;
        for (int t = 0; t < mesh.Tris.Count; t++)
        {
            float[] f = mesh.Tris[t];
            var p = new Vector3[3];
            for (int j = 0; j < 3; j++) p[j] = m.MultiplyPoint3x4(new Vector3(f[j * 5], f[j * 5 + 1], f[j * 5 + 2]));
            Vector3 n = Vector3.Cross(p[1] - p[0], p[2] - p[0]).normalized;
            tris.Append('[').Append(mesh.Tex[t]);
            for (int j = 0; j < 3; j++)
            {
                foreach (float v in new[] { p[j].x, p[j].y, p[j].z, n.x, n.y, n.z, f[j * 5 + 3], f[j * 5 + 4] })
                {
                    tris.Append(',').Append(F(v));
                }
            }
            tris.Append("],");
        }
    }

    private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
