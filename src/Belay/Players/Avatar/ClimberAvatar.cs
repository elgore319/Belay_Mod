using BepInEx.Configuration;
using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>
    /// The visible body of a teammate. Builds the climber out of the meshes in <see cref="AvatarDesign"/>
    /// and poses it every frame from the networked snapshot: arms reach with two-bone IK to wherever each
    /// hand is (closed fists on a hold, open hands otherwise), legs walk, dangle while hanging and splay
    /// while falling, the head follows the camera pitch, and the torso leans into the climb.
    /// </summary>
    public class ClimberAvatar
    {
        /// <summary>
        /// Height of the player's transform above the soles of the feet. ENT_Player's position is the
        /// middle of its 1.5 m character capsule (standingHeight), with the camera 0.45 m above it.
        /// </summary>
        public const float FeetDrop = 0.75f;

        /// <summary>
        /// The design is a 1.8 m climber; this fits it to the game's 1.5 m player capsule so the head sits
        /// near the teammate's camera and synced hands are within natural reach.
        /// </summary>
        public const float Scale = 0.83f;

        private static ConfigEntry<bool> headlamps;

        public static bool HeadlampsEnabled
        {
            get
            {
                if (headlamps == null && Plugin.Instance != null)
                {
                    headlamps = Plugin.Instance.Config.Bind("Avatars", "Headlamps", true,
                        "Teammates' helmet lamps light the area in front of them.");
                }
                return headlamps == null || headlamps.Value;
            }
        }

        private static Mesh packMesh, packLidMesh;
        private static Mesh torsoMesh, headMesh, upperArmMesh, forearmMesh, thighMesh, shinMesh, bootMesh;
        private static Mesh fistLeft, fistRight, openLeft, openRight;
        private static Shader litShader;

        private readonly Transform root;
        private readonly Transform torso, head;
        private readonly Limb leftArm, rightArm, leftLeg, rightLeg;
        private readonly Transform leftBoot, rightBoot;
        private readonly Transform leftFist, rightFist, leftOpen, rightOpen;
        private readonly Transform pack, packLid;
        private readonly Light lamp;
        private float packOut;

        private Quaternion torsoRotation = Quaternion.identity;
        private float bodyYaw;
        private float walkPhase;
        private float walkAmount;
        private Vector3 leftFoot, rightFoot;
        private bool posed;

        /// <summary>Where the rope clips in: the harness belay loop.</summary>
        public Vector3 RopeAnchor { get; private set; }

        /// <summary>Top of the helmet, for name tags.</summary>
        public Vector3 HeadTop { get; private set; }

        public ClimberAvatar(Transform parent, Color color)
        {
            EnsureMeshes();
            Material[] materials = MakeMaterials(color);

            root = new GameObject("Climber").transform;
            root.SetParent(parent, false);

            torso = Part("Torso", torsoMesh, materials);
            head = Part("Head", headMesh, materials);
            pack = Part("Pack", packMesh, materials);
            packLid = Part("PackLid", packLidMesh, materials);
            leftArm = new Limb(Part("LeftUpperArm", upperArmMesh, materials), Part("LeftForearm", forearmMesh, materials),
                AvatarDesign.UpperArm * Scale, AvatarDesign.Forearm * Scale);
            rightArm = new Limb(Part("RightUpperArm", upperArmMesh, materials), Part("RightForearm", forearmMesh, materials),
                AvatarDesign.UpperArm * Scale, AvatarDesign.Forearm * Scale);
            leftLeg = new Limb(Part("LeftThigh", thighMesh, materials), Part("LeftShin", shinMesh, materials),
                AvatarDesign.Thigh * Scale, AvatarDesign.Shin * Scale);
            rightLeg = new Limb(Part("RightThigh", thighMesh, materials), Part("RightShin", shinMesh, materials),
                AvatarDesign.Thigh * Scale, AvatarDesign.Shin * Scale);
            leftBoot = Part("LeftBoot", bootMesh, materials);
            rightBoot = Part("RightBoot", bootMesh, materials);
            leftFist = Part("LeftFist", fistLeft, materials);
            rightFist = Part("RightFist", fistRight, materials);
            leftOpen = Part("LeftHand", openLeft, materials);
            rightOpen = Part("RightHand", openRight, materials);

            var lampObject = new GameObject("Headlamp");
            lampObject.transform.SetParent(head, false);
            lampObject.transform.localPosition = new Vector3(0, 0.215f, 0.17f);
            lampObject.transform.localRotation = Quaternion.Euler(12f, 0, 0);
            lamp = lampObject.AddComponent<Light>();
            lamp.type = LightType.Spot;
            lamp.range = 14f;
            lamp.spotAngle = 55f;
            lamp.intensity = 1.6f;
            lamp.color = new Color(1f, 0.93f, 0.8f);
            lamp.shadows = LightShadows.None;
        }

        public void SetVisible(bool visible)
        {
            if (root.gameObject.activeSelf != visible)
            {
                root.gameObject.SetActive(visible);
            }
        }

        /// <summary>
        /// Poses the climber. <paramref name="position"/> is the player's transform position, the hands are
        /// world positions, <paramref name="velocity"/> is in meters per second.
        /// </summary>
        public void Pose(Vector3 position, float yaw, float pitch, Vector3 leftHand, Vector3 rightHand,
            bool leftGrip, bool rightGrip, Vector3 velocity, bool inventoryOpen, float dt)
        {
            bool hanging = leftGrip || rightGrip;
            Vector3 flat = new Vector3(velocity.x, 0, velocity.z);
            float speed = flat.magnitude;
            bool airborne = !hanging && Mathf.Abs(velocity.y) > 2.5f;
            bool grounded = !hanging && !airborne;
            float follow = posed ? 1f - Mathf.Exp(-10f * dt) : 1f;

            // Body yaw trails the camera a little, so turning your head doesn't snap the whole body.
            bodyYaw = posed ? Mathf.LerpAngle(bodyYaw, yaw, 1f - Mathf.Exp(-6f * dt)) : yaw;
            if (Mathf.Abs(Mathf.DeltaAngle(bodyYaw, yaw)) > 60f)
            {
                bodyYaw = yaw - Mathf.Sign(Mathf.DeltaAngle(bodyYaw, yaw)) * 60f;
            }
            Quaternion yawRotation = Quaternion.Euler(0, bodyYaw, 0);
            Vector3 forward = yawRotation * Vector3.forward;
            Vector3 right = yawRotation * Vector3.right;

            // Walk cycle.
            walkAmount = Mathf.MoveTowards(walkAmount, grounded ? Mathf.Clamp01(speed / 3f) : 0f, dt * 4f);
            walkPhase += dt * (grounded ? Mathf.Clamp(speed, 0f, 8f) * 2.4f : 0f);
            float bob = grounded ? Mathf.Abs(Mathf.Sin(walkPhase)) * 0.03f * walkAmount : 0f;

            // Torso: lean into the walk, lean toward the hold when hanging, a share of the look pitch.
            float signedPitch = Mathf.DeltaAngle(0f, pitch);
            float lean = Mathf.Clamp(signedPitch * 0.15f, -8f, 10f);
            float roll = 0f;
            if (grounded)
            {
                lean += Vector3.Dot(flat, forward) * 2.2f;
            }
            else if (hanging)
            {
                lean -= 6f;
                if (leftGrip != rightGrip)
                {
                    roll = leftGrip ? 9f : -9f;
                }
            }
            else
            {
                lean -= 10f;
            }
            Quaternion torsoTarget = yawRotation * Quaternion.Euler(Mathf.Clamp(lean, -20f, 20f), 0, roll);
            torsoRotation = posed ? Quaternion.Slerp(torsoRotation, torsoTarget, follow) : torsoTarget;

            Vector3 pelvis = position + Vector3.up * (AvatarDesign.PelvisHeight * Scale - FeetDrop + bob);
            torso.SetPositionAndRotation(pelvis, torsoRotation);
            RopeAnchor = pelvis + torsoRotation * (new Vector3(0, -AvatarDesign.HarnessLoopDrop - 0.02f, 0.13f) * Scale);

            // Backpack: rides on the back, and swings round to be held open in front while the
            // teammate is looking through their inventory.
            packOut = posed ? Mathf.MoveTowards(packOut, inventoryOpen ? 1f : 0f, dt / 0.4f) : (inventoryOpen ? 1f : 0f);
            float e = packOut * packOut * (3f - 2f * packOut);
            Vector3 backPosition = pelvis + torsoRotation * (AvatarDesign.PackOnBack * Scale);
            Quaternion backRotation = torsoRotation * Quaternion.Euler(0, 180f, 0);
            Vector3 frontPosition = pelvis + torsoRotation * (new Vector3(0, 0.26f, 0.44f) * Scale);
            Quaternion frontRotation = torsoRotation * Quaternion.Euler(-30f, 0, 0);
            float arc = Mathf.Sin(e * Mathf.PI);
            Vector3 packPosition = Vector3.Lerp(backPosition, frontPosition, e)
                + torsoRotation * (new Vector3(0.38f * arc, 0.06f * arc, 0) * Scale);
            Quaternion packRotation = Quaternion.Slerp(backRotation, torsoRotation * Quaternion.Euler(0, 90f, 0), Mathf.Clamp01(e * 2f));
            if (e > 0.5f)
            {
                packRotation = Quaternion.Slerp(torsoRotation * Quaternion.Euler(0, 90f, 0), frontRotation, (e - 0.5f) * 2f);
            }
            pack.SetPositionAndRotation(packPosition, packRotation);
            packLid.SetPositionAndRotation(pack.TransformPoint(AvatarDesign.PackLidHinge), packRotation * Quaternion.Euler(-115f * e, 0, 0));
            if (e > 0.01f)
            {
                // Both hands hold the sides of the pack.
                leftHand = Vector3.Lerp(leftHand, pack.TransformPoint(new Vector3(0.17f, -0.03f, 0.07f)), e);
                rightHand = Vector3.Lerp(rightHand, pack.TransformPoint(new Vector3(-0.17f, -0.03f, 0.07f)), e);
                leftGrip |= e > 0.5f;
                rightGrip |= e > 0.5f;
                signedPitch = Mathf.Lerp(signedPitch, 40f, e);
            }

            // Head looks where the camera looks.
            Vector3 neck = pelvis + torsoRotation * (new Vector3(0, AvatarDesign.NeckHeight, 0.01f) * Scale);
            Quaternion headRotation = Quaternion.Euler(Mathf.Clamp(signedPitch, -75f, 75f) * 0.85f, yaw, 0);
            head.SetPositionAndRotation(neck, headRotation);
            HeadTop = neck + Vector3.up * (0.3f * Scale);
            bool lampOn = HeadlampsEnabled;
            if (lamp.enabled != lampOn)
            {
                lamp.enabled = lampOn;
            }

            // Arms reach to the synced hand positions.
            PoseArm(leftArm, -1f, pelvis, leftHand, leftGrip, leftFist, leftOpen);
            PoseArm(rightArm, 1f, pelvis, rightHand, rightGrip, rightFist, rightOpen);

            // Legs.
            float ground = position.y - FeetDrop + AvatarDesign.AnkleHeight * Scale;
            Vector3 moveDir = speed > 0.1f ? flat / speed : forward;
            Vector3 hipLeft = pelvis + yawRotation * (new Vector3(-AvatarDesign.HipWidth, -0.07f, 0) * Scale);
            Vector3 hipRight = pelvis + yawRotation * (new Vector3(AvatarDesign.HipWidth, -0.07f, 0) * Scale);
            Vector3 targetLeft, targetRight;
            if (grounded)
            {
                float stride = 0.28f * walkAmount;
                targetLeft = StepTarget(hipLeft, ground, moveDir, stride, walkPhase);
                targetRight = StepTarget(hipRight, ground, moveDir, stride, walkPhase + Mathf.PI);
            }
            else
            {
                // Dangle below the hips, one knee drawn up like a climber feeling for a foothold, and
                // swing against the motion so the legs have some weight.
                float legLength = (AvatarDesign.Thigh + AvatarDesign.Shin) * Scale;
                Vector3 swing = Vector3.ClampMagnitude(-velocity * 0.06f, 0.35f);
                if (hanging)
                {
                    targetLeft = hipLeft + Vector3.down * (legLength * 0.78f) + forward * 0.22f - right * 0.04f + swing;
                    targetRight = hipRight + Vector3.down * (legLength * 0.95f) + forward * 0.05f + right * 0.03f + swing;
                }
                else
                {
                    float kick = Mathf.Sin(Time.time * 9f) * 0.08f;
                    targetLeft = hipLeft + Vector3.down * (legLength * 0.8f) + forward * (0.15f + kick) - right * 0.08f + swing;
                    targetRight = hipRight + Vector3.down * (legLength * 0.85f) + forward * (0.05f - kick) + right * 0.08f + swing;
                }
            }
            float footFollow = grounded ? 1f : 1f - Mathf.Exp(-8f * dt);
            leftFoot = posed ? Vector3.Lerp(leftFoot, targetLeft, footFollow) : targetLeft;
            rightFoot = posed ? Vector3.Lerp(rightFoot, targetRight, footFollow) : targetRight;

            Vector3 kneeHint = forward * 0.6f;
            Vector3 ankleLeft = leftLeg.Solve(hipLeft, leftFoot, hipLeft + kneeHint - right * 0.1f, forward, false);
            Vector3 ankleRight = rightLeg.Solve(hipRight, rightFoot, hipRight + kneeHint + right * 0.1f, forward, false);
            // Toes point down when the feet are off the ground.
            Quaternion bootRotation = grounded ? yawRotation : yawRotation * Quaternion.Euler(35f, 0, 0);
            leftBoot.SetPositionAndRotation(ankleLeft, Quaternion.Slerp(leftBoot.rotation, bootRotation, posed ? follow : 1f));
            rightBoot.SetPositionAndRotation(ankleRight, Quaternion.Slerp(rightBoot.rotation, bootRotation, posed ? follow : 1f));

            posed = true;
        }

        private void PoseArm(Limb arm, float side, Vector3 pelvis, Vector3 hand, bool grip, Transform fist, Transform open)
        {
            Vector3 shoulder = pelvis + torsoRotation * (new Vector3(side * AvatarDesign.ShoulderWidth, AvatarDesign.ShoulderHeight - 0.02f, 0) * Scale);
            Vector3 toHand = hand - shoulder;
            Vector3 dir = toHand.sqrMagnitude > 1e-6f ? toHand.normalized : torsoRotation * Vector3.forward;
            // The synced point is where the palm closes on the hold; the wrist sits just behind it.
            Vector3 wrist = hand - dir * ((grip ? 0.06f : 0.04f) * Scale);
            // Elbows point out, down and back.
            Vector3 pole = shoulder + torsoRotation * new Vector3(side * 0.6f, -0.6f, -0.35f);
            Vector3 up = torsoRotation * Vector3.up;
            Vector3 elbowToWrist = arm.Solve(shoulder, wrist, pole, up, true) - arm.Joint;

            // Back of the hand faces up when reaching forward and away from the wall when reaching up.
            Vector3 forearmDir = elbowToWrist.sqrMagnitude > 1e-6f ? elbowToWrist.normalized : dir;
            Vector3 back = Vector3.Lerp(up, -(torsoRotation * Vector3.forward), Mathf.Abs(Vector3.Dot(forearmDir, up)));
            Quaternion handRotation = SafeLook(forearmDir, back);
            Transform shown = grip ? fist : open;
            Transform hidden = grip ? open : fist;
            if (!shown.gameObject.activeSelf)
            {
                shown.gameObject.SetActive(true);
            }
            if (hidden.gameObject.activeSelf)
            {
                hidden.gameObject.SetActive(false);
            }
            shown.SetPositionAndRotation(wrist, handRotation);
        }

        private static Vector3 StepTarget(Vector3 hip, float ground, Vector3 moveDir, float stride, float phase)
        {
            float swing = Mathf.Sin(phase);
            float lift = Mathf.Max(0f, Mathf.Cos(phase)) * stride * 0.45f;
            return new Vector3(hip.x, ground + lift, hip.z) + moveDir * (swing * stride);
        }

        private static Quaternion SafeLook(Vector3 forward, Vector3 up)
        {
            if (forward.sqrMagnitude < 1e-8f)
            {
                return Quaternion.identity;
            }
            if (Vector3.Cross(forward, up).sqrMagnitude < 1e-6f)
            {
                up = Mathf.Abs(forward.y) < 0.9f ? Vector3.up : Vector3.forward;
            }
            return Quaternion.LookRotation(forward, up);
        }

        private Transform Part(string name, Mesh mesh, Material[] materials)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
            go.transform.localScale = Vector3.one * Scale;
            return go.transform;
        }

        /// <summary>Two segments (upper arm and forearm, or thigh and shin) solved with two-bone IK.</summary>
        private class Limb
        {
            private readonly Transform upper, lower;
            private readonly float upperLength, lowerLength;

            public Vector3 Joint { get; private set; }

            public Limb(Transform upper, Transform lower, float upperLength, float lowerLength)
            {
                this.upper = upper;
                this.lower = lower;
                this.upperLength = upperLength;
                this.lowerLength = lowerLength;
            }

            /// <summary>
            /// Bends the limb from <paramref name="start"/> toward <paramref name="target"/> with the
            /// middle joint toward <paramref name="pole"/>. With <paramref name="stretch"/>, a target out of
            /// reach stretches the limb to touch it (hands always land on their hold); otherwise the limb
            /// stops at full length. Returns where the end of the limb ended up.
            /// </summary>
            public Vector3 Solve(Vector3 start, Vector3 target, Vector3 pole, Vector3 twistUp, bool stretch)
            {
                Vector3 toTarget = target - start;
                float distance = toTarget.magnitude;
                Vector3 dir = distance > 1e-5f ? toTarget / distance : Vector3.down;
                float a = upperLength, b = lowerLength;
                float reach = a + b;
                float scale = 1f;
                if (distance > reach * 0.999f)
                {
                    if (stretch)
                    {
                        scale = Mathf.Min(distance / (reach * 0.999f), 3f);
                    }
                    distance = reach * 0.999f;
                }
                distance = Mathf.Max(distance, Mathf.Abs(a - b) + 0.01f);

                // Law of cosines for the angle at the start joint.
                float cos = Mathf.Clamp((a * a + distance * distance - b * b) / (2f * a * distance), -1f, 1f);
                float sin = Mathf.Sqrt(1f - cos * cos);
                Vector3 bend = Vector3.ProjectOnPlane(pole - start, dir);
                if (bend.sqrMagnitude < 1e-6f)
                {
                    bend = Vector3.ProjectOnPlane(twistUp, dir);
                }
                bend.Normalize();

                Joint = start + (dir * cos + bend * sin) * a * scale;
                Vector3 end = start + dir * distance * scale;
                Place(upper, start, Joint, bend);
                Place(lower, Joint, end, bend);
                return end;
            }

            private static void Place(Transform segment, Vector3 from, Vector3 to, Vector3 up)
            {
                Vector3 axis = to - from;
                float length = axis.magnitude;
                segment.SetPositionAndRotation(from, SafeLook(axis, up));
                segment.localScale = new Vector3(Scale, Scale, Mathf.Max(length, 0.01f));
            }
        }

        private static void EnsureMeshes()
        {
            if (torsoMesh != null)
            {
                return;
            }
            torsoMesh = Build("Belay_Torso", AvatarDesign.BuildTorso);
            packMesh = Build("Belay_Pack", AvatarDesign.BuildPack);
            packLidMesh = Build("Belay_PackLid", AvatarDesign.BuildPackLid);
            headMesh = Build("Belay_Head", AvatarDesign.BuildHead);
            upperArmMesh = Build("Belay_UpperArm", AvatarDesign.BuildUpperArm);
            forearmMesh = Build("Belay_Forearm", AvatarDesign.BuildForearm);
            thighMesh = Build("Belay_Thigh", AvatarDesign.BuildThigh);
            shinMesh = Build("Belay_Shin", AvatarDesign.BuildShin);
            bootMesh = Build("Belay_Boot", AvatarDesign.BuildBoot);
            fistLeft = Build("Belay_FistL", k => AvatarDesign.BuildFist(k, true));
            fistRight = Build("Belay_FistR", k => AvatarDesign.BuildFist(k, false));
            openLeft = Build("Belay_HandL", k => AvatarDesign.BuildOpenHand(k, true));
            openRight = Build("Belay_HandR", k => AvatarDesign.BuildOpenHand(k, false));
        }

        private static Mesh Build(string name, System.Action<MeshKit> build)
        {
            var kit = new MeshKit(Slot.Count);
            build(kit);
            Mesh mesh = kit.ToMesh(name);
            Object.DontDestroyOnLoad(mesh);
            return mesh;
        }

        private static Material[] MakeMaterials(Color color)
        {
            if (litShader == null)
            {
                // The default primitive material is guaranteed to render in this game's pipeline.
                GameObject probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
                litShader = probe.GetComponent<Renderer>().sharedMaterial.shader;
                Object.Destroy(probe);
            }
            PixelTextures.Recipe[] recipes = AvatarDesign.Recipes(color);
            var materials = new Material[Slot.Count];
            for (int i = 0; i < Slot.Count; i++)
            {
                var texture = new Texture2D(PixelTextures.Size, PixelTextures.Size, TextureFormat.RGBA32, false)
                {
                    name = "Belay_Avatar_" + i,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Repeat,
                };
                texture.SetPixels32(PixelTextures.Generate(recipes[i], 101 + i * 7));
                texture.Apply(false, true);

                var material = new Material(litShader) { color = Color.white, mainTexture = texture };
                // Matte, like the game's characters; only the goggles and metal catch a little light.
                float gloss = i == Slot.Visor ? 0.6f : i == Slot.Metal ? 0.35f : 0f;
                if (material.HasProperty("_Glossiness"))
                {
                    material.SetFloat("_Glossiness", gloss);
                }
                if (i == Slot.Lamp && material.HasProperty("_EmissionColor"))
                {
                    material.EnableKeyword("_EMISSION");
                    material.SetColor("_EmissionColor", recipes[i].Base * 2.5f);
                }
                materials[i] = material;
            }
            return materials;
        }
    }
}
