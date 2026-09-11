using System;
using GTA;
using GTA.Math;
using GTA.Native;
using Flatline.Core;

namespace Flatline.Scene
{
    /// <summary>
    /// The bag. Carried over, put down beside him, picked up again on the way out.
    ///
    /// A CREW WITH NOTHING IN THEIR HANDS IS TWO MEN WHO HAPPEN TO BE STANDING NEAR A BODY. The
    /// bag is the one prop that says what they are for before either of them has knelt, and
    /// the game ships it -- prop_med_bag_01, the red one that sits in the back of every
    /// ambulance interior -- so it costs nothing to bring.
    ///
    /// ATTACHED TO THE HAND ON THE WAY OVER, SET DOWN ON THE GROUND ONCE THERE. Welded to the
    /// wrist bone it swings with his arm, which is what a carried bag does; on the road beside
    /// the body it is out of the way of the scene and in the eyeline of anybody watching it.
    /// The offsets are small and not exposed, because the bag's origin turned out to be close
    /// enough to its handle that the defaults read as held.
    /// </summary>
    internal sealed class Kit
    {
        /// <summary>The bag, and the other one if the first will not load.</summary>
        private static readonly string[] Bags = { "prop_med_bag_01", "prop_med_bag_01b" };

        /// <summary>SKEL_R_Hand. The bone id every carry mod attaches to.</summary>
        private const int RightHand = 57005;

        private Prop _bag;
        private Ped _holder;

        public bool There
        {
            get { return _bag != null && _bag.Exists(); }
        }

        /// <summary>Into his hand. Whether it went.</summary>
        public bool Bring(Ped who)
        {
            if (!Crew.Alive(who)) return false;

            try
            {
                if (!There)
                {
                    foreach (var name in Bags)
                    {
                        var model = Crew.Load(name, 800);
                        if (model == null) continue;

                        _bag = World.CreateProp(model.Value, who.Position, true, false);
                        model.Value.MarkAsNoLongerNeeded();

                        if (_bag != null && _bag.Exists()) break;

                        _bag = null;
                    }

                    if (!There) { Log.Debug("No medic bag would load."); return false; }

                    _bag.IsPersistent = true;
                }

                var bone = Function.Call<int>(Hash.GET_PED_BONE_INDEX, who.Handle, RightHand);

                Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY,
                              _bag.Handle, who.Handle, bone,
                              0.06f, 0.0f, -0.02f,
                              0f, -90f, 0f,
                              false, false, false, false, 2, true, 0);

                _holder = who;
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not hand him the bag: " + ex.Message);
                return false;
            }
        }

        /// <summary>Out of his hand and onto the road at a spot.</summary>
        public void SetDown(Vector3 at)
        {
            if (!There) return;

            try
            {
                Function.Call(Hash.DETACH_ENTITY, _bag.Handle, true, true);
                Function.Call(Hash.SET_ENTITY_COORDS, _bag.Handle, at.X, at.Y, at.Z + 0.2f,
                              false, false, false, false);
                Function.Call(Hash.PLACE_OBJECT_ON_GROUND_PROPERLY, _bag.Handle);

                _holder = null;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not set the bag down: " + ex.Message);
            }
        }

        /// <summary>Gone. The engine would clean a loose prop up eventually; this does not wait.</summary>
        public void Release()
        {
            try
            {
                if (There)
                {
                    if (Function.Call<bool>(Hash.IS_ENTITY_ATTACHED, _bag.Handle))
                    {
                        Function.Call(Hash.DETACH_ENTITY, _bag.Handle, true, true);
                    }

                    _bag.IsPersistent = false;
                    _bag.Delete();
                }
            }
            catch
            {
                // Gone already.
            }

            _bag = null;
            _holder = null;
        }
    }
}
