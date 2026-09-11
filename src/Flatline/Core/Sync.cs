using System;
using GTA;
using GTA.Math;
using GTA.Native;

namespace Flatline.Core
{
    /// <summary>
    /// A synchronised scene: two people placed by the animation rather than by us.
    ///
    /// WHY THE CPR NEEDS ONE. The clips in mini@cpr come in matched pairs -- char_a is the one
    /// kneeling and pushing, char_b is the one on the ground -- and they were authored together
    /// around a single origin: the medic's hands land on the victim's sternum because both
    /// skeletons are positioned from the same point by the same file. Play the two halves as
    /// ordinary TASK_PLAY_ANIMs and each starts wherever its ped happens to be standing, which
    /// is a man doing compressions on the tarmac a foot to the left of a body. The first version
    /// of this mod did exactly that, and the only reason it was tolerable is that it did not
    /// animate the body at all.
    ///
    /// A synchronised scene is the engine's own answer: one origin and one rotation, every
    /// participant tasked against it, and the animation data supplies each one's offset. It is
    /// how the game itself plays these clips in the CPR minigame they were made for.
    ///
    /// ONE SCENE PER CLIP, NOT ONE SCENE PER SEQUENCE. A scene's phase runs 0 to 1 once and
    /// stops, and re-tasking a ped against a scene whose phase is already 1 starts the new clip
    /// on its last frame. Scenes are cheap and the engine discards one the moment nothing is
    /// tasked against it, so the sequence is built as a fresh scene at the same origin for each
    /// clip in turn. Origin and rotation are captured once so every clip in the sequence lands
    /// on exactly the same spot.
    ///
    /// EVERYTHING HERE IS TIME-BOXED BY THE CALLER. A scene reports its own phase, so the
    /// call-out advances on the clip actually finishing rather than on a stopwatch -- but the
    /// caller still keeps a ceiling on every step, because a scene the engine refused to start
    /// reports a phase of 0 forever.
    /// </summary>
    internal sealed class Sync
    {
        /// <summary>Rotation order 2 is ZXY, which is what every scripted scene in the game uses.</summary>
        private const int RotationOrder = 2;

        /// <summary>
        /// Instant mover blend.
        ///
        /// The mover is the ped's root; blending it slowly slides him across the ground to the
        /// scene origin over the first few frames, which reads as somebody being dragged into
        /// position by an invisible hand. Snapping it is a single frame of teleport, which the
        /// eye does not resolve at the distances anybody watches this from.
        /// </summary>
        private const float MoverBlend = 1000f;

        /// <summary>Close enough to the end to call it finished.</summary>
        private const float Over = 0.985f;

        private readonly Vector3 _origin;
        private readonly Vector3 _rotation;

        private int _scene = -1;

        public Sync(Vector3 origin, float heading)
        {
            _origin = origin;
            _rotation = new Vector3(0f, 0f, heading);
        }

        /// <summary>Where every clip in this sequence is played from.</summary>
        public Vector3 Origin
        {
            get { return _origin; }
        }

        /// <summary>
        /// Starts a fresh scene at the origin. Every participant must then be Cast into it.
        ///
        /// HOLD LAST FRAME IS THE DEFAULT for a one-shot, because the alternative is a ped
        /// snapping to a standing idle the instant a clip ends -- and the whole sequence below
        /// is built from clips that end in the pose the next one starts from.
        /// </summary>
        public bool Begin(bool looped, bool hold = true)
        {
            try
            {
                _scene = Function.Call<int>(Hash.CREATE_SYNCHRONIZED_SCENE,
                                            _origin.X, _origin.Y, _origin.Z,
                                            _rotation.X, _rotation.Y, _rotation.Z,
                                            RotationOrder);

                if (_scene < 0) return false;

                Function.Call(Hash.SET_SYNCHRONIZED_SCENE_LOOPED, _scene, looped);
                Function.Call(Hash.SET_SYNCHRONIZED_SCENE_HOLD_LAST_FRAME, _scene, hold && !looped);

                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not create a synchronised scene: " + ex.Message);
                _scene = -1;
                return false;
            }
        }

        /// <summary>
        /// Puts one participant into the current scene on one clip.
        ///
        /// FLAGS 0, RAGDOLL BLOCKING 0, IK 0. The interesting bits of the flag word are for
        /// scenes that should abort on damage or keep the ped's own physics; neither is wanted
        /// for somebody kneeling on a road doing compressions, and the caller watches for the
        /// body dying on its own. The dictionary is loaded through Anim.Ready, so a wrong name
        /// is a logged skip rather than a hang.
        /// </summary>
        public bool Cast(Ped who, string dict, string clip)
        {
            if (_scene < 0) return false;
            if (!Crew.Alive(who)) return false;
            if (!Anim.Ready(dict)) return false;

            try
            {
                Function.Call(Hash.TASK_SYNCHRONIZED_SCENE, who.Handle, _scene, dict, clip,
                              8f, -8f, 0, 0, MoverBlend, 0);

                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not cast into " + dict + "/" + clip + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>0 at the start, 1 at the end. Negative when there is no scene.</summary>
        public float Phase
        {
            get
            {
                if (_scene < 0) return -1f;

                try
                {
                    if (!Function.Call<bool>(Hash.IS_SYNCHRONIZED_SCENE_RUNNING, _scene)) return -1f;

                    return Function.Call<float>(Hash.GET_SYNCHRONIZED_SCENE_PHASE, _scene);
                }
                catch
                {
                    return -1f;
                }
            }
        }

        /// <summary>
        /// Whether the current clip has run its course.
        ///
        /// A SCENE THAT HAS STOPPED RUNNING COUNTS AS FINISHED. The engine drops a scene the
        /// moment nothing is tasked against it -- a ped that died, or was re-tasked by somebody
        /// else -- and a caller waiting on phase 1 would otherwise wait forever. The caller has
        /// its own ceiling as well; this just stops the common case from needing it.
        /// </summary>
        public bool Finished
        {
            get
            {
                var phase = Phase;

                return phase < 0f || phase >= Over;
            }
        }

        /// <summary>Whether anything is playing against this scene right now.</summary>
        public bool Running
        {
            get
            {
                if (_scene < 0) return false;

                try { return Function.Call<bool>(Hash.IS_SYNCHRONIZED_SCENE_RUNNING, _scene); }
                catch { return false; }
            }
        }

        /// <summary>
        /// Forgets the scene. The engine reclaims it once the participants are re-tasked.
        ///
        /// There is nothing to dispose. A scene lives exactly as long as something is tasked
        /// against it, so the way to end one is to give its participants something else to do
        /// -- which every caller does -- and this merely stops us reading a stale id.
        /// </summary>
        public void End()
        {
            _scene = -1;
        }
    }
}
