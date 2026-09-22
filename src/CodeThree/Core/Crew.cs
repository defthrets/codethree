using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;

namespace CodeThree.Core
{
    /// <summary>
    /// The small things every other file here needs: loading a model without hanging, asking
    /// whether a handle is still worth talking to, and finding a road to come in from.
    ///
    /// The equivalent of Five0 Patrol's Cops, cut down to what an ambulance actually needs.
    /// Copied rather than shared, because the two mods are separate assemblies that must each
    /// work with the other absent -- which is the same reason Splash is duplicated across the
    /// set rather than living in a library nobody would be able to load.
    /// </summary>
    internal static class Crew
    {
        /// <summary>What turns up, and who is in it. Both checked against the game's own list.</summary>
        public const string Van = "ambulance";
        public const string Medic = "s_m_m_paramedic_01";

        /// <summary>
        /// Loads a model, or gives up.
        ///
        /// TIME-BOXED, because the usual `while (!IsLoaded) Yield();` is an infinite loop
        /// inside a script tick for any name the game does not have -- and the whole reason
        /// this mod checks names against the dumps is that a wrong one is otherwise silent.
        /// A model that will not arrive is a call-out that does not happen, which is findable
        /// in the log and is not a hang.
        /// </summary>
        public static Model? Load(string name, int waitMs = 1200)
        {
            try
            {
                var model = new Model(name);
                if (!model.IsValid) return null;

                model.Request();

                var until = Game.GameTime + waitMs;
                while (!model.IsLoaded && Game.GameTime < until) Script.Yield();

                return model.IsLoaded ? model : (Model?)null;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not load " + name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>Alive, existing, and safe to give an order to.</summary>
        public static bool Alive(Entity who)
        {
            try { return who != null && who.Exists() && !who.IsDead; }
            catch { return false; }
        }

        /// <summary>
        /// Existing, whatever state it is in.
        ///
        /// SEPARATE FROM Alive AND THE DIFFERENCE IS THE WHOLE MOD. Everything this file does
        /// is done to a body, and a body fails Alive by definition -- so the corpse checks have
        /// to ask a different question from the crew checks, and mixing them up means either
        /// tasking a dead man or dropping the one entity the scene is about.
        /// </summary>
        public static bool There(Entity what)
        {
            try { return what != null && what.Exists(); }
            catch { return false; }
        }

        /// <summary>
        /// Somebody on the crew, put straight into a seat.
        ///
        /// CREATE_PED_INSIDE_VEHICLE rather than spawning on the pavement and asking them to
        /// walk over and get in. The second is a task that can fail, in traffic, out of sight,
        /// and what it leaves behind is an ambulance with nobody driving it.
        /// </summary>
        public static Ped Aboard(Vehicle van, int seat)
        {
            try
            {
                if (!Alive(van)) return null;

                var model = Load(Medic);
                if (model == null) return null;

                var handle = Function.Call<int>(Hash.CREATE_PED_INSIDE_VEHICLE,
                                                van.Handle, 4, model.Value.Hash, seat, true, false);

                model.Value.MarkAsNoLongerNeeded();

                var ped = Entity.FromHandle(handle) as Ped;
                if (!Alive(ped)) return null;

                ped.IsPersistent = true;

                // NOT AT ANYBODY, EVER. Paramedics are scenery with a job, and a medic who
                // joins in is the one thing that would make this worse than not having it.
                // Five0 Patrol's Medics reached the same conclusion in the same words.
                Function.Call(Hash.SET_PED_AS_ENEMY, ped.Handle, false);
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, ped.Handle, true);
                Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, ped.Handle, 0, false);
                Function.Call(Hash.SET_PED_CAN_BE_TARGETTED, ped.Handle, false);

                return ped;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not put a paramedic in: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// The engine's own answer, for picking the right injured walk.
        ///
        /// IS_PED_MALE rather than a guess off the model name, for the reason Five0 Patrol's
        /// Roster gives for the same call: it stays right if a model is ever swapped.
        /// </summary>
        public static bool IsMale(Ped who)
        {
            try { return !There(who) || Function.Call<bool>(Hash.IS_PED_MALE, who.Handle); }
            catch { return true; }
        }

        /// <summary>
        /// Where a ped's health stops being health.
        ///
        /// A PED IS DEAD AT A HUNDRED, NOT AT NOUGHT. Health runs 0 to 200 and the engine treats
        /// anything at or under 100 as fatally injured -- the bar the player sees is the top
        /// half, 100 to 200, mapped to empty and full. An NPC dropped to 40 does not lie there
        /// hurt; the engine kills him on the spot, because that is what an injured NPC does
        /// unless SET_PED_DIES_WHEN_INJURED has been turned off for him.
        ///
        /// THIS MOD SHIPPED WITH THAT BUG AND THE LOG CAUGHT IT. 0.2.0 brought the patient back
        /// into arrest at a fifth of his health -- 40 -- and every single call-out in a week of
        /// play ended six seconds after the crew reached him with "the patient was killed",
        /// because he was: by the engine, on the tick after the resurrection, for having 40
        /// health. The flee guard then did exactly what it was written to do. Hoodrich's dog has
        /// the same lesson written beside it and it was read past.
        ///
        /// So health is never set here as a fraction of the maximum. It is set as a fraction of
        /// the part above the floor.
        /// </summary>
        public const int Floor = 100;

        /// <summary>
        /// Puts a ped at some fraction of the health that is actually his to lose.
        ///
        /// 0 is on the floor -- alive, and one scratch from not. 1 is full. Anything in between
        /// is what the player's own bar would read at that fraction.
        /// </summary>
        public static void Hurt(Ped who, float fraction)
        {
            try
            {
                if (!There(who)) return;

                var max = Function.Call<int>(Hash.GET_PED_MAX_HEALTH, who.Handle);
                if (max <= Floor) max = 200;

                if (fraction < 0f) fraction = 0f;
                if (fraction > 1f) fraction = 1f;

                var health = Floor + (int)((max - Floor) * fraction);

                // NEVER ON THE LINE. A hundred exactly is dead on some code paths and dying on
                // others, and neither is what "just alive" is meant to mean.
                if (health <= Floor) health = Floor + 5;

                Function.Call(Hash.SET_ENTITY_HEALTH, who.Handle, health);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not set health: " + ex.Message);
            }
        }

        /// <summary>
        /// Keeps an entity from being tidied away by the engine, and keeps on keeping it.
        ///
        /// IsPersistent ALONE WAS NOT ENOUGH AND THE LOG PROVED IT. The body is marked persistent
        /// the moment an ambulance is dispatched, and it still vanished -- twice, both times
        /// within a frame of the trolley being spawned, with "the body had gone" logged 25ms
        /// after "the crew brought a trolley out".
        ///
        /// RESURRECT_PED IS WHY. Hoodrich's note beside its own call says a resurrected ped comes
        /// back blank -- out of its group, with none of its flags -- and the persistence flag is
        /// one of the flags. So from the moment the crew reach him and bring him into arrest, the
        /// patient is an ordinary ambient ped again as far as the population manager is
        /// concerned, and the next time anything yields the engine is free to reclaim him. Model
        /// loading yields. That is the whole bug.
        ///
        /// SET_ENTITY_AS_MISSION_ENTITY IS THE STRONGER FORM. IsPersistent sets the same flag but
        /// the script-owned variant, with its two arguments, is the one that also takes the
        /// entity out of the ambient population's budget -- which is what "do not reclaim this"
        /// actually means. Both are set, because they are cheap and this has now cost two
        /// evenings.
        /// </summary>
        public static void Hold(Entity what)
        {
            try
            {
                if (!There(what)) return;

                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, what.Handle, true, true);
                what.IsPersistent = true;
            }
            catch
            {
                // Gone already, which the caller finds out from its own checks.
            }
        }

        /// <summary>
        /// How big a model is, and where its origin sits inside it.
        ///
        /// THE END OF THE GUESSING. Every attachment offset in this mod was a considered guess,
        /// because a prop's origin is wherever the artist put it and there was said to be no way
        /// to find that out from outside the running game. There is: GET_MODEL_DIMENSIONS hands
        /// back the bounding box in model space, and the box says exactly where the origin is
        /// relative to the geometry. A trolley whose min.Z is -0.35 has its origin 35cm above its
        /// wheels, so to stand it on the ground you offset it up by 0.35 -- measured, not
        /// guessed, and right on any model including one a player has replaced.
        /// </summary>
        public static bool Measure(string name, out Vector3 min, out Vector3 max)
        {
            min = Vector3.Zero;
            max = Vector3.Zero;

            try
            {
                var model = new Model(name);
                if (!model.IsValid) return false;

                var lo = new OutputArgument();
                var hi = new OutputArgument();

                Function.Call(Hash.GET_MODEL_DIMENSIONS, model.Hash, lo, hi);

                min = lo.GetResult<Vector3>();
                max = hi.GetResult<Vector3>();

                return max.Z - min.Z > 0.01f;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not measure " + name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>Hands a ped or a vehicle back to the game to clean up in its own time.</summary>
        public static void Give(Entity what)
        {
            try
            {
                if (what == null || !what.Exists()) return;

                what.IsPersistent = false;
                what.MarkAsNoLongerNeeded();
            }
            catch
            {
                // Gone already.
            }
        }

        /// <summary>
        /// Somewhere on a road, a street or two away, to come in from.
        ///
        /// Lifted from Five0 Patrol's Medics for the same reason it exists there: a van that
        /// fades in fifteen metres from the body is a van nobody believes drove anywhere.
        /// </summary>
        public static bool Road(Vector3 near, Random rng, float from, float spread, out Vector3 spot)
        {
            spot = Vector3.Zero;

            for (var attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    var angle = rng.NextDouble() * Math.PI * 2d;
                    var dist = from + (float)rng.NextDouble() * spread;

                    var guess = near + new Vector3((float)Math.Cos(angle) * dist,
                                                   (float)Math.Sin(angle) * dist, 0f);

                    var road = World.GetNextPositionOnStreet(guess, true);

                    if (road == Vector3.Zero) continue;
                    if (road.DistanceTo(near) < from * 0.5f) continue;

                    spot = road;
                    return true;
                }
                catch
                {
                    // Next try.
                }
            }

            return false;
        }

        /// <summary>
        /// The peds around a point, dead ones included.
        ///
        /// World.GetNearbyPeds does not promise anything about the dead, so this filters on
        /// existence rather than on life -- the whole watch is looking for corpses.
        /// </summary>
        public static List<Ped> Near(Vector3 where, float range)
        {
            var found = new List<Ped>();

            try
            {
                var all = World.GetNearbyPeds(where, range);
                if (all == null) return found;

                foreach (var ped in all)
                {
                    if (There(ped)) found.Add(ped);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not sweep for peds: " + ex.Message);
            }

            return found;
        }
    }
}
