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

        /// <summary>Kneeling, working on his chest.</summary>
        Working,

        /// <summary>The moment it did or did not take.</summary>
        Verdict,

        /// <summary>He is breathing. Getting him up and out of the road.</summary>
        Rising,

        /// <summary>He is not. Back to the van for the trolley.</summary>
        Fetching,

        /// <summary>Trolley beside him, and him onto it.</summary>
        Loading,

        /// <summary>Wheeling him back.</summary>
        Wheeling,

        /// <summary>Into the back, doors shut, crew aboard.</summary>
        Stowing,

        /// <summary>Driving. Either to the hospital, or away empty.</summary>
        Driving,
    }

    /// <summary>
    /// One ambulance, one body, from the call to the hospital.
    ///
    /// THIS IS THE MOD. Everything else in the project finds a body, decides what killed it, or
    /// keeps another mod out of the way; this is the part somebody watches.
    ///
    /// ONE AT A TIME, AND THAT IS NOT A LIMITATION. Two ambulances at one junction is a
    /// pile-up rather than a scene, and a second call refused is a second call that would have
    /// arrived at an empty street anyway -- by the time the first finishes, whoever was standing
    /// over the second body has walked off. Five0 Patrol's Medics reached the same conclusion,
    /// in the same words, for the same reason.
    ///
    /// EVERY STEP HAS A DEADLINE AND A WAY OUT. A crew that cannot reach a body on a rooftop, a
    /// trolley that will not load, a van that cannot find the street -- none of those may leave
    /// the scene stuck, because a stuck scene holds two persistent peds, a vehicle and a prop
    /// for the rest of the session and refuses every later call. So each step is time-boxed and
    /// every failure falls forward to Driving, which is the state that ends.
    ///
    /// AND THE WHOLE THING IS HANDED BACK. The van, the crew and the trolley are ours and are
    /// marked persistent, which means the game will not clean them up. Done() is the only exit,
    /// and it is called from the deadline, from the distance check, from the failure paths and
    /// from the mod unloading.
    /// </summary>
    internal sealed class Callout
    {
        private readonly Settings _cfg;
        private readonly Random _rng;
        private readonly Gurney _trolley;

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
        /// THE OBVIOUS VERSION OF THIS IS A BUG AND IT WAS WRITTEN FIRST. Each of the steps
        /// below has work that must happen once -- open the doors, bring the trolley out, lay
        /// him on it -- and the tempting test is `now - _stepAt == 0`, on the reasoning that the
        /// first pass through a step is the one where no time has elapsed. It is not. To() is
        /// called at the END of the previous step, so by the time the new one gets a tick the
        /// clock has already moved on, and at sixty frames a second it has moved sixteen
        /// milliseconds. The entry work then never runs at all -- except on a machine fast
        /// enough to fit two ticks in one millisecond, where it runs sometimes.
        ///
        /// A flag has no such opinion about the clock.
        /// </summary>
        private bool _entering;

        private Verdict _verdict;
        private uint _weapon;
        private bool _carrying;

        /// <summary>Whether the body has been put in the back rather than over a shoulder.</summary>
        private bool _bodyInVan;

        /// <summary>Said out loud once per scene, if the player is near enough to care.</summary>
        public Action<string> Say;

        public Callout(Settings cfg, Random rng)
        {
            _cfg = cfg;
            _rng = rng;
            _trolley = new Gurney(cfg);
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
        /// pulls away rather than when it despawns, because that is the moment somebody
        /// watching would call it over.
        /// </summary>
        public bool Still(Vector3 where)
        {
            return _step != Step.None && _step != Step.Driving &&
                   _at.DistanceTo(where) < _cfg.SameScene;
        }

        // ---- the call ----------------------------------------------------------

        /// <summary>
        /// Sends one. Whether it went.
        /// </summary>
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

                // READ NOW, NOT AT THE SCENE. GET_PED_CAUSE_OF_DEATH is filled in when the ped
                // dies and there is no promise about how long it stays useful -- and by the time
                // the van arrives another mod may have searched, dragged or replaced the body.
                // The verdict is a fact about the death, so it is taken at the death.
                _verdict = Cause.Read(_body, out _weapon);

                // AND THE PLAYER MAY HAVE TURNED THE WHOLE IDEA OFF. Applied here rather than
                // inside Cause, because Cause answers what killed him -- which is a fact -- and
                // this is a decision about what the crew do with that answer.
                if (!_cfg.Resuscitate) _verdict = Verdict.Gone;

                _step = Step.Coming;
                _stepAt = Game.GameTime;
                _carrying = false;
                _bodyInVan = false;

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

        /// <summary>Lights, siren, and through the traffic rather than round it.</summary>
        private const int DriveStyle = 786603;

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
                // crew and a trolley persistent out there is exactly the leak this set keeps
                // finding in its own mods.
                if (me != null && me.Exists() &&
                    _van.Position.DistanceTo(me.Position) > _cfg.LetGoRange)
                {
                    Log.Debug("The call-out was left behind.");
                    Done();
                    return;
                }

                switch (_step)
                {
                    case Step.Coming:   Coming(now);   break;
                    case Step.Reaching: Reaching(now); break;
                    case Step.Working:  Working(now);  break;
                    case Step.Verdict:  Verdicting(now); break;
                    case Step.Rising:   Rising(now);   break;
                    case Step.Fetching: Fetching(now); break;
                    case Step.Loading:  Loading(now);  break;
                    case Step.Wheeling: Wheeling(now); break;
                    case Step.Stowing:  Stowing(now);  break;
                    case Step.Driving:  Driving(now);  break;
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

        /// <summary>
        /// True once per step, on the first tick that step is given.
        ///
        /// A step with no entry work of its own simply never asks, and the flag is set again by
        /// the next change -- so an unconsumed one cannot leak into a later step.
        /// </summary>
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
                // Given up on. A van that cannot find the street is worse than no van.
                if (now - _stepAt > _cfg.ComeMs) { Leave(now, "it could not get there"); }
                return;
            }

            // THE SIREN GOES OFF ON ARRIVAL AND THE LIGHTS STAY ON. Which is what they do: the
            // noise is for the traffic on the way, and the lights are for the street it is
            // parked in.
            Function.Call(Hash.SET_VEHICLE_SIREN, _van.Handle, false);

            Out_(_driver);
            Out_(_mate);

            To(Step.Reaching, now);

            Log.Info("The ambulance is at the body.");

            if (Say != null && Near()) Say("An ambulance pulls up.");
        }

        /// <summary>One of them out and over to him.</summary>
        private void Out_(Ped who)
        {
            if (!Crew.Alive(who)) return;

            try
            {
                Function.Call(Hash.TASK_LEAVE_VEHICLE, who.Handle, _van.Handle, 0);

                if (Crew.There(_body))
                {
                    Function.Call(Hash.TASK_GO_TO_ENTITY, who.Handle, _body.Handle,
                                  _cfg.ReachMs, 1.2f, 2f, 1073741824f, 0);
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

        // ---- walking over ------------------------------------------------------

        private void Reaching(int now)
        {
            // THE BODY CAN GO AWAY MID-SCENE and it is not an error. Five0 Patrol drags corpses
            // out of sight and the engine clears them on its own; either way there is nothing
            // left to work on, and the crew get back in.
            if (!Crew.There(_body)) { Leave(now, "the body had gone"); return; }

            var close = Crew.Alive(_driver) &&
                        _driver.Position.DistanceTo(_body.Position) < _cfg.KneelRange;

            if (!close && now - _stepAt < _cfg.ReachMs) return;

            To(Step.Working, now);
        }

        // ---- working on him ----------------------------------------------------

        private void Working(int now)
        {
            if (!Crew.There(_body)) { Leave(now, "the body had gone"); return; }

            // The mate stands off and watches. Two men doing compressions on one chest is
            // worse than one doing it and one looking on.
            Watch_(_mate);

            // SQUARED UP ONCE, ON THE WAY IN, AND BY HEADING RATHER THAN BY TASK.
            //
            // The first version of this asked TASK_TURN_PED_TO_FACE_ENTITY every pass, and the
            // two lines fought each other every frame: turning is a task, playing a clip is a
            // task, and each restarted the other -- so the medic stood over the body twitching
            // and the compressions never got past their first frame. Setting the heading is not
            // a task at all, so it cannot be in that argument.
            if (Entering()) Square(_driver);

            // THE CHEST COMPRESSIONS, RE-ASKED EVERY PASS. Anim.Play only restarts a clip that
            // is not already running, so this holds the loop rather than stuttering it -- see
            // the note in Anim.Play, which is the same lesson Five0 Patrol learned on its
            // surrender pose.
            Anim.Play(_driver, Anim.CprDict, Anim.CprPump, Anim.Loop);

            var spent = now - _stepAt;

            // HOW LONG THEY WORK IS NOT THE SAME EITHER WAY. A crew who arrive at somebody with
            // a chance work until they get him back; a crew who arrive at somebody shot through
            // the chest establish that fairly quickly and stop. Making both take the same
            // twenty-six seconds is what made the vanilla scene read as a formality.
            var need = _verdict == Verdict.Workable ? _cfg.WorkMs : _cfg.CheckMs;

            if (spent < need) return;

            To(Step.Verdict, now);
        }

        /// <summary>The second one, stood back, looking at what is happening.</summary>
        private void Watch_(Ped who)
        {
            if (!Crew.Alive(who)) return;

            try
            {
                if (!Crew.There(_body)) return;
                if (who.Position.DistanceTo(_body.Position) > 4f) return;

                Anim.Play(who, Anim.LookDict, Anim.LookClip, Anim.Loop);
            }
            catch
            {
                // He stands there instead, which is still a man at a scene.
            }
        }

        /// <summary>
        /// Squared up to the body, so the compressions land on a chest.
        ///
        /// SET_ENTITY_HEADING rather than a turn task -- see the call site. It is instant and
        /// slightly abrupt, and that is the correct trade: a man dropping to his knees beside a
        /// body has already turned on the way down, and nobody is watching his feet.
        /// </summary>
        private void Square(Ped who)
        {
            if (!Crew.Alive(who) || !Crew.There(_body)) return;

            try
            {
                var at = _body.Position - who.Position;

                if (at.Length() < 0.2f) return;

                who.Heading = (float)(Math.Atan2(at.Y, at.X) * 180d / Math.PI) - 90f;
            }
            catch
            {
                // Cosmetic.
            }
        }

        // ---- and whether it took -----------------------------------------------

        private void Verdicting(int now)
        {
            if (Entering())
            {
                Anim.Stop(_driver, Anim.CprDict, Anim.CprPump);

                Anim.Play(_driver, Anim.CprDict,
                          _verdict == Verdict.Workable ? Anim.CprWorked : Anim.CprFailed,
                          Anim.HoldLast);
            }

            if (now - _stepAt < _cfg.VerdictMs) return;

            if (!Crew.There(_body)) { Leave(now, "the body had gone"); return; }

            if (_verdict == Verdict.Workable) { Revive(now); return; }

            if (Say != null && Near()) Say("They stop working on him.");

            // NOT EVERYBODY WANTS THE REST OF IT. With the hospital run switched off the crew
            // do the one thing this mod was written to stop them doing -- get back in and leave
            // him there -- and that is a legitimate way to run it: the resuscitation alone is
            // most of the value, and the loading is the part that touches other mods' corpses.
            if (!_cfg.TakeToHospital) { Leave(now, "they are not taking him"); return; }

            To(Step.Fetching, now);
        }

        /// <summary>
        /// He comes round.
        ///
        /// RESURRECT_PED LEAVES A PED BLANK, which is Hoodrich's lesson and is written down
        /// there in the same words: no flags, out of whatever group it was in, and none of the
        /// things that made it a person in a street. So everything that matters is put back
        /// afterwards, and it is put back in ONE place rather than two, because two lists of
        /// flags drift apart and the copy that drifted is the one nobody tested.
        ///
        /// AND HE IS HANDED STRAIGHT BACK TO THE GAME. He is not ours -- he was a passer-by
        /// before somebody hit him and he is a passer-by again. Holding him persistent so he
        /// can be watched walking away is how a mod ends up with forty people it owns.
        /// </summary>
        private void Revive(int now)
        {
            try
            {
                var handle = _body.Handle;

                Function.Call(Hash.RESURRECT_PED, handle);

                var max = Function.Call<int>(Hash.GET_PED_MAX_HEALTH, handle);
                if (max <= 0) max = 200;

                // NOT BACK TO FULL. He has just been dead. A third of a bar is enough to stand
                // up and get out of the road, and it means a second beating finishes him.
                Function.Call(Hash.SET_ENTITY_HEALTH, handle, Math.Max(25, max / 3));

                Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, handle);
                Function.Call(Hash.SET_PED_CAN_RAGDOLL, handle, true);
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, handle, false);
                Function.Call(Hash.SET_PED_CAN_BE_TARGETTED, handle, true);

                // Up, and away from the man who has been pushing on his chest.
                Function.Call(Hash.TASK_WANDER_STANDARD, handle, 10f, 10);

                Crew.Give(_body);

                Log.Info("He came round -- it was " + Cause.Word(_weapon) + ".");

                if (Say != null && Near()) Say("They bring him round.");
            }
            catch (Exception ex)
            {
                Log.Debug("Could not bring him round: " + ex.Message);
            }

            _body = null;

            To(Step.Rising, now);
        }

        /// <summary>A moment for him to get up before the crew turn away.</summary>
        private void Rising(int now)
        {
            if (now - _stepAt < _cfg.RisingMs) return;

            Leave(now, null);
        }

        // ---- the trolley -------------------------------------------------------

        private void Fetching(int now)
        {
            if (!Crew.There(_body)) { Leave(now, "the body had gone"); return; }

            if (Entering())
            {
                Anim.Stop(_driver, Anim.CprDict, Anim.CprFailed);
                Anim.Stop(_mate, Anim.LookDict, Anim.LookClip);

                Doors(true);

                // THE TROLLEY IS BROUGHT OUT BESIDE THE BODY RATHER THAN WHEELED FROM THE VAN.
                // Wheeling it over is prettier and is a second pathing problem in a street that
                // already has a parked ambulance and two men in it; what it buys is a few
                // seconds nobody is looking at, and what it costs is a prop stuck on a kerb.
                //
                // BESIDE HIM, NOT ON HIM. Put down at his exact position it spawns through him,
                // which for the three seconds before he is loaded looks like a trolley that has
                // been dropped on a corpse. A metre towards the van is where somebody wheeling
                // it over would actually have stopped, and it puts the load and the walk back
                // in the same direction.
                if (!_trolley.Bring(Beside(_body.Position), _body.Heading + 90f))
                {
                    // No gurney on this install. They carry him.
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

            // Whoever is taking him back takes the trolley with him.
            if (!_carrying) _trolley.Take(_driver);

            Walk(_driver);
            Walk(_mate);

            To(Step.Wheeling, now);
        }

        /// <summary>
        /// A metre from the body, on the side the van is.
        ///
        /// Falls back to the body's own spot when the van is directly on top of it or gone,
        /// because a normalised zero-length vector is a NaN and a prop spawned at NaN is a prop
        /// that never appears and never says why.
        /// </summary>
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

        /// <summary>
        /// No trolley, so he goes over a shoulder.
        ///
        /// The same weld the trolley uses, one entity closer. It is not as good and it is not
        /// meant to be -- it is what happens on an install that has neither gurney prop, so
        /// that the mod still ends with the body in the ambulance rather than with the crew
        /// standing over him because a DLC was missing.
        /// </summary>
        private void Carry()
        {
            if (!Crew.Alive(_driver) || !Crew.There(_body)) return;

            try
            {
                Function.Call(Hash.SET_PED_TO_RAGDOLL, _body.Handle, 1, 1, 0, false, false, false);
                Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, _body.Handle);

                OnShoulder();
            }
            catch (Exception ex)
            {
                Log.Debug("Could not pick him up: " + ex.Message);
            }
        }

        /// <summary>
        /// The attach on its own, without the ragdoll and the task clear in front of it.
        ///
        /// SPLIT OUT FOR THE SETTINGS SCREEN. Re-fitting on every key press has to be cheap and
        /// idempotent, and re-ragdolling a body forty times while somebody holds a slider down
        /// is neither -- it fights the weld it is about to be put back into.
        /// </summary>
        private void OnShoulder()
        {
            if (!Crew.Alive(_driver) || !Crew.There(_body)) return;

            Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY,
                          _body.Handle, _driver.Handle, 0,
                          _cfg.CarryX, _cfg.CarryY, _cfg.CarryZ,
                          0f, 0f, _cfg.CarryYaw,
                          false, false, false, false, 2, true, 0);
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

        /// <summary>The attach on its own. Split out for the settings screen, as OnShoulder is.</summary>
        private void InBack()
        {
            if (!Crew.There(_body) || !Crew.Alive(_van)) return;

            Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY,
                          _body.Handle, _van.Handle, 0,
                          _cfg.BodyInVanX, _cfg.BodyInVanY, _cfg.BodyInVanZ,
                          0f, 0f, _cfg.BodyInVanYaw,
                          false, false, false, false, 2, true, 0);
        }

        /// <summary>
        /// Put everything back where the offsets NOW say it goes.
        ///
        /// THE POINT OF THE [FIT] SLIDERS. Those numbers are the one part of this mod that could
        /// not be measured -- a prop's origin is wherever the artist put it and there is no way
        /// to find that out from outside the running game -- so they are tuned by looking at a
        /// body on a trolley and moving it until it sits right. That is only possible if moving
        /// the number moves the body on the same frame; a value that took effect on the NEXT
        /// call-out would be the alt-tab-edit-restart loop again with extra steps.
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

        /// <summary>
        /// What the crew are doing, in three or four words, for the settings screen.
        ///
        /// ONLY EVER READ WHILE THE MENU IS OPEN. It exists because tuning the fit means waiting
        /// for a call-out to reach the step you care about, and without a readout the only way
        /// to know whether the trolley is out yet is to go and look at it -- which is awkward
        /// when the menu has the controls disabled.
        /// </summary>
        public string State
        {
            get
            {
                switch (_step)
                {
                    case Step.None:     return "nothing on";
                    case Step.Coming:   return "on the way";
                    case Step.Reaching: return "walking over";
                    case Step.Working:  return _verdict == Verdict.Workable
                                             ? "working on him" : "checking him";
                    case Step.Verdict:  return _verdict == Verdict.Workable
                                             ? "they have him" : "nothing to be done";
                    case Step.Rising:   return "he is getting up";
                    case Step.Fetching: return "fetching the trolley";
                    case Step.Loading:  return _carrying ? "picking him up" : "loading him";
                    case Step.Wheeling: return _carrying ? "carrying him back" : "wheeling him back";
                    case Step.Stowing:  return "into the back";
                    case Step.Driving:  return _to == Vector3.Zero
                                             ? "leaving" : "driving to the hospital";
                }

                return "?";
            }
        }

        /// <summary>Whether the man at this scene was one they could have had.</summary>
        public bool Workable
        {
            get { return _step != Step.None && _verdict == Verdict.Workable; }
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
        /// The back doors.
        ///
        /// THREE INDICES, BECAUSE THE AMBULANCE IS NOT A SALOON. Door 5 is the boot on most
        /// models and the rear pair on some; 2 and 3 are the rear side doors where a model has
        /// them. Asking for a door a model does not have is harmless, and asking for all three
        /// is how this works on both editions without a table of per-model door layouts.
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

            Anim.Stop(_driver, Anim.CprDict, Anim.CprPump);
            Anim.Stop(_mate, Anim.LookDict, Anim.LookClip);

            Board(_driver, -1);
            Board(_mate, 0);

            To(Step.Driving, now);

            _to = Vector3.Zero;
        }

        private void Driving(int now)
        {
            // A GRACE PERIOD BEFORE THE WHEELS TURN, so the crew are actually in it. A van that
            // pulls away the instant the state changes leaves a paramedic stood in the road for
            // the rest of the session -- which is Five0 Patrol's note on the same problem, and
            // the same fix.
            if (now - _stepAt < _cfg.BoardMs) return;

            // ONCE, NOT EVERY FRAME. The grace period above gates this, so the flag is still
            // unconsumed when the wait ends and this is the first pass after it -- which is
            // exactly the moment to give the order. Issuing a task every frame does not make it
            // happen harder; it restarts it, and a driving task restarted forty times a second
            // is a van that never gets out of first gear.
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

            // ARRIVED, or long enough that it does not matter. The hospital is a destination
            // rather than a place anything happens: nothing is unloaded, because a body carried
            // through a door the game does not open is a body walked into a wall. It gets there
            // and it is handed back.
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

        // ---- handing it all back ------------------------------------------------

        /// <summary>
        /// Everything given back to the game.
        ///
        /// THE TROLLEY GOES FIRST, and it takes the body off itself on the way out -- see
        /// Gurney.Release. An entity attached to a deleted one keeps an attachment to something
        /// that is not there, and what that looks like is a corpse hanging in mid-air over a
        /// road until the session ends.
        /// </summary>
        public void Done()
        {
            _trolley.Release();

            try
            {
                if (Crew.There(_body))
                {
                    Function.Call(Hash.DETACH_ENTITY, _body.Handle, true, true);
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
            _carrying = false;
            _bodyInVan = false;
        }

        /// <summary>
        /// One of the crew, put back the way he was found.
        ///
        /// THE BLOCKED EVENTS ARE THE ONE THAT MATTERS. A ped left with
        /// SET_BLOCKING_OF_NON_TEMPORARY_EVENTS on cannot react to anything for the rest of the
        /// session -- not gunfire, not a car coming at him, not the player. Handing him back
        /// without clearing it leaves a man standing in a street who has stopped being able to
        /// notice the world, and nothing on screen says why.
        /// </summary>
        private static void Loose(Ped who)
        {
            try
            {
                if (who == null || !who.Exists()) return;

                Anim.Stop(who, Anim.CprDict, Anim.CprPump);
                Anim.Stop(who, Anim.LookDict, Anim.LookClip);

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
