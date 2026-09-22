using System;
using GTA;
using GTA.Math;
using GTA.Native;
using CodeThree.Core;

namespace CodeThree.Scene
{
    /// <summary>
    /// The trolley: fetched out of the back, laid out beside him, loaded, wheeled, and put away.
    ///
    /// THE GAME HAS A GURNEY AND ALMOST NOBODY KNOWS IT. Every EMS mod for this game ships a
    /// replacement model, usually by overwriting an unrelated prop -- the popular pack turns
    /// prop_ld_binbag_01, a bin bag, into a stretcher. That is an asset replacement, it needs an
    /// RPF edit, and it is exactly the sort of install this set refuses to require: one dll and
    /// one ini, no game file touched.
    ///
    /// So the object list was searched instead, and there are two real ones in there:
    /// m25_2_prop_m52_gurney_01a and m23_2_prop_m32_lgstretcher_01a. Both arrived with recent
    /// updates -- mp2025_02 and mp2023_02 -- which is why nobody uses them and why they are
    /// tried in that order with a graceful nothing at the end.
    ///
    /// THE OFFSETS ARE MEASURED NOW, NOT GUESSED. The first two versions of this file said that
    /// a prop's origin is wherever the artist put it and there is no way to find that out from
    /// outside the running game, and put the numbers in the ini as considered guesses. That was
    /// wrong: GET_MODEL_DIMENSIONS gives the bounding box in model space, and the box says
    /// exactly where the origin sits inside the geometry. So the trolley now stands on the
    /// ground because its measured base is put on the ground, and the ini numbers are NUDGES on
    /// top of a measurement rather than the whole answer. See Fit.
    ///
    /// AND IT IS LOADED BEFORE IT IS NEEDED. Crew.Load yields -- for up to eight hundred
    /// milliseconds of real frames -- and yielding in the middle of a scene is what let the
    /// engine reclaim the patient while the crew were standing over him. The models are asked
    /// for at dispatch, a minute before anybody reaches for one. See Preload.
    /// </summary>
    internal sealed class Gurney
    {
        /// <summary>What the trolley is currently hanging off.</summary>
        private enum Held
        {
            /// <summary>Standing in the road on its own wheels.</summary>
            Loose,

            /// <summary>Being pushed.</summary>
            OnMedic,

            /// <summary>In the back.</summary>
            InVan,
        }

        /// <summary>
        /// The two the game actually has, best first.
        ///
        /// The gurney proper is preferred over the long stretcher because it has wheels and a
        /// frame, which is what a crew would bring to a body in a street.
        /// </summary>
        private static readonly string[] Props =
        {
            "m25_2_prop_m52_gurney_01a",
            "m23_2_prop_m32_lgstretcher_01a",
        };

        private readonly Settings _cfg;

        private Prop _trolley;
        private Ped _pushing;
        private Ped _load;

        private Held _holder;
        private Vehicle _aboard;

        /// <summary>
        /// The measured shape of whichever prop actually loaded.
        ///
        /// _standZ lifts the trolley so its wheels are on the ground when it hangs off a ped,
        /// whose own origin is between his feet. _bedZ is where the canvas is, as a height above
        /// the trolley's origin -- taken as three quarters of the way up the box, because the
        /// wheels are the bottom quarter and the rails are the top, and a bed is between them.
        /// The ini nudge exists for exactly the amount that estimate is out by.
        /// </summary>
        private float _standZ;
        private float _bedZ;
        private bool _measured;

        public Gurney(Settings cfg)
        {
            _cfg = cfg;
        }

        /// <summary>Whether there is a trolley in the world at all.</summary>
        public bool There
        {
            get { return _trolley != null && _trolley.Exists(); }
        }

        /// <summary>
        /// Where it is standing, for anybody who needs to walk to it.
        ///
        /// The position rather than the prop, deliberately: the only thing outside this file
        /// that cares is the second man walking over to steady it, and handing out the entity
        /// would let anything attach to or move a trolley whose attachment state is bookkept
        /// in here.
        /// </summary>
        public Vector3 Where
        {
            get { return There ? _trolley.Position : Vector3.Zero; }
        }

        /// <summary>
        /// Asks for the models, without waiting for them.
        ///
        /// CALLED AT DISPATCH, USED A MINUTE LATER. Request is not a blocking call; it puts the
        /// model on the streamer's list and returns. By the time the van has driven across two
        /// districts the model is in memory, and Bring never has to yield at all -- which is the
        /// whole point, because a yield mid-scene is a window for the engine to reclaim the
        /// patient, and that is not a theory, it is what the log caught it doing.
        /// </summary>
        public void Preload()
        {
            foreach (var name in Props)
            {
                try
                {
                    var model = new Model(name);
                    if (model.IsValid) model.Request();
                }
                catch
                {
                    // The load below finds out.
                }
            }
        }

