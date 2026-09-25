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

        /// <summary>
        /// The height of the road at a spot, asked of the world rather than inferred.
        ///
        /// EVERY FLOATING-PROP BUG IN THIS MOD HAS BEEN A BORROWED Z. The trolley took the
        /// body's height, and bodies lie against kerbs; then it took the medic's height, and a
        /// medic mid-animation is not reliably stood on the floor. Both are inferences about
        /// where the ground is, made from something that is only usually on it.
        ///
        /// GET_GROUND_Z_FOR_3D_COORD is the world's own answer, and it is asked from a metre up
        /// so the probe starts above the surface rather than inside whatever is lying on it.
        /// The fallback is the caller's guess, which is no worse than what it had before.
        /// </summary>
        public static float Ground(Vector3 at, float fallback)
        {
            try
            {
                float z;

                if (World.GetGroundHeight(new Vector3(at.X, at.Y, at.Z + 1f), out z,
                                          GetGroundHeightMode.Normal) &&
                    Math.Abs(z - fallback) < 8f)
                {
                    return z;
                }
            }
            catch
            {
                // The fallback below.
            }

            return fallback;
        }

        /// <summary>SKEL_Head and SKEL_Pelvis, by bone id.</summary>
        private const int HeadBone = 31086;
        private const int PelvisBone = 11816;

        /// <summary>
        /// Where somebody's pelvis actually is -- the ragdoll's, not the capsule's.
        ///
        /// A DEAD PED'S POSITION IS NOT WHERE HE IS LYING. The entity position follows the root,
        /// and the root of a ragdoll is somewhere near the pelvis but not reliably on it; the
        /// capsule's heading, meanwhile, is whatever he was facing when he was hit, and bears no
        /// relation to which way he fell. Posing him from either of those is how a body flips
        /// round the moment the crew kneel. The bones are where the body visibly is.
        /// </summary>
        public static bool Pelvis(Ped who, out Vector3 at)
        {
            at = Vector3.Zero;

            try
            {
                if (!There(who)) return false;

                at = Function.Call<Vector3>(Hash.GET_PED_BONE_COORDS, who.Handle, PelvisBone, 0f, 0f, 0f);

                return at != Vector3.Zero;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Which way he is lying: the heading from his pelvis to his head, on the ground.
        ///
        /// NaN when it cannot be told, which callers read as "leave the heading alone".
        /// </summary>
        public static float Lying(Ped who)
        {
            try
            {
                if (!There(who)) return float.NaN;

                var head = Function.Call<Vector3>(Hash.GET_PED_BONE_COORDS, who.Handle, HeadBone, 0f, 0f, 0f);
                var pelvis = Function.Call<Vector3>(Hash.GET_PED_BONE_COORDS, who.Handle, PelvisBone, 0f, 0f, 0f);

                var axis = Motion.Flat(head - pelvis);

                // Under twenty centimetres flat is a man sitting up or a bone read that failed;
                // either way there is no lying direction to speak of.
                if (axis.Length() < 0.2f) return float.NaN;

                return Motion.HeadingOf(axis);
            }
            catch
            {
                return float.NaN;
            }
        }

        /// <summary>
        /// Stops something colliding, or lets it again.
        ///
        /// A body carried through the air onto a trolley has to pass through the trolley's rail
        /// on the way, and a trolley pushed up to an ambulance has to be allowed to overlap its
        /// bumper for a frame. Collision in either case is the physics resolving an overlap by
        /// throwing one of them -- which, with a frozen trolley and a van, is how the van was
        /// being launched out of the world.
        /// </summary>
        public static void Solid(Entity what, bool solid)
        {
            try
            {
                if (!There(what)) return;

                Function.Call(Hash.SET_ENTITY_COLLISION, what.Handle, solid, solid);
            }
            catch
            {
                // It stays as it was.
            }
        }

        /// <summary>A point given in an entity's own frame, as a position in the world.</summary>
        public static Vector3 Offset(Entity from, float x, float y, float z)
        {
            try
            {
                if (!There(from)) return Vector3.Zero;

                return Function.Call<Vector3>(Hash.GET_OFFSET_FROM_ENTITY_IN_WORLD_COORDS,
                                              from.Handle, x, y, z);
            }
            catch
            {
                return Vector3.Zero;
            }
        }

        /// <summary>
        /// Walks somebody to a spot, round whatever is in the way, and turns him at the end.
        ///
        /// NAVMESH, NOT A STRAIGHT LINE. Every walk in the scene used TASK_GO_STRAIGHT_TO_COORD,
        /// which does exactly what it says: it walks in a straight line and stops against the
        /// first thing it meets. That thing was an open ambulance door -- the rear doors were
        /// opened early and swing out a metre either side, and a man sent straight at a point
        /// behind the bumper walked into one and stood there pushing against it until his
        /// timeout. The navmesh task paths round doors, the trolley, the bag and each other.
        ///
        /// Flags 2 + 512: slide to the exact coordinate and take up the heading at the end, and
        /// stop exactly there rather than within a radius. The trailing 40000 is what every
        /// script in the game passes and nobody has documented.
        /// </summary>
        public static void WalkTo(Ped who, Vector3 at, float heading, float speed, int ms)
        {
            try
            {
                if (!Alive(who)) return;

                var z = Ground(at, at.Z);

                Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD_ADVANCED, who.Handle,
                              at.X, at.Y, z, speed, ms, 0.25f, 2 | 512, heading, 40000f);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not send him walking: " + ex.Message);
            }
        }

        /// <summary>The same, without caring which way he ends up facing.</summary>
        public static void WalkTo(Ped who, Vector3 at, float speed, int ms)
        {
            try
            {
                if (!Alive(who)) return;

                var z = Ground(at, at.Z);

                Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD, who.Handle,
                              at.X, at.Y, z, speed, ms, 0.4f, 0, 40000f);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not send him walking: " + ex.Message);
            }
        }

        /// <summary>
        /// How far something is above the road, negative when it is under it.
        ///
        /// The check that catches a scene placed below the world. It should never fire now
        /// that scene origins sit on the ground -- and if it ever does, the log says which step
        /// and by how much, which is the whole of the diagnosis.
        /// </summary>
        public static float Above(Entity what)
        {
            try
            {
                if (!There(what)) return 0f;

                var at = what.Position;
                var ground = Ground(at, at.Z);

                return at.Z - ground;
            }
            catch
            {
                return 0f;
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
