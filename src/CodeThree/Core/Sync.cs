using System;
using GTA;
using GTA.Math;
using GTA.Native;

namespace CodeThree.Core
{
    /// <summary>
    /// A synchronised scene: two people placed by the animation rather than by us.
    ///
    /// WHY THE PAIRED CLIPS NEED ONE. mini@cpr, combat@drag_ped@ and random@crash_rescue all
    /// come in matched halves authored around a single origin: the medic's hands land on the
    /// victim's sternum because both skeletons are positioned from the same point by the same
    /// file. Played as ordinary TASK_PLAY_ANIMs each half starts wherever its ped happens to be
    /// standing. A synchronised scene is the engine's own answer: one origin and one rotation,
    /// every participant tasked against it, and the animation data supplies each one's offset.
    ///
    /// NOBODY IS TELEPORTED ONTO THEIR MARK ANY MORE. The first versions of this file created
    /// the scene wherever the patient's pelvis was and cast both men into it with an instant
    /// mover blend -- so the scene decided where everybody stood, and whoever was not already
    /// there snapped there in a single frame. That is most of what "the animation is jank"
    /// meant: a medic who walks over and then jumps half a metre sideways, a body that flips
    /// round the moment he kneels.
    ///
    /// Two things replace it, and both are measurements rather than guesses:
    ///
    /// ANCHORED -- the scene is placed so that the PASSIVE participant's clip starts exactly
    /// where he already is. GET_ANIM_INITIAL_OFFSET_POSITION says where a clip puts its ped
    /// relative to an origin; asked with the origin at zero it gives the clip's own offset, and
    /// solving backwards from where the patient is lying gives the origin that leaves him
    /// there. He does not move when the scene starts, because the scene was built around him.
    ///
    /// MARK -- the same native, asked forwards, says where the ACTIVE participant has to stand
    /// for his half to start. So the medic is walked to that spot and turned to that heading
    /// before he is cast, and joins a scene he is already in position for.
    ///
    /// PAUSED UNTIL HE IS THERE. The patient is cast first with the scene's rate at nought, so
    /// he holds the opening pose while the medic walks to his mark; then the medic is cast into
    /// the same scene and it is set running. Both start on the same frame of the same clip.
    /// </summary>
    internal sealed class Sync
    {
        /// <summary>Rotation order 2 is ZXY, which is what every scripted scene in the game uses.</summary>
        private const int RotationOrder = 2;

        /// <summary>
        /// The mover blend for a man joining a scene he has walked to.
        ///
        /// NOT INSTANT ANY MORE. 1000 is a single-frame snap and was the default, which is fine
        /// when the ped is already exactly on his mark and a visible jump when he is not. A
        /// quarter of a second takes up whatever the walk left over -- a few centimetres, a few
        /// degrees -- as a slide nobody sees.
        /// </summary>
        public const float Settle = 4f;

        /// <summary>For the one cast that SHOULD snap: a ragdoll becoming a posed patient.</summary>
        public const float Snap = 1000f;

        /// <summary>Close enough to the end to call it finished.</summary>
        private const float Over = 0.985f;

        private readonly Vector3 _origin;
        private readonly float _heading;

        private int _scene = -1;

        public Sync(Vector3 origin, float heading)
        {
            _origin = origin;
            _heading = heading;
        }

        /// <summary>Where every clip in this sequence is played from.</summary>
        public Vector3 Origin
        {
            get { return _origin; }
        }

        public float Heading
        {
            get { return _heading; }
        }

        /// <summary>
        /// A scene placed so that one clip starts exactly where somebody already is.
        ///
        /// Asked of the engine with the origin at zero and no rotation, the clip's start is its
        /// own local offset. The origin that puts that start at `at`, facing `heading`, is then
        /// found by turning the offset through the scene's heading and taking it away.
        ///
        /// If the native has nothing to say -- a dictionary that is not loaded, an edition that
        /// answers zero -- the offset is zero and this is exactly what the old code did: the
        /// scene rooted on him. It degrades to the previous behaviour; it never does worse.
        /// </summary>
        public static Sync Anchored(string dict, string clip, Vector3 at, float heading)
        {
            try
            {
                if (!Anim.Ready(dict)) return new Sync(at, heading);

                var off = Function.Call<Vector3>(Hash.GET_ANIM_INITIAL_OFFSET_POSITION,
                                                 dict, clip, 0f, 0f, 0f, 0f, 0f, 0f,
                                                 0f, RotationOrder);

                var rot = Function.Call<Vector3>(Hash.GET_ANIM_INITIAL_OFFSET_ROTATION,
                                                 dict, clip, 0f, 0f, 0f, 0f, 0f, 0f,
                                                 0f, RotationOrder);

                var h = heading - rot.Z;
                var turned = Motion.Rotate(off, h);

                return new Sync(new Vector3(at.X - turned.X, at.Y - turned.Y, at.Z - off.Z), h);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not anchor " + dict + "/" + clip + ": " + ex.Message);
                return new Sync(at, heading);
            }
        }

