using System;
using GTA;
using GTA.Native;

namespace CodeThree.Core
{
    /// <summary>
    /// Playing a clip on somebody, and never hanging the game to do it.
    ///
    /// ANIMATION DICTIONARY NAMES ARE THE MOST FRAGILE STRINGS IN A GTA MOD. They are not
    /// checked by the compiler, they differ between the two editions in places, and a wrong one
    /// fails silently -- REQUEST_ANIM_DICT on a name that does not exist never loads and never
    /// errors. The usual way this is written is a `while (!HAS_ANIM_DICT_LOADED) Yield();` loop,
    /// which on a wrong name is an infinite loop inside a script tick: the game hangs, with no
    /// log line, and it looks like a crash.
    ///
    /// So every clip in this mod goes through here, everything is time-boxed, and a dictionary
    /// that does not arrive is skipped. The cost of a wrong name is then a paramedic who kneels
    /// instead of working -- cosmetic, findable in the log, and not a hang.
    ///
    /// EVERY NAME BELOW WAS CHECKED AGAINST THE GAME'S OWN DUMP before it was written here, per
    /// the rule in Hoodrich's ANIMS.md -- 20,179 dictionaries and 269,414 clips, from
    /// DurtyFree/gta-v-data-dumps. The scenarios were checked against scenariosCompact.json and
    /// the movement clipsets against movementClipsetsCompact.json from the same place. That
    /// check is the entire point: this codebase once shipped twenty-five bad pairs in a single
    /// file and nobody could tell, because the failure is silent by design.
    /// </summary>
    internal static class Anim
    {
        /// <summary>Long enough for a real load off disk, short enough not to be a stall.</summary>
        private const int LoadWaitMs = 900;

        /// <summary>Loads a dictionary, or gives up. True when it is ready to play from.</summary>
        public static bool Ready(string dict)
        {
            try
            {
                if (string.IsNullOrEmpty(dict)) return false;

                if (Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict)) return true;

                Function.Call(Hash.REQUEST_ANIM_DICT, dict);

                var until = Game.GameTime + LoadWaitMs;

                while (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict) &&
                       Game.GameTime < until)
                {
                    Script.Yield();
                }

                var ok = Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict);

                if (!ok) Log.Warn("Animation dictionary did not load: " + dict);

