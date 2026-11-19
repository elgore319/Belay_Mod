using System.Collections.Generic;
using System.IO;
using Belay.Net;
using Belay.Players;
using Steamworks;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Ladder mode's nightmare (DEN_LadderNightmare) runs on each PC for its own player only. Each PC now
    /// streams where its own one is, and draws a look-alike copy of every teammate's. Display only: the copies
    /// make no sound and can't hurt anyone.
    /// </summary>
    public class LadderNightmareSync : MonoBehaviour
    {
        private const float SendInterval = 0.1f;
        private const float StaleAfter = 1f;

        private class Ghost
        {
            public GameObject Body;
            public Vector3 Target;
            public Quaternion Rotation;
            public float LastHeard;
        }

        private readonly Dictionary<SteamId, Ghost> ghosts = new Dictionary<SteamId, Ghost>();
        private DEN_LadderNightmare local;
        private float nextSend;
        private float nextFind;
        private bool sentGone = true;

        private void Start()
        {
            NetSession.Instance.On(PacketType.LadderNightmare, Handle);
        }

        private void Update()
        {
            if (!NetSession.InLobbyRun)
            {
                ClearAll();
                return;
            }
            if (local == null && Time.unscaledTime >= nextFind)
            {
                nextFind = Time.unscaledTime + 2f;
                local = FindFirstObjectByType<DEN_LadderNightmare>();
            }
            SendLocal();
            MoveGhosts();
        }

        private void SendLocal()
        {
            if (Time.unscaledTime < nextSend)
            {
                return;
            }
            nextSend = Time.unscaledTime + SendInterval;
            bool present = local != null && local.isActiveAndEnabled;
            if (!present && sentGone)
            {
                return;
            }
            Vector3 at = present ? WorldSpace.ToShared(local.transform.position) : Vector3.zero;
            Quaternion rot = present ? local.transform.rotation : Quaternion.identity;
            NetSession.Instance.Send(PacketType.LadderNightmare, w =>
            {
                w.Write(present);
                w.WriteVector(at);
                w.Write(rot.eulerAngles.y);
            }, reliable: !present);
            sentGone = !present;
        }

        private void Handle(SteamId origin, BinaryReader r)
        {
            bool present = r.ReadBoolean();
            Vector3 at = r.ReadVector();
            float yaw = r.ReadSingle();
            if (origin == NetSession.LocalId || !NetSession.InLobbyRun)
            {
                return;
            }
            if (!present)
            {
                Remove(origin);
                return;
            }
            if (!ghosts.TryGetValue(origin, out Ghost g) || g.Body == null)
            {
                GameObject body = MakeCopy();
                if (body == null)
                {
                    return;
                }
                g = new Ghost { Body = body };
                ghosts[origin] = g;
                body.transform.position = WorldSpace.ToLocal(at);
            }
            g.Target = at;
            g.Rotation = Quaternion.Euler(0f, yaw, 0f);
            g.LastHeard = Time.unscaledTime;
        }

        private void MoveGhosts()
        {
            List<SteamId> stale = null;
            foreach (KeyValuePair<SteamId, Ghost> pair in ghosts)
            {
                Ghost g = pair.Value;
                bool alive = PlayerSync.Instance != null && PlayerSync.Instance.Remotes.TryGetValue(pair.Key, out RemotePlayer remote)
                    && remote != null && remote.Present && !remote.Latest.Dead;
                if (g.Body == null || !alive || Time.unscaledTime - g.LastHeard > StaleAfter)
                {
                    (stale ??= new List<SteamId>()).Add(pair.Key);
                    continue;
                }
                Transform t = g.Body.transform;
                t.position = Vector3.Lerp(t.position, WorldSpace.ToLocal(g.Target), Time.deltaTime * 10f);
                t.rotation = Quaternion.Slerp(t.rotation, g.Rotation, Time.deltaTime * 10f);
            }
            if (stale != null)
            {
                foreach (SteamId id in stale) Remove(id);
            }
        }

        /// <summary>A renderers-only copy of this PC's nightmare, or null if this PC has none to copy.</summary>
        private GameObject MakeCopy()
        {
            if (local == null)
            {
                return null;
            }
            // Copied under an inactive parent so none of its scripts wake up before they're stripped.
            var holder = new GameObject("LadderNightmareHolder");
            holder.SetActive(false);
            GameObject copy = Instantiate(local.gameObject, holder.transform);
            for (int pass = 0; pass < 3; pass++)
            {
                foreach (Component c in copy.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || c is Transform || c is Renderer || c is MeshFilter || c is Animator)
                    {
                        continue;
                    }
                    try
                    {
                        DestroyImmediate(c);
                    }
                    catch (System.Exception)
                    {
                        // Something else still requires it; the next pass gets it.
                    }
                }
            }
            copy.name = "TeammateLadderNightmare";
            copy.transform.SetParent(null, true);
            Destroy(holder);
            return copy;
        }

        private void Remove(SteamId id)
        {
            if (ghosts.TryGetValue(id, out Ghost g) && g.Body != null)
            {
                Destroy(g.Body);
            }
            ghosts.Remove(id);
        }

        private void ClearAll()
        {
            foreach (Ghost g in ghosts.Values)
            {
                if (g.Body != null) Destroy(g.Body);
            }
            ghosts.Clear();
            local = null;
            sentGone = true;
        }
    }
}