        /// <summary>
        /// Where a participant must stand, and which way he must face, for this clip.
        ///
        /// Phase 0 is where his half starts. Phase 1 is where it ends -- which is how the trolley
        /// is put down beside the spot the lift will leave the patient, before the lift happens.
        /// </summary>
        public bool Mark(string dict, string clip, float phase, out Vector3 at, out float heading)
        {
            at = _origin;
            heading = _heading;

            try
            {
                if (!Anim.Ready(dict)) return false;

                var got = Function.Call<Vector3>(Hash.GET_ANIM_INITIAL_OFFSET_POSITION,
                                                 dict, clip, _origin.X, _origin.Y, _origin.Z,
                                                 0f, 0f, _heading, phase, RotationOrder);

                // A ZERO ANSWER IS THE NATIVE DECLINING, NOT THE WORLD ORIGIN -- and the out
                // value is left on the scene's own origin rather than written as zero, so a
                // caller that forgets to check the return still gets somewhere sensible. The
                // first draft of this wrote the zero through, and would have stood the trolley
                // at the centre of the map.
                if (got == Vector3.Zero) return false;

                var rot = Function.Call<Vector3>(Hash.GET_ANIM_INITIAL_OFFSET_ROTATION,
                                                 dict, clip, _origin.X, _origin.Y, _origin.Z,
                                                 0f, 0f, _heading, phase, RotationOrder);

                at = got;
                heading = rot.Z;
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not mark " + dict + "/" + clip + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Starts a fresh scene at the origin. Every participant must then be Cast into it.
        ///
        /// HOLD LAST FRAME ON EVERY ONE-SHOT, not only the final one. A one-shot that ends and
        /// lets go drops its ped into a standing idle until the next clip takes over -- which
        /// happens a tick later, and a tick of a paramedic standing bolt upright in the middle
        /// of chest compressions is a visible pop. Held, the end pose simply waits.
        /// </summary>
        public bool Begin(bool looped, bool hold = true)
        {
            try
            {
                _scene = Function.Call<int>(Hash.CREATE_SYNCHRONIZED_SCENE,
                                            _origin.X, _origin.Y, _origin.Z,
                                            0f, 0f, _heading, RotationOrder);

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
        /// The blend-in is the pose change and the mover blend is the position change; they are
        /// separate on purpose. A patient being brought into a new pose wants a slow blend-in
        /// and no slide; a medic arriving on his mark wants an ordinary blend and a small slide.
        /// </summary>
        public bool Cast(Ped who, string dict, string clip, float blendIn = 8f, float mover = Settle)
        {
            if (_scene < 0) return false;
            if (!Crew.Alive(who)) return false;
            if (!Anim.Ready(dict)) return false;

            try
            {
                Function.Call(Hash.TASK_SYNCHRONIZED_SCENE, who.Handle, _scene, dict, clip,
                              blendIn, -8f, 0, 0, mover, 0);

                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not cast into " + dict + "/" + clip + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// How fast the scene plays. Nought holds everybody on the current frame.
        ///
        /// That is how the patient waits in the opening pose while the medic walks to his mark:
        /// the scene exists, he is in it, and it simply is not moving yet.
        /// </summary>
        public void Rate(float rate)
        {
            if (_scene < 0) return;

            try { Function.Call(Hash.SET_SYNCHRONIZED_SCENE_RATE, _scene, rate); }
            catch { /* It plays at its own speed, which is the old behaviour. */ }
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

        /// <summary>Whether the current clip has run its course, or the scene has gone.</summary>
        public bool Finished
        {
            get
            {
                var phase = Phase;

                return phase < 0f || phase >= Over;
            }
        }

        /// <summary>
        /// Forgets the scene. The engine reclaims it once the participants are re-tasked.
        ///
        /// There is nothing to dispose. A scene lives exactly as long as something is tasked
        /// against it, so the way to end one is to give its participants something else to do.
        /// </summary>
        public void End()
        {
            _scene = -1;
        }
    }
}