        /// <summary>
        /// Brings one into the world at a spot, or reports that this install has neither prop.
        ///
        /// A FAILURE HERE IS NOT A FAILURE OF THE CALL-OUT. Returning false means the crew do
        /// the rest of it by hand, which still ends with the body in the ambulance.
        ///
        /// FROZEN, NOT DYNAMIC. It used to be spawned with physics on the reasoning that for the
        /// few seconds before anybody touches it, it is a real object in a street. What it
        /// actually is for those seconds is a heavy object dropped next to a ragdoll, which
        /// shoves the body it is about to carry. A trolley nobody has picked up yet does not need
        /// to move.
        /// </summary>
        public bool Bring(Vector3 at, float heading)
        {
            if (There) return true;

            foreach (var name in Props)
            {
                try
                {
                    var model = Crew.Load(name, 800);
                    if (model == null) continue;

                    _trolley = World.CreateProp(model.Value, at, false, false);

                    model.Value.MarkAsNoLongerNeeded();

                    if (_trolley == null || !_trolley.Exists()) { _trolley = null; continue; }

                    Crew.Hold(_trolley);

                    _trolley.Heading = heading;

                    Function.Call(Hash.FREEZE_ENTITY_POSITION, _trolley.Handle, true);
                    Function.Call(Hash.SET_ENTITY_LOD_DIST, _trolley.Handle, 300);

                    Shape(name);

                    Function.Call(Hash.SET_ENTITY_COORDS, _trolley.Handle,
                                  at.X, at.Y, at.Z + _standZ, false, false, false, false);

                    Log.Info("The crew brought a trolley out (" + name + ").");
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Debug("Could not bring out " + name + ": " + ex.Message);
                    _trolley = null;
                }
            }

            Log.Info("No trolley on this install -- neither gurney prop would load. " +
                     "The crew will carry him.");

            return false;
        }

        /// <summary>
        /// Measures the prop that actually loaded, once, and says so in the log.
        ///
        /// SAID OUT LOUD DELIBERATELY. If the body still sits wrong on the canvas, these two
        /// numbers are the entire diagnosis -- they say how tall the thing is and where its
        /// origin is inside it, which is everything the offsets are derived from.
        /// </summary>
        private void Shape(string name)
        {
            if (_measured) return;

            Vector3 min, max;

            if (!Crew.Measure(name, out min, out max))
            {
                // The old guesses, as a floor. Better than nothing and it still runs.
                _standZ = 0.55f;
                _bedZ = 0.55f;
                _measured = true;

                Log.Warn("Could not measure " + name + "; falling back to estimated offsets.");
                return;
            }

            _standZ = -min.Z;
            _bedZ = min.Z + (max.Z - min.Z) * 0.75f;
            _measured = true;

            Log.Info("Trolley measured: " + (max.Z - min.Z).ToString("0.00") + "m tall, origin " +
                     _standZ.ToString("0.00") + "m above the wheels, bed at " +
                     _bedZ.ToString("0.00") + ". Nudge in [Fit] if he sits wrong.");
        }

        /// <summary>
        /// The body onto the canvas.
        ///
        /// A WELD, NOT A CONSTRAINT, AND THAT IS THE OPPOSITE OF WHAT THE DRAG NEEDED. Five0
        /// Patrol's Drag uses ATTACH_ENTITY_TO_ENTITY_PHYSICALLY precisely because it wanted a
        /// ragdoll to stay a ragdoll. Here the opposite is true: a body on a trolley should be
        /// still, so the plain attach -- which takes the entity out of the physics entirely --
        /// is the right one.
        ///
        /// HE ARRIVES ALREADY POSED. He is alive by now and holding the game's own lying-dead
        /// clip, and an attached ped keeps playing whatever clip it has. That is what puts him
        /// flat rather than crumpled, and it is why nothing here ragdolls him first.
        /// </summary>
        public bool Lay(Ped body)
        {
            if (!There || !Crew.There(body)) return false;

            try
            {
                _load = body;

                OnCanvas();

                Log.Debug("The body is on the trolley.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not lay him on the trolley: " + ex.Message);
                return false;
            }
        }