                return ok;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not request " + dict + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Plays a clip on one ped, on its own. For anything paired, see Sync.
        ///
        /// THE LAST THREE ARGUMENTS ARE NOT POSITION LOCKS, whatever every community header
        /// calls them. They are bPhaseControlled, IkFlags (an int) and bAllowOverrideCloneUpdate
        /// -- see ANIMS.md, where passing true,true,true is documented as leaving the clip
        /// parked on frame 0 with leg IK disabled. false, 0, false.
        ///
        /// ALREADY PLAYING IS NOT A REASON TO PLAY IT AGAIN. Callers sit in per-tick loops and
        /// call unconditionally; TASK_PLAY_ANIM does not check whether the clip is running, it
        /// RESTARTS it, and a pose restarted forty times a second is a ped juddering on the spot.
        /// </summary>
        public static bool Play(Ped who, string dict, string clip, int flags, int ms = -1,
                                float blendIn = 8f)
        {
            try
            {
                if (!Crew.Alive(who)) return false;

                if (IsPlaying(who, dict, clip)) return true;

                if (!Ready(dict)) return false;

                // THE BLEND-IN IS HOW LONG THE CHANGE OF POSE TAKES: one over the number, in
                // seconds. 8 is an eighth of a second, which is right for somebody starting a
                // new action and wrong for a body being laid down, which should settle over half
                // a second or more rather than arrive in one.
                Function.Call(Hash.TASK_PLAY_ANIM, who.Handle, dict, clip,
                              blendIn, -8f, ms, flags, 0f, false, 0, false);

                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not play " + dict + "/" + clip + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Asks for every dictionary a scene will use, without waiting for any of them.
        ///
        /// A LOAD MID-SCENE IS A PATIENT STANDING UP. Ready() yields until a dictionary arrives,
        /// and a resurrected man with no task yet is a man standing in his idle pose -- so the
        /// first time a dictionary was needed after the resurrection, he visibly got up for as
        /// long as the disk took. Asked for at dispatch, they are in memory a minute before
        /// anybody reaches for them and Ready never has to wait.
        /// </summary>
        public static void Preload(params string[] dicts)
        {
            foreach (var dict in dicts)
            {
                try
                {
                    if (!string.IsNullOrEmpty(dict) &&
                        !Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict))
                    {
                        Function.Call(Hash.REQUEST_ANIM_DICT, dict);
                    }
                }
                catch
                {
                    // Ready() finds out later, with a time box.
                }
            }
        }

        /// <summary>Every dictionary the call-out uses, for Preload.</summary>
        public static readonly string[] Scene =
        {
            CprMedic, CprVictim, Rescue, GetUpDict, DeadDict, DeadFallbackDict, LiftDict, PushDict,
            FleeDict, LookDict,
        };

        /// <summary>Whether this ped is already running this exact clip. Slot 3 covers both.</summary>
        public static bool IsPlaying(Ped who, string dict, string clip)
        {
            try
            {
                if (!Crew.Alive(who)) return false;

                return Function.Call<bool>(Hash.IS_ENTITY_PLAYING_ANIM, who.Handle, dict, clip, 3);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Stops a clip without clearing the rest of their tasks.</summary>
        public static void Stop(Ped who, string dict, string clip)
        {
            try
            {
                if (!Crew.Alive(who)) return;

                Function.Call(Hash.STOP_ANIM_TASK, who.Handle, dict, clip, 3f);
            }
            catch
            {
                // Nothing worth saying. The clip ends when the ped is re-tasked anyway.
            }
        }

        /// <summary>
        /// Starts one of the game's own scenarios where he stands.
        ///
        /// A SCENARIO IS A CLIP SEQUENCE THE ENGINE RUNS FOR YOU: enter, a base loop, idle
        /// variations picked at random, and an exit when he is re-tasked. The three medic ones
        /// are literally what the vanilla paramedics play at a body, which is the texture this
        /// mod wants for the second man -- the difference is that here somebody is actually
        /// doing something beside him.
        ///
        /// The last argument plays the enter clip rather than snapping into the base pose.
        /// </summary>
        public static bool Scenario(Ped who, string name)
        {
            try
            {
                if (!Crew.Alive(who) || string.IsNullOrEmpty(name)) return false;

                Function.Call(Hash.TASK_START_SCENARIO_IN_PLACE, who.Handle, name, 0, true);

                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not start scenario " + name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Changes how somebody walks, for good.
        ///
        /// A MOVEMENT CLIPSET IS NOT A CLIP. It replaces the ped's whole locomotion set -- idle,
        /// walk, run, turns, stops -- so a man given the injured one limps everywhere he goes
        /// from then on, without anything re-asking every tick. The right thing for somebody
        /// who has just been brought back from a beating and handed back to the city.
        ///
        /// Time-boxed like the dictionaries, for the same reason.
        /// </summary>
        public static bool Clipset(Ped who, string name)
        {
            try
            {
                if (!Crew.Alive(who) || string.IsNullOrEmpty(name)) return false;

                if (!Function.Call<bool>(Hash.HAS_CLIP_SET_LOADED, name))
                {
                    Function.Call(Hash.REQUEST_CLIP_SET, name);

                    var until = Game.GameTime + LoadWaitMs;

                    while (!Function.Call<bool>(Hash.HAS_CLIP_SET_LOADED, name) &&
                           Game.GameTime < until)
                    {
                        Script.Yield();
                    }
                }

                if (!Function.Call<bool>(Hash.HAS_CLIP_SET_LOADED, name))
                {
                    Log.Warn("Movement clipset did not load: " + name);
                    return false;
                }

                Function.Call(Hash.SET_PED_MOVEMENT_CLIPSET, who.Handle, name, 0.4f);

                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not set clipset " + name + ": " + ex.Message);
                return false;
            }
        }

        // ---- flags ---------------------------------------------------------------

        /// <summary>Loop, and hold the ped where he is. See the table in ANIMS.md.</summary>
        public const int Loop = 1;

        /// <summary>Play once and stay in the final pose rather than snapping out of it.</summary>
        public const int Hold = 2;

        // ---- the CPR, in matched pairs ------------------------------------------
        //
        // mini@cpr is the game's own CPR minigame. char_a is the one kneeling and pushing,
        // char_b is the one on the ground, and every clip name exists in BOTH dictionaries so
        // that a synchronised scene can play the two halves of one moment against one origin.
        // The first version of this mod used two of these seven clips on one participant. This
        // uses all seven on both.
        //
        // "kol" is the kneeling pose the sequence passes through between rounds -- down on one
        // leg beside him, hands off his chest -- and the transitions in and out of it are what
        // make the compressions read as rounds rather than as one continuous pump.

        public const string CprMedic = "mini@cpr@char_a@cpr_str";
        public const string CprVictim = "mini@cpr@char_b@cpr_str";

        /// <summary>Dropping to a knee beside him. One shot.</summary>
        public const string Kneel = "cpr_kol";

        /// <summary>Kneeling, looking him over. Loops.</summary>
        public const string KneelIdle = "cpr_kol_idle";

        /// <summary>Leaning in, hands to the sternum. One shot.</summary>
        public const string KneelToCpr = "cpr_kol_to_cpr";

        /// <summary>The compressions. Loops. His chest goes with them.</summary>
        public const string Pump = "cpr_pumpchest";

        /// <summary>Sitting back on his heels between rounds. One shot.</summary>
        public const string CprToKneel = "cpr_cpr_to_kol";

        /// <summary>It took. He comes up; the victim ends sat up. One shot, held.</summary>
        public const string Worked = "cpr_success";

        /// <summary>It did not. One shot, held.</summary>
        public const string Failed = "cpr_fail";

        // ---- getting him to his feet --------------------------------------------
        //
        // random@crash_rescue is the roadside random event where you pull somebody out of a
        // wreck. Its help_victim_up pair is one person hauling another to their feet, which is
        // the exact beat that follows a successful resuscitation and that mini@cpr has no clip
        // for: cpr_success leaves him sat on the road, and this is how he stops being sat there.

        public const string Rescue = "random@crash_rescue@help_victim_up";
        public const string RescueMedic = "helping_victim_to_feet_player";
        public const string RescueVictim = "helping_victim_to_feet_victim";

        /// <summary>The fallback if the paired scene will not start: he gets up on his own.</summary>
        public const string GetUpDict = "get_up@directional@movement@from_seated@injured";
        public const string GetUpClip = "getup_l_0";

        // ---- lying dead --------------------------------------------------------
        //
        // THE WHOLE TROLLEY PROBLEM, SOLVED BY ONE DICTIONARY. A corpse is a ragdoll and cannot
        // be posed, so the first version welded him to the trolley in whatever shape he died
        // and hoped. But once he has been through the scene he is alive and animatable, and the
        // game ships eight lying-dead poses -- so he is laid out flat on his back, held there,
        // and the trolley offsets in the ini are tuned against one pose that never varies
        // rather than against however each man happened to fall.

        // ON A SLAB, NOT ON A ROAD. dead_a was one of eight unlabelled death poses and was never
        // checked to be flat on the back -- it was picked because the dictionary was called
        // "dead". The morgue-table set from the casino heist has a clip for exactly a body
        // laid out flat on a surface: ko_back, knocked out, on his back, on the table. That is a
        // man on a gurney with the gurney removed. The old pose stays as the fallback.
        public const string DeadDict = "anim@gangops@morgue@table@";
        public const string DeadPose = "ko_back";

        public const string DeadFallbackDict = "dead";
        public const string DeadFallbackPose = "dead_a";

        // ---- the second man ----------------------------------------------------
        //
        // The vanilla paramedic's own scenarios, by name, checked against scenariosCompact.json.
        // TEND_TO_DEAD is kneeling over him, working; TIME_OF_DEATH is stood up with the
        // clipboard, writing it down. The clips under them are the amb@medic@standing@* set.

        public const string TendScenario = "CODE_HUMAN_MEDIC_TEND_TO_DEAD";
        public const string TimeOfDeathScenario = "CODE_HUMAN_MEDIC_TIME_OF_DEATH";

        /// <summary>The scenario's own flee exit, played straight when the patient is shot.</summary>
        public const string FleeDict = "amb@medic@standing@tendtodead@exit";
        public const string FleeClip = "exit_flee";

        /// <summary>Kneeling and looking, for a man who has nothing else to do at a scene.</summary>
        public const string LookDict = "amb@code_human_police_investigate@idle_a";
        public const string LookClip = "idle_a";

        // ---- lifting him onto the trolley ---------------------------------------
        //
        // combat@drag_ped@ IS THE GAME'S OWN BODY LIFT, and it is a matched pair: every clip
        // exists twice, once suffixed _plyr for the person doing the lifting and once _ped for
        // the person being lifted. That is the same shape as mini@cpr and it goes through a
        // synchronised scene for the same reason -- the arms go under the shoulders because
        // both skeletons are placed from one origin by the animation data.
        //
        // THERE IS NO TWO-MAN LIFT IN THIS GAME. The whole dump was searched: combat@drag_ped@
        // is one rescuer and one casualty, and nothing anywhere has two people lifting a third.
        // So the driver lifts and the mate works beside him, which is what the clips allow
        // rather than what would be ideal.
        //
        // FROM BEHIND, NOT FROM THE FRONT. The front variant was chosen because the medic has
        // been kneeling at the man's chest and that is where he already is -- and it lifts him
        // face to face, which is how you help somebody up, not how you get a casualty onto a
        // stretcher. The back variant is the real one: in behind the head, arms under the
        // shoulders, lift. He walks round to his mark for it like any other; the mark is
        // asked of the clip, so "behind" is wherever this clip says behind is.

        public const string LiftDict = "combat@drag_ped@";
        public const string LiftMedic = "injured_pickup_back_plyr";
        public const string LiftBody = "injured_pickup_back_ped";

        // ---- wheeling it -------------------------------------------------------

        /// <summary>
        /// Hands out in front at handle height, leaning into it.
        ///
        /// THE ONLY PUSHING POSE THE GAME HAS, and it belongs to a tramp with a shopping trolley
        /// -- PROP_HUMAN_BUM_SHOPPING_CART, whose clips are these. At the grip it is a man with
        /// both hands on a bar in front of him at waist height, which is a gurney as readily as
        /// a shopping trolley.
        ///
        /// THERE IS NO PUSHING WALK. The movement clipsets were searched for one and the game
        /// has none -- nothing named cart, carry, push, box or trolley. So this cannot be a
        /// locomotion set, and instead goes on as an UPPER BODY SECONDARY clip over an ordinary
        /// walk: his legs do the walking the task gave him and his arms hold the bar. See Push.
        /// </summary>
        public const string PushDict = "amb@prop_human_bum_shopping_cart@male@base";
        public const string PushClip = "base";

        /// <summary>
        /// Loop, upper body only, secondary slot.
        ///
        /// 1 + 16 + 32. The upper-body bit is what leaves the legs to the walk underneath, and
        /// the secondary bit is what stops it cancelling the walk task outright. Five0 Patrol
        /// has the same number written down beside its hands-up pose, for the same reason.
        /// </summary>
        public const int Push = 49;

        /// <summary>
        /// The same, plus AF_NOT_INTERRUPTABLE (8), for when the walk task refuses the first.
        ///
        /// The log said the pose was not taking: the navmesh walk task can clear a secondary
        /// clip the moment it starts a new leg of the route, and re-issuing it every tick then
        /// just restarts it on frame nought forever. Not-interruptable is the bit that tells the
        /// movement task to leave it alone.
        /// </summary>
        public const int PushHard = 57;

        // ---- how he walks away -------------------------------------------------

        public const string LimpMale = "move_m@injured";
        public const string LimpFemale = "move_f@injured";
    }
}
