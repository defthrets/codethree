using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;
using CodeThree.Core;

namespace CodeThree.Scene
{
    /// <summary>Where a call-out has got to.</summary>
    internal enum Step
    {
        None,

        /// <summary>On the road, lights and siren, not there yet.</summary>
        Coming,

        /// <summary>Out of the van and walking to him.</summary>
        Reaching,

        /// <summary>The CPR, from the first knee down to the moment it did or did not take.</summary>
        Working,

        /// <summary>It took. Getting him to his feet.</summary>
        Rising,

        /// <summary>It did not. Standing over him, writing it down.</summary>
        Pronounce,

        /// <summary>The trolley out beside him.</summary>
        Fetching,

        /// <summary>Arms under his shoulders, lifting him.</summary>
        Lifting,

        /// <summary>Carried onto the canvas, and the medic round to the back of it.</summary>
        Loading,

        /// <summary>Wheeling him to the van.</summary>
        Wheeling,

        /// <summary>Rolled into the back, doors shut, crew aboard.</summary>
        Stowing,

        /// <summary>Driving. Either to the hospital, or away empty.</summary>
        Driving,

        /// <summary>Somebody shot the patient. The crew back off and go.</summary>
        Fleeing,
    }

    /// <summary>
    /// One ambulance, one body, from the call to the hospital.
    ///
    /// THIS IS THE MOD. Everything else in the project finds a body, decides what killed it, or
    /// keeps another mod out of the way; this is the part somebody watches.
    ///
    /// THE PATIENT IS A PARTICIPANT, NOT A PROP. A corpse is a ragdoll and cannot be animated,
    /// so he is brought back into arrest the moment the crew reach him -- alive in the engine's
    /// eyes, unconscious in everybody else's -- and from then on he is one of two people in a
    /// synchronised scene, placed by the animation data rather than by us.
    ///
    /// NOTHING TELEPORTS. That is the rule 0.4.0 was built around, because every complaint that
    /// the scene looked janky came down to something jumping: the medic snapping onto his mark,
    /// the body flipping round as he knelt, the trolley rising to meet the body, the body
    /// leaping from the medic's arms onto the bed, the trolley appearing in the back of the van.
    /// Each of those is now either a measurement or a movement:
    ///
    ///   the patient anchors every scene where he already lies (Sync.Anchored), so he does not
    ///   move when it starts -- and his opening pose is turned to match the way his ragdoll
    ///   actually fell, read off his bones, rather than the capsule's meaningless heading;
    ///
    ///   the medic WALKS to the exact spot and heading the clip wants him on (Sync.Mark), and
    ///   joins a scene that has been held paused for him;
    ///
    ///   the body is carried along an arc onto the bed while his pose settles to lying, and only
    ///   attached once he is exactly where the attachment puts him;
    ///
    ///   the trolley is eased through the back doors before it is attached to the van.
    ///
    /// EVERY STEP HAS A DEADLINE AND A WAY OUT. A stuck call-out holds two persistent peds, a
    /// vehicle, a prop and a resurrected man for the rest of the session, so each step is
    /// time-boxed, every failure falls forward, and Done() is the only exit.
    ///
    /// AND HE GOES BACK THE WAY HE WAS FOUND. A man they could not save is dead when they leave:
    /// Done() kills him again if he is still ours, so no other mod ever meets a corpse that
    /// stood up. A man they saved is handed to the city alive, with a limp.
    /// </summary>
    internal sealed class Callout
    {
        private struct Beat
        {
            public string Clip;
            public bool Loop;
            public int Ms;
        }

        /// <summary>Whether the man being walked to his mark is on it.</summary>
        private enum Walk { None, Going, There }

        // ---- timings that are the scene's own, not the player's -----------------

        private const int OneShotMs = 4500;
        private const int FirstLookMs = 2400;
        private const int BetweenMs = 1800;
        private const int LastLookMs = 1100;
        private const int FleeMs = 2600;

        /// <summary>The longest anybody is given to reach a mark before the scene starts anyway.</summary>
        private const int MarkMs = 5000;

        /// <summary>How long the body takes to be carried from the medic's arms onto the bed.</summary>
        private const int CarryMs = 850;

        /// <summary>How long the trolley takes to roll in through the back doors.</summary>
        private const int RollMs = 1300;

        /// <summary>The beat with him on the canvas before anybody moves.</summary>
        private const int SettleMs = 500;

        /// <summary>A ceiling on the lift itself; the paired clip normally ends well inside it.</summary>
        private const int LiftMs = 6000;

        /// <summary>No progress towards the body for this long is a van that is stuck.</summary>
        private const int StuckMs = 12000;

        /// <summary>Stuck closer than this, the crew get out and walk the rest.</summary>
        private const float WalkInRange = 70f;

        /// <summary>How many times a stuck van is put back on a road before it is given up on.</summary>
        private const int MostWarps = 2;

        private const int DriveStyle = 786603;

        private readonly Settings _cfg;
        private readonly Random _rng;
        private readonly Gurney _trolley;
        private readonly Kit _kit;

        private Vehicle _van;
        private Ped _driver;
        private Ped _mate;
        private Ped _body;

        private Vector3 _at;
        private Vector3 _to;

        private Step _step;
        private int _stepAt;

        /// <summary>
        /// Set by every change of step and consumed by the step that has just started. A flag,
        /// because `now - _stepAt == 0` is never true: To() runs at the END of the previous step.
        /// </summary>
        private bool _entering;

        private Verdict _verdict;
        private uint _weapon;
        private bool _carrying;
        private bool _bodyInVan;

        /// <summary>
        /// Whether he is currently alive by our hand -- from the resurrection until he is either
        /// handed back alive or killed again. Done() reads it to decide whether there is a man
        /// who needs putting back the way he was found.
        /// </summary>
        private bool _ours;

        private bool _vanLost;
        private bool _mateBusy;

        /// <summary>Whether the second man has been sent walking from the van yet.</summary>
        private bool _mateOut;

        /// <summary>When he was sent to his spot beside the body, so he is not waited on forever.</summary>
        private int _mateAt;

        /// <summary>
        /// Where the van last was, so one can be put back if it is taken away.
        ///
        /// THE VAN IS BEING DELETED MID-SCENE AND NOTHING IN THIS MOD DOES IT. Three times in
        /// one evening "the van was deleted while working on him" -- during the CPR, before the
        /// trolley exists, with nothing of ours touching it. It is a mission entity, held and
        /// re-held, and it still goes. Whatever takes it -- a sweeper in another mod's dll, an
        /// ASI with no ini -- cannot be found in any config file on this machine, and this mod
        /// cannot stop it. What it can do is notice on the next tick and put another ambulance
        /// back where that one stood. The crew are on foot and not looking at it.
        /// </summary>
        private Vector3 _vanAt;
        private float _vanHeading;
        private int _heldAt;

        /// <summary>Warned once per step about somebody under the floor, so the log is not a torrent.</summary>
        private bool _groundWarned;

        private Sync _scene;
        private Beat[] _beats;
        private int _beat;
        private int _beatAt;
        private int _verdictAt;

        // ---- walking somebody to a mark ----------------------------------------

        private Walk _walk;
        private int _walkAt;
        private Vector3 _walkTo;

        // ---- a patient being posed, and checked -------------------------------

        private string _poseDict;
        private string _poseClip;
        private float _poseLying;
        private Vector3 _posePelvis;
        private float _poseBlend;
        private float _poseMover;
        private int _posedAt;
        private bool _poseCheck;

        /// <summary>
        /// For each opening pose, which way a body in it lies relative to its root, and where
        /// its pelvis sits in the root's frame. Measured the first time a pose is used and kept
        /// for the session, so only the very first scene ever needs a correction.
        /// </summary>
        private static readonly Dictionary<string, float> Twist = new Dictionary<string, float>();
        private static readonly Dictionary<string, Vector3> PelvisAt = new Dictionary<string, Vector3>();

        // ---- the scene's moving parts -----------------------------------------

        private int _sceneAt;
        private Vector3 _carryFrom;
        private float _carryFromHeading;
        private Vector3 _carryTo;
        private float _carryToHeading;
        private bool _laid;
        private int _laidAt;
        private bool _rolled;
        private int _boardAt;
        private Vector3 _stopAt;

