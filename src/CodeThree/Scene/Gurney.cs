using System;
using GTA;
using GTA.Math;
using GTA.Native;
using CodeThree.Core;

namespace CodeThree.Scene
{
    /// <summary>
    /// The trolley: put down beside him, loaded, wheeled, and rolled into the back.
    ///
    /// THE GAME HAS A GURNEY AND ALMOST NOBODY KNOWS IT. Every EMS mod for this game ships a
    /// replacement model, usually by overwriting an unrelated prop -- the popular pack turns
    /// prop_ld_binbag_01, a bin bag, into a stretcher. That needs an RPF edit, which this set
    /// refuses to require. The object list has two real ones: m25_2_prop_m52_gurney_01a and
    /// m23_2_prop_m32_lgstretcher_01a, both from recent updates, tried in that order.
    ///
    /// EVERYTHING ABOUT ITS SHAPE IS MEASURED. GET_MODEL_DIMENSIONS gives the bounding box in
    /// model space, and the box answers every question the offsets used to guess at: how far
    /// the origin sits above the wheels, how high the canvas is, how long the thing is, and --
    /// the one nothing asked before -- WHICH AXIS IS THE LONG ONE. A prop's "forward" is
    /// whatever the artist chose; if the gurney was modelled lying along X, then turning it to
    /// face the ambulance lays it out sideways, and wheeling it "forwards" pushes it crab-wise.
    ///
    /// AND IT NEVER COLLIDES WHILE IT IS MOVING. It is moved by hand -- placed a fixed distance
    /// in front of the man pushing it every tick, and eased into the van at the end -- and a
    /// frozen prop with collision on, placed into another object, is the physics engine told to
    /// resolve an overlap with something that cannot move. It resolved it by moving the other
    /// thing. Four times the other thing was the ambulance, which was flung out of the world
    /// and deleted with the crew stood behind it. See Follow.
    /// </summary>
    internal sealed class Gurney
    {
        /// <summary>What the trolley is currently doing.</summary>
        private enum Held
        {
            /// <summary>Standing in the road on its own wheels.</summary>
            Loose,

            /// <summary>Being pushed.</summary>
            OnMedic,

            /// <summary>Easing through the back doors.</summary>
            Rolling,

            /// <summary>In the back.</summary>
            InVan,
        }

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

        // ---- the measured shape ------------------------------------------------

        /// <summary>How far to lift the origin so the wheels are on the road.</summary>
        private float _standZ;

        /// <summary>How high the canvas is above the origin.</summary>
        private float _bedZ;

        /// <summary>
        /// What the body turned out to need, on top of the numbers, to lie along the canvas.
        ///
        /// MEASURED OFF HIS SKELETON ONCE HE IS ON IT, AND NEVER REMEMBERED. Which way a lying
        /// clip points a man relative to his own root is not written down anywhere, so he is
        /// laid down with the ini's guess, his pelvis-to-head line is read a quarter of a second
        /// later against the trolley's long axis, and the difference is taken up here.
        ///
        /// The first version of this kept the answer for the session. One call-out whose scene
        /// had been built fourteen metres from the body measured "turn 87 degrees, lift 99cm",
        /// remembered it, and applied it to every patient after -- two of whom had lined up
        /// perfectly. A correction is only ever as good as the scene it was measured in, so it
        /// lives and dies with the trolley it was measured on, and anything outside what a man
        /// on a bed could plausibly need is thrown away rather than applied.
        /// </summary>
        private float _yawFix;
        private float _zFix;
        private bool _squared;

        /// <summary>
        /// Further than this is not a man off the canvas; it is a man somewhere else.
        ///
        /// A METRE AND A HALF, NOT HALF A METRE. The lying clips keep the ped's root about a
        /// metre above the drawn body -- see Callout._poseZ -- so a man attached by his root at
        /// bed height is drawn a metre lower, on the ground under the canvas. The log said
        /// "99cm under" every time and the old bound refused it as not a fit problem. It was
        /// the whole fit problem.
        /// </summary>
        private const float MostLift = 1.5f;

