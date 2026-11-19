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
    /// Teammates' item effects: item sounds play from their body using the same clips, shots flash at their hand,
    /// and lit items light up where they look. All driven by the items' own code.
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

        private readonly Dictionary<string, List<UT_AudioClipHandler>> handlers = new Dictionary<string, List<UT_AudioClipHandler>>();
        private readonly Dictionary<SteamId, HeldLight[]> lights = new Dictionary<SteamId, HeldLight[]>();
        private float nextLightSend;
        private bool sentLit;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.ItemSound, HandleSound);
            NetSession.Instance.On(PacketType.HeldLight, HandleLight);
        }

        internal static bool Live => NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready;

        // ---------------------------------------------------------------- Sounds

        private static readonly Dictionary<int, int> handOfHandler = new Dictionary<int, int>();
        private static readonly Dictionary<string, float> lastSound = new Dictionary<string, float>();

        /// <summary>Which of the local player's hands this sound player belongs to, or -1.</summary>
        private static int LocalHandOf(UT_AudioClipHandler h)
        {
            int key = h.GetInstanceID();
            if (handOfHandler.TryGetValue(key, out int hand))
            {
                return hand;
            }
            if (handOfHandler.Count > 512)
            {
                handOfHandler.Clear();
            }
            hand = -1;
            HandItem item = h.GetComponentInParent<HandItem>(true);
            if (item != null && item.hand != null && item.hand.GetPlayer() == ENT_Player.playerObject)
            {
                hand = item.hand.id;
            }
            handOfHandler[key] = hand;
            return hand;
        }

        internal static void OnLocalSound(UT_AudioClipHandler h, string sound)
        {
            if (h == null || string.IsNullOrEmpty(sound) || ENT_Player.playerObject == null)
            {
                return;
            }
            int hand = LocalHandOf(h);
            if (hand < 0 || hand > 1)
            {
                return;
            }
            string prefab = Avatar.HeldItems.LocalHeld(hand);
            if (prefab.Length == 0)
            {
                return;
            }
            // Automatic weapons can ask for the same sound every frame.
            string key = hand + sound;
            float now = Time.unscaledTime;
            if (lastSound.TryGetValue(key, out float last) && now - last < 0.05f)
            {
                return;
            }
            lastSound[key] = now;
            NetSession.Instance.Send(PacketType.ItemSound, w =>
            {
                w.Write((byte)hand);
                w.Write(prefab);
                w.Write(sound);
            }, reliable: false);
        }

        private void HandleSound(SteamId origin, BinaryReader r)
        {
            int hand = r.ReadByte();
            string prefab = r.ReadString();
            string sound = r.ReadString();
            if (origin == NetSession.LocalId || !Live || PlayerSync.Instance == null
                || !PlayerSync.Instance.Remotes.TryGetValue(origin, out RemotePlayer remote) || remote == null || remote.Latest.Dead)
            {
                return;
            }
            try
            {
                UT_AudioClipHandler.AudioSet set = FindSet(prefab, sound);
                if (set != null && !set.loop && set.clips != null && set.clips.Count > 0)
                {
                    AudioClip clip = set.clips[UnityEngine.Random.Range(0, set.clips.Count)];
                    if (clip != null)
                    {
                        // Your own gun plays flat in your ears; a teammate's comes from where they are.
                        AudioManager.PlaySound(clip, remote.transform, UnityEngine.Random.Range(set.minVolume, set.maxVolume),
                            UnityEngine.Random.Range(set.minPitch, set.maxPitch), 1f);
                    }
                }
                if (sound.EndsWith(":shoot", StringComparison.OrdinalIgnoreCase))
                {
                    Flash(remote.HandPosition(hand) + Look(remote) * 0.3f);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't play a teammate's {prefab} sound {sound}: {e.Message}");
            }
        }

        private UT_AudioClipHandler.AudioSet FindSet(string prefab, string sound)
        {
            int colon = sound.IndexOf(':');
            if (colon <= 0 || colon >= sound.Length - 1)
            {
                return null;
            }
            string group = sound.Substring(0, colon), name = sound.Substring(colon + 1);
            foreach (UT_AudioClipHandler h in Handlers(prefab))
            {
                if (h == null || h.groups == null) continue;
                foreach (UT_AudioClipHandler.AudioGroup g in h.groups)
                {
                    if (g == null || g.audioSets == null || !string.Equals(g.name, group, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (UT_AudioClipHandler.AudioSet s in g.audioSets)
                    {
                        if (s != null && string.Equals(s.name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            return s;
                        }
                    }
                }
            }
            return null;
        }

        private List<UT_AudioClipHandler> Handlers(string prefab)
        {
            if (handlers.TryGetValue(prefab, out List<UT_AudioClipHandler> known))
            {
                return known;
            }
            var list = new List<UT_AudioClipHandler>();
            try
            {
                HandItem hand = CL_AssetManager.GetAssetGameObject(prefab)?.GetComponent<Item_Object>()?.itemData?.handItemAsset;
                if (hand != null)
                {
                    list.AddRange(hand.GetComponentsInChildren<UT_AudioClipHandler>(true));
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"No sounds found for {prefab}: {e.Message}");
            }
            handlers[prefab] = list;
            return list;
        }

        private static Vector3 Look(RemotePlayer remote)
        {
            return Quaternion.Euler(remote.Latest.Pitch, remote.Latest.Yaw, 0f) * Vector3.forward;
        }

        /// <summary>A short burst of light where a teammate's gun went off.</summary>
        private static void Flash(Vector3 at)
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

    [HarmonyPatch]
    internal static class ItemFxPatches
    {
        [HarmonyPatch(typeof(UT_AudioClipHandler), nameof(UT_AudioClipHandler.PlaySound), new[] { typeof(string) })]
        [HarmonyPostfix]
        private static void Played(UT_AudioClipHandler __instance, string sound) => Share(__instance, sound);

        [HarmonyPatch(typeof(UT_AudioClipHandler), nameof(UT_AudioClipHandler.PlaySound), new[] { typeof(string), typeof(float) })]
        [HarmonyPostfix]
        private static void PlayedAtVolume(UT_AudioClipHandler __instance, string sound) => Share(__instance, sound);

        private static void Share(UT_AudioClipHandler h, string sound)
        {
            if (!ItemFx.Live)
            {
                return;
            }
            try
            {
                ItemFx.OnLocalSound(h, sound);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Item sound not shared: " + e.Message);
            }
        }
    }
}