        // ---- getting there -----------------------------------------------------

        private float _bestDist;
        private int _bestAt;
        private int _warps;
        private int _reachMs;

        /// <summary>The ambulance's rear, measured once from its model.</summary>
        private static float _vanRear = float.NaN;

        /// <summary>Said out loud, if the player is near enough to care and has asked for it.</summary>
        public Action<string> Say;

        public Callout(Settings cfg, Random rng)
        {
            _cfg = cfg;
            _rng = rng;
            _trolley = new Gurney(cfg);
            _kit = new Kit();
        }

        public bool Out
        {
            get { return _step != Step.None; }
        }

        public Vector3 At
        {
            get { return _at; }
        }

        /// <summary>What the police wait on: whether the crew are still at this scene.</summary>
        public bool Still(Vector3 where)
        {
            return _step != Step.None && _step != Step.Driving &&
                   _at.DistanceTo(where) < _cfg.SameScene;
        }

        public bool Workable
        {
            get { return _step != Step.None && _verdict == Verdict.Workable; }
        }

        // ---- the call ----------------------------------------------------------

        /// <summary>
        /// Sends one. Whether it went.
        ///
        /// The two optional arguments exist for the test call-out on the settings screen: a
        /// verdict that overrides what killed him, and a shorter distance to come in from, so a
        /// scene can be watched in ten seconds instead of a murder and a two-minute wait.
        /// </summary>
        public bool Send(Death death, Verdict? force = null, float comeFrom = -1f)
        {
            if (Out) return false;
            if (death == null || !Crew.There(death.Body)) return false;

            try
            {
                var where = death.Body.Position;

                var near = comeFrom > 0f;

                Vector3 from;
                if (!Crew.Road(where, _rng, near ? comeFrom : _cfg.ComeFrom,
                               near ? 15f : _cfg.ComeSpread, out from))
                {
                    Log.Debug("No road to send an ambulance in from.");
                    return false;
                }

                var model = Crew.Load(Crew.Van);
                if (model == null) { Log.Debug("The ambulance model would not load."); return false; }

                _van = World.CreateVehicle(model.Value, from, Motion.HeadingOf(Motion.Flat(where - from)));
                model.Value.MarkAsNoLongerNeeded();

                if (!Crew.Alive(_van)) { Done(); return false; }

                // HELD AS A MISSION ENTITY, not merely persistent. The van was being deleted out
                // from under scenes -- mostly by the trolley launching it, which is fixed in the
                // Gurney, but a van the population manager is allowed to reclaim is a van the
                // next streaming hitch can take.
                Crew.Hold(_van);
                _van.IsEngineRunning = true;

                Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, _van.Handle);
                Function.Call(Hash.SET_VEHICLE_LIGHTS, _van.Handle, 2);
                Function.Call(Hash.SET_VEHICLE_SIREN, _van.Handle, true);

                _driver = Crew.Aboard(_van, -1);
                if (_driver == null) { Done(); return false; }

                _mate = Crew.Aboard(_van, 0);

                // EVERYTHING THE SCENE WILL LOAD, ASKED FOR NOW. A load mid-scene yields, and a
                // yield with a freshly resurrected man in it is a man standing up for as long as
                // the disk takes. By the time the van arrives it is all in memory.
                _trolley.Preload();
                Anim.Preload(Anim.Scene);

                _body = death.Body;
                _at = where;

                Crew.Hold(_body);

                // READ NOW, NOT AT THE SCENE. Once he has been resurrected the cause is gone.
                _verdict = Cause.Read(_body, out _weapon);

                if (!_cfg.Resuscitate) _verdict = Verdict.Gone;
                if (force.HasValue) _verdict = force.Value;

                _step = Step.Coming;
                _stepAt = Game.GameTime;
                _entering = true;
                _carrying = false;
                _bodyInVan = false;
                _ours = false;
                _mateBusy = false;
                _mateOut = false;
                _vanLost = false;
                _walk = Walk.None;
                _poseCheck = false;
                _groundWarned = false;
                _vanAt = from;
                _vanHeading = _van.Heading;
                _heldAt = Game.GameTime;

                Drive(_at, _cfg.ThereRange * 0.6f);

                Log.Info("An ambulance was called to a body -- " + Cause.Word(_weapon) +
                         ", " + (_verdict == Verdict.Workable ? "workable" : "gone") +
                         (force.HasValue ? " (a test)" : "") + ".");

                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not send an ambulance: " + ex.Message);
                Done();
                return false;
            }
        }