        /// <summary>A lying man's pelvis sits about this far above whatever he is lying on.</summary>
        private const float PelvisAboveBed = 0.12f;

        /// <summary>Half its length along the long axis.</summary>
        private float _halfLength = 1f;

        /// <summary>
        /// Nought if the model's long axis is its own forward (Y), ninety if it is sideways (X).
        ///
        /// Added to any heading the trolley is given, so "face the ambulance" means the long
        /// axis faces the ambulance whichever way the artist built it.
        /// </summary>
        private float _axisYaw;

        private bool _measured;

        // ---- the roll into the van ---------------------------------------------

        private Vector3 _rollFrom;
        private float _rollFromHeading;

        public Gurney(Settings cfg)
        {
            _cfg = cfg;
        }

        public bool There
        {
            get { return _trolley != null && _trolley.Exists(); }
        }

        /// <summary>Where it is standing.</summary>
        public Vector3 Where
        {
            get { return There ? _trolley.Position : Vector3.Zero; }
        }

        /// <summary>
        /// The direction its long axis points -- the way it goes when pushed.
        ///
        /// Taken off the prop's own heading with the axis correction removed, so it is right
        /// whichever axis the model was built along.
        /// </summary>
        public Vector3 Along
        {
            get { return There ? Motion.Facing(_trolley.Heading - _axisYaw) : Vector3.Zero; }
        }

        /// <summary>The heading of that direction, for a man who needs to face it.</summary>
        public float AlongHeading
        {
            get { return There ? _trolley.Heading - _axisYaw : 0f; }
        }

        /// <summary>The spot at the back end, where a man stands to push it.</summary>
        public Vector3 Behind
        {
            get { return There ? _trolley.Position - Along * _cfg.TrolleyPushY : Vector3.Zero; }
        }

        /// <summary>Half its measured length, for anybody stopping it short of something.</summary>
        public float HalfLength
        {
            get { return _halfLength; }
        }

