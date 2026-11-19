using System.Collections.Generic;
using Belay.Net;
using Belay.Players;
using DarkMachine.AI;
using HarmonyLib;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Teeth (the Habitation shaft hunter) and the Face are host-run and copied (EnemyMotion). The game aims them
    /// at the local player once, so in co-op they only chased the host, even after the host died. On the host they
    /// now chase the nearest living player; each copy still bites on its own PC. Also cleans up a Teeth kill after
    /// a revive.
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
        /// After a revive: undo a Teeth (or Phage) kill on this PC: the player lock (normally only cleared by
        /// loading a save), Teeth frozen in its kill pose with the hands effect, and the camera in its mouth.
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
    /// The host's Teeth tries to shove a teammate body like a creature (no GameEntity) and throws. The teammate's
    /// PC does the real bite, so the error is dropped.
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