        private void Drive(Vector3 to, float stopWithin)
        {
            try
            {
                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                              _driver.Handle, _van.Handle, to.X, to.Y, to.Z,
                              _cfg.Speed, DriveStyle, stopWithin);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not give the driver his route: " + ex.Message);
            }
        }

        // ---- the tick ----------------------------------------------------------

        public void Update()
        {
            if (_step == Step.None) return;

            var now = Game.GameTime;

            try
            {
                if (!Crew.Alive(_driver))
                {
                    Log.Info("The call-out ended: the driver was lost.");
                    Done();
                    return;
                }

                // THE VAN IS NOT THE SCENE. A lost van is noted once and the scene carries on;
                // only the steps that need somewhere to put him give up.
                if (!Crew.Alive(_van))
                {
                    // TAKEN AWAY WITH THE CREW ON FOOT: another one goes back where it stood.
                    // Between the crew getting out and the trolley rolling in, the van is a
                    // parked prop nobody is looking at, and a replacement in the same spot is
                    // indistinguishable from the original. See _vanAt.
                    if (_step >= Step.Reaching && _step <= Step.Stowing && Revan())
                    {
                        Log.Warn("The van was " + HowLost(_van) + " while " + State +
                                 "; another was put back where it stood.");
                    }
                    else
                    {
                        if (!_vanLost)
                        {
                            _vanLost = true;
                            Log.Warn("The van was " + HowLost(_van) + " while " + State +
                                     ". The crew carry on without it.");
                        }

                        if (_step == Step.Coming || _step >= Step.Fetching)
                        {
                            OnFoot("there is no van to put him in");
                            return;
                        }
                    }
                }
                else
                {
                    _vanAt = _van.Position;
                    _vanHeading = _van.Heading;

                    // RE-HELD EVERY COUPLE OF SECONDS. Cheap, and if whatever is deleting the van
                    // works by clearing the mission flag first, this is the counter to it.
                    if (now - _heldAt > 2000) { Crew.Hold(_van); _heldAt = now; }
                }

                Grounded();

                var me = Game.Player.Character;
                var anchor = Crew.Alive(_van) ? _van.Position : _at;

                if (me != null && me.Exists() && anchor.DistanceTo(me.Position) > _cfg.LetGoRange)
                {
                    Log.Info("The call-out ended: you were " + (int)anchor.DistanceTo(me.Position) + "m away.");
                    Done();
                    return;
                }

                switch (_step)
                {
                    case Step.Coming:    Coming(now);    break;
                    case Step.Reaching:  Reaching(now);  break;
                    case Step.Working:   Working(now);   break;
                    case Step.Rising:    Rising(now);    break;
                    case Step.Pronounce: Pronounce(now); break;
                    case Step.Fetching:  Fetching(now);  break;
                    case Step.Lifting:   Lifting(now);   break;
                    case Step.Loading:   Loading(now);   break;
                    case Step.Wheeling:  Wheeling(now);  break;
                    case Step.Stowing:   Stowing(now);   break;
                    case Step.Driving:   Driving(now);   break;
                    case Step.Fleeing:   Fleeing(now);   break;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("The call-out went wrong in " + _step + ": " + ex.Message);
                Done();
            }
        }

        private void To(Step step, int now)
        {
            _step = step;
            _stepAt = now;
            _entering = true;
            _walk = Walk.None;
            _groundWarned = false;
        }

        /// <summary>
        /// Another ambulance, where the last one was standing.
        ///
        /// The model was released after the first spawn and may need a moment from disk; the
        /// patient is held and every dictionary is already in memory, so a yield here costs a
        /// frame and nothing else.
        /// </summary>
        private bool Revan()
        {
            try
            {
                if (_vanAt == Vector3.Zero) return false;

                var model = Crew.Load(Crew.Van, 600);
                if (model == null) return false;

                var van = World.CreateVehicle(model.Value, _vanAt, _vanHeading);
                model.Value.MarkAsNoLongerNeeded();

                if (!Crew.Alive(van)) return false;

                Crew.Hold(van);
                van.IsEngineRunning = true;

                Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, van.Handle);
                Function.Call(Hash.SET_VEHICLE_LIGHTS, van.Handle, 2);

                _van = van;
                _heldAt = Game.GameTime;

                if (_step >= Step.Wheeling) Doors(true);

                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not put the van back: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Nobody under the floor. Should never fire now the scenes sit on the ground; if it
        /// does, the log says which step and by how much, and the man is lifted back up.
        /// </summary>
        private void Grounded()
        {
            if (_step < Step.Working || _step > Step.Loading) return;

            try
            {
                Lift(_body, "the patient");
                Lift(_driver, "the medic");
            }
            catch
            {
                // Diagnostic only.
            }
        }

        private void Lift(Ped who, string name)
        {
            if (!Crew.Alive(who)) return;

            var above = Crew.Above(who);
            if (above > -0.45f) return;

            if (!_groundWarned)
            {
                _groundWarned = true;
                Log.Warn(name + " was " + (-above).ToString("0.00") + "m under the road while " +
                         State + "; lifted back up.");
            }

            var at = who.Position;
            Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, who.Handle,
                          at.X, at.Y, at.Z - above, false, false, false);
        }

        private bool Entering()
        {
            if (!_entering) return false;

            _entering = false;
            return true;
        }

        // ---- on the way --------------------------------------------------------

        /// <summary>
        /// Driving in, and not giving up on the first dead end.
        ///
        /// THIRTY-EIGHT CALL-OUTS ENDED "IT COULD NOT GET THERE". A van pathing across a city
        /// finds a jammed junction, a kerb it will not mount, a body on a footbridge -- and the
        /// old rule was simply a hundred-second clock, after which it drove off. So the scene
        /// never happened a third of the time, and from the street that looks like the mod not
        /// working at all.
        ///
        /// NOW PROGRESS IS WATCHED, NOT TIME. No metre gained in twelve seconds is a van that is
        /// stuck. Close enough to walk, the crew get out and walk the rest -- which is what a
        /// real crew does. Too far, the van is put back on a road forty-odd metres from the body
        /// and tries again, out of the player's sight, at most twice.
        /// </summary>
        private void Coming(int now)
        {
            if (Entering())
            {
                _bestDist = float.MaxValue;
                _bestAt = now;
                _warps = 0;
            }

            var d = _van.Position.DistanceTo(_at);

            if (d <= _cfg.ThereRange) { Arrive(now, d); return; }

            if (d < _bestDist - 3f)
            {
                _bestDist = d;
                _bestAt = now;
            }

            if (now - _bestAt > StuckMs)
            {
                if (d < WalkInRange)
                {
                    Log.Info("The van is stuck " + (int)d + "m out; the crew walk the rest.");
                    Arrive(now, d);
                    return;
                }

                if (_warps < MostWarps && Unstick())
                {
                    _warps++;
                    _bestDist = float.MaxValue;
                }

                _bestAt = now;
            }

            if (now - _stepAt > _cfg.ComeMs) Leave(now, "it could not get there");
        }

        /// <summary>A stuck van put back on a road near the body, somewhere the player is not looking.</summary>
        private bool Unstick()
        {
            try
            {
                var me = Game.Player.Character;

                for (var attempt = 0; attempt < 6; attempt++)
                {
                    Vector3 spot;
                    if (!Crew.Road(_at, _rng, 45f, 25f, out spot)) continue;

                    if (me != null && me.Exists() && spot.DistanceTo(me.Position) < 40f) continue;

                    Function.Call(Hash.SET_ENTITY_COORDS, _van.Handle, spot.X, spot.Y, spot.Z,
                                  false, false, false, false);

                    _van.Heading = Motion.HeadingOf(Motion.Flat(_at - spot));

                    Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, _van.Handle);

                    Drive(_at, _cfg.ThereRange * 0.6f);

                    Log.Info("The van was stuck; it was put back on a road " +
                             (int)spot.DistanceTo(_at) + "m out.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not unstick the van: " + ex.Message);
            }

            return false;
        }

        private void Arrive(int now, float distance)
        {
            Function.Call(Hash.SET_VEHICLE_SIREN, _van.Handle, false);

            // THE WALK IS GIVEN TIME FOR ITS DISTANCE. ReachMs was written for a van parked in
            // the next bay; a crew walking in from seventy metres needs longer than that.
            _reachMs = Math.Max(_cfg.ReachMs, (int)(distance / 2.5f * 1000f) + 4000);

            Out_(_driver, 1.4f);

            if (_cfg.MedicBag) _kit.Bring(_mate);

            Out_(_mate, 2.2f, Flank());

            To(Step.Reaching, now);

            Log.Info("The ambulance is at the body.");

            if (Say != null && Near()) Say("An ambulance pulls up.");
        }

        private void Out_(Ped who, float stopAt, Vector3? spot = null)
        {
            if (!Crew.Alive(who)) return;

            try
            {
                Function.Call(Hash.TASK_LEAVE_VEHICLE, who.Handle, _van.Handle, 0);

                // A MAN WITH A SPOT TO GO TO IS SENT THERE ONCE HE IS OUT. Giving him the walk on
                // the same tick as the leave replaces the leave, and a navmesh walk issued to a
                // man still in his seat is a man who stays in his seat. Reaching watches for him
                // to be out of the cab and sends him then.
                if (spot.HasValue) return;

                if (Crew.There(_body))
                {
                    Function.Call(Hash.TASK_GO_TO_ENTITY, who.Handle, _body.Handle,
                                  _reachMs, stopAt, 2f, 1073741824f, 0);
                    return;
                }

                Crew.WalkTo(who, _at, 1.8f, _reachMs);
            }
            catch (Exception ex)
            {
                Log.Debug("The crew could not get out: " + ex.Message);
            }
        }

        /// <summary>A spot beside the body, square to the way the van came in.</summary>
        private Vector3 Flank()
        {
            try
            {
                if (!Crew.There(_body) || !Crew.Alive(_van)) return _at;

                var body = _body.Position;
                var toward = Motion.Flat(body - _van.Position);

                if (toward.Length() < 0.5f) return body;

                toward = toward.Normalized;

                return body + new Vector3(-toward.Y, toward.X, 0f) * 1.7f;
            }
            catch
            {
                return _at;
            }
        }

        // ---- walking over ------------------------------------------------------

        private void Reaching(int now)
        {
            if (!Crew.There(_body)) { Leave(now, "the body had gone"); return; }

            // The second man, once he is actually out of the cab: to his side of the body,
            // round the doors rather than through them, turning to face the patient at the end.
            if (!_mateOut && Crew.Alive(_mate) && !_mate.IsInVehicle())
            {
                _mateOut = true;

                var flank = Flank();
                Crew.WalkTo(_mate, flank, Motion.HeadingOf(Motion.Flat(_body.Position - flank)), 1.6f, _reachMs);
            }

            var close = Crew.Alive(_driver) &&
                        _driver.Position.DistanceTo(_body.Position) < _cfg.KneelRange;

            if (!close && now - _stepAt < _reachMs) return;

            To(Step.Working, now);
        }

        // ---- marks and poses ----------------------------------------------------

        /// <summary>
        /// Walks a man to the exact spot and heading a clip wants him on.
        ///
        /// THE MEDIC IS NEVER SNAPPED ONTO HIS MARK. Sync.Mark asks the engine where the clip
        /// starts him, and he walks there and turns to face the right way; only then is he cast
        /// into the scene, and the few centimetres the walk left over are taken up by the mover
        /// blend. A mark the engine will not give is treated as already reached, which is the
        /// old behaviour -- a snap -- and no worse than it.
        /// </summary>
        private void Approach(Ped who, Sync scene, string dict, string clip, int now)
        {
            _walk = Walk.Going;
            _walkAt = now;

            Vector3 at;
            float heading;

            if (!Crew.Alive(who) || scene == null || !scene.Mark(dict, clip, 0f, out at, out heading))
            {
                _walk = Walk.There;
                return;
            }

            _walkTo = at;

            Crew.WalkTo(who, at, heading, 1f, MarkMs);
        }

        private bool Approached(Ped who, int now)
        {
            if (_walk == Walk.There) return true;
            if (_walk != Walk.Going) return false;

            if (!Crew.Alive(who) ||
                Motion.FlatDistance(who.Position, _walkTo) < 0.3f ||
                now - _walkAt > MarkMs)
            {
                _walk = Walk.There;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Puts the patient into the opening pose of a clip, held still, lying the way he lies.
        ///
        /// THE BODY USED TO FLIP ROUND WHEN THE MEDIC KNELT. A resurrected ped is posed from its
        /// capsule's heading, and a ragdoll's capsule heading is whatever he was facing when he
        /// was hit -- nothing to do with which way he fell. So a man lying head-north snapped to
        /// lying head-east the instant the CPR began.
        ///
        /// Now his bones are read first: which way his pelvis-to-head line points and where his
        /// pelvis is. The scene is then anchored so the POSE's pelvis lands on his and the pose
        /// lies along his line. How a given pose lies relative to its own root is not in any
        /// file, so the first time a pose is used it is measured off the posed skeleton a
        /// moment later and remembered -- see CheckPose -- and every later scene gets it right
        /// from the first frame.
        /// </summary>
        private void Pose(string dict, string clip, float lying, Vector3 pelvis,
                          float blend, float mover, int now)
        {
            _poseDict = dict;
            _poseClip = clip;
            _poseLying = lying;
            _posePelvis = pelvis;
            _poseBlend = blend;
            _poseMover = mover;

            PoseNow();

            _posedAt = now;
            _poseCheck = !Twist.ContainsKey(clip) && !float.IsNaN(lying) && pelvis != Vector3.Zero;
        }

        private void PoseNow()
        {
            if (!Crew.Alive(_body)) return;

            float twist;
            Vector3 local;

            if (!Twist.TryGetValue(_poseClip, out twist)) twist = 0f;
            if (!PelvisAt.TryGetValue(_poseClip, out local)) local = Vector3.Zero;

            var heading = float.IsNaN(_poseLying) ? _body.Heading : _poseLying - twist;

            var root = _body.Position;

            if (_posePelvis != Vector3.Zero)
            {
                var p = _posePelvis - Motion.Rotate(local, heading);
                root = new Vector3(p.X, p.Y, root.Z);
            }

            root.Z = Crew.Ground(root, root.Z);

            _scene = Sync.Anchored(_poseDict, _poseClip, root, heading);

            if (_scene.Begin(false, true) &&
                _scene.Cast(_body, _poseDict, _poseClip, _poseBlend, _poseMover))
            {
                _scene.Rate(0f);
            }
            else
            {
                Log.Warn("Could not pose him for " + _poseClip + ".");
            }
        }

        /// <summary>
        /// The pose, measured off his skeleton once it has settled, and put right if it is off.
        ///
        /// Waits for the blend: a skeleton read half way through a half-second blend is half the
        /// old pose, and would teach the cache the wrong answer for the whole session.
        /// </summary>
        private void CheckPose(int now)
        {
            if (!_poseCheck) return;

            var wait = Math.Max(80, (int)(1000f / Math.Max(0.5f, _poseBlend)) + 80);
            if (now - _posedAt < wait) return;

            _poseCheck = false;

            try
            {
                var lyingNow = Crew.Lying(_body);

                Vector3 pelvisNow;
                if (float.IsNaN(lyingNow) || !Crew.Pelvis(_body, out pelvisNow)) return;

                var root = _body.Position;
                var heading = _body.Heading;

                Twist[_poseClip] = Motion.Wrap(lyingNow - heading);
                PelvisAt[_poseClip] = Motion.Rotate(Motion.Flat(pelvisNow - root), -heading);

                var turned = Math.Abs(Motion.Wrap(lyingNow - _poseLying));
                var moved = Motion.FlatDistance(pelvisNow, _posePelvis);

                Log.Debug("Measured " + _poseClip + ": off by " + turned.ToString("0") + " degrees and " +
                          moved.ToString("0.00") + "m" + (turned > 12f || moved > 0.25f ? "; re-placed." : "."));

                if (turned > 12f || moved > 0.25f) PoseNow();
            }
            catch (Exception ex)
            {
                Log.Debug("Could not measure the pose: " + ex.Message);
            }
        }

        // ---- the CPR -----------------------------------------------------------

        private void Working(int now)
        {
            if (Entering())
            {
                if (!Arrest(now))
                {
                    Log.Info("He could not be brought into arrest; treating him as gone.");
                    _verdict = Verdict.Gone;
                    To(Step.Pronounce, now);
                    return;
                }

                Choreograph();
                Mate_();

                _beat = -1;
                return;
            }

            if (!Crew.Alive(_body))
            {
                Log.Warn("The patient died " + ((now - _stepAt) / 1000f).ToString("0.0") +
                         "s into the scene" + (now - _stepAt < 1500 ? " -- too soon to have been shot." : "."));
                To(Step.Fleeing, now);
                return;
            }

            if (!_mateBusy) Settle_(now);

            // STILL SETTING UP: the patient posed and held, the medic walking to his mark.
            if (_beat < 0)
            {
                CheckPose(now);

                if (_poseCheck) return;

                if (_walk == Walk.None) { Approach(_driver, _scene, Anim.CprMedic, Anim.Kneel, now); return; }

                if (!Approached(_driver, now)) return;

                // On his mark. Into the scene the patient is already holding, and it starts.
                _scene.Cast(_driver, Anim.CprMedic, Anim.Kneel);
                _scene.Rate(1f);

                _beat = 0;
                _beatAt = now;
                return;
            }

            if (_beat >= _beats.Length)
            {
                if (now - _verdictAt < _cfg.VerdictMs) return;

                if (_verdict == Verdict.Workable) To(Step.Rising, now);
                else To(Step.Pronounce, now);

                return;
            }

            if (!BeatDone(now)) return;

            Advance(now);
        }

        /// <summary>
        /// Brought back, into arrest, and posed exactly where and how he fell.
        ///
        /// HIS BONES ARE READ WHILE HE IS STILL A RAGDOLL, because the moment he is resurrected
        /// they are wherever the capsule puts them. The dictionaries are made ready BEFORE the
        /// resurrection for the same reason: a man with no task stands up, and a load that
        /// yields with him in that state is a corpse standing up on camera.
        ///
        /// RESURRECT_PED LEAVES A PED BLANK -- no flags, out of his group, not held. What goes
        /// back on is exactly what makes him a patient: nothing can target him, nothing he sees
        /// moves him, he cannot fall, he is held, and he has a third of the bar -- never a third
        /// of the NUMBER, which is under a hundred and therefore dead. See Crew.Floor.
        /// </summary>
        private bool Arrest(int now)
        {
            if (!Crew.There(_body)) return false;

            try
            {
                var lying = Crew.Lying(_body);

                Vector3 pelvis;
                if (!Crew.Pelvis(_body, out pelvis)) pelvis = Vector3.Zero;

                Anim.Ready(Anim.CprVictim);
                Anim.Ready(Anim.CprMedic);

                var h = _body.Handle;

                Function.Call(Hash.RESURRECT_PED, h);

                if (!Crew.Alive(_body)) return false;

                Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, h);

                Crew.Hurt(_body, 0.33f);

                Function.Call(Hash.SET_PED_CAN_RAGDOLL, h, false);
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, h, true);
                Function.Call(Hash.SET_PED_CAN_BE_TARGETTED, h, false);
                Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, h, 0, false);

                Crew.Hold(_body);

                _ours = true;

                // SNAPPED, THIS ONCE, AND ON PURPOSE. A ragdoll becoming a posed patient has to
                // change in a single frame, because the only thing it could blend FROM is the
                // standing idle a resurrected ped wakes up in.
                Pose(Anim.CprVictim, Anim.Kneel, lying, pelvis, Sync.Snap, Sync.Snap, now);

                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not bring him into arrest: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// The order of the clips. Two rounds for a man with a chance, one for a man without --
        /// which is what makes the two outcomes read differently before either ending plays.
        /// </summary>
        private void Choreograph()
        {
            if (_verdict == Verdict.Workable)
            {
                var half = Math.Max(1500, _cfg.WorkMs / 2);

                _beats = new[]
                {
                    Once(Anim.Kneel),
                    Loop(Anim.KneelIdle, FirstLookMs),
                    Once(Anim.KneelToCpr),
                    Loop(Anim.Pump, half),
                    Once(Anim.CprToKneel),
                    Loop(Anim.KneelIdle, BetweenMs),
                    Once(Anim.KneelToCpr),
                    Loop(Anim.Pump, half),
                    Once(Anim.CprToKneel),
                    Loop(Anim.KneelIdle, LastLookMs),
                    Once(Anim.Worked),
                };

                return;
            }

            _beats = new[]
            {
                Once(Anim.Kneel),
                Loop(Anim.KneelIdle, FirstLookMs),
                Once(Anim.KneelToCpr),
                Loop(Anim.Pump, Math.Max(1500, _cfg.CheckMs)),
                Once(Anim.CprToKneel),
                Loop(Anim.KneelIdle, BetweenMs),
                Once(Anim.Failed),
            };
        }

        private static Beat Once(string clip)
        {
            return new Beat { Clip = clip, Loop = false, Ms = OneShotMs };
        }

        private static Beat Loop(string clip, int ms)
        {
            return new Beat { Clip = clip, Loop = true, Ms = ms };
        }

        /// <summary>
        /// The next clip, on both of them, against a fresh scene at the same origin.
        ///
        /// EVERY ONE-SHOT HOLDS ITS LAST FRAME, not just the final one. A one-shot that let go at
        /// its end dropped the medic into a standing idle for the tick before the next clip took
        /// over -- a man bolt upright for a frame in the middle of a resuscitation.
        /// </summary>
        private void Advance(int now)
        {
            _beat++;
            _beatAt = now;

            if (_beat >= _beats.Length)
            {
                _verdictAt = now;
                return;
            }

            var beat = _beats[_beat];

            _scene.End();

            if (!_scene.Begin(beat.Loop, !beat.Loop))
            {
                Log.Warn("The scene would not start for " + beat.Clip + ".");
                return;
            }

            var a = _scene.Cast(_driver, Anim.CprMedic, beat.Clip);
            var b = _scene.Cast(_body, Anim.CprVictim, beat.Clip);

            if (!a || !b)
            {
                Log.Warn("Could not cast " + (!a && !b ? "either of them" : !a ? "the medic" : "the patient") +
                         " into " + beat.Clip + ".");
            }
        }

        /// <summary>
        /// Whether the current beat has run its course. A loop is done when its time is up; a
        /// one-shot when the scene says so, not trusted on the first frame, and not waited on
        /// past its ceiling.
        /// </summary>
        private bool BeatDone(int now)
        {
            var beat = _beats[_beat];
            var age = now - _beatAt;

            if (beat.Loop) return age >= beat.Ms;

            if (age < 200) return false;

            var phase = _scene.Phase;

            if (phase >= 0.985f) return true;
            if (phase < 0f && age > 1000) return true;

            return age > beat.Ms;
        }

        /// <summary>
        /// The second man: the bag down beside him, and himself to the far side if he is not
        /// there yet. The kneeling waits for him to arrive -- see Settle_.
        ///
        /// HE USED TO KNEEL WHEREVER HE HAPPENED TO BE STANDING, which after a straight-line
        /// walk that hit a door was often halfway back to the van, facing the wrong way. The
        /// scenario is started where he is, so where he is has to be right first.
        /// </summary>
        private void Mate_()
        {
            _mateBusy = false;
            _mateAt = Game.GameTime;

            if (!Crew.Alive(_mate) || !Crew.There(_body)) return;

            try
            {
                var flank = Flank();

                if (_kit.There) _kit.SetDown(flank + Motion.Flat(_at - flank).Normalized * 0.9f);

                if (Motion.FlatDistance(_mate.Position, flank) > 0.7f)
                {
                    Crew.WalkTo(_mate, flank, Motion.HeadingOf(Motion.Flat(_body.Position - flank)), 1.4f, 6000);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("The second man could not be sent over: " + ex.Message);
            }
        }

        /// <summary>Kneeling, tending, the moment he is on his spot -- or after six seconds of trying.</summary>
        private void Settle_(int now)
        {
            if (!Crew.Alive(_mate) || !Crew.There(_body)) { _mateBusy = true; return; }

            var flank = Flank();
            var there = Motion.FlatDistance(_mate.Position, flank) < 0.7f;

            if (!there && now - _mateAt < 6000) return;

            try
            {
                var at = Motion.Flat(_body.Position - _mate.Position);
                if (at.Length() > 0.2f) _mate.Heading = Motion.HeadingOf(at);

                _mateBusy = Anim.Scenario(_mate, Anim.TendScenario);

                if (!_mateBusy) Look(_mate);
            }
            catch (Exception ex)
            {
                Log.Debug("The second man could not settle: " + ex.Message);
            }

            // Whether it took or not, he is not asked again this scene.
            _mateBusy = true;
        }

        private void Look(Ped who)
        {
            if (!Crew.Alive(who) || !Crew.There(_body)) return;

            try
            {
                if (who.Position.DistanceTo(_body.Position) > 4f) return;

                Anim.Play(who, Anim.LookDict, Anim.LookClip, Anim.Loop);
            }
            catch
            {
                // He stands there instead, which is still a man at a scene.
            }
        }

        // ---- he comes round ----------------------------------------------------

        /// <summary>
        /// Hauled to his feet: the patient anchors the scene where he is sitting, the medic
        /// walks to his mark, and it runs.
        /// </summary>
        private void Rising(int now)
        {
            if (Entering())
            {
                Log.Info("He came round -- it was " + Cause.Word(_weapon) + ".");

                if (Say != null && Near()) Say("They bring him round.");

                _scene.End();
                _sceneAt = 0;

                // SAT UP, SO THERE IS NO LYING DIRECTION TO MATCH -- Crew.Lying says as much --
                // and the anchor is simply his root. The pose blend is a quarter second: he is
                // already sitting, and the new clip starts sitting.
                _scene = Sync.Anchored(Anim.Rescue, Anim.RescueVictim, _body.Position, _body.Heading);

                var ok = _scene.Begin(false, true) &&
                         _scene.Cast(_body, Anim.Rescue, Anim.RescueVictim, 4f, Sync.Settle);

                if (!ok)
                {
                    Log.Debug("The helping-up scene would not start; he gets up himself.");
                    Function.Call(Hash.CLEAR_PED_TASKS, _driver.Handle);
                    Anim.Play(_body, Anim.GetUpDict, Anim.GetUpClip, 0);
                    _walk = Walk.There;
                    _sceneAt = now;
                    return;
                }

                _scene.Rate(0f);

                Approach(_driver, _scene, Anim.Rescue, Anim.RescueMedic, now);
                return;
            }

            if (!Crew.Alive(_body)) { To(Step.Fleeing, now); return; }

            if (_sceneAt == 0)
            {
                if (!Approached(_driver, now)) return;

                _scene.Cast(_driver, Anim.Rescue, Anim.RescueMedic);
                _scene.Rate(1f);
                _sceneAt = now;
                return;
            }

            var age = now - _sceneAt;

            if (age < 300) return;

            var phase = _scene.Phase;
            var done = phase >= 0.985f || (phase < 0f && age > 1200) || age > _cfg.RisingMs;

            if (!done) return;

            LetGo();
            Leave(now, null);
        }

        /// <summary>Handed to the city, alive, limping, with a third of his bar.</summary>
        private void LetGo()
        {
            try
            {
                var h = _body.Handle;

                _scene.End();

                Function.Call(Hash.CLEAR_PED_TASKS, h);

                Crew.Hurt(_body, 0.33f);
                Crew.Solid(_body, true);

                Function.Call(Hash.SET_PED_CAN_RAGDOLL, h, true);
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, h, false);
                Function.Call(Hash.SET_PED_CAN_BE_TARGETTED, h, true);

                if (_cfg.InjuredWalk)
                {
                    Anim.Clipset(_body, Crew.IsMale(_body) ? Anim.LimpMale : Anim.LimpFemale);
                }

                Function.Call(Hash.TASK_WANDER_STANDARD, h, 10f, 10);

                _ours = false;

                Crew.Give(_body);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not let him go: " + ex.Message);
            }

            _body = null;

            Unsettle(_mate);
        }

        // ---- he does not --------------------------------------------------------

        /// <summary>
        /// Calling it -- and, while the second man writes it down, the patient eased from the
        /// last frame of the CPR into the opening pose of the lift, where he waits.
        ///
        /// HE IS PUT INTO THE LIFT'S POSE HERE, NOT WHEN THE LIFT STARTS. That is what stops the
        /// lift beginning with him jumping into it: by the time the medic reaches his mark, the
        /// patient has been lying in exactly the pose the lift begins from for several seconds,
        /// having settled into it over half a second while nobody was touching him.
        /// </summary>
        private void Pronounce(int now)
        {
            if (Entering())
            {
                _scene.End();

                Function.Call(Hash.CLEAR_PED_TASKS, _driver.Handle);

                if (_cfg.TimeOfDeath) Anim.Scenario(_mate, Anim.TimeOfDeathScenario);
                else Unsettle(_mate);

                if (Crew.Alive(_body))
                {
                    Vector3 pelvis;
                    if (!Crew.Pelvis(_body, out pelvis)) pelvis = Vector3.Zero;

                    Pose(Anim.LiftDict, Anim.LiftBody, Crew.Lying(_body), pelvis, 2f, Sync.Settle, now);
                    Crew.Hold(_body);
                }

                if (Say != null && Near()) Say("They stop working on him.");
                return;
            }

            CheckPose(now);

            if (now - _stepAt < _cfg.PronounceMs) return;

            if (!_cfg.TakeToHospital) { Leave(now, "they are not taking him"); return; }

            if (_vanLost) { OnFoot("there is no van to put him in"); return; }

            Unsettle(_mate);

            To(Step.Fetching, now);
        }

        private static void Unsettle(Ped who)
        {
            if (!Crew.Alive(who)) return;

            try { Function.Call(Hash.CLEAR_PED_TASKS, who.Handle); }
            catch { /* He stands there. */ }
        }

        // ---- shot under their hands --------------------------------------------

        private void Fleeing(int now)
        {
            if (Entering())
            {
                if (_scene != null) _scene.End();

                Log.Info("The patient was killed with the crew working on him.");

                Anim.Play(_driver, Anim.FleeDict, Anim.FleeClip, 0);
                Anim.Play(_mate, Anim.FleeDict, Anim.FleeClip, 0);

                _ours = false;

                if (Say != null && Near()) Say("The crew back off.");
                return;
            }

            if (now - _stepAt < FleeMs) return;

            Leave(now, "the patient was shot");
        }

        // ---- the trolley -------------------------------------------------------

        /// <summary>
        /// The trolley out and put down -- beside where the lift will LEAVE him.
        ///
        /// THE LIFT IS ASKED WHERE IT ENDS BEFORE IT STARTS. Sync.Mark at phase 1 says where the
        /// clip deposits the patient, and at phase 0 where the medic stands to lift him. The
        /// trolley goes alongside the deposit spot, parallel to that line, on the van's side --
        /// so the carry from his arms to the bed is a short step sideways rather than a flight
        /// across the pavement, and nobody is standing where it appears.
        /// </summary>
        private void Fetching(int now)
        {
            if (!Crew.There(_body)) { Leave(now, "the body had gone"); return; }

            if (Entering())
            {
                // THE DOORS STAY SHUT UNTIL THE TROLLEY IS COMING. They used to be opened here,
                // a full minute before anybody went near the van, and an ambulance's rear doors
                // swing out a metre either side -- so every walk to the back of it ended against
                // a door. They open when the wheeling starts; see Wheeling.
                if (_kit.There) _kit.Bring(_mate);

                Vector3 spot;
                float along;
                Lay_out(out spot, out along);

                if (!_trolley.Bring(spot, along)) _carrying = true;
            }

            if (now - _stepAt < _cfg.FetchMs) return;

            To(_carrying ? Step.Loading : Step.Lifting, now);
        }

        private void Lay_out(out Vector3 spot, out float along)
        {
            Vector3 end;
            float ignored;

            if (_scene == null || !_scene.Mark(Anim.LiftDict, Anim.LiftBody, 1f, out end, out ignored))
            {
                end = _body.Position;
            }

            var stance = Vector3.Zero;
            var haveStance = _scene != null &&
                             _scene.Mark(Anim.LiftDict, Anim.LiftMedic, 0f, out stance, out ignored);

            var line = haveStance ? Motion.Flat(end - stance) : Motion.Facing(_body.Heading);
            if (line.Length() < 0.3f) line = Motion.Facing(_body.Heading);
            line = line.Normalized;

            var toVan = Crew.Alive(_van) ? Motion.Flat(_van.Position - end) : Vector3.Zero;

            var side = new Vector3(-line.Y, line.X, 0f);
            if (Vector3.Dot(side, toVan) < 0f) side = -side;

            // Parallel to the lift line, pointing whichever way is more towards the van -- so
            // the back of it is where the medic will stand and forward is where he will push.
            var forward = Vector3.Dot(line, toVan) >= 0f ? line : -line;

            spot = end + side * 0.9f;
            along = Motion.HeadingOf(forward);

            Log.Debug("Trolley laid out " + Motion.FlatDistance(spot, _body.Position).ToString("0.00") +
                      "m from him, " + (haveStance ? "off the lift's own marks." : "off his heading."));
        }

        // ---- lifting him --------------------------------------------------------

        /// <summary>
        /// The medic to his mark, then the lift. The patient has been holding its opening pose
        /// since Pronounce, in the same scene, paused -- so it starts with nobody jumping.
        /// </summary>
        private void Lifting(int now)
        {
            if (!Crew.There(_body)) { Leave(now, "the body had gone"); return; }

            if (Entering())
            {
                _sceneAt = 0;

                if (_scene == null)
                {
                    Log.Warn("There was no lift scene to join; he goes straight onto the canvas.");
                    To(Step.Loading, now);
                    return;
                }

                Approach(_driver, _scene, Anim.LiftDict, Anim.LiftMedic, now);
                Steady();
                return;
            }

            if (_sceneAt == 0)
            {
                if (!Approached(_driver, now)) return;

                if (!_scene.Cast(_driver, Anim.LiftDict, Anim.LiftMedic))
                {
                    Log.Warn("The lift would not start; he goes straight onto the canvas.");
                    To(Step.Loading, now);
                    return;
                }

                _scene.Rate(1f);
                _sceneAt = now;
                return;
            }

            var age = now - _sceneAt;

            if (age < 300) return;

            var phase = _scene.Phase;
            var done = phase >= 0.985f || (phase < 0f && age > 1200) || age > LiftMs;

            if (!done) return;

            To(Step.Loading, now);
        }

        /// <summary>The second man to the far side of the trolley, facing it, ready to receive him.</summary>
        private void Steady()
        {
            if (!Crew.Alive(_mate) || !_trolley.There || !Crew.There(_body)) return;

            try
            {
                var away = Motion.Flat(_trolley.Where - _body.Position);
                if (away.Length() < 0.2f) return;

                var at = _trolley.Where + away.Normalized * 0.8f;

                Crew.WalkTo(_mate, at, Motion.HeadingOf(-away), 1f, 5000);
            }
            catch
            {
                // He stands where he is, which is still a man at a scene.
            }
        }

        /// <summary>
        /// Onto the canvas -- carried, not teleported -- and the medic round to the back.
        ///
        /// THE BODY IS MOVED, ALONG AN ARC, FROM THE MEDIC'S ARMS TO THE BED. It used to be
        /// attached to the trolley the instant the lift ended, which moved him from wherever the
        /// lift left him to the canvas in one frame: a man vanishing from somebody's arms and
        /// reappearing lying down. Now his root travels to exactly the point the attachment will
        /// put it, rising a little in the middle, over most of a second, while his pose settles
        /// from being held into lying -- and only when he is there is he attached, which then
        /// changes nothing you can see.
        ///
        /// Frozen and without collision for the journey: frozen so gravity does not pull him
        /// down between the frames he is placed on, uncollided so he can pass over the rail.
        /// </summary>
        private void Loading(int now)
        {
            if (!Crew.There(_body)) { Leave(now, "the body had gone"); return; }

            if (Entering())
            {
                if (_scene != null) _scene.End();

                if (_carrying)
                {
                    Carry();
                    Carrying(now);
                    return;
                }

                _laid = false;

                if (!_trolley.Bed(out _carryTo, out _carryToHeading))
                {
                    _trolley.Lay(_body);
                    _laid = true;
                    _laidAt = now;
                    Step_(_driver, _trolley.Behind, _trolley.AlongHeading, MarkMs);
                    return;
                }

                _carryFrom = _body.Position;
                _carryFromHeading = _body.Heading;

                Crew.Solid(_body, false);
                Function.Call(Hash.FREEZE_ENTITY_POSITION, _body.Handle, true);

                // He lets go, and the patient settles into lying over the same span he is
                // carried across.
                Function.Call(Hash.CLEAR_PED_TASKS, _driver.Handle);
                Anim.Play(_body, Anim.DeadDict, Anim.DeadPose, Anim.Hold, -1, 1000f / CarryMs);

                _sceneAt = now;
                return;
            }

            if (!_laid)
            {
                var t = (now - _sceneAt) / (float)CarryMs;

                if (t < 1f)
                {
                    var e = Motion.Smooth(t);
                    var at = Motion.Lerp(_carryFrom, _carryTo, e) +
                             new Vector3(0f, 0f, (float)Math.Sin(Math.PI * t) * 0.25f);

                    Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, _body.Handle,
                                  at.X, at.Y, at.Z, false, false, false);

                    _body.Heading = Motion.Turn(_carryFromHeading, _carryToHeading, e);
                    return;
                }

                Function.Call(Hash.FREEZE_ENTITY_POSITION, _body.Handle, false);

                _trolley.Lay(_body);
                _laid = true;
                _laidAt = now;

                // AND THE MAN WALKS TO THE TROLLEY, NOT THE TROLLEY TO THE MAN, turning at the
                // end to face along it -- so when he takes hold, it is already in front of him
                // and the first Follow moves it by nothing.
                Step_(_driver, _trolley.Behind, _trolley.AlongHeading, MarkMs);
                return;
            }

            if (now - _laidAt < SettleMs) return;

            var set = Crew.Alive(_driver) &&
                      Motion.FlatDistance(_driver.Position, _trolley.Behind) < 0.5f;

            if (!set && now - _laidAt < _cfg.LoadMs) return;

            _trolley.Take(_driver);

            Walk_(now);

            To(Step.Wheeling, now);
        }

        /// <summary>No trolley, so he goes over a shoulder. The install has neither gurney prop.</summary>
        private void Carry()
        {
            OnShoulder();
        }

        private void OnShoulder()
        {
            if (!Crew.Alive(_driver) || !Crew.There(_body)) return;

            try
            {
                Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY,
                              _body.Handle, _driver.Handle, 0,
                              _cfg.CarryX, _cfg.CarryY, _cfg.CarryZ,
                              0f, 0f, _cfg.CarryYaw,
                              false, false, false, false, 2, true, 0);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not pick him up: " + ex.Message);
            }
        }

        private void Carrying(int now)
        {
            Walk_(now);
            To(Step.Wheeling, now);
        }

        /// <summary>Walks somebody to an exact spot, on the ground, turning to a heading at the end.</summary>
        private static void Step_(Ped who, Vector3 to, float heading, int ms)
        {
            Crew.WalkTo(who, to, heading, 1f, ms);
        }

        // ---- to the van --------------------------------------------------------

        /// <summary>
        /// Where the medic stops, and the second man goes.
        ///
        /// SHORT OF THE BUMPER, BY MEASUREMENT. He used to be sent to a point 3.2 metres behind
        /// the van's centre, which for an ambulance is barely past its bumper -- and the trolley
        /// goes 1.1 metres in front of him, so it was placed inside the van, every tick. With
        /// the trolley frozen and colliding that is the physics told to push an immovable thing
        /// into a vehicle, and it pushed the vehicle: four times the van was flung out of the
        /// world and deleted with the crew stood behind it.
        ///
        /// The van's rear is now read off its model, and he stops far enough back that the front
        /// of the trolley is half a metre short of it. The roll in through the doors is its own
        /// step.
        /// </summary>
        private void Walk_(int now)
        {
            if (!Crew.Alive(_van)) return;

            if (float.IsNaN(_vanRear))
            {
                Vector3 min, max;
                _vanRear = Crew.Measure(Crew.Van, out min, out max) ? min.Y : -3f;
            }

            var back = _carrying ? 1.0f : _cfg.TrolleyPushY + _trolley.HalfLength + 0.5f;

            _stopAt = Crew.Offset(_van, 0f, _vanRear - back, 0f);

            Step_(_driver, _stopAt, _van.Heading, _cfg.WheelMs);

            // THE SECOND MAN GOES TO THE CAB, NOT THE BACK. He was sent to a spot beside the rear
            // corner, which is exactly the arc the right-hand door swings through, and he stood
            // in it with the door in his face. Beside the passenger door there is nothing to
            // stand in, and it is where he is getting in anyway.
            var side = Crew.Offset(_van, 1.9f, 1.2f, 0f);
            Step_(_mate, side, _van.Heading + 90f, _cfg.WheelMs);
        }

        private void Wheeling(int now)
        {
            // The doors, as the trolley sets off towards them -- see Fetching for why not before.
            if (Entering()) Doors(true);

            if (!_carrying)
            {
                // The shopping-trolley pose on his upper body, over the walk his task gives him;
                // and the trolley in front of him at the road's height, every tick.
                Anim.Play(_driver, Anim.PushDict, Anim.PushClip, Anim.Push);
                _trolley.Follow();
            }

            var there = Crew.Alive(_driver) && Motion.FlatDistance(_driver.Position, _stopAt) < 0.6f;

            if (!there && now - _stepAt < _cfg.WheelMs) return;

            To(Step.Stowing, now);
        }

        /// <summary>
        /// Rolled in through the doors, then attached, then everybody aboard.
        ///
        /// It used to be attached straight into the van, which moved it two metres in one frame.
        /// It rolls now, and the attach happens where the roll ends.
        /// </summary>
        private void Stowing(int now)
        {
            if (Entering())
            {
                _kit.Release();

                if (_carrying)
                {
                    InVan();
                    _rolled = true;
                    Aboard(now);
                    return;
                }

                _trolley.RollFrom();
                _rolled = false;
                _sceneAt = now;
                return;
            }

            if (!_rolled)
            {
                var t = (now - _sceneAt) / (float)RollMs;

                if (t < 1f)
                {
                    _trolley.Roll(_van, Motion.Smooth(t));
                    return;
                }

                if (!_trolley.Stow(_van)) InVan();

                _rolled = true;
                Aboard(now);
                return;
            }

            if (now - _boardAt < _cfg.StowMs) return;

            _to = Hospitals.Nearest(_van.Position);

            To(Step.Driving, now);

            Function.Call(Hash.SET_VEHICLE_SIREN, _van.Handle, false);
            Drive(_to, 20f);

            Log.Info("They are taking him to the hospital, " + (int)_van.Position.DistanceTo(_to) + "m away.");

            if (Say != null && Near()) Say("The ambulance leaves for the hospital.");
        }

        private void Aboard(int now)
        {
            Anim.Stop(_driver, Anim.PushDict, Anim.PushClip);

            // DOORS SHUT BEFORE ANYBODY IS SENT TO A SEAT. They used to close after the boarding
            // timer, so both men were routed round to the cab past two open rear doors, and
            // the one who was standing between them got stuck. The trolley is in; there is
            // nothing left for them to be open for.
            Doors(false);

            Board(_driver, -1);
            Board(_mate, 0);

            _boardAt = now;
        }

        private void InVan()
        {
            if (!Crew.There(_body) || !Crew.Alive(_van)) return;

            try
            {
                Function.Call(Hash.DETACH_ENTITY, _body.Handle, true, true);

                _bodyInVan = true;

                InBack();
            }
            catch (Exception ex)
            {
                Log.Debug("Could not get him into the back: " + ex.Message);
            }
        }

        private void InBack()
        {
            if (!Crew.There(_body) || !Crew.Alive(_van)) return;

            Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY,
                          _body.Handle, _van.Handle, 0,
                          _cfg.BodyInVanX, _cfg.BodyInVanY, _cfg.BodyInVanZ,
                          0f, 0f, _cfg.BodyInVanYaw,
                          false, false, false, false, 2, true, 0);
        }

        private void Board(Ped who, int seat)
        {
            if (!Crew.Alive(who) || !Crew.Alive(_van)) return;

            try
            {
                Function.Call(Hash.TASK_ENTER_VEHICLE, who.Handle, _van.Handle,
                              _cfg.StowMs, seat, 2f, 1, 0);
            }
            catch
            {
                // The van goes without him and he is handed back at the end.
            }
        }

        /// <summary>The back doors: 5 is the boot on most models and the rear pair on some; 2 and 3 where a model has them.</summary>
        private void Doors(bool open)
        {
            if (!Crew.Alive(_van)) return;

            foreach (var door in new[] { 2, 3, 5 })
            {
                try
                {
                    if (open) Function.Call(Hash.SET_VEHICLE_DOOR_OPEN, _van.Handle, door, false, false);
                    else Function.Call(Hash.SET_VEHICLE_DOOR_SHUT, _van.Handle, door, false);
                }
                catch
                {
                    // This model does not have that door.
                }
            }
        }

        // ---- away ---------------------------------------------------------------

        private void Leave(int now, string why)
        {
            if (why != null) Log.Info("The call-out ended: " + why + ".");

            if (_vanLost || !Crew.Alive(_van)) { OnFoot(null); return; }

            if (_scene != null) _scene.End();

            _kit.Release();

            Unsettle(_driver);
            Unsettle(_mate);

            Board(_driver, -1);
            Board(_mate, 0);

            To(Step.Driving, now);

            _to = Vector3.Zero;
        }

        private void OnFoot(string why)
        {
            if (why != null) Log.Info("The call-out ended: " + why + ".");

            Done();
        }

        private static string HowLost(Vehicle van)
        {
            try
            {
                if (van == null || !van.Exists()) return "deleted -- something removed it";

                var fire = Function.Call<bool>(Hash.IS_ENTITY_ON_FIRE, van.Handle);
                var health = Function.Call<int>(Hash.GET_ENTITY_HEALTH, van.Handle);

                return "wrecked (health " + health + (fire ? ", on fire" : "") + ")";
            }
            catch
            {
                return "lost";
            }
        }

        private void Driving(int now)
        {
            if (now - _stepAt < _cfg.BoardMs) return;

            if (Entering() && _to == Vector3.Zero)
            {
                try
                {
                    Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER,
                                  _driver.Handle, _van.Handle, _cfg.Speed, DriveStyle);
                }
                catch
                {
                    // It sits there. The release below still comes.
                }
            }

            if (_to != Vector3.Zero && _van.Position.DistanceTo(_to) < _cfg.ArrivedRange)
            {
                Log.Info("The ambulance reached the hospital.");
                Done();
                return;
            }

            if (now - _stepAt > _cfg.GoneMs) Done();
        }

        private bool Near()
        {
            try
            {
                var me = Game.Player.Character;
                if (me == null || !me.Exists()) return false;

                return me.Position.DistanceTo(_at) < _cfg.TellRange;
            }
            catch
            {
                return false;
            }
        }

        // ---- the settings screen -----------------------------------------------

        public void Refit()
        {
            if (_step == Step.None) return;

            try
            {
                _trolley.Refit();

                if (!_carrying) return;

                if (_bodyInVan) InBack();
                else OnShoulder();
            }
            catch (Exception ex)
            {
                Log.Debug("Could not re-fit the scene: " + ex.Message);
            }
        }

        public string State
        {
            get
            {
                switch (_step)
                {
                    case Step.None:      return "nothing on";
                    case Step.Coming:    return "on the way";
                    case Step.Reaching:  return "walking over";
                    case Step.Working:   return Working_();
                    case Step.Rising:    return "getting him up";
                    case Step.Pronounce: return "calling it";
                    case Step.Fetching:  return "fetching the trolley";
                    case Step.Lifting:   return "lifting him";
                    case Step.Loading:   return _carrying ? "picking him up" : "onto the canvas";
                    case Step.Wheeling:  return _carrying ? "carrying him back" : "wheeling him back";
                    case Step.Stowing:   return "into the back";
                    case Step.Driving:   return _to == Vector3.Zero ? "leaving" : "driving to the hospital";
                    case Step.Fleeing:   return "backing off";
                }

                return "?";
            }
        }

        private string Working_()
        {
            if (_beats == null) return "at the body";
            if (_beat < 0) return "kneeling down";
            if (_beat >= _beats.Length) return _verdict == Verdict.Workable ? "they have him" : "nothing to be done";

            var clip = _beats[_beat].Clip;

            if (clip == Anim.Pump) return "compressions";
            if (clip == Anim.KneelIdle) return "checking him";
            if (clip == Anim.Worked) return "they have him";
            if (clip == Anim.Failed) return "nothing to be done";

            return "working on him";
        }

        // ---- handing it all back ------------------------------------------------

        /// <summary>
        /// Everything given back to the game. The trolley lets go of the body before it is
        /// deleted; THEN a man still ours goes back to dead, solid and unfrozen; only then is
        /// anything handed to the population manager.
        /// </summary>
        public void Done()
        {
            if (_scene != null) _scene.End();

            _kit.Release();
            _trolley.Release();

            try
            {
                if (Crew.There(_body))
                {
                    if (Function.Call<bool>(Hash.IS_ENTITY_ATTACHED, _body.Handle))
                    {
                        Function.Call(Hash.DETACH_ENTITY, _body.Handle, true, true);
                    }

                    Function.Call(Hash.FREEZE_ENTITY_POSITION, _body.Handle, false);
                    Crew.Solid(_body, true);

                    if (_ours && Crew.Alive(_body)) Kill(_body);

                    Crew.Give(_body);
                }
            }
            catch
            {
                // Gone already.
            }

            Loose(_driver);
            Loose(_mate);

            Crew.Give(_van);

            _van = null;
            _driver = null;
            _mate = null;
            _body = null;

            _at = Vector3.Zero;
            _to = Vector3.Zero;

            _step = Step.None;
            _stepAt = 0;
            _beats = null;
            _beat = 0;
            _carrying = false;
            _bodyInVan = false;
            _ours = false;
            _mateBusy = false;
            _mateOut = false;
            _vanLost = false;
            _walk = Walk.None;
            _poseCheck = false;
            _groundWarned = false;
            _vanAt = Vector3.Zero;
            _scene = null;
        }

        /// <summary>
        /// Dead again, as he was found: everything Arrest put on him taken off, so what is left
        /// is an ordinary corpse that ragdolls, bleeds and can be searched.
        /// </summary>
        private static void Kill(Ped who)
        {
            try
            {
                var h = who.Handle;

                Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, h);
                Function.Call(Hash.SET_PED_CAN_RAGDOLL, h, true);
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, h, false);
                Function.Call(Hash.SET_PED_CAN_BE_TARGETTED, h, true);
                Function.Call(Hash.SET_ENTITY_HEALTH, h, 0);
            }
            catch
            {
                // Gone already.
            }
        }

        /// <summary>One of the crew, put back the way he was found, and given somewhere to go.</summary>
        private static void Loose(Ped who)
        {
            try
            {
                if (who == null || !who.Exists()) return;

                Function.Call(Hash.CLEAR_PED_TASKS, who.Handle);
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, who.Handle, false);
                Function.Call(Hash.SET_PED_CAN_BE_TARGETTED, who.Handle, true);

                if (!who.IsInVehicle()) Function.Call(Hash.TASK_WANDER_STANDARD, who.Handle, 10f, 10);
            }
            catch
            {
                // Gone already.
            }

            Crew.Give(who);
        }
    }
}
