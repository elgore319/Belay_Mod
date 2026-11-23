using System;
using System.Collections.Generic;
using System.IO;
using Belay.Net;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.Players
{
    /// <summary>
    /// Teammates' lit items light up where they look, and their shots flash at their hand. Item sounds and other
    /// effects are ItemEffects'.
    /// </summary>
    public class ItemFx : MonoBehaviour
    {
        public static ItemFx Instance { get; private set; }

        private const float LightSendInterval = 0.25f;
        private const float LightTimeout = 1.5f;

        private class HeldLight
        {
            public CL_Lamp Lamp;
            public float Seen;
        }

        private readonly Dictionary<SteamId, HeldLight[]> lights = new Dictionary<SteamId, HeldLight[]>();
        private float nextLightSend;
        private bool sentLit;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.HeldLight, HandleLight);
        }

        internal static bool Live => NetSession.InLobbyRun;

        private static Vector3 Look(RemotePlayer remote)
        {
            return Quaternion.Euler(remote.Latest.Pitch, remote.Latest.Yaw, 0f) * Vector3.forward;
        }

        /// <summary>A short burst of light where a teammate's gun went off.</summary>
        internal static void Flash(Vector3 at)
        {
            var go = new GameObject("Belay_MuzzleFlash");
            go.SetActive(false);
            go.transform.position = at;
            CL_Lamp lamp = go.AddComponent<CL_Lamp>();
            lamp.SetColor(new Color(1f, 0.82f, 0.55f));
            lamp.range = lamp.curRange = 7f;
            lamp.intensity = 1.6f;
            lamp.attentuation = 1;
            lamp.lampActive = true;
            lamp.fearMultiplier = 0f;
            go.SetActive(true);
            Destroy(go, 0.07f);
        }

        // ---------------------------------------------------------------- Held lights

        private static readonly AccessTools.FieldRef<CL_Lamp, float> IntensityMult = AccessTools.FieldRefAccess<CL_Lamp, float>("intensityMultiplier");

        private static CL_Lamp LocalLamp(int hand)
        {
            ENT_Player p = ENT_Player.playerObject;
            if (p == null || p.hands == null || p.hands.Length <= hand)
            {
                return null;
            }
            HandItem item = p.hands[hand].inventoryHand?.currentItem?.GetHandItem();
            if (item == null)
            {
                return null;
            }
            CL_Lamp best = null;
            float bestScore = 0.01f;
            foreach (CL_Lamp l in item.GetComponentsInChildren<CL_Lamp>(false))
            {
                if (l == null || !l.isActiveAndEnabled) continue;
                float score = Mathf.Abs(l.GetCurrentIntensity()) * l.range * l.color.maxColorComponent;
                if (score > bestScore)
                {
                    best = l;
                    bestScore = score;
                }
            }
            return best;
        }

        private void Update()
        {
            if (!Live || ENT_Player.playerObject == null)
            {
                return;
            }
            if (Time.unscaledTime >= nextLightSend)
            {
                nextLightSend = Time.unscaledTime + LightSendInterval;
                SendLights();
            }
        }

        private void SendLights()
        {
            CL_Lamp left = LocalLamp(0), right = LocalLamp(1);
            bool lit = left != null || right != null;
            if (!lit && !sentLit)
            {
                return;
            }
            sentLit = lit;
            NetSession.Instance.Send(PacketType.HeldLight, w =>
            {
                WriteLamp(w, left);
                WriteLamp(w, right);
            }, reliable: false);
        }

        private static void WriteLamp(BinaryWriter w, CL_Lamp l)
        {
            w.Write(l != null);
            if (l == null)
            {
                return;
            }
            Color c = l.color;
            w.Write(c.r);
            w.Write(c.g);
            w.Write(c.b);
            w.Write(l.range);
            w.Write(l.angle);
            w.Write(l.GetCurrentIntensity());
            w.Write((byte)Mathf.Clamp(l.attentuation, 0, 10));
        }

        private void HandleLight(SteamId origin, BinaryReader r)
        {
            for (int hand = 0; hand < 2; hand++)
            {
                bool on = r.ReadBoolean();
                Color c = default;
                float range = 0f, angle = 0f, intensity = 0f;
                int atten = 1;
                if (on)
                {
                    c = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    range = r.ReadSingle();
                    angle = r.ReadSingle();
                    intensity = r.ReadSingle();
                    atten = r.ReadByte();
                }
                if (origin == NetSession.LocalId || !Live)
                {
                    continue;
                }
                try
                {
                    ApplyLight(origin, hand, on, c, range, angle, intensity, atten);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Couldn't show a teammate's light: " + e.Message);
                }
            }
        }

        private void ApplyLight(SteamId origin, int hand, bool on, Color c, float range, float angle, float intensity, int atten)
        {
            if (!lights.TryGetValue(origin, out HeldLight[] pair))
            {
                pair = new HeldLight[2];
                lights[origin] = pair;
            }
            if (!on)
            {
                Remove(pair, hand);
                return;
            }
            HeldLight held = pair[hand];
            if (held == null || held.Lamp == null)
            {
                var go = new GameObject("Belay_HeldLight");
                go.SetActive(false);
                CL_Lamp lamp = go.AddComponent<CL_Lamp>();
                lamp.fearMultiplier = 0f;
                held = new HeldLight { Lamp = lamp };
                pair[hand] = held;
            }
            CL_Lamp l = held.Lamp;
            l.SetColor(c);
            l.range = l.curRange = range;
            l.angle = angle;
            l.intensity = intensity;
            l.attentuation = atten;
            l.lampActive = true;
            held.Seen = Time.unscaledTime;
            if (!l.gameObject.activeSelf)
            {
                l.gameObject.SetActive(true);
            }
            IntensityMult(l) = 1f;
        }

        private static void Remove(HeldLight[] pair, int hand)
        {
            if (pair[hand] != null)
            {
                if (pair[hand].Lamp != null) Destroy(pair[hand].Lamp.gameObject);
                pair[hand] = null;
            }
        }

        private void LateUpdate()
        {
            if (lights.Count == 0)
            {
                return;
            }
            float now = Time.unscaledTime;
            foreach (SteamId id in new List<SteamId>(lights.Keys))
            {
                HeldLight[] pair = lights[id];
                RemotePlayer remote = null;
                bool gone = !Live || PlayerSync.Instance == null || !PlayerSync.Instance.Remotes.TryGetValue(id, out remote)
                    || remote == null || remote.Latest.Dead;
                for (int hand = 0; hand < 2; hand++)
                {
                    HeldLight held = pair[hand];
                    if (held == null) continue;
                    if (gone || held.Lamp == null || now - held.Seen > LightTimeout)
                    {
                        Remove(pair, hand);
                        continue;
                    }
                    Vector3 look = Look(remote);
                    held.Lamp.transform.SetPositionAndRotation(remote.HandPosition(hand) + look * 0.25f, Quaternion.LookRotation(look));
                }
                if (pair[0] == null && pair[1] == null)
                {
                    lights.Remove(id);
                }
            }
        }
    }
}
