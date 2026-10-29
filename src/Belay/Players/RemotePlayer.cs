using Belay.Net;
using UnityEngine;

namespace Belay.Players
{
    /// <summary>
    /// Stand-in body for a teammate: a capsule, two hands and a name tag, smoothed toward the latest
    /// network snapshot. Colliders are stripped so it never blocks the local player.
    /// </summary>
    public class RemotePlayer : MonoBehaviour
    {
        private const float BodyHeight = 1.6f;

        public Steamworks.SteamId SteamId;
        public string DisplayName;
        public PlayerState Latest;
        public float LastReceived;

        private Transform body;
        private Transform leftHand;
        private Transform rightHand;
        private TextMesh nameTag;
        private Renderer leftHandRenderer;
        private Renderer rightHandRenderer;
        private bool hasState;

        public Vector3 LocalPosition => transform.position;
        public bool IsHanging => Latest.LeftHanging || Latest.RightHanging;

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

        private void Build(Color color)
        {
            body = MakePrimitive(PrimitiveType.Capsule, transform, new Vector3(0.5f, BodyHeight * 0.5f, 0.5f), color);
            body.localPosition = Vector3.zero;

            leftHand = MakePrimitive(PrimitiveType.Sphere, null, Vector3.one * 0.18f, color);
            rightHand = MakePrimitive(PrimitiveType.Sphere, null, Vector3.one * 0.18f, color);
            leftHand.SetParent(transform, true);
            rightHand.SetParent(transform, true);
            leftHandRenderer = leftHand.GetComponent<Renderer>();
            rightHandRenderer = rightHand.GetComponent<Renderer>();

            var tag = new GameObject("NameTag");
            tag.transform.SetParent(transform, false);
            tag.transform.localPosition = Vector3.up * 0.6f;
            nameTag = tag.AddComponent<TextMesh>();
            nameTag.text = DisplayName;
            nameTag.characterSize = 0.05f;
            nameTag.fontSize = 48;
            nameTag.anchor = TextAnchor.LowerCenter;
            nameTag.alignment = TextAlignment.Center;
            nameTag.color = color;
        }

        private static Transform MakePrimitive(PrimitiveType type, Transform parent, Vector3 scale, Color color)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            Object.Destroy(go.GetComponent<Collider>());
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }
            go.transform.localScale = scale;
            var renderer = go.GetComponent<Renderer>();
            renderer.material.color = color;
            return go.transform;
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
            leftHand.position = WorldSpace.ToLocal(Latest.LeftHand);
            rightHand.position = WorldSpace.ToLocal(Latest.RightHand);
        }

        private void Update()
        {
            bool visible = hasState && WorldSpace.Ready && !Latest.Dead;
            SetVisible(visible);
            if (!visible)
            {
                return;
            }

            float t = 1f - Mathf.Exp(-15f * Time.unscaledDeltaTime);
            Vector3 target = WorldSpace.ToLocal(Latest.Position);
            if ((target - transform.position).sqrMagnitude > 25f * 25f)
            {
                Snap();
            }
            transform.position = Vector3.Lerp(transform.position, target, t);
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0f, Latest.Yaw, 0f), t);
            leftHand.position = Vector3.Lerp(leftHand.position, WorldSpace.ToLocal(Latest.LeftHand), t);
            rightHand.position = Vector3.Lerp(rightHand.position, WorldSpace.ToLocal(Latest.RightHand), t);

            // Gripping hands go white so you can read who is holding on.
            leftHandRenderer.material.color = Latest.LeftHanging ? Color.white : nameTag.color;
            rightHandRenderer.material.color = Latest.RightHanging ? Color.white : nameTag.color;

            Camera cam = Camera.main;
            if (cam != null)
            {
                nameTag.transform.rotation = Quaternion.LookRotation(nameTag.transform.position - cam.transform.position);
            }
        }

        private void SetVisible(bool visible)
        {
            if (body.gameObject.activeSelf != visible)
            {
                body.gameObject.SetActive(visible);
                leftHand.gameObject.SetActive(visible);
                rightHand.gameObject.SetActive(visible);
                nameTag.gameObject.SetActive(visible);
            }
        }
    }
}
