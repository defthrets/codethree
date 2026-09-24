using System;
using GTA;
using GTA.Native;
using CodeThree.Core;
using CodeThree.Scene;
using CodeThree.UI;

namespace CodeThree
{
    /// <summary>
    /// The one Script subclass, owning tick order for everything.
    ///
    /// WHAT THIS MOD IS. GTA V already dispatches an ambulance to a body. It arrives with its
    /// lights on, two paramedics get out, they walk over, they stand there, they get back in and
    /// they drive away -- and the body is still lying in the road behind them. Whatever those
    /// crews were meant to be for, they are not for that, and the effect of watching it twice is
    /// that you stop looking at ambulances at all.
    ///
    /// Code Three is the rest of that call-out. They kneel and they work on him, and then one of
    /// two things happens depending on what actually killed him: a beating or a bat and he comes
    /// round and walks off, a gun or a car or a fire and they stop, fetch the trolley, load him,
    /// and drive him to the nearest hospital.
    ///
    /// ONE SCRIPT, NOT SEVERAL. SHVDN will happily run a Script subclass per system and it is
    /// tempting because the watch and the call-out are independent -- but they are not
    /// independent about ORDER, and with separate scripts the order is whatever SHVDN happened
    /// to construct them in, which is stable until somebody renames a file. So the order below
    /// is the design. That rule is carried over from Five0 Patrol, where it is written out at
    /// length for the same reason.
    /// </summary>
    public sealed class Main : Script
    {
        private readonly Settings _cfg;

        private readonly Watch _watch;
        private readonly Callout _call;
        private readonly Options _menu;

        /// <summary>
        /// One Random for the whole mod.
        ///
        /// Two of them seeded a millisecond apart on the same tick is the classic way to get two
        /// systems making identical "random" choices all session.
        /// </summary>
        private readonly Random _rng = new Random();

        private bool _parked;

        /// <summary>
        /// GTA's own ambulance department.
        ///
        /// Dispatch service 5. Named rather than written as a bare 5 at the call site, because
        /// a bare 5 in a native call is unreadable and the neighbouring numbers are the police
        /// helicopter and the fire brigade -- both of which this mod has no business touching.
        /// </summary>
        private const int AmbulanceDispatch = 5;

        /// <summary>How often the suppression is re-asserted. See Vanilla().</summary>
        private const int AssertEveryMs = 5000;

        private int _assertedAt;
        private bool _suppressed;

        public Main()
        {
            try
            {
                // Fully qualified. Script exposes an inherited `Settings` property that
                // otherwise wins name resolution over our own type, and it presents as "no
                // argument given for 'filename'" -- a message about a class nobody wrote.
                _cfg = Core.Settings.Load();
                Log.Level = _cfg.Logging;

                _watch = new Watch(_cfg);
                _call = new Callout(_cfg, _rng)
                {
                    Say = _cfg.Announce ? (Action<string>)Screen.Ticker : null,
                };

                // WHAT THE OTHER MODS ASK. Wired here rather than in the API file itself,
                // because Main is the only place that knows these objects exist and the order
                // they were built in -- a reference taken a moment too early is a null that
                // never fixes itself. Same shape as Hoodrich.Api.Block.Wire.
                Api.Medics.Wire(
                    () => !_parked && _cfg.Enabled,
                    () => _call.Out,
                    () => new[] { _call.At.X, _call.At.Y, _call.At.Z },
                    xyz => xyz != null && xyz.Length >= 3 &&
                           _call.Still(new GTA.Math.Vector3(xyz[0], xyz[1], xyz[2])));

                _menu = new Options(_cfg)
                {
                    Say = Screen.Ticker,

                    // THE FIT ROWS ONLY MEAN ANYTHING WITH THIS WIRED. An attachment offset is
                    // copied into the engine at the moment of attaching and keeps no link back
                    // to the number it came from, so moving a slider does nothing to a body
                    // already strapped to a trolley. See Callout.Refit.
                    Refit = () => _call.Refit(),

                    // And what the crew are doing, in the corner of the panel, so the fit can be
                    // aimed without closing the menu to go and look.
                    Scene = () => _call.Out ? _call.State : null,
                    Hopeful = () => _call.Workable,

                    Stage = Stage,
                };

                // THE SEAL FOLDER IS DELIBERATELY NOT SET HERE. It is tempting -- Paths.Icons
                // is right there and knows the answer -- and it is the wrong thing to do. The
                // mark on the load row is shared by the whole set, and Splash gives the claim
                // to the first mod with art it can actually FIND, precisely so that a mod which
                // cannot find its own does not take the claim and then draw nothing. Setting the
                // folder from here asserts we have the art without looking, so an install
                // missing the two PNGs would silently cost every other mod the seal as well.
                // Splash.FindIcons checks the file exists. Let it.

                Interval = 0;
                Tick += OnTick;
                Aborted += OnAborted;

                // THE MENU TAKES KEYS FROM THE EVENT, NOT FROM THE TICK. A menu wants Escape and
                // the arrows as arrows, which the game has no controls for, and KeyDown gives
                // real edge detection for free -- where polling needs a frame-perfect tick, and
                // getting that wrong is a menu that ignores most of what you press.
                KeyDown += OnKey;

                Log.Info(Build.Name + " " + Build.Version + " by " + Build.By + " loaded. " +
                         "Resuscitation " + OnOff(_cfg.Resuscitate) +
                         ", hospital runs " + OnOff(_cfg.TakeToHospital) + ".");
            }
            catch (Exception ex)
            {
                _parked = true;
                Log.Error("Failed to start; disabled for this session.", ex);
            }
        }