        /// <summary>The body against the canvas, at the measured height plus whatever the ini says.</summary>
        private void OnCanvas()
        {
            if (!There || !Crew.There(_load)) return;

            Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY,
                          _load.Handle, _trolley.Handle, 0,
                          _cfg.BodyOnTrolleyX,
                          _cfg.BodyOnTrolleyY,
                          _bedZ + _cfg.BodyOnTrolleyZ,
                          0f, 0f, _cfg.BodyOnTrolleyYaw,
                          false, false,
                          false,   // the body is not a ped in a vehicle
                          false, 2, true, 0);
        }

        /// <summary>And the trolley against whoever currently has it.</summary>
        private void ToHolder()
        {
            if (!There) return;

            if (_holder == Held.OnMedic && Crew.Alive(_pushing))
            {
                // THE MEASURED HEIGHT IS THE WHOLE OF THIS. A ped's origin is between his feet,
                // so standing the trolley on the ground in front of him means lifting it by
                // exactly the distance from its own origin down to its wheels -- which is what
                // _standZ is. The previous version had a flat -0.9 here, which buried it.
                Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY,
                              _trolley.Handle, _pushing.Handle, 0,
                              _cfg.TrolleyPushX,
                              _cfg.TrolleyPushY,
                              _standZ + _cfg.TrolleyPushZ,
                              0f, 0f, 0f,
                              false, false, false, false, 2, true, 0);
                return;
            }

            if (_holder == Held.InVan && Crew.Alive(_aboard))
            {
                Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY,
                              _trolley.Handle, _aboard.Handle, 0,
                              _cfg.TrolleyInVanX, _cfg.TrolleyInVanY, _cfg.TrolleyInVanZ,
                              0f, 0f, 0f,
                              false, false, false, false, 2, true, 0);
            }
        }

        /// <summary>
        /// Re-apply every attachment at the numbers that are in the settings NOW.
        ///
        /// Called by the settings screen each time a [Fit] row is nudged. An attach onto an
        /// entity that is already attached to that same entity simply replaces the offset, so
        /// this is safe to call as often as somebody can press a key.
        ///
        /// THE BODY GOES LAST, because it hangs off the trolley -- moving the trolley first and
        /// the body second means both land where the new numbers say in one pass.
        /// </summary>
        public void Refit()
        {
            try
            {
                ToHolder();
                OnCanvas();
            }
            catch (Exception ex)
            {
                Log.Debug("Could not re-fit the trolley: " + ex.Message);
            }
        }

        /// <summary>
        /// Put into a medic's hands, so it goes where he goes.
        ///
        /// PUSHED RATHER THAN PATHED. The alternative is to give the trolley its own movement and
        /// try to keep a walking man beside it, which is two things to synchronise and a prop
        /// that will happily walk through a bollard. Welded in front of him at the height its own
        /// wheels want, with the shopping-trolley pose on his upper body, it does what a trolley
        /// does -- and the man steering it is the game's own pathing.
        /// </summary>
        public bool Take(Ped medic)
        {
            if (!There || !Crew.Alive(medic)) return false;

            try
            {
                _pushing = medic;
                _holder = Held.OnMedic;

                // It is being carried now, so it must not also be nailed to the world.
                Function.Call(Hash.FREEZE_ENTITY_POSITION, _trolley.Handle, false);

                ToHolder();
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not hand him the trolley: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// And into the back of the van, body and all.
        ///
        /// The trolley keeps the body, so this is one attach rather than two -- and it means
        /// nothing can end up half-loaded, with the trolley aboard and the man still in the road.
        /// </summary>
        public bool Stow(Vehicle van)
        {
            if (!There || !Crew.Alive(van)) return false;

            try
            {
                _pushing = null;
                _aboard = van;
                _holder = Held.InVan;

                ToHolder();

                Log.Debug("The trolley is in the back.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not load the trolley: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Everything let go of, in the order that leaves nothing floating.
        ///
        /// THE BODY IS DETACHED BEFORE THE TROLLEY IS DELETED. An entity attached to a deleted
        /// one keeps the attachment it had to something that no longer exists, and what that
        /// looks like is a corpse hanging in the air over a road for the rest of the session.
        /// </summary>
        public void Release()
        {
            try
            {
                if (Crew.There(_load))
                {
                    Function.Call(Hash.DETACH_ENTITY, _load.Handle, true, true);
                }
            }
            catch
            {
                // Gone already.
            }

            try
            {
                if (There)
                {
                    Function.Call(Hash.DETACH_ENTITY, _trolley.Handle, true, true);

                    _trolley.IsPersistent = false;
                    _trolley.Delete();
                }
            }
            catch
            {
                // Gone already.
            }

            _trolley = null;
            _pushing = null;
            _load = null;
            _aboard = null;
            _holder = Held.Loose;
        }
    }
}
