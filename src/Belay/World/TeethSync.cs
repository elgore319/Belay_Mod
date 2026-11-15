using System.Collections.Generic;
using Belay.Net;
using Belay.Players;
using DarkMachine.AI;
using HarmonyLib;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Teeth (the thing that hunts the team up the Habitation shaft) and the Face are creatures that the host runs and
    /// the other PCs copy (EnemyMotion). The game points it at the local player once, when it spawns, so in
    /// co-op it only ever went after the host, and kept "chasing" the host's body after the host died.
    /// On the host it now goes after the nearest living player. Each PC's copy still does the biting, so
    /// when the host's Teeth reaches you it gets you on your own PC.
    ///
    /// It also cleans up after a Teeth kill when you're revived: the kill locks the player and freezes
    /// Teeth in its kill pose on that PC, which the game only undoes by reloading a save.
    /// </summary>
    public class TeethSync : MonoBehaviour
    {
        private const float RetargetEvery = 0.5f;
        /// <summary>Only switch to someone else when they're this much closer, so it doesn't flip back and forth.</summary>
        private const float SwitchMargin = 3f;

        private float nextRetarget;

        private static bool Live => NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready;

        private void Update()
        {
            if (!Live || !NetSession.Instance.IsHost || Time.unscaledTime < nextRetarget)
            {
                return;
            }
            nextRetarget = Time.unscaledTime + RetargetEvery;
            try
            {
                DEN_Teeth teeth = DEN_Teeth.instance;
                if (teeth != null && !teeth.dead)
                {
                    Transform pick = Nearest(teeth.transform.position, teeth.target);
                    if (pick != null && pick != teeth.target)
                    {
                        teeth.target = pick;
                        Plugin.Log.LogInfo("Teeth is after " + Who(pick));
                    }
                }
                // The Face (the long creature in the walls) is pointed at the local player the same way.
                if (DEN_Face.faceInstances != null)
                {
                    foreach (DEN_Face face in DEN_Face.faceInstances)
                    {
                        if (face == null || face.dead)
                        {
                            continue;
                        }
                        Transform pick = Nearest(face.transform.position, face.target);
                        if (pick != null && pick != face.target)
                        {
                            face.SetTarget(pick);
                            Plugin.Log.LogInfo("The Face is after " + Who(pick));
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't point a hunter at the team: " + e.Message);
                nextRetarget = Time.unscaledTime + 10f;
            }
        }

        internal static string Who(Transform t)
        {
            ENT_Player me = ENT_Player.playerObject;
            return me != null && t == me.transform ? "you" : t.GetComponent<RemoteBody>()?.Owner?.DisplayName ?? "a teammate";
        }

        /// <summary>The nearest living player (you or a teammate), sticking with the current one unless another is clearly closer.</summary>
        internal static Transform Nearest(Vector3 at, Transform current)
        {
            var candidates = new List<Transform>();
            ENT_Player me = ENT_Player.playerObject;
            if (me != null && !me.dead)
            {
                candidates.Add(me.transform);
            }
            foreach (RemoteBody body in RemoteBody.All)
            {
                if (body != null && body.Owner != null && !body.Owner.Latest.Dead && body.Collider != null && body.Collider.enabled
                    && Time.unscaledTime - body.Owner.LastReceived <= 3f)
                {
                    candidates.Add(body.transform);
                }
            }
            if (candidates.Count == 0)
            {
                return null;
            }
            bool currentValid = current != null && candidates.Contains(current);
            Transform best = currentValid ? current : null;
            float bestDist = currentValid ? Vector3.Distance(at, current.position) - SwitchMargin : float.MaxValue;
            foreach (Transform t in candidates)
            {
                float d = Vector3.Distance(at, t.position);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = t;
                }
            }
            return best;
        }

        /// <summary>
        /// After being revived: undo what a Teeth (or Phage) kill left behind on this PC. The kill sets the
        /// game's player lock (nothing clears it but loading a save), freezes Teeth in its kill pose with its
        /// hands effect over your screen, and moves your camera into its mouth.
        /// </summary>
        internal static void CleanUpAfterRevive(ENT_Player player)
        {
            CL_GameManager gMan = CL_GameManager.gMan;
            if (gMan != null)
            {
                gMan.lockPlayer = false;
                gMan.canPause = true;
            }
            player.UnLock();
            if (player.camRoot != null && CamRootSaved)
            {
                player.camRoot.transform.localPosition = camRootLocalPos;
                player.camRoot.transform.localRotation = camRootLocalRot;
            }
            if (player.cam != null)
            {
                player.cam.transform.localPosition = camLocalPos;
                player.cam.transform.localRotation = Quaternion.identity;
            }
            DEN_Teeth teeth = DEN_Teeth.instance;
            if (teeth == null)
            {
                return;
            }
            try
            {
                List<AIStateComponent> states = Traverse.Create(teeth).Field("aiStates").GetValue<List<AIStateComponent>>();
                if (states != null)
                {
                    foreach (AIStateComponent state in states)
                    {
                        if (state is AIC_Teeth_Chase)
                        {
                            Traverse.Create(state).Field("hasKilledPlayer").SetValue(false);
                        }
                    }
                }
                Traverse t = Traverse.Create(teeth);
                t.Field("locked").SetValue(false);
                t.Field("speedMult").SetValue(1f);
                t.Field("charging").SetValue(false);
                if (teeth.screenHandEffect != null)
                {
                    teeth.screenHandEffect.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
                if (teeth.anim != null)
                {
                    teeth.anim.ResetTrigger("killplayer");
                    teeth.anim.Rebind();
                }
                // Back off first, like it does when hurt, so you aren't eaten again the moment you stand up.
                teeth.ChangeState("retreat");
                Plugin.Log.LogInfo("Cleared Teeth's kill on this PC after the revive");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't reset Teeth after the revive, removing it here: " + e.Message);
                teeth.Despawn();
            }
        }

        private static bool CamRootSaved;
        private static Vector3 camRootLocalPos;
        private static Quaternion camRootLocalRot;
        private static Vector3 camLocalPos;

        /// <summary>Remember where the camera normally sits while you're alive and free, to put it back after a kill animation.</summary>
        private void LateUpdate()
        {
            ENT_Player player = ENT_Player.playerObject;
            CL_GameManager gMan = CL_GameManager.gMan;
            if (player == null || player.dead || gMan == null || gMan.lockPlayer || player.camRoot == null || player.cam == null)
            {
                return;
            }
            if (!CamRootSaved)
            {
                camRootLocalPos = player.camRoot.transform.localPosition;
                camRootLocalRot = player.camRoot.transform.localRotation;
                camLocalPos = player.cam.transform.localPosition;
                CamRootSaved = true;
            }
        }
    }

    /// <summary>
    /// When Teeth on the host is next to a teammate's body it tries to shove it as if it were a creature
    /// (GetComponent&lt;GameEntity&gt; on the body is null), which throws. That's only the host's copy: the
    /// teammate's own PC does the real bite, so the error is dropped.
    /// </summary>
    [HarmonyPatch(typeof(AIC_Teeth_Chase), nameof(AIC_Teeth_Chase.Execute))]
    internal static class TeethChaseTeammatePatch
    {
        [HarmonyFinalizer]
        private static System.Exception Finalizer(System.Exception __exception, AIGameEntity e)
        {
            if (__exception is System.NullReferenceException && e != null && e.target != null && e.target.GetComponent<RemoteBody>() != null)
            {
                return null;
            }
            return __exception;
        }
    }
}