        /// <summary>Asks for the models at dispatch, a minute before they are needed. See Callout.Send.</summary>
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
                    // The load in Bring finds out.
                }
            }
        }

        /// <summary>
        /// Puts one down at a spot, its long axis pointing along a heading.
        ///
        /// FROZEN AND ON THE ROAD. The height is asked of the world at the spot -- see
        /// Crew.Ground -- and the measured origin does the rest. It is frozen because nothing
        /// should move it until a man takes hold of it, and it keeps its collision while it
        /// stands there, because people should walk round a trolley rather than through it.
        /// </summary>
        public bool Bring(Vector3 at, float alongHeading, float nearZ)
        {
            if (There) return true;

            // A correction is only as good as the trolley it was measured on. See _yawFix.
            _yawFix = 0f;
            _zFix = 0f;
            _squared = false;

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

                    Function.Call(Hash.SET_ENTITY_LOD_DIST, _trolley.Handle, 300);

                    Shape(name);

                    _trolley.Heading = alongHeading + _axisYaw;

                    // THE ROAD'S HEIGHT, CHECKED AGAINST THE PATIENT'S. The probe is usually
                    // right, and when it is wrong it is wrong by a storey -- a spot inside a
                    // wall, a probe that found a roof -- and that is the trolley in the sky. A
                    // man lying beside it is at road height by definition, so anything more
                    // than a metre and a half from him is not the road, and his height is used.
                    var ground = Crew.Ground(at, nearZ);

                    if (Math.Abs(ground - nearZ) > 1.5f)
                    {
                        Log.Warn("The ground probe put the trolley " + (ground - nearZ).ToString("0.0") +
                                 "m from the patient's height; using his instead.");
                        ground = nearZ;
                    }

                    Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, _trolley.Handle,
                                  at.X, at.Y, ground + _standZ, false, false, false);

                    Function.Call(Hash.FREEZE_ENTITY_POSITION, _trolley.Handle, true);

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
        /// SAID OUT LOUD DELIBERATELY. If he sits wrong on the canvas or the trolley goes the
        /// wrong way, these numbers are the whole diagnosis.
        /// </summary>
        private void Shape(string name)
        {
            if (_measured) return;

            Vector3 min, max;

            if (!Crew.Measure(name, out min, out max))
            {
                _standZ = 0f;
                _bedZ = 0.75f;
                _halfLength = 1f;
                _axisYaw = 0f;
                _measured = true;

                Log.Warn("Could not measure " + name + "; falling back to estimated offsets.");
                return;
            }

            var lengthX = max.X - min.X;
            var lengthY = max.Y - min.Y;

            _standZ = -min.Z;
            _bedZ = min.Z + (max.Z - min.Z) * 0.75f;
            _axisYaw = lengthX > lengthY ? 90f : 0f;
            _halfLength = Math.Max(lengthX, lengthY) * 0.5f;
            _measured = true;

            Log.Info("Trolley measured: " + (max.Z - min.Z).ToString("0.00") + "m tall, " +
                     (_halfLength * 2f).ToString("0.00") + "m long along " +
                     (_axisYaw > 0f ? "X" : "Y") + ", origin " + _standZ.ToString("0.00") +
                     "m above the wheels, bed at " + _bedZ.ToString("0.00") + ".");
        }

        /// <summary>
        /// Where a body lying on the canvas will be -- its root, and which way it faces.
        ///
        /// THE TARGET THE BODY IS CARRIED TO, measured in the trolley's own frame and turned
        /// into the world. It is exactly the point Lay attaches him to, which is what lets the
        /// carry end in an attach with nothing left to jump.
        /// </summary>
        public bool Bed(out Vector3 at, out float heading)
        {
            at = Vector3.Zero;
            heading = 0f;

            if (!There) return false;

            at = Crew.Offset(_trolley, _cfg.BodyOnTrolleyX, _cfg.BodyOnTrolleyY,
                             _bedZ + _cfg.BodyOnTrolleyZ + _zFix);
            heading = _trolley.Heading + _cfg.BodyOnTrolleyYaw + _yawFix;

            return at != Vector3.Zero;
        }

        /// <summary>
        /// The body onto the canvas.
        ///
        /// A WELD, NOT A CONSTRAINT. A body on a trolley should be still, so the plain attach --
        /// which takes the entity out of the physics entirely -- is the right one. He arrives
        /// already carried to the exact point this attaches him at, so it changes nothing you
        /// can see; it only stops him being left behind when the trolley moves.
        /// </summary>
        public bool Lay(Ped body)
        {
            if (!There || !Crew.There(body)) return false;

            try
            {
                _load = body;

                OnCanvas();
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not lay him on the trolley: " + ex.Message);
                return false;
            }
        }

        private void OnCanvas()
        {
            if (!There || !Crew.There(_load)) return;

            // isPed IS TRUE, AND THAT IS THE WHOLE OF WHY HE WAS UNDER THE BED. The thirteenth
            // argument says whether the thing being attached is a ped, and it was passed as
            // false -- so the engine attached him as it would a crate, and a crate has no
            // capsule to lift clear of the surface. He lay flat, along the canvas, aligned to
            // the centimetre, on the ground directly beneath it: the X, the Y and the pose all
            // honoured and the Z not. With the flag set the offset is applied to a ped.
            Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY,
                          _load.Handle, _trolley.Handle, 0,
                          _cfg.BodyOnTrolleyX,
                          _cfg.BodyOnTrolleyY,
                          _bedZ + _cfg.BodyOnTrolleyZ + _zFix,
                          0f, 0f, _cfg.BodyOnTrolleyYaw + _yawFix,
                          false, false, false, true, 2, true, 0);
        }

        /// <summary>
        /// Reads how he is actually lying on it, and corrects the attach so he lies along it,
        /// flat, once per session. Call a beat after Lay, when his skeleton has settled into
        /// the pose.
        ///
        /// ALONG, EITHER WAY ROUND. A body lying head-to-foot along the canvas and one lying
        /// foot-to-head are both lying along it; the correction is the smaller turn that puts
        /// his line parallel to the axis, so it never spins him a half-turn to swap ends.
        /// </summary>
        public void Square(Ped body)
        {
            if (_squared || !There || !Crew.There(body)) return;

            _squared = true;

            try
            {
                var lying = Crew.Lying(body);

                Vector3 pelvis;
                if (float.IsNaN(lying) || !Crew.Pelvis(body, out pelvis)) return;

                // The turn that makes his line parallel to the long axis, whichever end is which.
                var off = Motion.Wrap(lying - AlongHeading);
                if (off > 90f) off -= 180f;
                if (off < -90f) off += 180f;

                var bed = Crew.Offset(_trolley, _cfg.BodyOnTrolleyX, _cfg.BodyOnTrolleyY,
                                      _bedZ + _cfg.BodyOnTrolleyZ);
                var lift = (bed.Z + PelvisAboveBed) - pelvis.Z;

                // A LIFT BIGGER THAN HALF A METRE IS NOT A MEASUREMENT OF A MAN ON A BED. It is
                // a man on the ground under the bed, or in the next street, and correcting the
                // attach by it would put the next patient in the air. Logged, and left alone.
                if (Math.Abs(lift) > MostLift)
                {
                    Log.Warn("On the canvas: he is " + Math.Abs(lift * 100f).ToString("0") + "cm " +
                             (lift > 0 ? "under" : "over") + " the bed, which is further than any " +
                             "root convention explains. Left as set.");
                    return;
                }

                var turned = Math.Abs(off) > 3f;
                var moved = Math.Abs(lift) > 0.04f;

                if (turned) _yawFix = Motion.Wrap(_yawFix - off);
                if (moved) _zFix = _zFix + lift;

                Log.Info("On the canvas: " + (turned ? "turned " + (-off).ToString("0") + " degrees" : "square") +
                         ", " + (moved ? (lift > 0 ? "lifted " : "lowered ") + Math.Abs(lift * 100f).ToString("0") + "cm" : "level") +
                         ". Fit now yaw " + (_cfg.BodyOnTrolleyYaw + _yawFix).ToString("0") +
                         ", bed " + (_bedZ + _cfg.BodyOnTrolleyZ + _zFix).ToString("0.00") + ".");

                if (turned || moved) OnCanvas();
            }
            catch (Exception ex)
            {
                Log.Debug("Could not square him on the canvas: " + ex.Message);
            }
        }

        /// <summary>
        /// Put into a medic's hands. He should already be standing at Behind, facing Along --
        /// which makes the first Follow move it by nothing at all.
        ///
        /// COLLISION OFF FROM HERE. It is about to be placed by hand every tick, and a frozen
        /// colliding prop placed into anything is the physics told to throw the other thing.
        /// </summary>
        public bool Take(Ped medic)
        {
            if (!There || !Crew.Alive(medic)) return false;

            try
            {
                _pushing = medic;
                _holder = Held.OnMedic;

                Function.Call(Hash.FREEZE_ENTITY_POSITION, _trolley.Handle, true);
                Crew.Solid(_trolley, false);

                Follow();
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not hand him the trolley: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Keeps the trolley in front of the man pushing it. Called every tick while he walks.
        ///
        /// DRIVEN, NOT ATTACHED. A ped's bone 0 is his pelvis, so a weld put the wheels at his
        /// waist; and a weld turns with its bone, so it tilted every time he leaned. Placed by
        /// hand at the road's height, a fixed distance along his facing, it cannot do either.
        ///
        /// ITS LONG AXIS FOLLOWS HIS FACING, through the measured axis correction, so it goes
        /// where he is walking end-first -- not crab-wise, which is what a model built along X
        /// does if it is simply given his heading.
        /// </summary>
        public void Follow()
        {
            if (!There || _holder != Held.OnMedic || !Crew.Alive(_pushing)) return;

            try
            {
                var feet = _pushing.Position;
                var ahead = feet + _pushing.ForwardVector * _cfg.TrolleyPushY
                                 + _pushing.RightVector * _cfg.TrolleyPushX;

                var ground = Crew.Ground(ahead, feet.Z);

                Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, _trolley.Handle,
                              ahead.X, ahead.Y, ground + _standZ + _cfg.TrolleyPushZ,
                              false, false, false);

                _trolley.Heading = _pushing.Heading + _axisYaw;
            }
            catch
            {
                // One frame of the trolley not keeping up is not worth a log line every tick.
            }
        }

        /// <summary>
        /// Starts it rolling into the back of the van.
        ///
        /// IT USED TO APPEAR THERE. Stow attached it to the van in one frame, so a trolley two
        /// metres behind the bumper was simply inside the van on the next. Now it is eased from
        /// where it stands to exactly where Stow will attach it, and only attached once it has
        /// arrived -- so the attach, like the body's, changes nothing you can see.
        /// </summary>
        public void RollFrom()
        {
            if (!There) return;

            _rollFrom = _trolley.Position;
            _rollFromHeading = _trolley.Heading;
            _holder = Held.Rolling;

            Crew.Solid(_trolley, false);
        }

        /// <summary>Part of the way in, 0 to 1. Eased by the caller.</summary>
        public void Roll(Vehicle van, float t)
        {
            if (!There || !Crew.Alive(van)) return;

            try
            {
                var to = InVan(van);
                if (to == Vector3.Zero) return;

                var at = Motion.Lerp(_rollFrom, to, t);

                Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, _trolley.Handle,
                              at.X, at.Y, at.Z, false, false, false);

                _trolley.Heading = Motion.Turn(_rollFromHeading, van.Heading + _axisYaw, t);
            }
            catch
            {
                // Stow puts it where it belongs regardless.
            }
        }

        /// <summary>Where Stow will put it: the in-van offset, as a point in the world.</summary>
        private Vector3 InVan(Vehicle van)
        {
            return Crew.Offset(van, _cfg.TrolleyInVanX, _cfg.TrolleyInVanY, _cfg.TrolleyInVanZ);
        }

        /// <summary>
        /// Into the back for good, body and all.
        ///
        /// A REAL ATTACHMENT HERE, because a vehicle's bone 0 genuinely is its chassis origin --
        /// the trap is specific to peds. Turned by the axis correction, so it lies along the van
        /// rather than across it.
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
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not load the trolley: " + ex.Message);
                return false;
            }
        }

        private void ToHolder()
        {
            if (!There) return;

            if (_holder == Held.OnMedic) { Follow(); return; }

            if (_holder == Held.InVan && Crew.Alive(_aboard))
            {
                Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY,
                              _trolley.Handle, _aboard.Handle, 0,
                              _cfg.TrolleyInVanX, _cfg.TrolleyInVanY, _cfg.TrolleyInVanZ,
                              0f, 0f, _axisYaw,
                              false, false, false, false, 2, true, 0);
            }
        }

        /// <summary>Re-applies every attachment at the numbers in the settings now. See Options.Refit.</summary>
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
        /// Everything let go of, in the order that leaves nothing floating.
        ///
        /// THE BODY IS DETACHED BEFORE THE TROLLEY IS DELETED, or it keeps an attachment to
        /// something that no longer exists and hangs in the air for the rest of the session.
        /// </summary>
        public void Release()
        {
            try
            {
                if (Crew.There(_load)) Function.Call(Hash.DETACH_ENTITY, _load.Handle, true, true);
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
            _yawFix = 0f;
            _zFix = 0f;
            _squared = false;
        }
    }
}
