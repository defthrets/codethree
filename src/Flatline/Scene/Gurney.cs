using System;
using GTA;
using GTA.Math;
using GTA.Native;
using Flatline.Core;

namespace Flatline.Scene
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
    /// tried in that order with a graceful nothing at the end. On an install without either,
    /// the crew carry him instead; see Callout.
    ///
    /// THE OFFSETS ARE IN THE INI AND THAT IS DELIBERATE. A prop's origin is wherever the
    /// artist put it, and there is no way to measure one from outside the running game. The
    /// numbers below are a considered starting point rather than a measurement, and anybody who
    /// finds the body floating a hand's width above the canvas can nudge it without a compiler.
    /// Pretending to a precision that was not available is how a mod ends up with a magic
    /// constant nobody dares touch.
    /// </summary>
    internal sealed class Gurney
    {
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

        private readonly Settings _cfg;

        private Prop _trolley;
        private Ped _pushing;
        private Ped _load;

        /// <summary>
        /// Where it is and who has it, remembered so the fit can be re-applied.
        ///
        /// KEPT ONLY BECAUSE OF THE SETTINGS SCREEN. Nothing in the call-out itself needs to
        /// look back at how the trolley was attached -- each step attaches it and moves on. But
        /// the whole reason the [Fit] offsets are on a screen rather than only in the ini is
        /// that you tune them by WATCHING the thing move, and that means re-attaching whatever
        /// is currently attached the instant a number changes. Without these two the menu could
        /// only affect the next call-out, which is the alt-tab-and-restart loop again wearing a
        /// different hat.
        /// </summary>
        private Held _holder;
        private Vehicle _aboard;

        public Gurney(Settings cfg)
        {
            _cfg = cfg;
        }

        /// <summary>Whether there is a trolley in the world at all.</summary>
        public bool There
        {
            get { return _trolley != null && _trolley.Exists(); }
        }

        /// <summary>The trolley itself, for anybody who needs to measure to it.</summary>
        public Prop Item
        {
            get { return _trolley; }
        }

        /// <summary>
        /// Brings one into the world at a spot, or reports that this install has neither prop.
        ///
        /// A FAILURE HERE IS NOT A FAILURE OF THE CALL-OUT. Returning false means the crew do
        /// the rest of it by hand, which still ends with the body in the ambulance -- so this
        /// never throws and never leaves the scene half-built.
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

                    // ON THE GROUND, AND WITH PHYSICS. Placed without the ground check it sits
                    // at whatever height the body happened to be lying at, which on a kerb or a
                    // slope is a trolley hovering. Dynamic because for the few seconds before
                    // anybody touches it, it is a real object in a street rather than scenery --
                    // and once it is attached the weld takes it out of the simulation anyway.
                    _trolley = World.CreateProp(model.Value, at, true, true);

                    model.Value.MarkAsNoLongerNeeded();

                    if (_trolley == null || !_trolley.Exists()) { _trolley = null; continue; }

                    _trolley.IsPersistent = true;
                    _trolley.Heading = heading;

                    Function.Call(Hash.SET_ENTITY_LOD_DIST, _trolley.Handle, 300);

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
        /// The body onto the canvas.
        ///
        /// A WELD, NOT A CONSTRAINT, AND THAT IS THE OPPOSITE OF WHAT THE DRAG NEEDED.
        /// Five0 Patrol's Drag uses ATTACH_ENTITY_TO_ENTITY_PHYSICALLY precisely because it
        /// wanted a ragdoll to stay a ragdoll -- the wrists held and the rest trailing along
        /// the pavement. Here the opposite is true: a body on a trolley should be still. A
        /// physical constraint would leave him rolling off it at the first corner, so the plain
        /// attach -- which takes the entity out of the physics entirely -- is the right one.
        ///
        /// AND THE POSE IS WHATEVER HE DIED IN. A dead ped cannot be animated into lying
        /// straight, so a man who fell awkwardly is loaded awkwardly. That is honest, it is what
        /// a body looks like, and the alternative -- resurrecting him to pose him and then
        /// killing him again -- is a lie the rest of the mod would have to keep telling.
        /// </summary>
        public bool Lay(Ped body)
        {
            if (!There || !Crew.There(body)) return false;

            try
            {
                // NOT RAGDOLLED FIRST ANY MORE. He arrives here alive and holding the lying-dead
                // pose the call-out put him in -- see Callout.PoseDead -- and an attached ped
                // keeps playing whatever clip it has, which is exactly what puts him flat on the
                // canvas. Ragdolling him now would throw that pose away and put us back to
                // welding a heap.
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

        /// <summary>The body against the canvas, at whatever the offsets currently say.</summary>
        private void OnCanvas()
        {
            if (!There || !Crew.There(_load)) return;

            Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY,
                          _load.Handle, _trolley.Handle, 0,
                          _cfg.BodyOnTrolleyX, _cfg.BodyOnTrolleyY, _cfg.BodyOnTrolleyZ,
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
                Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY,
                              _trolley.Handle, _pushing.Handle, 0,
                              _cfg.TrolleyPushX, _cfg.TrolleyPushY, _cfg.TrolleyPushZ,
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
        /// this is safe to call as often as somebody can press a key -- which, since it is a
        /// key repeat, is quite often.
        ///
        /// THE BODY GOES LAST. It hangs off the trolley, so moving the trolley first and the
        /// body second means both end up where the new numbers say in one pass; the other order
        /// leaves the body one frame behind, which on a slider being held down reads as the
        /// body lagging the thing it is strapped to.
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
        /// PUSHED RATHER THAN PATHED. The alternative is to give the trolley its own movement
        /// and try to keep a walking man beside it, which is two things to synchronise and a
        /// prop that will happily walk through a bollard. Welded in front of him at hip height
        /// it does what a trolley does, and the man steering it is the game's own pathing.
        /// </summary>
        public bool Take(Ped medic)
        {
            if (!There || !Crew.Alive(medic)) return false;

            try
            {
                _pushing = medic;
                _holder = Held.OnMedic;

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
                    Crew.Give(_load);
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
