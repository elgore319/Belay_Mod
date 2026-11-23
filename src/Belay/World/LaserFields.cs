using System.Collections.Generic;
using Belay.Net;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// The Habitation Labs security field (Event_MovingSecurityZone): a red kill field that switches on when you walk
    /// in, rises through the room behind you and then parks at the top for good. Each PC runs its own field from its
    /// own player's trigger, so everyone gets the chase; once a field has finished its sweep it switches off, so a
    /// teammate who is still below it (late, revived, respawned) can climb past instead of dying in it.
    /// </summary>
    public class LaserFields : MonoBehaviour
    {
        private const string FieldKillType = "laserfield";
        private const float PollInterval = 0.25f;
        private const float ScanInterval = 1f;
        /// <summary>Moving less than this (meters) between polls counts as stopped.</summary>
        private const float StillDistance = 0.005f;
        /// <summary>Stopped this long (seconds) after having moved means the sweep is over.</summary>
        private const float DoneAfter = 2f;

        private class Track
        {
            public Vector3 lastPos;
            public bool moved;
            public float stillFor;
        }

        private readonly Dictionary<UT_InstantDeath, Track> fields = new Dictionary<UT_InstantDeath, Track>();
        private float pollTimer;
        private float scanTimer;

        /// <summary>The kill field of a security zone (the object that also carries its mover).</summary>
        internal static bool IsField(Component c)
        {
            if (c == null)
            {
                return false;
            }
            UT_InstantDeath death = c.GetComponent<UT_InstantDeath>();
            return death != null && death.type == FieldKillType;
        }

        private void Update()
        {
            if (!NetSession.InLobbyRun)
            {
                fields.Clear();
                return;
            }
            scanTimer -= Time.deltaTime;
            if (scanTimer <= 0f)
            {
                scanTimer = ScanInterval;
                foreach (UT_InstantDeath death in FindObjectsByType<UT_InstantDeath>(FindObjectsSortMode.None))
                {
                    if (death != null && death.type == FieldKillType && !fields.ContainsKey(death))
                    {
                        fields[death] = new Track { lastPos = death.transform.position };
                    }
                }
            }
            pollTimer -= Time.deltaTime;
            if (pollTimer > 0f || fields.Count == 0)
            {
                return;
            }
            float dt = PollInterval - pollTimer;
            pollTimer = PollInterval;
            foreach (var pair in new List<KeyValuePair<UT_InstantDeath, Track>>(fields))
            {
                UT_InstantDeath death = pair.Key;
                Track t = pair.Value;
                if (death == null || !death.gameObject.activeInHierarchy)
                {
                    fields.Remove(death);
                    continue;
                }
                Vector3 pos = death.transform.position;
                // World recentering shifts everything at once; only a big jump like that is ignored.
                float step = (pos - t.lastPos).magnitude;
                t.lastPos = pos;
                if (step > 50f)
                {
                    continue;
                }
                if (step > StillDistance)
                {
                    t.moved = true;
                    t.stillFor = 0f;
                    continue;
                }
                if (!t.moved)
                {
                    continue; // switched on, still counting down to its rise
                }
                t.stillFor += dt;
                if (t.stillFor >= DoneAfter)
                {
                    Plugin.Log.LogInfo("Security field finished its sweep, switching it off: " + ButtonSync.ObjectId(death));
                    fields.Remove(death);
                    death.gameObject.SetActive(false);
                }
            }
        }
    }
}
