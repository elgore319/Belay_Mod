using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Belay.Net;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.Players
{
    /// <summary>
    /// Everything a teammate's item shows or plays, seen and heard by everyone: the exact sounds (from their body, or
    /// where they hit), the particles their item gives off (sparks, frost, the eye's glow), effects it leaves in the
    /// world (hit sparks, blood, marks on the wall, the game's own particle bursts) and the grappling tongue.
    /// Shots, blasts and lights already have their own sync (ThrownSync, Blasts, ItemFx); this covers the rest.
    /// </summary>
    public class ItemEffects : MonoBehaviour
    {
        public static ItemEffects Instance { get; private set; }

        private const float StateInterval = 0.15f;
        private const float StateTimeout = 1f;
        private const float BodyRadius = 2.5f;

        private enum Fx : byte
        {
            Particle = 1,
            Spawn = 2,
            Decal = 3,
            FxParticle = 4,
            FxTracer = 5,
            Flash = 6,
        }

        private void Awake()
        {
            Instance = this;
            ItemEffectsPatcher.Apply();
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.ItemSound, HandleSound);
            NetSession.Instance.On(PacketType.ItemEffect, HandleEffect);
            NetSession.Instance.On(PacketType.HeldEffects, HandleState);
        }

        internal static bool Live => NetSession.InLobbyRun;

        // ================================================================ Which item is this?

        private struct Owner
        {
            public bool IsPart;
            public HandItem Item;
            public string Path;
        }

        private static readonly Dictionary<int, Owner> owners = new Dictionary<int, Owner>();

        /// <summary>The local player's held item a part (particles, a sound source) belongs to, with its path inside it.</summary>
        internal static bool LocalOwner(Component c, out int hand, out HandItem item, out string path)
        {
            hand = -1;
            item = null;
            path = null;
            if (c == null || ENT_Player.playerObject == null)
            {
                return false;
            }
            int key = c.GetInstanceID();
            if (!owners.TryGetValue(key, out Owner o) || o.IsPart && o.Item == null)
            {
                if (owners.Count > 4096)
                {
                    owners.Clear();
                }
                // Looked up once per object: most particles and sounds in the game aren't part of any item.
                HandItem hi = c.GetComponentInParent<HandItem>(true);
                o = hi == null ? new Owner() : new Owner { IsPart = true, Item = hi, Path = PathIn(hi.transform, c.transform) };
                owners[key] = o;
            }
            if (!o.IsPart || o.Item == null)
            {
                return false;
            }
            hand = HandOf(o.Item);
            if (hand < 0)
            {
                return false;
            }
            item = o.Item;
            path = o.Path;
            return true;
        }

        /// <summary>Which of the local player's hands holds this item, or -1.</summary>
        internal static int HandOf(HandItem hi)
        {
            if (hi == null || hi.hand == null || ENT_Player.playerObject == null)
            {
                return -1;
            }
            try
            {
                if (hi.hand.GetPlayer() != ENT_Player.playerObject) return -1;
                int id = hi.hand.id;
                return id == 0 || id == 1 ? id : -1;
            }
            catch
            {
                return -1;
            }
        }

        private static string PathIn(Transform root, Transform t)
        {
            var parts = new List<string>();
            while (t != null && t != root)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            if (t == null)
            {
                return null;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string Held(int hand) => Avatar.HeldItems.LocalHeld(hand);

        private static Transform Cam => Camera.main != null ? Camera.main.transform : null;

        private static bool NearBody(Vector3 p)
        {
            ENT_Player me = ENT_Player.playerObject;
            return me != null && (p - me.transform.position).sqrMagnitude < BodyRadius * BodyRadius
                || Cam != null && (p - Cam.position).sqrMagnitude < BodyRadius * BodyRadius;
        }

        private static bool OnLocalBody(Transform t)
        {
            ENT_Player me = ENT_Player.playerObject;
            return t != null && me != null && (t == me.transform || t.IsChildOf(me.transform) || Cam != null && t.IsChildOf(Cam));
        }

        // ================================================================ Sounds

        private static readonly Dictionary<string, float> lastSound = new Dictionary<string, float>();

        /// <summary>A sound the local player's item just made. Sent as the exact clip, so everyone hears the same one.</summary>
        internal static void OnLocalClip(AudioClip clip, int hand, bool atBody, Vector3 at, float volume, float pitch)
        {
            if (clip == null || hand < 0 || hand > 1)
            {
                return;
            }
            string prefab = Held(hand);
            string name = clip.name;
            string key = hand + name;
            float now = Time.unscaledTime;
            if (lastSound.TryGetValue(key, out float last) && now - last < 0.05f)
            {
                return;
            }
            lastSound[key] = now;
            if (lastSound.Count > 512) lastSound.Clear();
            Vector3 shared = atBody ? Vector3.zero : WorldSpace.ToShared(at);
            NetSession.Instance.Send(PacketType.ItemSound, w =>
            {
                w.Write((byte)hand);
                w.Write(prefab);
                w.Write(name);
                w.Write(atBody);
                w.WriteVector(shared);
                w.Write(volume);
                w.Write(pitch);
            }, reliable: false);
        }

        private void HandleSound(SteamId origin, BinaryReader r)
        {
            int hand = r.ReadByte();
            string prefab = r.ReadString();
            string name = r.ReadString();
            bool atBody = r.ReadBoolean();
            Vector3 at = r.ReadVector();
            float volume = r.ReadSingle();
            float pitch = r.ReadSingle();
            if (!Remote(origin, out RemotePlayer remote))
            {
                return;
            }
            try
            {
                AudioClip clip = Clip(prefab, name);
                if (clip == null)
                {
                    return;
                }
                // Your own item plays flat in your ears; a teammate's comes from where they are.
                if (atBody)
                {
                    AudioManager.PlaySound(clip, remote.transform, volume, pitch, 1f);
                }
                else
                {
                    AudioManager.PlaySound(clip, WorldSpace.ToLocal(at), volume, pitch, 1f);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't play a teammate's {prefab} sound {name}: {e.Message}");
            }
        }

        // ================================================================ One-off effects

        private static readonly Dictionary<string, float> lastFx = new Dictionary<string, float>();

        private static bool Throttle(string key, float gap)
        {
            float now = Time.unscaledTime;
            if (lastFx.TryGetValue(key, out float last) && now - last < gap)
            {
                return true;
            }
            if (lastFx.Count > 512) lastFx.Clear();
            lastFx[key] = now;
            return false;
        }

        private static void SendFx(Fx kind, int hand, Action<BinaryWriter> body)
        {
            string prefab = hand >= 0 ? Held(hand) : "";
            NetSession.Instance.Send(PacketType.ItemEffect, w =>
            {
                w.Write((byte)kind);
                w.Write((byte)Mathf.Clamp(hand, 0, 1));
                w.Write(prefab);
                body(w);
            }, reliable: false);
        }

        private static readonly FieldInfo EmitParticle = AccessTools.Field(typeof(ParticleSystem.EmitParams), "m_Particle");
        private static readonly FieldInfo EmitPosSet = AccessTools.Field(typeof(ParticleSystem.EmitParams), "m_PositionSet");
        private static readonly FieldInfo EmitVelSet = AccessTools.Field(typeof(ParticleSystem.EmitParams), "m_VelocitySet");
        private static readonly FieldInfo EmitLifeSet = AccessTools.Field(typeof(ParticleSystem.EmitParams), "m_StartLifetimeSet");

        /// <summary>
        /// A particle system in the local player's item played or burst. Mode 0 = Play, 1 = Emit(count),
        /// 2 = Emit with set velocity/position/lifetime.
        /// </summary>
        internal static void OnLocalParticles(ParticleSystem ps, int mode, int count, object emitParams)
        {
            if (!LocalOwner(ps, out int hand, out HandItem _, out string path) || path == null)
            {
                return;
            }
            if (mode == 0 && ps.main.loop)
            {
                return; // looping ones are mirrored by the held-state stream
            }
            if (Throttle(hand + path + mode, mode == 2 ? 0.02f : 0.05f))
            {
                return;
            }
            Transform cam = Cam;
            Vector3 pos = ps.transform.position;
            bool world = !NearBody(pos);
            Vector3 where;
            Quaternion rot;
            if (world || cam == null)
            {
                where = WorldSpace.ToShared(pos);
                rot = ps.transform.rotation;
                world = true;
            }
            else
            {
                where = cam.InverseTransformPoint(pos);
                rot = Quaternion.Inverse(cam.rotation) * ps.transform.rotation;
            }
            bool posSet = false, velSet = false, lifeSet = false;
            Vector3 pPos = Vector3.zero, pVel = Vector3.zero;
            float life = 0f;
            if (mode == 2 && emitParams != null)
            {
                try
                {
                    var p = (ParticleSystem.Particle)EmitParticle.GetValue(emitParams);
                    posSet = (bool)EmitPosSet.GetValue(emitParams);
                    velSet = (bool)EmitVelSet.GetValue(emitParams);
                    lifeSet = (bool)EmitLifeSet.GetValue(emitParams);
                    pPos = p.position;
                    pVel = p.velocity;
                    life = p.startLifetime;
                    if (posSet && ps.main.simulationSpace == ParticleSystemSimulationSpace.World)
                    {
                        pPos = WorldSpace.ToShared(pPos);
                    }
                }
                catch
                {
                    posSet = velSet = lifeSet = false;
                }
            }
            float scale = ps.transform.lossyScale.x;
            SendFx(Fx.Particle, hand, w =>
            {
                w.Write(path);
                w.Write((byte)mode);
                w.Write((short)Mathf.Clamp(count, 0, 500));
                w.Write(world);
                w.WriteVector(where);
                WriteRot(w, rot);
                w.Write(scale);
                w.Write(posSet);
                if (posSet) w.WriteVector(pPos);
                w.Write(velSet);
                if (velSet) w.WriteVector(pVel);
                w.Write(lifeSet);
                if (lifeSet) w.Write(life);
            });
        }

        /// <summary>An effect the local player's item put into the world (hit sparks, blood puffs).</summary>
        internal static void OnLocalSpawn(int hand, GameObject original, GameObject made)
        {
            if (original == null || made == null || !IsEffect(made))
            {
                return;
            }
            string name = original.name;
            Vector3 at = WorldSpace.ToShared(made.transform.position);
            Quaternion rot = made.transform.rotation;
            SendFx(Fx.Spawn, hand, w =>
            {
                w.Write(name);
                w.WriteVector(at);
                WriteRot(w, rot);
            });
        }

        /// <summary>Only pure effects are copied: anything that is an item, a shot, a blast or something solid has its own sync.</summary>
        private static bool IsEffect(GameObject go)
        {
            if (go.GetComponentInChildren<ParticleSystem>(true) == null && go.GetComponentInChildren<CL_Lamp>(true) == null)
            {
                return false;
            }
            if (go.GetComponentInChildren<GameEntity>(true) != null || go.GetComponentInChildren<Projectile>(true) != null
                || go.GetComponentInChildren<Item_Object>(true) != null || go.GetComponentInChildren<UT_Explosion>(true) != null
                || go.GetComponentInChildren<Rigidbody>(true) != null || go.GetComponentInChildren<Canvas>(true) != null)
            {
                return false;
            }
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
            {
                if (c != null && !c.isTrigger) return false;
            }
            return !OnLocalBody(go.transform);
        }

        internal static void OnLocalDecal(int hand, FX_DecalShared shared, Vector3 pos, Vector3 normal, Vector3 up)
        {
            if (shared == null)
            {
                return;
            }
            string name = shared.name;
            Vector3 at = WorldSpace.ToShared(pos);
            SendFx(Fx.Decal, hand, w =>
            {
                w.Write(name);
                w.WriteVector(at);
                w.WriteVector(normal);
                w.WriteVector(up);
            });
        }

        internal static void OnLocalFxParticle(int hand, string id, Vector3 pos, int amount, float radius, Vector3 velocity, Quaternion rot)
        {
            if (string.IsNullOrEmpty(id))
            {
                return;
            }
            Vector3 at = WorldSpace.ToShared(pos);
            SendFx(Fx.FxParticle, hand, w =>
            {
                w.Write(id);
                w.WriteVector(at);
                w.Write((short)Mathf.Clamp(amount, 0, 500));
                w.Write(radius);
                w.WriteVector(velocity);
                WriteRot(w, rot);
            });
        }

        internal static void OnLocalTracer(int hand, string id, Vector3 from, Vector3 to, float speed, string hitId, string muzzleId)
        {
            if (string.IsNullOrEmpty(id))
            {
                return;
            }
            Vector3 a = WorldSpace.ToShared(from), b = WorldSpace.ToShared(to);
            SendFx(Fx.FxTracer, hand, w =>
            {
                w.Write(id);
                w.WriteVector(a);
                w.WriteVector(b);
                w.Write(speed);
                w.Write(hitId ?? "");
                w.Write(muzzleId ?? "");
            });
        }

        internal static void OnLocalShot(int hand)
        {
            SendFx(Fx.Flash, hand, w => { });
        }

        private static void WriteRot(BinaryWriter w, Quaternion q)
        {
            w.Write(q.x);
            w.Write(q.y);
            w.Write(q.z);
            w.Write(q.w);
        }

        private static Quaternion ReadRot(BinaryReader r)
        {
            var q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            return q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f ? Quaternion.identity : Quaternion.Normalize(q);
        }

        private void HandleEffect(SteamId origin, BinaryReader r)
        {
            var kind = (Fx)r.ReadByte();
            int hand = r.ReadByte();
            string prefab = r.ReadString();
            try
            {
                switch (kind)
                {
                    case Fx.Particle:
                        ReadParticle(origin, hand, prefab, r);
                        break;
                    case Fx.Spawn:
                    {
                        string name = r.ReadString();
                        Vector3 at = r.ReadVector();
                        Quaternion rot = ReadRot(r);
                        if (Remote(origin, out RemotePlayer _)) Spawn(prefab, name, WorldSpace.ToLocal(at), rot);
                        break;
                    }
                    case Fx.Decal:
                    {
                        string name = r.ReadString();
                        Vector3 at = r.ReadVector(), normal = r.ReadVector(), up = r.ReadVector();
                        if (Remote(origin, out RemotePlayer _)) Decal(name, WorldSpace.ToLocal(at), normal, up);
                        break;
                    }
                    case Fx.FxParticle:
                    {
                        string id = r.ReadString();
                        Vector3 at = WorldSpace.ToLocal(r.ReadVector());
                        int amount = r.ReadInt16();
                        float radius = r.ReadSingle();
                        Vector3 vel = r.ReadVector();
                        Quaternion rot = ReadRot(r);
                        if (!Remote(origin, out RemotePlayer _)) break;
                        using (Replaying())
                        {
                            if (radius > 0f) FXManager.PlayParticleWithRadius(id, at, radius, amount, vel);
                            else FXManager.PlayParticleWithRotation(id, at, amount, rot);
                        }
                        break;
                    }
                    case Fx.FxTracer:
                    {
                        string id = r.ReadString();
                        Vector3 a = WorldSpace.ToLocal(r.ReadVector()), b = WorldSpace.ToLocal(r.ReadVector());
                        float speed = r.ReadSingle();
                        string hitId = r.ReadString(), muzzleId = r.ReadString();
                        if (!Remote(origin, out RemotePlayer _)) break;
                        using (Replaying())
                        {
                            FXManager.PlayTracer(id, a, b, speed, hitId, muzzleId);
                        }
                        break;
                    }
                    case Fx.Flash:
                        if (Remote(origin, out RemotePlayer shooter))
                        {
                            ItemFx.Flash(shooter.HandPosition(hand) + Look(shooter) * 0.3f);
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't show a teammate's {prefab} effect {kind}: {e.Message}");
            }
        }

        private void ReadParticle(SteamId origin, int hand, string prefab, BinaryReader r)
        {
            string path = r.ReadString();
            int mode = r.ReadByte();
            int count = r.ReadInt16();
            bool world = r.ReadBoolean();
            Vector3 where = r.ReadVector();
            Quaternion rot = ReadRot(r);
            float scale = r.ReadSingle();
            bool posSet = r.ReadBoolean();
            Vector3 pPos = posSet ? r.ReadVector() : Vector3.zero;
            bool velSet = r.ReadBoolean();
            Vector3 pVel = velSet ? r.ReadVector() : Vector3.zero;
            bool lifeSet = r.ReadBoolean();
            float life = lifeSet ? r.ReadSingle() : 0f;
            if (!Remote(origin, out RemotePlayer remote))
            {
                return;
            }
            Mirror m = GetMirror(origin, hand, prefab, path, false);
            if (m == null)
            {
                return;
            }
            m.Scale(scale);
            m.Place(remote, hand, world, world ? WorldSpace.ToLocal(where) : where, rot);
            m.Seen = Time.unscaledTime;
            if (mode == 0)
            {
                m.System.Play(true);
            }
            else if (mode == 1)
            {
                m.System.Emit(Mathf.Max(1, count));
            }
            else
            {
                var p = new ParticleSystem.EmitParams();
                if (posSet)
                {
                    p.position = m.System.main.simulationSpace == ParticleSystemSimulationSpace.World ? WorldSpace.ToLocal(pPos) : pPos;
                }
                else
                {
                    p.applyShapeToPosition = true;
                }
                if (velSet) p.velocity = pVel;
                if (lifeSet) p.startLifetime = life;
                m.System.Emit(p, Mathf.Max(1, count));
            }
        }

        // ================================================================ Held state (looping particles, looping sounds, grapple tongue)

        private float nextState;
        private bool sentState;

        private struct LoopFx
        {
            public string Path;
            public float Rate;
            public Vector3 Where;
            public Quaternion Rot;
            public float Scale;
        }

        private struct LoopSound
        {
            public string Path;
            public string Clip;
            public float Volume;
            public float Pitch;
        }

        private static readonly AccessTools.FieldRef<HandItem_GrapplingHook, bool> Grappling = AccessTools.FieldRefAccess<HandItem_GrapplingHook, bool>("isGrappling");
        private static readonly AccessTools.FieldRef<HandItem_GrapplingHook, bool> HasPoint = AccessTools.FieldRefAccess<HandItem_GrapplingHook, bool>("hasGrappledPoint");

        private static HandItem LocalItem(int hand)
        {
            ENT_Player p = ENT_Player.playerObject;
            if (p == null || p.hands == null || p.hands.Length <= hand)
            {
                return null;
            }
            try
            {
                return p.hands[hand].inventoryHand?.currentItem?.GetHandItem();
            }
            catch
            {
                return null;
            }
        }

        private void Update()
        {
            if (!Live || ENT_Player.playerObject == null || Time.unscaledTime < nextState)
            {
                return;
            }
            nextState = Time.unscaledTime + StateInterval;
            try
            {
                SendState();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Held effects not shared: " + e.Message);
                nextState = Time.unscaledTime + 5f;
            }
        }

        private void SendState()
        {
            var fx = new List<LoopFx>[2];
            var sounds = new List<LoopSound>[2];
            var tongue = new bool[2];
            var tip = new Vector3[2];
            var tongueScale = new Vector3[2];
            var prefabs = new string[2];
            bool any = false;
            Transform cam = Cam;
            for (int hand = 0; hand < 2; hand++)
            {
                fx[hand] = new List<LoopFx>();
                sounds[hand] = new List<LoopSound>();
                prefabs[hand] = Held(hand);
                HandItem item = LocalItem(hand);
                if (item == null || !item.isActiveAndEnabled || cam == null)
                {
                    continue;
                }
                foreach (ParticleSystem ps in item.GetComponentsInChildren<ParticleSystem>(false))
                {
                    if (ps == null || !ps.isEmitting || !ps.main.loop || HasLoopingParent(ps, item.transform))
                    {
                        continue;
                    }
                    string path = PathIn(item.transform, ps.transform);
                    if (path == null) continue;
                    fx[hand].Add(new LoopFx
                    {
                        Path = path,
                        Rate = ps.emission.rateOverTimeMultiplier,
                        Where = cam.InverseTransformPoint(ps.transform.position),
                        Rot = Quaternion.Inverse(cam.rotation) * ps.transform.rotation,
                        Scale = ps.transform.lossyScale.x,
                    });
                }
                foreach (AudioSource src in item.GetComponentsInChildren<AudioSource>(false))
                {
                    if (src == null || !src.isPlaying || !src.loop || src.clip == null || src.volume <= 0.001f)
                    {
                        continue;
                    }
                    string path = PathIn(item.transform, src.transform);
                    if (path == null) continue;
                    sounds[hand].Add(new LoopSound { Path = path, Clip = src.clip.name, Volume = src.volume, Pitch = src.pitch });
                }
                if (item is HandItem_GrapplingHook hook && Grappling(hook) && HasPoint(hook) && hook.hookMesh != null)
                {
                    tongue[hand] = true;
                    tip[hand] = WorldSpace.ToShared(hook.grapplePoint);
                    tongueScale[hand] = hook.hookMesh.transform.lossyScale;
                }
                any |= fx[hand].Count > 0 || sounds[hand].Count > 0 || tongue[hand];
            }
            if (!any && !sentState)
            {
                return;
            }
            sentState = any;
            NetSession.Instance.Send(PacketType.HeldEffects, w =>
            {
                for (int hand = 0; hand < 2; hand++)
                {
                    w.Write(prefabs[hand]);
                    w.Write((byte)Mathf.Min(fx[hand].Count, 16));
                    for (int i = 0; i < fx[hand].Count && i < 16; i++)
                    {
                        LoopFx f = fx[hand][i];
                        w.Write(f.Path);
                        w.Write(f.Rate);
                        w.WriteVector(f.Where);
                        WriteRot(w, f.Rot);
                        w.Write(f.Scale);
                    }
                    w.Write((byte)Mathf.Min(sounds[hand].Count, 8));
                    for (int i = 0; i < sounds[hand].Count && i < 8; i++)
                    {
                        LoopSound s = sounds[hand][i];
                        w.Write(s.Path);
                        w.Write(s.Clip);
                        w.Write(s.Volume);
                        w.Write(s.Pitch);
                    }
                    w.Write(tongue[hand]);
                    if (tongue[hand])
                    {
                        w.WriteVector(tip[hand]);
                        w.WriteVector(tongueScale[hand]);
                    }
                }
            }, reliable: false);
        }

        private static bool HasLoopingParent(ParticleSystem ps, Transform root)
        {
            Transform t = ps.transform.parent;
            while (t != null && t != root.parent)
            {
                ParticleSystem up = t.GetComponent<ParticleSystem>();
                if (up != null && up.isEmitting && up.main.loop) return true;
                if (t == root) break;
                t = t.parent;
            }
            return false;
        }

        private void HandleState(SteamId origin, BinaryReader r)
        {
            float now = Time.unscaledTime;
            bool ok = Remote(origin, out RemotePlayer remote);
            for (int hand = 0; hand < 2; hand++)
            {
                string prefab = r.ReadString();
                int nFx = r.ReadByte();
                for (int i = 0; i < nFx; i++)
                {
                    string path = r.ReadString();
                    float rate = r.ReadSingle();
                    Vector3 where = r.ReadVector();
                    Quaternion rot = ReadRot(r);
                    float scale = r.ReadSingle();
                    if (!ok) continue;
                    try
                    {
                        Mirror m = GetMirror(origin, hand, prefab, path, true);
                        if (m == null) continue;
                        m.Scale(scale);
                        m.Place(remote, hand, false, where, rot);
                        ParticleSystem.EmissionModule em = m.System.emission;
                        em.rateOverTimeMultiplier = rate;
                        if (!m.System.isEmitting) m.System.Play(true);
                        m.Seen = now;
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"Couldn't show a teammate's {prefab} particles: {e.Message}");
                    }
                }
                int nSounds = r.ReadByte();
                for (int i = 0; i < nSounds; i++)
                {
                    string path = r.ReadString();
                    string clip = r.ReadString();
                    float volume = r.ReadSingle();
                    float pitch = r.ReadSingle();
                    if (!ok) continue;
                    try
                    {
                        LoopSource(origin, hand, prefab, path, clip, volume, pitch, now, remote);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"Couldn't play a teammate's {prefab} loop: {e.Message}");
                    }
                }
                bool tongue = r.ReadBoolean();
                if (tongue)
                {
                    Vector3 tip = r.ReadVector();
                    Vector3 scale = r.ReadVector();
                    if (ok)
                    {
                        try
                        {
                            Tongue(origin, hand, prefab, WorldSpace.ToLocal(tip), scale, now);
                        }
                        catch (Exception e)
                        {
                            Plugin.Log.LogWarning("Couldn't show a teammate's grapple: " + e.Message);
                        }
                    }
                }
            }
        }

        // ================================================================ Remote copies

        /// <summary>A copy of one of a teammate's item particle systems, kept at their hand (or where it played).</summary>
        private class Mirror
        {
            public SteamId Origin;
            public int Hand;
            public ParticleSystem System;
            public bool Looping;
            public float Seen;
            public bool FollowHand;
            public Vector3 Offset;
            public Quaternion Rot;
            private float scale = -1f;

            public void Scale(float s)
            {
                // Only systems that scale with what they're attached to took the item's size; the rest keep their own.
                if (System.main.scalingMode != ParticleSystemScalingMode.Hierarchy)
                {
                    return;
                }
                if (s > 0.0001f && Mathf.Abs(s - scale) > 0.0001f)
                {
                    scale = s;
                    System.transform.localScale = Vector3.one * s;
                }
            }

            public void Place(RemotePlayer remote, int hand, bool world, Vector3 where, Quaternion rot)
            {
                FollowHand = !world;
                Offset = where;
                Rot = rot;
                if (world)
                {
                    System.transform.SetPositionAndRotation(where, rot);
                }
                else
                {
                    Follow(remote);
                }
            }

            public void Follow(RemotePlayer remote)
            {
                // Just in front of the hand holding the item, turned the way it faces for them.
                Quaternion look = Quaternion.Euler(remote.Latest.Pitch, remote.Latest.Yaw, 0f);
                Vector3 pos = remote.HandPosition(Hand) + look * Vector3.forward * 0.2f;
                System.transform.SetPositionAndRotation(pos, look * Rot);
            }
        }

        private readonly Dictionary<string, Mirror> mirrors = new Dictionary<string, Mirror>();
        private readonly Dictionary<string, HandItem> handAssets = new Dictionary<string, HandItem>();

        private HandItem HandAsset(string prefab)
        {
            if (string.IsNullOrEmpty(prefab))
            {
                return null;
            }
            if (handAssets.TryGetValue(prefab, out HandItem known))
            {
                return known;
            }
            HandItem hand = null;
            try
            {
                hand = CL_AssetManager.GetAssetGameObject(prefab)?.GetComponent<Item_Object>()?.itemData?.handItemAsset;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"No hand item found for {prefab}: {e.Message}");
            }
            handAssets[prefab] = hand;
            return hand;
        }

        private Mirror GetMirror(SteamId origin, int hand, string prefab, string path, bool looping)
        {
            string key = origin.Value + "|" + hand + "|" + prefab + "|" + path + "|" + looping;
            if (mirrors.TryGetValue(key, out Mirror m) && m != null && m.System != null)
            {
                return m;
            }
            HandItem asset = HandAsset(prefab);
            if (asset == null)
            {
                return null;
            }
            Transform src = path.Length == 0 ? asset.transform : asset.transform.Find(path);
            ParticleSystem srcPs = src != null ? src.GetComponent<ParticleSystem>() : null;
            if (srcPs == null)
            {
                mirrors[key] = null;
                return null;
            }
            GameObject copy = Instantiate(src.gameObject);
            copy.name = "Belay_ItemFx_" + prefab + "_" + src.name;
            CleanCopy(copy);
            ParticleSystem ps = copy.GetComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            m = new Mirror { Origin = origin, Hand = hand, System = ps, Looping = looping, Seen = Time.unscaledTime };
            mirrors[key] = m;
            return m;
        }

        /// <summary>Just the effect: the item's own sprites and scripts go, and it shows on the normal camera.</summary>
        private static void CleanCopy(GameObject copy)
        {
            copy.transform.SetParent(null, false);
            foreach (MonoBehaviour b in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (b != null && !(b is CL_Lamp)) DestroyImmediate(b);
            }
            foreach (Renderer rend in copy.GetComponentsInChildren<Renderer>(true))
            {
                if (rend != null && !(rend is ParticleSystemRenderer)) rend.enabled = false;
            }
            foreach (Collider c in copy.GetComponentsInChildren<Collider>(true))
            {
                if (c != null) c.enabled = false;
            }
            foreach (AudioSource a in copy.GetComponentsInChildren<AudioSource>(true))
            {
                if (a != null) a.enabled = false;
            }
            foreach (Animator a in copy.GetComponentsInChildren<Animator>(true))
            {
                if (a != null) a.enabled = false;
            }
            foreach (Transform t in copy.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = 0;
            }
            copy.SetActive(true);
        }

        private class LoopAudio
        {
            public AudioSource Source;
            public float Seen;
            public int Hand;
            public SteamId Origin;
        }

        private readonly Dictionary<string, LoopAudio> loops = new Dictionary<string, LoopAudio>();

        private void LoopSource(SteamId origin, int hand, string prefab, string path, string clipName, float volume, float pitch, float now, RemotePlayer remote)
        {
            string key = origin.Value + "|" + hand + "|" + prefab + "|" + path;
            if (!loops.TryGetValue(key, out LoopAudio la) || la.Source == null)
            {
                var go = new GameObject("Belay_ItemLoop_" + prefab);
                AudioSource s = go.AddComponent<AudioSource>();
                HandItem asset = HandAsset(prefab);
                Transform t = asset == null ? null : path.Length == 0 ? asset.transform : asset.transform.Find(path);
                AudioSource like = t != null ? t.GetComponent<AudioSource>() : null;
                if (like != null)
                {
                    s.outputAudioMixerGroup = like.outputAudioMixerGroup;
                    s.rolloffMode = like.rolloffMode;
                    s.minDistance = like.minDistance;
                    s.maxDistance = Mathf.Max(like.maxDistance, 15f);
                }
                else
                {
                    s.minDistance = 1f;
                    s.maxDistance = 25f;
                }
                s.playOnAwake = false;
                s.loop = true;
                s.spatialBlend = 1f;
                la = new LoopAudio { Source = s, Hand = hand, Origin = origin };
                loops[key] = la;
            }
            AudioSource src = la.Source;
            if (src.clip == null || src.clip.name != clipName)
            {
                AudioClip clip = Clip(prefab, clipName);
                if (clip == null)
                {
                    return;
                }
                src.clip = clip;
                src.Stop();
            }
            src.volume = volume;
            src.pitch = pitch;
            if (!src.isPlaying) src.Play();
            src.transform.position = remote.HandPosition(hand);
            la.Seen = now;
        }

        private class TongueCopy
        {
            public Transform Mesh;
            public Vector3 Tip;
            public Vector3 Scale;
            public float Seen;
            public int Hand;
            public SteamId Origin;
        }

        private readonly Dictionary<string, TongueCopy> tongues = new Dictionary<string, TongueCopy>();

        private void Tongue(SteamId origin, int hand, string prefab, Vector3 tip, Vector3 scale, float now)
        {
            string key = origin.Value + "|" + hand;
            if (!tongues.TryGetValue(key, out TongueCopy t) || t.Mesh == null)
            {
                var hook = HandAsset(prefab) as HandItem_GrapplingHook;
                if (hook == null || hook.hookMesh == null)
                {
                    return;
                }
                GameObject copy = Instantiate(hook.hookMesh.gameObject);
                copy.name = "Belay_Grapple_" + prefab;
                CleanCopy(copy);
                foreach (MeshRenderer mr in copy.GetComponentsInChildren<MeshRenderer>(true))
                {
                    mr.enabled = true;
                }
                t = new TongueCopy { Mesh = copy.transform, Hand = hand, Origin = origin };
                tongues[key] = t;
            }
            t.Tip = tip;
            t.Scale = scale;
            t.Seen = now;
        }

        private void LateUpdate()
        {
            float now = Time.unscaledTime;
            if (mirrors.Count > 0)
            {
                List<string> dead = null;
                foreach (KeyValuePair<string, Mirror> kv in mirrors)
                {
                    Mirror m = kv.Value;
                    if (m == null) continue;
                    bool gone = m.System == null || !Remote(m.Origin, out RemotePlayer remote);
                    if (!gone && m.Looping && now - m.Seen > StateTimeout && m.System.isEmitting)
                    {
                        m.System.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                    }
                    if (gone || now - m.Seen > (m.Looping ? 6f : 20f) && !m.System.IsAlive(true))
                    {
                        if (m.System != null) Destroy(m.System.gameObject);
                        (dead ?? (dead = new List<string>())).Add(kv.Key);
                        continue;
                    }
                    if (m.FollowHand && PlayerSync.Instance.Remotes.TryGetValue(m.Origin, out remote) && remote != null)
                    {
                        m.Follow(remote);
                    }
                }
                if (dead != null) foreach (string k in dead) mirrors.Remove(k);
            }
            if (loops.Count > 0)
            {
                List<string> dead = null;
                foreach (KeyValuePair<string, LoopAudio> kv in loops)
                {
                    LoopAudio la = kv.Value;
                    if (la.Source == null || now - la.Seen > StateTimeout || !Remote(la.Origin, out RemotePlayer remote))
                    {
                        if (la.Source != null) Destroy(la.Source.gameObject);
                        (dead ?? (dead = new List<string>())).Add(kv.Key);
                        continue;
                    }
                    la.Source.transform.position = remote.HandPosition(la.Hand);
                }
                if (dead != null) foreach (string k in dead) loops.Remove(k);
            }
            if (tongues.Count > 0)
            {
                List<string> dead = null;
                foreach (KeyValuePair<string, TongueCopy> kv in tongues)
                {
                    TongueCopy t = kv.Value;
                    if (t.Mesh == null || now - t.Seen > StateTimeout || !Remote(t.Origin, out RemotePlayer remote))
                    {
                        if (t.Mesh != null) Destroy(t.Mesh.gameObject);
                        (dead ?? (dead = new List<string>())).Add(kv.Key);
                        continue;
                    }
                    Vector3 from = remote.HandPosition(t.Hand);
                    Vector3 d = t.Tip - from;
                    if (d.sqrMagnitude < 0.0001f) continue;
                    // The game stretches this mesh along its length to reach the latched point.
                    t.Mesh.SetPositionAndRotation(from, Quaternion.LookRotation(d));
                    t.Mesh.localScale = new Vector3(t.Scale.x, t.Scale.y, d.magnitude);
                }
                if (dead != null) foreach (string k in dead) tongues.Remove(k);
            }
        }

        // ================================================================ Lookups

        private readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
        private readonly Dictionary<string, float> missingClips = new Dictionary<string, float>();

        private AudioClip Clip(string prefab, string name)
        {
            if (clips.TryGetValue(name, out AudioClip known) && known != null)
            {
                return known;
            }
            if (missingClips.TryGetValue(name, out float retry) && Time.unscaledTime < retry)
            {
                return null;
            }
            HandAsset(prefab); // loads the item, and with it its sounds
            foreach (AudioClip c in Resources.FindObjectsOfTypeAll<AudioClip>())
            {
                if (c != null && (!clips.TryGetValue(c.name, out AudioClip had) || had == null)) clips[c.name] = c;
            }
            if (clips.TryGetValue(name, out known) && known != null)
            {
                missingClips.Remove(name);
                return known;
            }
            missingClips[name] = Time.unscaledTime + 30f;
            Plugin.Log.LogInfo($"Item effects: no sound named {name} here (from {prefab})");
            return null;
        }

        private readonly Dictionary<string, GameObject> effectPrefabs = new Dictionary<string, GameObject>();

        private GameObject EffectPrefab(string prefab, string name)
        {
            if (effectPrefabs.TryGetValue(name, out GameObject known))
            {
                return known;
            }
            GameObject found = null;
            HandItem asset = HandAsset(prefab);
            if (asset != null)
            {
                foreach (MonoBehaviour b in asset.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    found = FindIn(b, name, 0);
                    if (found != null) break;
                }
            }
            if (found == null)
            {
                foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (go != null && go.name == name && !go.scene.IsValid() && go.transform.parent == null)
                    {
                        found = go;
                        break;
                    }
                }
            }
            effectPrefabs[name] = found;
            return found;
        }

        /// <summary>Looks through an item's fields (and lists of hit effects) for the effect prefab it spawned.</summary>
        private static GameObject FindIn(object o, string name, int depth)
        {
            if (o == null || depth > 2)
            {
                return null;
            }
            foreach (FieldInfo f in o.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Type ft = f.FieldType;
                object v;
                try
                {
                    v = f.GetValue(o);
                }
                catch
                {
                    continue;
                }
                if (v == null) continue;
                if (v is GameObject g)
                {
                    if (g.name == name) return g;
                }
                else if (v is Component c && !(v is Transform))
                {
                    if (c != null && c.gameObject.name == name && !c.gameObject.scene.IsValid()) return c.gameObject;
                }
                else if (v is IList list && !(v is string))
                {
                    foreach (object e in list)
                    {
                        if (e is GameObject eg && eg != null && eg.name == name) return eg;
                        if (e != null && !(e is UnityEngine.Object) && e.GetType().IsClass && !e.GetType().IsPrimitive)
                        {
                            GameObject inner = FindIn(e, name, depth + 1);
                            if (inner != null) return inner;
                        }
                    }
                }
                else if (!(v is UnityEngine.Object) && ft.IsClass && ft != typeof(string) && ft.IsSerializable)
                {
                    GameObject inner = FindIn(v, name, depth + 1);
                    if (inner != null) return inner;
                }
            }
            return null;
        }

        private void Spawn(string prefab, string name, Vector3 at, Quaternion rot)
        {
            GameObject effect = EffectPrefab(prefab, name);
            if (effect == null)
            {
                return;
            }
            GameObject go;
            using (Replaying())
            {
                go = Instantiate(effect, at, rot);
            }
            go.name = "Belay_TeamFx_" + name;
            World.Blasts.MarkCopy(go);
            if (WorldLoader.initialized)
            {
                go.transform.parent = WorldLoader.GetCurrentLevelParentRoot();
            }
            // Effects normally clean themselves up; in case this one doesn't, it goes after a while.
            Destroy(go, 30f);
        }

        private readonly Dictionary<string, FX_DecalShared> decals = new Dictionary<string, FX_DecalShared>();

        private void Decal(string name, Vector3 at, Vector3 normal, Vector3 up)
        {
            if (!decals.TryGetValue(name, out FX_DecalShared shared))
            {
                shared = null;
                foreach (FX_DecalShared d in Resources.FindObjectsOfTypeAll<FX_DecalShared>())
                {
                    if (d != null && d.name == name)
                    {
                        shared = d;
                        break;
                    }
                }
                decals[name] = shared;
            }
            if (shared == null)
            {
                return;
            }
            Transform parent = null;
            if (Physics.Raycast(at + normal * 0.2f, -normal, out RaycastHit hit, 0.5f, ~0, QueryTriggerInteraction.Ignore))
            {
                parent = hit.collider.transform;
            }
            using (Replaying())
            {
                FXManager.DecalHandle handle = FXManager.DecalCreate(FXManager.DecalPriority.Low);
                FXManager.DecalSharedApply(handle, FXManager.DecalSharedData(shared));
                FXManager.DecalPointingFrom(handle, at, normal, up, parent);
            }
        }

        // ================================================================ Helpers

        private static bool Remote(SteamId origin, out RemotePlayer remote)
        {
            remote = null;
            return origin != NetSession.LocalId && Live && PlayerSync.Instance != null
                && PlayerSync.Instance.Remotes.TryGetValue(origin, out remote) && remote != null && !remote.Latest.Dead;
        }

        private static Vector3 Look(RemotePlayer remote)
        {
            return Quaternion.Euler(remote.Latest.Pitch, remote.Latest.Yaw, 0f) * Vector3.forward;
        }

        /// <summary>While a teammate's effect is being replayed here, nothing it does is sent back out.</summary>
        private static IDisposable Replaying()
        {
            ItemScope.Quiet++;
            return new QuietEnd();
        }

        private sealed class QuietEnd : IDisposable
        {
            private bool done;

            public void Dispose()
            {
                if (done) return;
                done = true;
                ItemScope.Quiet--;
            }
        }
    }

    /// <summary>
    /// Tracks when the game is running the local player's item code (or one of its sound sets), so the sounds and
    /// effects it makes on the way can be told apart from everything else and shared.
    /// </summary>
    internal static class ItemScope
    {
        private static readonly List<int> hands = new List<int>();

        /// <summary>Above 0 while something that has its own sync (a shot, a spawned effect, a replay) is running.</summary>
        internal static int Quiet;

        /// <summary>Above 0 while a sound set is playing: its lower-level sound calls are covered by the set.</summary>
        internal static int InSoundSet;

        /// <summary>Above 0 while a shot is made: the shot, its sound and tracer are ThrownSync's. The gun's own particles still count.</summary>
        internal static int InShot;

        internal static FX_DecalShared PendingDecal;

        /// <summary>The hand of the local item whose code is running, or -1.</summary>
        internal static int Hand => hands.Count > 0 ? hands[hands.Count - 1] : -1;

        internal static bool Active => Quiet == 0 && InShot == 0 && hands.Count > 0 && ItemEffects.Live;

        internal static void Push(int hand) => hands.Add(hand);

        internal static void Pop()
        {
            if (hands.Count > 0) hands.RemoveAt(hands.Count - 1);
        }
    }

    /// <summary>Every method of every hand item (and its coroutines) marks the scope while it runs.</summary>
    internal static class ItemScopePatch
    {
        private static readonly Dictionary<Type, FieldInfo> owners = new Dictionary<Type, FieldInfo>();

        internal static IEnumerable<MethodBase> Targets()
        {
            var seen = new HashSet<MethodBase>();
            foreach (Type t in AccessTools.GetTypesFromAssembly(typeof(HandItem).Assembly))
            {
                if (t == null || !(t == typeof(HandItem) || t.IsSubclassOf(typeof(HandItem))))
                {
                    continue;
                }
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (Patchable(m) && seen.Add(m)) yield return m;
                }
                foreach (Type nested in t.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                {
                    FieldInfo self = nested.GetField("<>4__this", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    MethodInfo move = nested.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (self != null && move != null && typeof(HandItem).IsAssignableFrom(self.FieldType) && Patchable(move) && seen.Add(move))
                    {
                        owners[nested] = self;
                        yield return move;
                    }
                }
            }
        }

        private static bool Patchable(MethodInfo m)
        {
            return !m.IsAbstract && !m.IsGenericMethodDefinition && !m.ContainsGenericParameters && m.GetMethodBody() != null
                && !m.IsSpecialName && !m.Name.StartsWith("<");
        }

        private static void Prefix(object __instance, out bool __state)
        {
            __state = false;
            if (__instance == null || !ItemEffects.Live)
            {
                return;
            }
            HandItem item = __instance as HandItem;
            if (item == null && owners.TryGetValue(__instance.GetType(), out FieldInfo self))
            {
                item = self.GetValue(__instance) as HandItem;
            }
            int hand = ItemEffects.HandOf(item);
            if (hand >= 0)
            {
                ItemScope.Push(hand);
                __state = true;
            }
        }

        private static void Finalizer(bool __state)
        {
            if (__state) ItemScope.Pop();
        }
    }

    /// <summary>
    /// What an item does to something else (hurting a creature, breaking a handhold, shattering a prop) is already
    /// replayed on everyone's game by its own sync, sounds and all, so none of it is sent again as the item's.
    /// </summary>
    internal static class ItemScopeQuiet
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            var seen = new HashSet<MethodBase>();
            foreach (Type t in AccessTools.GetTypesFromAssembly(typeof(HandItem).Assembly))
            {
                if (t == null || t.IsInterface || !typeof(Damageable).IsAssignableFrom(t)) continue;
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.Name == "Damage" && !m.IsAbstract && m.GetMethodBody() != null && seen.Add(m)) yield return m;
                }
            }
            foreach (MethodBase m in new MethodBase[]
            {
                AccessTools.Method(typeof(CL_Handhold_Breakable), "Break"),
                AccessTools.Method(typeof(UT_ShatterProp), "Shatter"),
            })
            {
                if (m != null && seen.Add(m)) yield return m;
            }
        }

        private static void Prefix(out bool __state)
        {
            __state = ItemScope.Active;
            if (__state) ItemScope.Quiet++;
        }

        private static void Finalizer(bool __state)
        {
            if (__state) ItemScope.Quiet--;
        }
    }

    internal static class ItemEffectPatches
    {
        // ---------------------------------------------------------------- Sound sets (the items' named sounds)

        private static readonly AccessTools.FieldRef<UT_AudioClipHandler.AudioSet, UT_AudioClipHandler> SetHandler =
            AccessTools.FieldRefAccess<UT_AudioClipHandler.AudioSet, UT_AudioClipHandler>("handler");

        internal static IEnumerable<MethodBase> SoundSetMethods()
        {
            Type t = typeof(UT_AudioClipHandler.AudioSet);
            yield return AccessTools.Method(t, "Play", new[] { typeof(float), typeof(AudioSource) });
            yield return AccessTools.Method(t, "Play", new[] { typeof(float), typeof(Vector3) });
            yield return AccessTools.Method(t, "PlaySound", new[] { typeof(float), typeof(AudioClip) });
            yield return AccessTools.Method(t, "PlayAtPoint", new[] { typeof(float), typeof(Vector3), typeof(AudioSource) });
        }

        internal static class SoundSetScope
        {
            private static void Prefix(UT_AudioClipHandler.AudioSet __instance, out int __state)
            {
                __state = 0;
                if (!ItemEffects.Live || __instance == null)
                {
                    return;
                }
                int hand = -1;
                try
                {
                    UT_AudioClipHandler h = SetHandler(__instance);
                    if (h != null && ItemEffects.LocalOwner(h, out int owned, out HandItem _, out string _))
                    {
                        hand = owned;
                    }
                }
                catch
                {
                    hand = -1;
                }
                if (hand < 0) hand = ItemScope.Hand;
                if (hand >= 0)
                {
                    // The sound set's own calls underneath are seen as this item's.
                    ItemScope.Push(hand);
                    __state = 1;
                }
                else
                {
                    ItemScope.InSoundSet++;
                    __state = 2;
                }
            }

            private static void Finalizer(int __state)
            {
                if (__state == 1) ItemScope.Pop();
                else if (__state == 2) ItemScope.InSoundSet--;
            }
        }

        // ---------------------------------------------------------------- Sounds at the lowest level

        private static void SoundAt(AudioClip clip, Vector3 position, float volume, float pitch)
        {
            if (!ItemScope.Active || clip == null || ItemScope.InSoundSet > 0)
            {
                return;
            }
            Try(() => ItemEffects.OnLocalClip(clip, ItemScope.Hand, NearBodyPoint(position), position, volume, pitch));
        }

        private static void SoundFrom(AudioClip clip, Transform from, float volume, float pitch)
        {
            if (!ItemScope.Active || clip == null || ItemScope.InSoundSet > 0)
            {
                return;
            }
            Try(() =>
            {
                bool body = from == null || OnBody(from) || NearBodyPoint(from.position);
                ItemEffects.OnLocalClip(clip, ItemScope.Hand, body, from != null ? from.position : Vector3.zero, volume, pitch);
            });
        }

        // The public Play/PlayOneShot are tiny enough to be inlined into the game's code, so the helpers they call are patched.
        private static void SourcePlay(AudioSource source) => SourceSound(source, source != null ? source.clip : null, 1f);

        private static void SourceOneShot(AudioSource source, AudioClip clip, float volumeScale) => SourceSound(source, clip, volumeScale, true);

        private static void SourceSound(AudioSource src, AudioClip clip, float scale, bool oneShot = false)
        {
            if (!ItemScope.Active || src == null || clip == null)
            {
                return;
            }
            Try(() =>
            {
                bool owned = ItemEffects.LocalOwner(src, out int hand, out HandItem _, out string _);
                if (!oneShot && src.loop)
                {
                    return; // a held item's looping sound is streamed with its state; others would never stop
                }
                if (!owned) hand = ItemScope.Hand;
                bool body = owned || OnBody(src.transform) || NearBodyPoint(src.transform.position);
                ItemEffects.OnLocalClip(clip, hand, body, src.transform.position, src.volume * scale, src.pitch);
            });
        }

        private static bool OnBody(Transform t)
        {
            ENT_Player me = ENT_Player.playerObject;
            Transform cam = Camera.main != null ? Camera.main.transform : null;
            return me != null && (t == me.transform || t.IsChildOf(me.transform)) || cam != null && t.IsChildOf(cam);
        }

        private static bool NearBodyPoint(Vector3 p)
        {
            ENT_Player me = ENT_Player.playerObject;
            Transform cam = Camera.main != null ? Camera.main.transform : null;
            return me != null && (p - me.transform.position).sqrMagnitude < 6.25f || cam != null && (p - cam.position).sqrMagnitude < 6.25f;
        }

        // ---------------------------------------------------------------- Particles in held items

        private static void ParticlesPlayed(ParticleSystem __instance)
        {
            if (ItemEffects.Live && ItemScope.Quiet == 0) Try(() => ItemEffects.OnLocalParticles(__instance, 0, 0, null));
        }

        private static void ParticlesEmitted(ParticleSystem __instance, int count)
        {
            if (ItemEffects.Live && ItemScope.Quiet == 0) Try(() => ItemEffects.OnLocalParticles(__instance, 1, count, null));
        }

        private static void ParticlesEmittedWith(ParticleSystem __instance, ParticleSystem.EmitParams emitParams, int count)
        {
            if (ItemEffects.Live && ItemScope.Quiet == 0)
            {
                object boxed = emitParams;
                Try(() => ItemEffects.OnLocalParticles(__instance, 2, count, boxed));
            }
        }

        // ---------------------------------------------------------------- Effects put into the world

        private static void SpawnStart(out int __state)
        {
            // Whatever the new object does as it wakes up is its own business, not the item's.
            __state = ItemScope.Active ? ItemScope.Hand : -1;
            if (__state >= 0) ItemScope.Quiet++;
        }

        private static void SpawnEnd(UnityEngine.Object original, UnityEngine.Object __result, int __state)
        {
            if (__state < 0)
            {
                return;
            }
            ItemScope.Quiet--;
            GameObject src = original as GameObject ?? (original as Component)?.gameObject;
            GameObject made = __result as GameObject ?? (__result as Component)?.gameObject;
            if (src != null && made != null)
            {
                Try(() => ItemEffects.OnLocalSpawn(__state, src, made));
            }
        }

        private static void DecalData(FX_DecalShared shared)
        {
            if (ItemScope.Active) ItemScope.PendingDecal = shared;
        }

        private static void DecalPlaced(Vector3 worldPos, Vector3 normal, Vector3 up)
        {
            FX_DecalShared shared = ItemScope.PendingDecal;
            ItemScope.PendingDecal = null;
            if (ItemScope.Active && shared != null)
            {
                Try(() => ItemEffects.OnLocalDecal(ItemScope.Hand, shared, worldPos, normal, up));
            }
        }

        private static void FxParticle(string id, Vector3 pos, int amount)
        {
            if (ItemScope.Active) Try(() => ItemEffects.OnLocalFxParticle(ItemScope.Hand, id, pos, amount, 0f, Vector3.zero, Quaternion.identity));
        }

        private static void FxParticleRadius(string id, Vector3 pos, float radius, int amount, Vector3 velocity)
        {
            if (ItemScope.Active) Try(() => ItemEffects.OnLocalFxParticle(ItemScope.Hand, id, pos, amount, Mathf.Max(radius, 0.0001f), velocity, Quaternion.identity));
        }

        private static void FxParticleRotation(string id, Vector3 pos, int amount, Quaternion rotation)
        {
            if (ItemScope.Active) Try(() => ItemEffects.OnLocalFxParticle(ItemScope.Hand, id, pos, amount, 0f, Vector3.zero, rotation));
        }

        private static void TracerStart(out bool __state)
        {
            // A tracer plays its muzzle and hit particles itself; only the tracer call is shared.
            __state = ItemScope.Active;
            if (__state)
            {
                ItemScope.Quiet++;
            }
        }

        private static void TracerEnd(string tracerParticleID, Vector3 startPoint, Vector3 endPoint, float speed, string hitParticleID, string muzzleFlashParticleID, bool __state)
        {
            if (!__state)
            {
                return;
            }
            ItemScope.Quiet--;
            Try(() => ItemEffects.OnLocalTracer(ItemScope.Hand, tracerParticleID, startPoint, endPoint, speed, hitParticleID, muzzleFlashParticleID));
        }

        // ---------------------------------------------------------------- Shots: the shot itself is ThrownSync's

        private static void ShotStart(out bool __state)
        {
            __state = ItemEffects.Live;
            if (__state) ItemScope.InShot++;
        }

        private static void ShotEnd(bool __state)
        {
            if (__state) ItemScope.InShot--;
        }

        private static void Shot(HandItem_Shoot __instance)
        {
            if (!ItemEffects.Live)
            {
                return;
            }
            int hand = ItemEffects.HandOf(__instance);
            if (hand >= 0) Try(() => ItemEffects.OnLocalShot(hand));
        }

        private static void Try(Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Item effect not shared: " + e.Message);
            }
        }
    }

    /// <summary>
    /// Patched by hand, one target at a time: if a game update renames something, only that one effect stops being
    /// shared, and the rest of the mod still loads.
    /// </summary>
    internal static class ItemEffectsPatcher
    {
        private static bool done;

        internal static void Apply()
        {
            if (done)
            {
                return;
            }
            done = true;
            var h = new Harmony(Plugin.Guid + ".itemeffects");
            int ok = 0, failed = 0;
            void Patch(MethodBase target, Type cls, string prefix = null, string postfix = null, string finalizer = null)
            {
                if (target == null)
                {
                    failed++;
                    Plugin.Log.LogWarning($"Item effects: no target for {cls.Name}.{prefix ?? postfix ?? finalizer}");
                    return;
                }
                try
                {
                    h.Patch(target,
                        prefix: prefix == null ? null : new HarmonyMethod(AccessTools.Method(cls, prefix)),
                        postfix: postfix == null ? null : new HarmonyMethod(AccessTools.Method(cls, postfix)),
                        finalizer: finalizer == null ? null : new HarmonyMethod(AccessTools.Method(cls, finalizer)));
                    ok++;
                }
                catch (Exception e)
                {
                    failed++;
                    Plugin.Log.LogWarning($"Item effects: couldn't patch {target.DeclaringType?.Name}.{target.Name}: {e.Message}");
                }
            }

            Type scope = typeof(ItemScopePatch), quiet = typeof(ItemScopeQuiet), fx = typeof(ItemEffectPatches), set = typeof(ItemEffectPatches.SoundSetScope);
            foreach (MethodBase m in ItemScopePatch.Targets()) Patch(m, scope, "Prefix", null, "Finalizer");
            foreach (MethodBase m in ItemScopeQuiet.Targets()) Patch(m, quiet, "Prefix", null, "Finalizer");
            foreach (MethodBase m in ItemEffectPatches.SoundSetMethods()) Patch(m, set, "Prefix", null, "Finalizer");

            Type mixer = typeof(UnityEngine.Audio.AudioMixerGroup);
            Patch(AccessTools.Method(typeof(AudioManager), nameof(AudioManager.PlaySound), new[] { typeof(AudioClip), typeof(Vector3), typeof(float), typeof(float), typeof(float), typeof(bool), typeof(float), mixer, typeof(string) }), fx, postfix: "SoundAt");
            Patch(AccessTools.Method(typeof(AudioManager), nameof(AudioManager.PlaySound), new[] { typeof(AudioClip), typeof(Transform), typeof(float), typeof(float), typeof(float), typeof(bool), typeof(float), mixer, typeof(string) }), fx, postfix: "SoundFrom");
            Patch(AccessTools.Method(typeof(AudioSource), "PlayHelper"), fx, postfix: "SourcePlay");
            Patch(AccessTools.Method(typeof(AudioSource), "PlayOneShotHelper"), fx, postfix: "SourceOneShot");
            Patch(AccessTools.Method(typeof(ParticleSystem), nameof(ParticleSystem.Play), new[] { typeof(bool) }), fx, postfix: "ParticlesPlayed");
            Patch(AccessTools.Method(typeof(ParticleSystem), "Emit_Internal"), fx, postfix: "ParticlesEmitted");
            Patch(AccessTools.Method(typeof(ParticleSystem), nameof(ParticleSystem.Emit), new[] { typeof(ParticleSystem.EmitParams), typeof(int) }), fx, postfix: "ParticlesEmittedWith");
            Patch(AccessTools.Method(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Instantiate), new[] { typeof(UnityEngine.Object), typeof(Vector3), typeof(Quaternion) }), fx, prefix: "SpawnStart", finalizer: "SpawnEnd");
            Patch(AccessTools.Method(typeof(FXManager), nameof(FXManager.DecalSharedData)), fx, prefix: "DecalData");
            Patch(AccessTools.Method(typeof(FXManager), nameof(FXManager.DecalPointingFrom)), fx, postfix: "DecalPlaced");
            Patch(AccessTools.Method(typeof(FXManager), nameof(FXManager.PlayParticle)), fx, postfix: "FxParticle");
            Patch(AccessTools.Method(typeof(FXManager), nameof(FXManager.PlayParticleWithRadius)), fx, postfix: "FxParticleRadius");
            Patch(AccessTools.Method(typeof(FXManager), nameof(FXManager.PlayParticleWithRotation)), fx, postfix: "FxParticleRotation");
            Patch(AccessTools.Method(typeof(FXManager), nameof(FXManager.PlayTracer)), fx, prefix: "TracerStart", finalizer: "TracerEnd");
            Patch(AccessTools.Method(typeof(HandItem_Shoot), "CreateProjectile"), fx, prefix: "ShotStart", finalizer: "ShotEnd");
            Patch(AccessTools.Method(typeof(HandItem_Shoot), nameof(HandItem_Shoot.Shoot)), fx, postfix: "Shot");
            Plugin.Log.LogInfo($"Item effects: {ok} hooks in place" + (failed > 0 ? $", {failed} failed" : ""));
        }
    }
}
