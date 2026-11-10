using Belay.Net;
using Belay.Players.Avatar;
using UnityEngine;

namespace Belay.Players
{
    /// <summary>
    /// A teammate's body: the procedural climber from <see cref="ClimberAvatar"/> plus a name tag, smoothed
    /// toward the latest network snapshot. It has no colliders, so it never blocks the local player.
    /// </summary>
    public class RemotePlayer : MonoBehaviour
    {
        public Steamworks.SteamId SteamId;
        public string DisplayName;
        public PlayerState Latest;
        public float LastReceived;

        private ClimberAvatar avatar;
        private Vector3 leftHand;
        private Vector3 rightHand;
        private Vector3 velocity;
        private TextMesh nameTag;
        private TextMesh nameShadow;
        private Color color;
        private bool hasState;

        public Vector3 LocalPosition => transform.position;
        public bool IsHanging => Latest.LeftHanging || Latest.RightHanging;

        /// <summary>Where a rope to this player should attach: their harness.</summary>
        public Vector3 RopeAnchor => avatar != null && hasState ? avatar.RopeAnchor : transform.position + Vector3.down * 0.4f;

        public static RemotePlayer Create(Steamworks.SteamId id, string displayName, Color color)
        {
            var root = new GameObject("Belay_Remote_" + displayName);
            DontDestroyOnLoad(root);
            var remote = root.AddComponent<RemotePlayer>();
            remote.SteamId = id;
            remote.DisplayName = displayName;
            remote.Build(color);
            return remote;
        }

        private void Build(Color playerColor)
        {
            color = playerColor;
            avatar = new ClimberAvatar(transform, color);

            nameShadow = MakeTag("NameShadow", Color.black);
            nameTag = MakeTag("NameTag", Color.Lerp(color, Color.white, 0.25f));
        }

        private TextMesh MakeTag(string name, Color tagColor)
        {
            var tag = new GameObject(name);
            tag.transform.SetParent(transform, false);
            var text = tag.AddComponent<TextMesh>();
            text.text = DisplayName;
            text.characterSize = 0.04f;
            text.fontSize = 64;
            text.anchor = TextAnchor.LowerCenter;
            text.alignment = TextAlignment.Center;
            text.color = tagColor;
            return text;
        }

        public void Apply(PlayerState state)
        {
            Latest = state;
            LastReceived = Time.unscaledTime;
            if (!hasState)
            {
                hasState = true;
                Snap();
            }
        }

        private void Snap()
        {
            transform.position = WorldSpace.ToLocal(Latest.Position);
            leftHand = WorldSpace.ToLocal(Latest.LeftHand);
            rightHand = WorldSpace.ToLocal(Latest.RightHand);
            velocity = Vector3.zero;
        }

        private void LateUpdate()
        {
            bool visible = hasState && WorldSpace.Ready && !Latest.Dead;
            SetVisible(visible);
            if (!visible)
            {
                return;
            }

            float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            float t = 1f - Mathf.Exp(-15f * dt);
            Vector3 target = WorldSpace.ToLocal(Latest.Position);
            if ((target - transform.position).sqrMagnitude > 25f * 25f)
            {
                Snap();
            }
            Vector3 before = transform.position;
            transform.position = Vector3.Lerp(transform.position, target, t);
            velocity = Vector3.Lerp(velocity, (transform.position - before) / dt, 1f - Mathf.Exp(-8f * dt));

            // A gripping hand is pinned to its hold, so it snaps there faster than a free hand moves.
            float gripT = 1f - Mathf.Exp(-30f * dt);
            leftHand = Vector3.Lerp(leftHand, WorldSpace.ToLocal(Latest.LeftHand), Latest.LeftHanging ? gripT : t);
            rightHand = Vector3.Lerp(rightHand, WorldSpace.ToLocal(Latest.RightHand), Latest.RightHanging ? gripT : t);

            HandSkins.PublishTick();
            avatar.Pose(transform.position, Latest.Yaw, Latest.Pitch, leftHand, rightHand,
                Latest.LeftHanging, Latest.RightHanging, velocity, Latest.InventoryOpen, dt,
                HandSkins.SkinOf(SteamId, 0), HandSkins.SkinOf(SteamId, 1));

            UpdateNameTag();
        }

        private void UpdateNameTag()
        {
            Camera cam = Camera.main;
            Vector3 anchor = avatar.HeadTop + Vector3.up * 0.12f;
            if (cam == null)
            {
                nameTag.transform.position = anchor;
                return;
            }
            Vector3 fromCam = anchor - cam.transform.position;
            float distance = fromCam.magnitude;
            // Grow with distance so far-off teammates stay readable, up to a cap.
            float size = 0.04f * Mathf.Clamp(distance / 6f, 1f, 5f);
            string label = distance > 15f ? DisplayName + "  " + Mathf.RoundToInt(distance) + "m" : DisplayName;
            Quaternion facing = Quaternion.LookRotation(fromCam);
            Vector3 lift = Vector3.up * (size * 1.5f);

            nameTag.transform.SetPositionAndRotation(anchor + lift, facing);
            nameShadow.transform.SetPositionAndRotation(anchor + lift + facing * new Vector3(size * 0.08f, -size * 0.08f, size * 0.05f), facing);
            foreach (TextMesh text in new[] { nameTag, nameShadow })
            {
                text.characterSize = size;
                if (text.text != label)
                {
                    text.text = label;
                }
            }
        }

        private void SetVisible(bool visible)
        {
            avatar.SetVisible(visible);
            if (nameTag.gameObject.activeSelf != visible)
            {
                nameTag.gameObject.SetActive(visible);
                nameShadow.gameObject.SetActive(visible);
            }
        }
    }
}
