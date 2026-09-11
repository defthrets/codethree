using System;
using GTA;
using GTA.Math;
using GTA.Native;
using Flatline.Core;

namespace Flatline.Scene
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

        /// <summary>Back to the van for the trolley.</summary>
        Fetching,

        /// <summary>Trolley beside him, and him onto it.</summary>
        Loading,

        /// <summary>Wheeling him back.</summary>
        Wheeling,

        /// <summary>Into the back, doors shut, crew aboard.</summary>
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
    /// THE PATIENT IS A PARTICIPANT, NOT A PROP. That is the whole difference between this and
    /// the first version. A corpse is a ragdoll and cannot be animated, so 0.1.0 played half a
    /// two-hander over a heap and hoped the compressions landed somewhere near a chest. Here he
    /// is brought back into arrest the moment the crew reach him -- alive in the engine's eyes,
    /// unconscious in everybody else's -- and from then on he is one of two people in a
    /// synchronised scene, placed by the animation data rather than by us. The hands land on
    /// the sternum because the game authored both halves of every clip around one origin.
    ///
    /// AND IT IS THE WHOLE SEQUENCE, NOT A LOOP. mini@cpr has seven clips and they are a story:
    /// down to a knee, a look at him, the lean in, the compressions, sitting back, another look
    /// -- and then either the one where he comes up or the one where the medic does. 0.1.0
    /// played the compressions and the ending. This plays all of it, in the order it was
    /// written, advanced by each clip finishing rather than by a stopwatch.
    ///
    /// ONE AT A TIME, AND THAT IS NOT A LIMITATION. Two ambulances at one junction is a
    /// pile-up rather than a scene, and a second call refused is a second call that would have
    /// arrived at an empty street anyway.
    ///
    /// EVERY STEP HAS A DEADLINE AND A WAY OUT. A crew that cannot reach a body on a rooftop, a
    /// scene the engine will not start, a trolley that will not load -- none of those may leave
    /// the call-out stuck, because a stuck call-out holds two persistent peds, a vehicle, a prop
    /// and now a resurrected man for the rest of the session. So each step is time-boxed, every
    /// failure falls forward to Driving, and Done() is the only exit -- called from the
    /// deadlines, the distance check, the failure paths and the mod unloading.
    ///
    /// AND HE GOES BACK THE WAY HE WAS FOUND. A man they could not save was dead when they
    /// arrived and is dead when they leave: Done() kills him again if he is still ours, so no
    /// other mod ever sees a corpse that stood up. A man they saved is handed to the city alive
    /// with a limp, and from that moment is nobody's but the game's.
    /// </summary>
    internal sealed class Callout
    {
        /// <summary>One clip in the sequence, and how long it gets.</summary>
        private struct Beat
        {
            public string Clip;

            /// <summary>Looped for Ms, or played once with Ms as the ceiling.</summary>
            public bool Loop;
            public int Ms;
        }

        /// <summary>A one-shot that has not reported finished by this is not going to.</summary>
        private const int OneShotMs = 4500;

        /// <summary>The first look at him, between kneeling and leaning in.</summary>
        private const int FirstLookMs = 2400;

        /// <summary>The look between rounds.</summary>
        private const int BetweenMs = 1800;

        /// <summary>And the last one, before the verdict.</summary>
        private const int LastLookMs = 1100;

        /// <summary>How long the crew back off for before they get back in.</summary>
        private const int FleeMs = 2600;

        /// <summary>Lights, siren, and through the traffic rather than round it.</summary>
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
        /// Set by every change of step and consumed by the step that has just started.
        ///
        /// THE OBVIOUS VERSION OF THIS IS A BUG AND IT WAS WRITTEN FIRST. Each step has work
        /// that must happen once, and the tempting test is `now - _stepAt == 0`. It is not
        /// true: To() is called at the END of the previous step, so by the time the new one
        /// gets a tick the clock has moved on, and the entry work never runs at all -- except
        /// on a machine fast enough to fit two ticks in one millisecond, where it runs sometimes.
        /// A flag has no such opinion about the clock.
        /// </summary>
        private bool _entering;

        private Verdict _verdict;
        private uint _weapon;
        private bool _carrying;
        private bool _bodyInVan;

        /// <summary>The scene every paired clip is played against. Same origin for all of them.</summary>
        private Sync _scene;

        private Beat[] _beats;
        private int _beat;
        private int _beatAt;

        /// <summary>When the last beat finished, so the verdict is held for exactly VerdictMs.</summary>
        private int _verdictAt;

        /// <summary>
        /// Whether he is currently alive by our hand.
        ///
        /// TRUE FROM THE RESURRECTION UNTIL HE IS EITHER HANDED BACK ALIVE OR KILLED AGAIN. It
        /// is the flag Done() reads to decide whether there is a man who needs putting back the
        /// way he was found, and it is the only thing standing between "the player drove off
        /// during the CPR" and "a resurrected stranger stands frozen in the road forever".
        /// </summary>
        private bool _ours;

        /// <summary>Whether he is holding the lying-dead pose, which is what the trolley wants.</summary>
        private bool _posed;

        /// <summary>Whether the second man got his scenario, or is faking it with a clip.</summary>
        private bool _mateBusy;

        /// <summary>Said out loud once per scene, if the player is near enough to care.</summary>
        public Action<string> Say;

        public Callout(Settings cfg, Random rng)
        {
            _cfg = cfg;
            _rng = rng;
            _trolley = new Gurney(cfg);
            _kit = new Kit();
        }

        /// <summary>Whether a call-out is running at all.</summary>
        public bool Out
        {
            get { return _step != Step.None; }
        }

        /// <summary>Where it is, for anybody who asks across the API.</summary>
        public Vector3 At
        {
            get { return _at; }
        }

        /// <summary>
        /// Whether the crew are still at this scene.
        ///
        /// WHAT THE POLICE WAIT ON. Five0 Patrol keeps its officers at a body until the
        /// ambulance has gone, and it asks this to find out. It goes false the moment the van
        /// pulls away rather than when it despawns.
        /// </summary>
        public bool Still(Vector3 where)
        {
            return _step != Step.None && _step != Step.Driving &&
                   _at.DistanceTo(where) < _cfg.SameScene;
        }

        /// <summary>Whether the man at this scene was one they could have had.</summary>
        public bool Workable
        {
            get { return _step != Step.None && _verdict == Verdict.Workable; }
        }

        // ---- the call ----------------------------------------------------------

        /// <summary>Sends one. Whether it went.</summary>
        public bool Send(Death death)
        {
            if (Out) return false;
            if (death == null || !Crew.There(death.Body)) return false;

            try
            {
                var where = death.Body.Position;

                Vector3 from;
                if (!Crew.Road(where, _rng, _cfg.ComeFrom, _cfg.ComeSpread, out from))
                {
                    Log.Debug("No road to send an ambulance in from.");
                    return false;
                }

                var model = Crew.Load(Crew.Van);
                if (model == null) { Log.Debug("The ambulance model would not load."); return false; }

                _van = World.CreateVehicle(model.Value, from, 0f);
                model.Value.MarkAsNoLongerNeeded();

                if (!Crew.Alive(_van)) { Done(); return false; }

                _van.IsPersistent = true;
                _van.IsEngineRunning = true;

                Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, _van.Handle);

                // LIGHTS AND SIREN THE WHOLE WAY. An ambulance has nothing to find out: it has
                // been told there is a body, and it is late.
                Function.Call(Hash.SET_VEHICLE_LIGHTS, _van.Handle, 2);
                Function.Call(Hash.SET_VEHICLE_SIREN, _van.Handle, true);

                _driver = Crew.Aboard(_van, -1);
                if (_driver == null) { Done(); return false; }

                _mate = Crew.Aboard(_van, 0);

                _body = death.Body;
                _at = where;

                // HELD FROM HERE. The engine tidies corpses away on its own schedule, and a
                // van that takes a minute to arrive was, before, arriving at nothing about one
                // time in five -- the log said "the body had gone" and nobody could say why.
                // It is ours now, and Done() gives it back.
                _body.IsPersistent = true;

                // READ NOW, NOT AT THE SCENE. GET_PED_CAUSE_OF_DEATH is filled in when the ped
                // dies and there is no promise about how long it stays useful -- and once he
                // has been resurrected it is gone for good. The verdict is a fact about the
                // death, so it is taken at the death.
                _verdict = Cause.Read(_body, out _weapon);

                if (!_cfg.Resuscitate) _verdict = Verdict.Gone;

                _step = Step.Coming;
                _stepAt = Game.GameTime;
                _entering = true;
                _carrying = false;
                _bodyInVan = false;
                _ours = false;
                _posed = false;
                _mateBusy = false;

                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                              _driver.Handle, _van.Handle,
                              where.X, where.Y, where.Z,
                              _cfg.Speed, DriveStyle, _cfg.ThereRange * 0.6f);

                Log.Info("An ambulance was called to a body -- " + Cause.Word(_weapon) +
                         ", " + (_verdict == Verdict.Workable ? "workable" : "gone") + ".");

                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not send an ambulance: " + ex.Message);
                Done();
                return false;
            }
        }

        // ---- the tick ----------------------------------------------------------

        public void Update()
        {
            if (_step == Step.None) return;

            var now = Game.GameTime;

            try
            {
                if (!Crew.Alive(_van) || !Crew.Alive(_driver)) { Done(); return; }

                var me = Game.Player.Character;

                // GONE FROM THE WORLD RATHER THAN FROM THE SCENE. A call-out the player has
                // driven three streets away from is not worth a tick, and holding a van, two
                // crew, a trolley and a resurrected man persistent out there is exactly the leak
                // this set keeps finding in its own mods.
                if (me != null && me.Exists() &&
                    _van.Position.DistanceTo(me.Position) > _cfg.LetGoRange)
                {
                    Log.Debug("The call-out was left behind.");
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
                    case Step.Loading:   Loading(now);   break;
                    case Step.Wheeling:  Wheeling(now);  break;
                    case Step.Stowing:   Stowing(now);   break;
                    case Step.Driving:   Driving(now);   break;
                    case Step.Fleeing:   Fleeing(now);   break;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("The call-out went wrong: " + ex.Message);
                Done();
            }
        }

        private void To(Step step, int now)
        {
            _step = step;
            _stepAt = now;
            _entering = true;
        }

        private bool Entering()
        {
            if (!_entering) return false;

            _entering = false;
            return true;
        }

        // ---- on the way --------------------------------------------------------

        private void Coming(int now)
        {
            if (_van.Position.DistanceTo(_at) > _cfg.ThereRange)
            {
                if (now - _stepAt > _cfg.ComeMs) { Leave(now, "it could not get there"); }
                return;
            }

            // THE SIREN GOES OFF ON ARRIVAL AND THE LIGHTS STAY ON. The noise is for the traffic
            // on the way, and the lights are for the street it is parked in.
            Function.Call(Hash.SET_VEHICLE_SIREN, _van.Handle, false);

            Out_(_driver, 1.4f);

            // THE SECOND MAN BRINGS THE BAG, and goes to the other side of him. Two men
            // walking to the same point from the same door arrive standing in each other, and
            // the scene that follows puts one of them kneeling exactly where the other one was.
            if (_cfg.MedicBag) _kit.Bring(_mate);

            Out_(_mate, 2.2f, Flank());

            To(Step.Reaching, now);

            Log.Info("The ambulance is at the body.");

            if (Say != null && Near()) Say("An ambulance pulls up.");
        }

        /// <summary>One of them out and over to him, or to a spot near him.</summary>
        private void Out_(Ped who, float stopAt, Vector3? spot = null)
        {
            if (!Crew.Alive(who)) return;

            try
            {
                Function.Call(Hash.TASK_LEAVE_VEHICLE, who.Handle, _van.Handle, 0);

                if (spot.HasValue)
                {
                    var s = spot.Value;

                    Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD, who.Handle,
                                  s.X, s.Y, s.Z, 1.8f, _cfg.ReachMs, 0f, 0.4f);
                    return;
                }

                if (Crew.There(_body))
                {
                    Function.Call(Hash.TASK_GO_TO_ENTITY, who.Handle, _body.Handle,
                                  _cfg.ReachMs, stopAt, 2f, 1073741824f, 0);
                    return;
                }

                Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD, who.Handle,
                              _at.X, _at.Y, _at.Z, 1.8f, _cfg.ReachMs, 0f, 0.5f);
            }
            catch (Exception ex)
            {
                Log.Debug("The crew could not get out: " + ex.Message);
            }
        }

        /// <summary>
        /// A spot beside the body, square to the way the van came in.
        ///
        /// The driver walks straight at him from the van, so "square to the van" is the side
        /// the driver is not on. Falls back to the body itself when there is no van to measure
        /// from, which only happens on the way to Done anyway.
        /// </summary>
        private Vector3 Flank()
        {
            try
            {
                if (!Crew.There(_body) || !Crew.Alive(_van)) return _at;

                var body = _body.Position;
                var toward = body - _van.Position;
                toward.Z = 0f;

                if (toward.Length() < 0.5f) return body;

                toward = toward.Normalized;

                // Perpendicular, on the ground.
                var side = new Vector3(-toward.Y, toward.X, 0f);

                return body + side * 1.7f;
            }
            catch
            {
                return _at;
            }
        }

        // ---- walking over ------------------------------------------------------

        private void Reaching(int now)
        {
            // THE BODY CAN GO AWAY MID-SCENE and it is not an error. Five0 Patrol drags corpses
            // out of sight and Hoodrich searches them; either way there is nothing left to work
            // on, and the crew get back in.
            if (!Crew.There(_body)) { Leave(now, "the body had gone"); return; }

            var close = Crew.Alive(_driver) &&
                        _driver.Position.DistanceTo(_body.Position) < _cfg.KneelRange;

            if (!close && now - _stepAt < _cfg.ReachMs) return;

            To(Step.Working, now);
        }

        // ---- the scene ---------------------------------------------------------

        private void Working(int now)
        {
            if (Entering())
            {
                if (!Arrest())
                {
                    // He would not come back into the engine's idea of alive. Nothing to be
                    // done but what 0.1.0 did: call it, and load him as he lies.
                    Log.Info("He could not be brought into arrest; treating him as gone.");
                    _verdict = Verdict.Gone;
                    To(Step.Pronounce, now);
                    return;
                }

                Choreograph();
                Mate_();

                _beat = -1;
                Advance(now);
                return;
            }

            // SHOT UNDER THEIR HANDS. He is alive during this, so the player can kill him
            // again -- and if they do, the crew do what the scenario's own exit was authored
            // for, which is to get away from whoever did it.
            if (!Crew.Alive(_body)) { To(Step.Fleeing, now); return; }

            if (!_mateBusy) Look(_mate);

            // Past the last beat: the verdict is on screen, held. Give it its moment.
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
        /// Brought back, into arrest.
        ///
        /// RESURRECT_PED LEAVES A PED BLANK -- Hoodrich's lesson, in Hoodrich's words: no flags,
        /// out of whatever group it was in, none of what made it a person in a street. Here
        /// that is a feature. What goes back on is exactly the set that makes him a patient:
        /// nothing can target him, nothing he sees can move him, he cannot fall over, and he
        /// has just enough health that being knocked by a passing car will not end the scene.
        ///
        /// THE SCENE ORIGIN IS TAKEN HERE, ON THE GROUND UNDER HIM. A ragdoll's position is
        /// somewhere in its pelvis, which on a kerb is a hand's width above the road, and a
        /// scene rooted there plays a hand's width above the road. GetGroundHeight is asked
        /// from a little way up so it finds the road and not the inside of him.
        /// </summary>
        private bool Arrest()
        {
            if (!Crew.There(_body)) return false;

            try
            {
                var h = _body.Handle;
                var where = _body.Position;
                var heading = _body.Heading;

                float ground;
                if (World.GetGroundHeight(where + new Vector3(0f, 0f, 1f), out ground,
                                          GetGroundHeightMode.Normal) &&
                    Math.Abs(ground - where.Z) < 3f)
                {
                    where.Z = ground;
                }

                Function.Call(Hash.RESURRECT_PED, h);

                if (!Crew.Alive(_body)) return false;

                Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, h);

                var max = Function.Call<int>(Hash.GET_PED_MAX_HEALTH, h);
                if (max <= 0) max = 200;

                Function.Call(Hash.SET_ENTITY_HEALTH, h, Math.Max(30, max / 5));

                Function.Call(Hash.SET_PED_CAN_RAGDOLL, h, false);
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, h, true);
                Function.Call(Hash.SET_PED_CAN_BE_TARGETTED, h, false);
                Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, h, 0, false);

                _ours = true;
                _posed = false;

                _scene = new Sync(where, heading);

                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not bring him into arrest: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// The order of the clips, and how long each one gets.
        ///
        /// TWO ROUNDS FOR A MAN WITH A CHANCE, ONE FOR A MAN WITHOUT. That is what makes the two
        /// outcomes read differently before either ending has played: a crew that keeps going
        /// back to his chest is a crew that thinks there is something there. The compressions
        /// take the ini's WorkMs and CheckMs -- the numbers that already existed -- split across
        /// the rounds, so nobody's tuning is thrown away.
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
        /// THE LAST BEAT IS HELD. cpr_success leaves him sat up and cpr_fail leaves the medic
        /// sat back; both are the picture the verdict is judged from, and a scene that snapped
        /// to idle the frame it ended would throw that picture away before anybody saw it.
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
            var last = _beat == _beats.Length - 1;

            _scene.End();

            if (!_scene.Begin(beat.Loop, last))
            {
                Log.Debug("The scene would not start for " + beat.Clip + ".");
                return;
            }

            var a = _scene.Cast(_driver, Anim.CprMedic, beat.Clip);
            var b = _scene.Cast(_body, Anim.CprVictim, beat.Clip);

            if (!a || !b) Log.Debug("Could not cast both into " + beat.Clip + ".");
        }

        /// <summary>
        /// Whether the current beat has run its course.
        ///
        /// A LOOP IS DONE WHEN ITS TIME IS UP. A one-shot is done when the scene says so -- with
        /// two guards, because the scene's own answer is not to be trusted on the first frame or
        /// the last. It takes a tick for the task to register, during which the scene reports
        /// "not running", which Sync reads as finished; so nothing under two hundred
        /// milliseconds counts. And a scene that never started at all reports the same thing
        /// forever; so anything not running after a second is taken as over, and anything at
        /// all is taken as over at the ceiling.
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
        /// The second man: bag down beside him, then kneeling, tending, on the far side.
        ///
        /// THE GAME'S OWN SCENARIO RATHER THAN A CLIP. It runs enter, a base loop and idle
        /// variations of its own accord, which is a man who is doing something rather than a
        /// man holding a pose -- and it is the exact thing the vanilla paramedics play at a
        /// body, so it is the texture this mod is meant to be finishing rather than replacing.
        /// </summary>
        private void Mate_()
        {
            if (!Crew.Alive(_mate) || !Crew.There(_body)) return;

            try
            {
                if (_kit.There) _kit.SetDown(Flank() + (_at - Flank()).Normalized * 0.9f);

                var at = _body.Position - _mate.Position;
                if (at.Length() > 0.2f)
                {
                    _mate.Heading = (float)(Math.Atan2(at.Y, at.X) * 180d / Math.PI) - 90f;
                }

                _mateBusy = Anim.Scenario(_mate, Anim.TendScenario);
            }
            catch (Exception ex)
            {
                Log.Debug("The second man could not settle: " + ex.Message);
                _mateBusy = false;
            }
        }

        /// <summary>The fallback for a man with nothing to do: kneel and look.</summary>
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

        private void Rising(int now)
        {
            if (Entering())
            {
                _scene.End();

                var paired = _scene.Begin(false, false) &&
                             _scene.Cast(_driver, Anim.Rescue, Anim.RescueMedic) &&
                             _scene.Cast(_body, Anim.Rescue, Anim.RescueVictim);

                if (!paired)
                {
                    // On his own, then. The medic simply stands.
                    Log.Debug("The helping-up scene would not start; he gets up himself.");
                    Function.Call(Hash.CLEAR_PED_TASKS, _driver.Handle);
                    Anim.Play(_body, Anim.GetUpDict, Anim.GetUpClip, 0);
                }

                Log.Info("He came round -- it was " + Cause.Word(_weapon) + ".");

                if (Say != null && Near()) Say("They bring him round.");
                return;
            }

            if (!Crew.Alive(_body)) { To(Step.Fleeing, now); return; }

            var age = now - _stepAt;

            if (age < 300) return;

            var phase = _scene.Phase;
            var done = phase >= 0.985f || (phase < 0f && age > 1200) || age > _cfg.RisingMs;

            if (!done) return;

            LetGo();
            Leave(now, null);
        }

        /// <summary>
        /// Handed to the city, alive and limping.
        ///
        /// EVERYTHING ARREST TOOK OFF HIM GOES BACK ON, then he is nobody's. Holding a man you
        /// have just saved so you can watch him walk away is how a mod ends up owning forty
        /// people; he is marked no longer needed and the engine can have him back the moment
        /// he is out of sight.
        ///
        /// NOT BACK TO FULL. A third of a bar is enough to walk off and it means a second beating
        /// finishes him -- which is the honest consequence of having been dead a minute ago.
        /// </summary>
        private void LetGo()
        {
            try
            {
                var h = _body.Handle;

                _scene.End();

                Function.Call(Hash.CLEAR_PED_TASKS, h);

                var max = Function.Call<int>(Hash.GET_PED_MAX_HEALTH, h);
                if (max <= 0) max = 200;

                Function.Call(Hash.SET_ENTITY_HEALTH, h, Math.Max(25, max / 3));
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

        private void Pronounce(int now)
        {
            if (Entering())
            {
                _scene.End();

                // The medic stands. The second man writes it down, or just stands too.
                Function.Call(Hash.CLEAR_PED_TASKS, _driver.Handle);

                if (_cfg.TimeOfDeath) Anim.Scenario(_mate, Anim.TimeOfDeathScenario);
                else Unsettle(_mate);

                PoseDead();

                if (Say != null && Near()) Say("They stop working on him.");
                return;
            }

            if (now - _stepAt < _cfg.PronounceMs) return;

            // NOT EVERYBODY WANTS THE REST OF IT. With the hospital run switched off the crew do
            // the one thing this mod was written to stop them doing -- get back in and leave him
            // there -- and that is a legitimate way to run it: the resuscitation is most of the
            // value, and the loading is the part that touches other mods' corpses.
            if (!_cfg.TakeToHospital) { Leave(now, "they are not taking him"); return; }

            Unsettle(_mate);

            To(Step.Fetching, now);
        }

        /// <summary>
        /// Laid out flat, and kept that way.
        ///
        /// THIS IS WHAT MAKES THE TROLLEY WORK. He is alive, so he can be posed; the game ships
        /// eight lying-dead clips; and a ped keeps playing its clip when it is attached to
        /// something. So he holds dead_a, the trolley picks him up in that pose, and the
        /// offsets in the ini are tuned against one shape that never varies -- instead of
        /// against however each man happened to fall, which is what made them unverifiable.
        /// </summary>
        private void PoseDead()
        {
            if (!Crew.Alive(_body)) return;

            _posed = Anim.Play(_body, Anim.DeadDict, Anim.DeadPose, Anim.Hold);
        }

        /// <summary>Out of whatever scenario or clip he was in, and back on his feet.</summary>
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
                _scene.End();

                Log.Info("The patient was killed with the crew working on him.");

                Anim.Play(_driver, Anim.FleeDict, Anim.FleeClip, 0);
                Anim.Play(_mate, Anim.FleeDict, Anim.FleeClip, 0);

                // He was ours a moment ago and is dead again now, by somebody else's hand. He is
                // not ours any more and there is nothing to put back.
                _ours = false;

                if (Say != null && Near()) Say("The crew back off.");
                return;
            }

            if (now - _stepAt < FleeMs) return;

            Leave(now, "the patient was shot");
        }

        // ---- the trolley -------------------------------------------------------

        private void Fetching(int now)
        {
            if (!Crew.There(_body)) { Leave(now, "the body had gone"); return; }

            if (Entering())
            {
                Doors(true);

                // The bag goes back with him, if he brought one.
                if (_kit.There) _kit.Bring(_mate);

                // BESIDE HIM, NOT ON HIM. Put down at his exact position it spawns through him,
                // which for the three seconds before he is loaded looks like a trolley dropped
                // on a corpse. A metre towards the van is where somebody wheeling it over would
                // actually have stopped.
                if (!_trolley.Bring(Beside(_body.Position), _body.Heading + 90f))
                {
                    _carrying = true;
                }
            }

            if (now - _stepAt < _cfg.FetchMs) return;

            To(Step.Loading, now);
        }

        private void Loading(int now)
        {
            if (!Crew.There(_body)) { Leave(now, "the body had gone"); return; }

            if (Entering())
            {
                if (_carrying) Carry();
                else if (!_trolley.Lay(_body)) { _carrying = true; Carry(); }
            }

            if (now - _stepAt < _cfg.LoadMs) return;

            if (!_carrying) _trolley.Take(_driver);

            Walk(_driver);
            Walk(_mate);

            To(Step.Wheeling, now);
        }

        /// <summary>
        /// No trolley, so he goes over a shoulder.
        ///
        /// The same weld the trolley uses, one entity closer. It is what happens on an install
        /// that has neither gurney prop, so that the mod still ends with the body in the
        /// ambulance rather than with the crew standing over him because a DLC was missing.
        /// </summary>
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

        /// <summary>A metre from the body, on the side the van is.</summary>
        private Vector3 Beside(Vector3 body)
        {
            try
            {
                if (!Crew.Alive(_van)) return body;

                var away = _van.Position - body;
                away.Z = 0f;

                if (away.Length() < 0.5f) return body;

                return body + away.Normalized * 1.1f;
            }
            catch
            {
                return body;
            }
        }

        /// <summary>Back to the van, at a walk. Nobody runs a trolley.</summary>
        private void Walk(Ped who)
        {
            if (!Crew.Alive(who) || !Crew.Alive(_van)) return;

            try
            {
                var back = _van.Position - _van.ForwardVector * 3.2f;

                Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD, who.Handle,
                              back.X, back.Y, back.Z, 1.2f, _cfg.WheelMs, 0f, 0.4f);
            }
            catch
            {
                // The deadline moves it on regardless.
            }
        }

        private void Wheeling(int now)
        {
            var there = Crew.Alive(_driver) && Crew.Alive(_van) &&
                        _driver.Position.DistanceTo(_van.Position) < 4.5f;

            if (!there && now - _stepAt < _cfg.WheelMs) return;

            To(Step.Stowing, now);
        }

        private void Stowing(int now)
        {
            if (Entering())
            {
                if (_carrying) InVan();
                else if (!_trolley.Stow(_van)) InVan();

                // The bag goes in the back with everything else.
                _kit.Release();

                Board(_driver, -1);
                Board(_mate, 0);
            }

            if (now - _stepAt < _cfg.StowMs) return;

            Doors(false);

            _to = Hospitals.Nearest(_van.Position);

            To(Step.Driving, now);

            try
            {
                Function.Call(Hash.SET_VEHICLE_SIREN, _van.Handle, false);

                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                              _driver.Handle, _van.Handle,
                              _to.X, _to.Y, _to.Z, _cfg.Speed, DriveStyle, 20f);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not send it to the hospital: " + ex.Message);
            }

            Log.Info("They are taking him to the hospital, " +
                     (int)_van.Position.DistanceTo(_to) + "m away.");

            if (Say != null && Near()) Say("The ambulance leaves for the hospital.");
        }

        /// <summary>The body straight into the back, when there is no trolley to put it on.</summary>
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

        /// <summary>
        /// The back doors. Three indices, because the ambulance is not a saloon: 5 is the boot
        /// on most models and the rear pair on some; 2 and 3 are the rear side doors where a
        /// model has them. Asking for a door a model does not have is harmless.
        /// </summary>
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

        /// <summary>Crew back in and the van sent off, with nobody in the back.</summary>
        private void Leave(int now, string why)
        {
            if (why != null) Log.Info("The call-out ended: " + why + ".");

            if (_scene != null) _scene.End();

            _kit.Release();

            Unsettle(_driver);
            Unsettle(_mate);

            Board(_driver, -1);
            Board(_mate, 0);

            To(Step.Driving, now);

            _to = Vector3.Zero;
        }

        private void Driving(int now)
        {
            // A GRACE PERIOD BEFORE THE WHEELS TURN, so the crew are actually in it.
            if (now - _stepAt < _cfg.BoardMs) return;

            // ONCE, NOT EVERY FRAME. Issuing a driving task every frame does not make it happen
            // harder; it restarts it, and a task restarted forty times a second is a van that
            // never gets out of first gear.
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

        /// <summary>Whether the player is close enough for a line about it to make sense.</summary>
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

        /// <summary>
        /// Put everything back where the offsets NOW say it goes. See Options.Refit.
        /// </summary>
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

        /// <summary>What the crew are doing, in three or four words, for the settings screen.</summary>
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
                    case Step.Loading:   return _carrying ? "picking him up" : "loading him";
                    case Step.Wheeling:  return _carrying ? "carrying him back" : "wheeling him back";
                    case Step.Stowing:   return "into the back";
                    case Step.Driving:   return _to == Vector3.Zero
                                              ? "leaving" : "driving to the hospital";
                    case Step.Fleeing:   return "backing off";
                }

                return "?";
            }
        }

        private string Working_()
        {
            if (_beats == null) return "at the body";
            if (_beat >= _beats.Length) return _verdict == Verdict.Workable ? "they have him" : "nothing to be done";
            if (_beat < 0) return "kneeling";

            var clip = _beats[_beat].Clip;

            if (clip == Anim.Pump) return "compressions";
            if (clip == Anim.KneelIdle) return "checking him";
            if (clip == Anim.Worked) return "they have him";
            if (clip == Anim.Failed) return "nothing to be done";

            return "working on him";
        }

        // ---- handing it all back ------------------------------------------------

        /// <summary>
        /// Everything given back to the game.
        ///
        /// THE ORDER IS THE POINT. The trolley lets go of the body before it is deleted, or the
        /// body keeps an attachment to nothing and hangs in the air. THEN the body is put back
        /// the way it was found: a man still ours is a man we resurrected and never released,
        /// and he goes back to dead -- so no other mod ever meets a corpse that stood up and
        /// froze. Only after that is anything handed to the population manager.
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
            _posed = false;
            _mateBusy = false;
            _scene = null;
        }

        /// <summary>
        /// Dead again, as he was found.
        ///
        /// Everything Arrest put on him comes off first, so what is left is an ordinary corpse:
        /// ragdolls, can be searched, can be dragged, bleeds. SET_ENTITY_HEALTH to nought is the
        /// cleanest death the engine offers -- no weapon, no force, no reaction clip.
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

        /// <summary>
        /// One of the crew, put back the way he was found.
        ///
        /// THE BLOCKED EVENTS ARE THE ONE THAT MATTERS. A ped left with
        /// SET_BLOCKING_OF_NON_TEMPORARY_EVENTS on cannot react to anything for the rest of the
        /// session, and nothing on screen says why.
        /// </summary>
        private static void Loose(Ped who)
        {
            try
            {
                if (who == null || !who.Exists()) return;

                Function.Call(Hash.CLEAR_PED_TASKS, who.Handle);
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, who.Handle, false);
                Function.Call(Hash.SET_PED_CAN_BE_TARGETTED, who.Handle, true);
            }
            catch
            {
                // Gone already.
            }

            Crew.Give(who);
        }
    }
}