        private static string OnOff(bool b) => b ? "on" : "off";

        // ---- the tick ----------------------------------------------------------

        private void OnTick(object sender, EventArgs e)
        {
            // The set's mark, for a few seconds after load. Bows out on its own and costs a
            // comparison thereafter; see UI.Splash.
            Splash.Render();

            if (_parked || _cfg == null) return;

            try
            {
                // SWITCHED OFF IS NOT THE SAME AS GONE, and an early return here would take the
                // menu with it. The draw that puts the menu on screen lives in the finally at
                // the bottom of this method, and a return never reaches a finally -- so turning
                // the mod off would hide the one row you need to turn it back on, and leave the
                // arrow keys steering while an invisible menu ate them. The systems stand down;
                // the menu does not. Five0 Patrol learned this the hard way and wrote it down.
                if (!_cfg.Enabled)
                {
                    // SWITCHED OFF MID-SESSION STILL HANDS BACK. A player who turns the mod off
                    // in the ini and presses Insert should not be left with a van and two crew
                    // standing persistent in a street, and should get the game's own ambulances
                    // back rather than neither mod's.
                    if (_call.Out) _call.Done();
                    Vanilla(true);
                    return;
                }

                Vanilla(!_cfg.SuppressVanillaAmbulances);

                // 1. WHO HAS JUST DIED. Reads the world and changes nothing in it.
                _watch.Update();

                // 2. AND WHETHER ANYBODY IS FREE TO GO. Before the call-out is ticked, so a
                //    death noticed this frame is answered on the same one -- the sweep already
                //    costs up to three-quarters of a second of latency and there is no reason
                //    to add a frame to it.
                if (!_call.Out) Send();

                // 3. THE SCENE ITSELF, LAST AND EVERY FRAME. Not on a slow clock: it holds
                //    animation poses, and a pose re-asserted twice a second is a man juddering
                //    rather than a man kneeling.
                _call.Update();
            }
            catch (Exception ex)
            {
                Log.Error("Tick failed.", ex);
            }
            finally
            {
                // LAST, AND IN THE FINALLY. Several paths above return early, and a menu drawn
                // inside the try flickers off on any frame where something takes a shortcut or
                // throws.
                //
                // FRAME BEFORE DRAW, AND IT RUNS WHETHER THE MENU IS OPEN OR NOT. It holds down
                // the game's own controls while the panel is up, so the arrows pick rows instead
                // of steering the car you were standing next to.
                if (_menu != null) { _menu.Frame(); _menu.Draw(); }
            }
        }

        private void OnKey(object sender, System.Windows.Forms.KeyEventArgs e)
        {
            if (_parked || _cfg == null) return;

            // The menu opens even with the mod switched off, because turning it back on is one
            // of the things you would want the menu for.
            try { if (_menu != null) _menu.Key(e.KeyCode, e.Modifiers); }
            catch (Exception ex) { Log.Debug("Key handling failed: " + ex.Message); }
        }

