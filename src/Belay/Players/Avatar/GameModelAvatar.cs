using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Belay.Players.Avatar
{
    /// <summary>
    /// A teammate drawn with one of the game's own models instead of our climber: the giant roach for
    /// players in Roach Mode, and the zombie scientist, zombie security guard and the Bazaar's Soach
    /// shopkeeper as outfit looks. The model is copied from the game's loaded assets (never shipped with
    /// the mod), stripped of everything but its renderers and bones, and posed every frame from the
    /// hidden climber's skeleton, so it stands, walks, hangs and reaches for holds the same way.
    /// </summary>
    public sealed class GameModelAvatar
    {
        public const string GiantRoach = "roach", ZombieScientist = "zombie-scientist", ZombieSecurity = "zombie-security",
            Soach = "soach";

        /// <summary>How one game model is found, cleaned up and rigged.</summary>
        private sealed class Spec
        {
            public Func<GameObject> Source;
            /// <summary>Child objects to throw away (other variants, loot, scanner markers, parasites...).</summary>
            public string[] Remove = new string[0];
            /// <summary>Keep the model's own animation (the roach scuttles on its own).</summary>
            public bool KeepAnimator;
            /// <summary>Two bones that give the model's facing: front minus back, or right minus left.</summary>
            public string Front, Back, Left, Right;
            /// <summary>Height to scale the model to, in meters; 0 keeps the game's own size.</summary>
            public float Height;
            public string Hips;
            /// <summary>Arm and leg chains: upper bone, lower bone, end bone.</summary>
            public string[] ArmL, ArmR, LegL, LegR;
            /// <summary>Optional hind legs (the Soach walks on four): they step with the opposite climber leg.</summary>
            public string[] RearL, RearR;
            public string Head;
        }

        private static readonly Dictionary<string, Spec> Specs = new Dictionary<string, Spec>
        {
            [GiantRoach] = new Spec
            {
                Source = () => Entity("Denizen_GiantRoach"),
                KeepAnimator = true,
                Front = "head", Back = "body",
            },
            [ZombieScientist] = new Spec
            {
                Source = () => Entity("Denizen_Barnacle_Zombie_Scientist"),
                Remove = new[] { "Zombie_v2_animbake", "Denizen_Barnacle_Small", "Hitbox_Hips", "Scan_Target" },
                Left = "UL.L", Right = "UL.R", Height = 1.55f, Hips = "Hips", Head = "Neck1",
                ArmL = new[] { "UA.L", "LA.L", "HND.L" }, ArmR = new[] { "UA.R", "LA.R", "HND.R" },
                LegL = new[] { "UL.L", "LL.L", "F1.L" }, LegR = new[] { "UL.R", "LL.R", "F1.R" },
            },
            [ZombieSecurity] = new Spec
            {
                Source = () => Entity("Denizen_Barnacle_Zombie_Security"),
                Remove = new[] { "Den_Zombie_Scientist_01", "Denizen_Barnacle_Small", "Hitbox_Hips", "Scan_Target" },
                Left = "ULL", Right = "ULR", Height = 1.6f, Hips = "Hips", Head = "Neck1",
                ArmL = new[] { "UAL", "LAL", "LAL_end" }, ArmR = new[] { "UAR", "LAR", "LAR.001" },
                LegL = new[] { "ULL", "LLL", "Al" }, LegR = new[] { "ULR", "LLR", "AR" },
            },
            [Soach] = new Spec
            {
                Source = SoachTrader,
                Remove = new[] { "DEN_Lengen_GEO.002" },
                Left = "ArmTop.L", Right = "ArmTop.R", Height = 1.7f, Hips = "Chest_1", Head = "Head",
                ArmL = new[] { "ArmTop.L", "ArmMid.L", "Hand.L" }, ArmR = new[] { "ArmTop.R", "ArmMid.R", "Hand.R" },
                LegL = new[] { "F_Leg_top.L", "F_Leg_mid.L", "F_Foot.L" }, LegR = new[] { "F_Leg_top.R", "F_Leg_mid.R", "F_Foot.R" },
                RearL = new[] { "R_Leg_top.L", "R_Leg_mid.L", "R_Foot.L" }, RearR = new[] { "R_Leg_top.R", "R_Leg_mid.R", "R_Foot.R" },
            },
        };

        private static readonly Dictionary<string, float> retryAt = new Dictionary<string, float>();

        public readonly string Kind;
        private readonly Spec spec;
        private readonly Transform root;
        private readonly Transform model;
        private readonly Animator animator;
        private readonly Quaternion facingFix;
        private readonly float groundOffset;
        private readonly Transform hips, head;
        private readonly Transform[][] chains = new Transform[6][];
        /// <summary>Where each hind foot and knee stand at rest, relative to the model root.</summary>
        private readonly Vector3[] rearFoot = new Vector3[2], rearKnee = new Vector3[2];
        private readonly List<KeyValuePair<Transform, Quaternion>> rest = new List<KeyValuePair<Transform, Quaternion>>();
        private float hang;

        /// <summary>The copied game model (for the totem revive, <see cref="ModelReform"/>).</summary>
        internal Transform Model => model;

        /// <summary>Builds the model, or returns null while the game hasn't loaded it (tried again later).</summary>
        public static GameModelAvatar Create(string kind, Transform parent)
        {
            if (kind == null || !Specs.TryGetValue(kind, out Spec spec))
            {
                return null;
            }
            if (retryAt.TryGetValue(kind, out float at) && Time.unscaledTime < at)
            {
                return null;
            }
            GameObject source = null;
            try
            {
                source = spec.Source();
            }
            catch (Exception e)
            {
                if (!retryAt.ContainsKey(kind))
                {
                    Plugin.Log.LogWarning("Couldn't find the " + kind + " model: " + e.Message);
                }
            }
            if (source == null)
            {
                retryAt[kind] = Time.unscaledTime + 5f;
                return null;
            }
            try
            {
                return new GameModelAvatar(kind, spec, source, parent);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Couldn't build the " + kind + " model: " + e.Message);
                retryAt[kind] = Time.unscaledTime + 30f;
                return null;
            }
        }

        public static bool IsKind(string kind) => kind != null && Specs.ContainsKey(kind);

        private GameModelAvatar(string kind, Spec spec, GameObject source, Transform parent)
        {
            Kind = kind;
            this.spec = spec;
            root = new GameObject("GameModel_" + kind).transform;
            root.SetParent(parent, false);

            // Copy it under an inactive holder so none of the game's scripts wake up, then strip them.
            var holder = new GameObject("Belay_ModelHolder");
            holder.SetActive(false);
            GameObject copy = UnityEngine.Object.Instantiate(source, holder.transform, false);
            copy.transform.localPosition = Vector3.zero;
            copy.transform.localRotation = Quaternion.identity;
            foreach (string name in spec.Remove)
            {
                foreach (Transform t in copy.GetComponentsInChildren<Transform>(true))
                {
                    if (t != null && t != copy.transform && t.name.StartsWith(name, StringComparison.Ordinal))
                    {
                        UnityEngine.Object.DestroyImmediate(t.gameObject);
                    }
                }
            }
            Strip(copy, spec.KeepAnimator);
            model = copy.transform;
            model.SetParent(root, false);
            UnityEngine.Object.Destroy(holder);
            animator = spec.KeepAnimator ? copy.GetComponentInChildren<Animator>(true) : null;
            if (animator != null)
            {
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            foreach (Renderer r in copy.GetComponentsInChildren<Renderer>(true))
            {
                if (r is SkinnedMeshRenderer skinned)
                {
                    skinned.updateWhenOffscreen = true;
                }
            }

            hips = Find(spec.Hips);
            head = Find(spec.Head);
            chains[0] = Chain(spec.ArmL);
            chains[1] = Chain(spec.ArmR);
            chains[2] = Chain(spec.LegL);
            chains[3] = Chain(spec.LegR);
            chains[4] = Chain(spec.RearL);
            chains[5] = Chain(spec.RearR);

            // Face +z, stand on the ground, and match the climber's size.
            Vector3 forward = Vector3.forward;
            Transform front = Find(spec.Front), back = Find(spec.Back), left = Find(spec.Left), right = Find(spec.Right);
            if (front != null && back != null)
            {
                forward = front.position - back.position;
            }
            else if (left != null && right != null)
            {
                forward = Vector3.Cross(right.position - left.position, Vector3.up);
            }
            forward = Vector3.ProjectOnPlane(forward, Vector3.up);
            facingFix = forward.sqrMagnitude > 1e-6f ? Quaternion.Inverse(Quaternion.LookRotation(forward.normalized)) : Quaternion.identity;
            model.localRotation = facingFix;
            float low = float.MaxValue, high = float.MinValue;
            foreach (Transform t in copy.GetComponentsInChildren<Transform>(true))
            {
                if (t == model)
                {
                    continue;
                }
                low = Mathf.Min(low, t.position.y);
                high = Mathf.Max(high, t.position.y);
            }
            if (spec.Height > 0f && high > low)
            {
                float scale = spec.Height / (high - low);
                model.localScale = Vector3.one * scale;
                low = root.position.y + (low - root.position.y) * scale;
            }
            groundOffset = root.position.y - low;
            model.localPosition = Vector3.up * groundOffset;

            foreach (Transform t in new[] { hips, head })
            {
                Remember(t);
            }
            for (int i = 0; i < 2; i++)
            {
                if (chains[4 + i] != null)
                {
                    rearKnee[i] = root.InverseTransformPoint(chains[4 + i][1].position);
                    rearFoot[i] = root.InverseTransformPoint(chains[4 + i][2].position);
                }
            }
            foreach (Transform[] chain in chains)
            {
                if (chain != null)
                {
                    Remember(chain[0]);
                    Remember(chain[1]);
                }
            }
        }

        /// <summary>
        /// Poses the model from the climber that was just posed (and is hidden): its feet, pelvis, torso
        /// lean, hands and feet.
        /// </summary>
        public void Follow(ClimberAvatar.Skeleton s, Vector3 feet, float yaw, Vector3 velocity, bool hanging, float dt)
        {
            root.SetPositionAndRotation(feet, Quaternion.Euler(0f, yaw, 0f));
            if (animator != null)
            {
                // The roach runs its own scuttle; speed it up with the teammate, and tilt it up the wall
                // while they hang on.
                float speed = new Vector2(velocity.x, velocity.z).magnitude + Mathf.Abs(velocity.y) * 0.5f;
                animator.speed = Mathf.Lerp(0.15f, 1.6f, Mathf.Clamp01(speed / 3.5f));
                hang = Mathf.MoveTowards(hang, hanging ? 1f : 0f, dt * 3f);
                root.rotation = Quaternion.Euler(-70f * hang, yaw, 0f);
                root.position = feet + root.rotation * Vector3.forward * (-0.3f * hang) + Vector3.up * (0.35f * hang);
                return;
            }

            foreach (KeyValuePair<Transform, Quaternion> r in rest)
            {
                r.Key.localRotation = r.Value;
            }
            Quaternion yawRotation = Quaternion.Euler(0f, yaw, 0f);
            if (hips != null)
            {
                // Lean and twist with the climber's torso, and sit at its pelvis height.
                hips.rotation = s.Torso * Quaternion.Inverse(yawRotation) * hips.rotation;
                hips.position = s.Pelvis;
            }
            if (head != null)
            {
                head.rotation = s.Head * Quaternion.Inverse(s.Torso) * head.rotation;
            }
            Reach(chains[0], s.LeftHand, s.LeftElbow);
            Reach(chains[1], s.RightHand, s.RightElbow);
            Reach(chains[2], s.LeftFoot, s.LeftKnee);
            Reach(chains[3], s.RightFoot, s.RightKnee);
            // Hind legs trot with the diagonal leg: the left hind steps with the right foot and the right
            // hind with the left, copying that foot's stride (forward/back and lift) from where they rest.
            StepRear(chains[4], 0, s.RightFoot);
            StepRear(chains[5], 1, s.LeftFoot);
        }

        private void StepRear(Transform[] chain, int i, Vector3 leadFoot)
        {
            if (chain == null)
            {
                return;
            }
            Vector3 lead = root.InverseTransformPoint(leadFoot);
            Vector3 stride = Vector3.ClampMagnitude(new Vector3(0f, Mathf.Max(lead.y, 0f), lead.z), 0.6f);
            Reach(chain, root.TransformPoint(rearFoot[i] + stride), root.TransformPoint(rearKnee[i] + stride));
        }

        public void SetVisible(bool visible)
        {
            if (root.gameObject.activeSelf != visible)
            {
                root.gameObject.SetActive(visible);
            }
        }

        public void Destroy()
        {
            if (root != null)
            {
                UnityEngine.Object.Destroy(root.gameObject);
            }
        }

        /// <summary>Two-bone reach with the model's own bone lengths, bending toward the climber's joint.</summary>
        private static void Reach(Transform[] chain, Vector3 target, Vector3 pole)
        {
            if (chain == null)
            {
                return;
            }
            Transform upper = chain[0], lower = chain[1], end = chain[2];
            Vector3 start = upper.position;
            float a = Vector3.Distance(upper.position, lower.position);
            float b = Vector3.Distance(lower.position, end.position);
            Vector3 to = target - start;
            float d = Mathf.Clamp(to.magnitude, Mathf.Abs(a - b) + 0.01f, (a + b) * 0.999f);
            Vector3 dir = to.sqrMagnitude > 1e-8f ? to.normalized : Vector3.down;
            float cos = Mathf.Clamp((a * a + d * d - b * b) / (2f * a * d), -1f, 1f);
            Vector3 bend = Vector3.ProjectOnPlane(pole - start, dir);
            bend = bend.sqrMagnitude > 1e-8f ? bend.normalized : Vector3.ProjectOnPlane(Vector3.forward, dir).normalized;
            Vector3 joint = start + (dir * cos + bend * Mathf.Sqrt(1f - cos * cos)) * a;
            Aim(upper, lower.position, joint);
            Aim(lower, end.position, start + dir * d);
        }

        /// <summary>Turns a bone so that its child at <paramref name="childNow"/> points at <paramref name="goal"/>.</summary>
        private static void Aim(Transform bone, Vector3 childNow, Vector3 goal)
        {
            Vector3 from = childNow - bone.position, to = goal - bone.position;
            if (from.sqrMagnitude < 1e-8f || to.sqrMagnitude < 1e-8f)
            {
                return;
            }
            bone.rotation = Quaternion.FromToRotation(from, to) * bone.rotation;
        }

        private void Remember(Transform t)
        {
            if (t != null)
            {
                rest.Add(new KeyValuePair<Transform, Quaternion>(t, t.localRotation));
            }
        }

        private Transform[] Chain(string[] names)
        {
            if (names == null)
            {
                return null;
            }
            var chain = new Transform[3];
            for (int i = 0; i < 3; i++)
            {
                chain[i] = Find(names[i]);
                if (chain[i] == null)
                {
                    return null;
                }
            }
            return chain;
        }

        private Transform Find(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            foreach (Transform t in model.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name)
                {
                    return t;
                }
            }
            return null;
        }

        /// <summary>Removes everything but transforms, renderers, mesh filters and (optionally) the animator.</summary>
        private static void Strip(GameObject copy, bool keepAnimator)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                foreach (Component c in copy.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || c is Transform || c is Renderer || c is MeshFilter || (keepAnimator && c is Animator))
                    {
                        continue;
                    }
                    try
                    {
                        UnityEngine.Object.DestroyImmediate(c);
                    }
                    catch (Exception)
                    {
                        // Something else still requires it; the next pass gets it.
                    }
                }
            }
            foreach (Renderer r in copy.GetComponentsInChildren<Renderer>(true))
            {
                // Scanner dots and other sprites are markers, not the body.
                if (r is SpriteRenderer || r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer)
                {
                    r.enabled = false;
                }
            }
        }

        private static GameObject Entity(string name)
        {
            return CL_AssetManager.GetAssetGameObject(name);
        }

        private static readonly Dictionary<string, AsyncOperationHandle<GameObject>> bazaarLoads = new Dictionary<string, AsyncOperationHandle<GameObject>>();
        private static float nextSceneScan;

        /// <summary>
        /// The shopkeeper stands in the Bazaar level, and levels aren't loaded until the run reaches them
        /// (they live behind Addressables, so the holder's level field is empty). Ask Addressables for the
        /// Bazaar prefabs ourselves, on our own handle so the game's loading is untouched, and take just him
        /// out once they arrive. Until then this returns null and the caller retries in a few seconds.
        /// </summary>
        private static GameObject SoachTrader()
        {
            foreach (M_Level.LevelAssetHolder holder in BazaarLevels())
            {
                GameObject found = FindTrader(holder.level != null ? holder.level.gameObject : null);
                if (found != null)
                {
                    return found;
                }
                if (holder.levelAssetReference == null || !holder.levelAssetReference.RuntimeKeyIsValid())
                {
                    continue;
                }
                if (!bazaarLoads.TryGetValue(holder.id, out AsyncOperationHandle<GameObject> handle))
                {
                    handle = Addressables.LoadAssetAsync<GameObject>(holder.levelAssetReference.RuntimeKey);
                    bazaarLoads[holder.id] = handle;
                }
                if (!handle.IsValid())
                {
                    continue;
                }
                if (handle.Status == AsyncOperationStatus.Succeeded)
                {
                    found = FindTrader(handle.Result);
                    if (found != null)
                    {
                        return found;
                    }
                }
                else if (handle.Status == AsyncOperationStatus.Failed)
                {
                    Plugin.Log.LogWarning("Couldn't load " + holder.id + " for the soach model: " + handle.OperationException?.Message);
                    bazaarLoads[holder.id] = default(AsyncOperationHandle<GameObject>);
                }
            }
            // Last resort: a Bazaar the run already spawned.
            if (Time.unscaledTime >= nextSceneScan)
            {
                nextSceneScan = Time.unscaledTime + 30f;
                foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
                {
                    if (t.name == TraderName)
                    {
                        return t.gameObject;
                    }
                }
            }
            return null;
        }

        private const string TraderName = "DEN_Soach_Trader_01";

        private static GameObject FindTrader(GameObject level)
        {
            if (level == null)
            {
                return null;
            }
            foreach (Transform t in level.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == TraderName)
                {
                    return t.gameObject;
                }
            }
            return null;
        }

        private static List<M_Level.LevelAssetHolder> bazaarLevels;

        /// <summary>Every level in the game's databases whose id names the Bazaar.</summary>
        private static List<M_Level.LevelAssetHolder> BazaarLevels()
        {
            if (bazaarLevels != null && bazaarLevels.Count > 0)
            {
                return bazaarLevels;
            }
            var list = new List<M_Level.LevelAssetHolder>();
            foreach (string id in new[] { "C_Roach_Bazaar_01", "C_Roach_Bazaar_Small_01" })
            {
                M_Level.LevelAssetHolder holder = CL_AssetManager.GetLevelAsset(id);
                if (holder != null)
                {
                    list.Add(holder);
                }
            }
            if (list.Count == 0 && AccessTools.Field(typeof(CL_AssetManager), "databases")?.GetValue(null) is System.Collections.IEnumerable databases)
            {
                foreach (CL_AssetManager.WKDatabaseHolder db in databases)
                {
                    foreach (M_Level.LevelAssetHolder holder in db.database.levelAssets)
                    {
                        if (holder?.id != null && holder.id.IndexOf("bazaar", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            list.Add(holder);
                        }
                    }
                }
            }
            if (list.Count == 0)
            {
                Plugin.Log.LogWarning("No Bazaar level found for the soach model.");
            }
            bazaarLevels = list;
            return list;
        }
    }
}
