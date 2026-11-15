using Belay.Net;
using Belay.Players.Avatar;
using UnityEngine;

namespace Belay.Players
{
    /// <summary>
    /// A teammate's body: the procedural climber from <see cref="ClimberAvatar"/> plus a name tag, played back
    /// smoothly from its network snapshots (see <see cref="MotionBuffer"/>). It has no colliders, so it never blocks the local player.
    /// </summary>
    public class RemotePlayer : MonoBehaviour
    {
        public Steamworks.SteamId SteamId;
        public string DisplayName;

        /// <summary>Never solid (e.g. the test bot camera's stand-in for you, which sits where you are).</summary>
        public bool Ghost;
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
        private RemoteBody body;
        private bool bodyFailed;
        private readonly MotionBuffer motion = new MotionBuffer();

        public Vector3 LocalPosition => transform.position;
        public bool IsHanging => Latest.LeftHanging || Latest.RightHanging;

        /// <summary>Where a hand is drawn on this PC (0 = left, 1 = right).</summary>
        public Vector3 HandPosition(int index) => index == 0 ? leftHand : rightHand;

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
            motion.Add(state, LastReceived);
            if (!hasState)
            {
                hasState = true;
                Snap();
            }
        }

        /// <summary>Shows an item action (a hammer swing, setting a piton...) on this teammate's arm.</summary>
        public void PlayAction(int hand, PlayerAction action)
        {
            avatar?.PlayAction(hand, action);
        }

        private void Snap()
        {
            motion.Reset();
            MotionBuffer.Frame frame = motion.Sample(Time.unscaledTime, 0f);
            transform.position = frame.Body;
            leftHand = frame.LeftHand;
            rightHand = frame.RightHand;
            velocity = Vector3.zero;
        }

        private void LateUpdate()
        {
            bool visible = hasState && WorldSpace.Ready && !Latest.Dead;
            SetVisible(visible);
            UpdateBody(visible);
            if (!visible)
            {
                return;
            }

            float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            if ((MotionBuffer.Body(Latest) - transform.position).sqrMagnitude > 25f * 25f)
            {
                Snap();
            }
            // Drawn a few hundredths of a second in the past, on a curve through the snapshots, so the
            // body glides at an even pace however unevenly the packets arrive.
            MotionBuffer.Frame frame = motion.Sample(Time.unscaledTime, dt);
            PlayerState shown = frame.State;
            transform.position = frame.Body;
            leftHand = frame.LeftHand;
            rightHand = frame.RightHand;
            // The avatar's walk/fall blending reads this; a light filter keeps footsteps from twitching.
            velocity = Vector3.Lerp(velocity, frame.Velocity, 1f - Mathf.Exp(-12f * dt));

            HandSkins.PublishTick();
            avatar.Pose(transform.position, frame.Yaw, frame.Pitch, leftHand, rightHand,
                shown.LeftHanging, shown.RightHanging, velocity, shown.InventoryOpen, dt,
                HandSkins.SkinOf(SteamId, 0), HandSkins.SkinOf(SteamId, 1), shown.LeftItem, shown.RightItem,
                shown.Crouched);

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

        /// <summary>The solid, grippable capsule (made once the level is up, since its layer comes from the player).</summary>
        private void UpdateBody(bool visible)
        {
            bool solid = visible && Plugin.SolidTeammates.Value && !Ghost;
            if (body == null)
            {
                if (!solid)
                {
                    return;
                }
                if (bodyFailed)
                {
                    return;
                }
                try
                {
                    body = RemoteBody.Create(this);
                }
                catch (System.Exception e)
                {
                    bodyFailed = true;
                    Plugin.Log.LogWarning($"Couldn't make {DisplayName} solid: {e}");
                    return;
                }
            }
            body.SetSolid(solid);
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