        /// <summary>
        /// A call-out staged in front of the player, from the settings screen.
        ///
        /// A STRANGER, FOUR METRES AHEAD, KILLED CLEANLY, AND A VAN FROM FORTY OUT. What killed
        /// him is decided by the row that was chosen rather than read off his death, because the
        /// point is to watch a particular ending. Anything already running is stood down first:
        /// a test is somebody asking to see the scene now.
        ///
        /// He is never seen standing by the watch, so the watch never answers him as well -- the
        /// same rule that stops it answering bodies the player walked up on.
        /// </summary>
        private void Stage(bool workable)
        {
            try
            {
                if (_parked || !_cfg.Enabled) return;

                var me = Game.Player.Character;
                if (me == null || !me.Exists()) return;

                if (_call.Out) _call.Done();

                var model = Crew.Load("a_m_y_downtown_01");
                if (model == null) { Log.Warn("The test patient's model would not load."); return; }

                // IN A CAR, OFF TO THE SIDE. Four metres ahead of a car is under its bumper.
                var at = me.Position + (me.IsInVehicle() ? me.RightVector * 5f : me.ForwardVector * 4f);
                at.Z = Crew.Ground(at, at.Z);

                var ped = World.CreatePed(model.Value, at, me.Heading + 180f);
                model.Value.MarkAsNoLongerNeeded();

                if (!Crew.There(ped)) { Log.Warn("The test patient would not spawn."); return; }

                Function.Call(Hash.SET_ENTITY_HEALTH, ped.Handle, 0);

                var sent = _call.Send(new Death { Body = ped, Where = at, When = Game.GameTime },
                                      workable ? Verdict.Workable : Verdict.Gone, 40f);

                if (!sent)
                {
                    Log.Warn("The test call-out would not dispatch -- no road near enough.");
                    Crew.Give(ped);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not stage a call-out: " + ex.Message);
            }
        }

        /// <summary>
        /// Somebody free, and somebody to go to.
        /// </summary>
        private void Send()
        {
            try
            {
                var me = Game.Player.Character;
                if (me == null || !me.Exists()) return;

                var next = _watch.Next(me.Position);
                if (next == null) return;

                // CLAIMED WHETHER OR NOT IT WENT. A death that cannot be answered -- no road to
                // come in from, no ambulance model, a body on a rooftop -- must not be offered
                // again on the next tick, or the mod spends the rest of the session failing to
                // dispatch to the same corpse forty times a second.
                _watch.Claim(next);

                _call.Send(next);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not send anybody: " + ex.Message);
            }
        }

        /// <summary>
        /// The game's own ambulances, on or off.
        ///
        /// RE-ASSERTED ON A SLOW CLOCK RATHER THAN SET ONCE. ENABLE_DISPATCH_SERVICE is a
        /// global the whole session shares, and anything else in the scripts folder may set it
        /// -- a trainer, a mission mod, the game itself on a load. Set once at start-up, the
        /// suppression quietly stops holding somewhere in the second hour and two ambulances
        /// start turning up again, which is the hardest kind of bug to report.
        ///
        /// Cheap: one native call every five seconds, and only when the answer has changed or
        /// the clock has come round.
        /// </summary>
        private void Vanilla(bool on)
        {
            var now = Game.GameTime;

            if (_suppressed == !on && now - _assertedAt < AssertEveryMs) return;

            _assertedAt = now;
            _suppressed = !on;

            try
            {
                Function.Call(Hash.ENABLE_DISPATCH_SERVICE, AmbulanceDispatch, on);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not set the ambulance dispatch service: " + ex.Message);
            }
        }

        // ---- teardown ----------------------------------------------------------

        /// <summary>
        /// One teardown step, whatever it does to itself.
        ///
        /// THE WHOLE OF OnAborted IN ONE try IS THE CLASSIC MISTAKE. Every line of it hands
        /// something back -- the dispatch service, two persistent peds, a vehicle, a prop, a
        /// corpse welded to that prop -- and a single throw anywhere in the list skips
        /// everything after it. The failure is silent and it is permanent.
        /// </summary>
        private static void Safely(string what, Action step)
        {
            try
            {
                step();
            }
            catch (Exception ex)
            {
                Log.Debug("Unloading: " + what + " would not let go (" + ex.Message + ").");
            }
        }

        private void OnAborted(object sender, EventArgs e)
        {
            try
            {
                // THE GAME'S AMBULANCES BACK FIRST. It is the one thing here that outlives the
                // script: a dispatch service left switched off is a city with no ambulances at
                // all, for the rest of the session, with nothing on screen to say why and no
                // way to put it back short of restarting.
                Safely("the ambulance dispatch service",
                       () => Function.Call(Hash.ENABLE_DISPATCH_SERVICE, AmbulanceDispatch, true));

                // AND EVERYTHING WE PUT ON THE STREET. The van, the crew and the trolley are
                // marked persistent, which means the game will not clean them up itself -- and
                // the body is welded to the trolley, so the order inside Done matters as much
                // as calling it does.
                Safely("the call-out", () => { if (_call != null) _call.Done(); });

                Safely("the watch", () => { if (_watch != null) _watch.Release(); });

                Safely("the api", Api.Medics.Unwire);

                Log.Info("Unloaded. Nothing was being held.");
            }
            catch
            {
                // Teardown. Nothing left to tell.
            }
        }
    }
}
