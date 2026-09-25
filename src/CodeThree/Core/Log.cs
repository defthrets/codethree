using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace CodeThree.Core
{
    internal enum LogLevel
    {
        Error = 0,
        Warn = 1,
        Info = 2,
        Debug = 3
    }

    /// <summary>
    /// File logger for CodeThree.log.
    ///
    /// Every method swallows its own exceptions. A logger that can throw takes the whole
    /// script down from inside a Tick handler, which is precisely the moment the log is the
    /// only thing that would have told you why.
    ///
    /// Carried over from Five0 Patrol unchanged but for the name. Six mods with six subtly
    /// different loggers is six different log formats to read at three in the morning.
    /// </summary>
    internal static class Log
    {
        private const long MaxBytes = 2 * 1024 * 1024;

        private static readonly object Gate = new object();
        private static bool _started;

        public static LogLevel Level = LogLevel.Info;

        public static void Error(string message, Exception ex = null) => Write(LogLevel.Error, message, ex);
        public static void Warn(string message) => Write(LogLevel.Warn, message, null);
        public static void Info(string message) => Write(LogLevel.Info, message, null);
        public static void Debug(string message) => Write(LogLevel.Debug, message, null);

        private static void Write(LogLevel level, string message, Exception ex)
        {
            if (level > Level) return;

            try
            {
                lock (Gate)
                {
                    var path = Paths.LogFile;

                    if (!_started)
                    {
                        RollIfLarge(path);
                        _started = true;
                        AppendLine(path, "");
                        AppendLine(path, "=== " + Build.Name + " " + Build.Version + " by " + Build.By +
                                         " started " +
                                         DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss",
                                                               CultureInfo.InvariantCulture) + " ===");
                    }

                    var sb = new StringBuilder();
                    sb.Append('[')
                      .Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
                      .Append("] ");
                    sb.Append(level.ToString().ToUpperInvariant().PadRight(5)).Append(' ');
                    sb.Append(message);

                    if (ex != null)
                    {
                        sb.AppendLine();
                        sb.Append("    ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);

                        if (!string.IsNullOrEmpty(ex.StackTrace))
                        {
                            sb.AppendLine();
                            sb.Append(ex.StackTrace);
                        }
                    }

                    AppendLine(path, sb.ToString());
                }
            }
            catch
            {
                // Logging must never be the reason a script dies.
            }
        }

        private static void AppendLine(string path, string line)
        {
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }

        private static void RollIfLarge(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < MaxBytes) return;

                var old = path + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(path, old);
            }
            catch
            {
                // A locked or unrollable log is not worth failing over.
            }
        }
    }

    /// <summary>
    /// What this thing is called, in the one place anything is allowed to ask.
    ///
    /// The file names are a separate matter: CodeThree.dll, CodeThree.ini, CodeThree.log and the
    /// folder beside them are PATHS, and renaming a path breaks every installation that
    /// exists. This is the word people read.
    /// </summary>
    internal static class Build
    {
        /// <summary>
        /// 0.1.0 -- somebody comes, and they do something.
        ///
        /// GTA V DISPATCHES AN AMBULANCE AND THEN WASTES IT. The van arrives with its lights
        /// on, two paramedics get out, they walk to the body, they stand over it, and then
        /// they get back in and drive away. The body is still lying in the road. Whatever the
        /// crew were supposed to be for, they are not for that -- and the effect of watching it
        /// twice is that you stop looking at ambulances altogether.
        ///
        /// Five0 Patrol made that worse rather than better, because it CALLS one. An officer
        /// finds a body, gets on the radio, a van turns up -- and then performs the same
        /// nothing, at a scene the police mod had gone to some trouble to build.
        ///
        /// So this mod is the other half of that call. They kneel, they work on him, and one
        /// of two things happens. If what killed him was somebody's fists or a bat -- something
        /// with a pulse still worth chasing -- he comes round, and gets up, and walks off. If it
        /// was a gun, a car, a fire or a drop, they stop, they fetch the trolley out of the
        /// back, they load him onto it, they wheel him to the van, and they drive him to the
        /// hospital.
        ///
        /// WHICH OF THOSE TWO IS NOT A COIN TOSS. It is what actually killed him, read off the
        /// engine's own record of it -- see Core.Cause.
        ///
        /// 0.2.0 -- the scene, done properly.
        ///
        /// 0.1.0 used two of the seven CPR clips, on one man, over a ragdoll that could not be
        /// animated because it was dead. It worked, in the sense that a paramedic knelt and
        /// pushed at the air near a body. This version makes the body a participant: he is
        /// brought back into arrest when they reach him, placed by a synchronised scene so the
        /// hands land on his chest and the chest goes with them, and taken through the whole
        /// sequence the game authored -- down to a knee, a look, the lean in, the compressions,
        /// sitting back, another look, and either the moment it takes or the moment it does
        /// not. Success ends with the medic pulling him to his feet and a limp he keeps. Failure
        /// ends with the second man stood over him writing down the time, and a body that lies
        /// flat on the trolley every time because it is posed rather than fallen.
        ///
        /// The second man carries the bag. The crew back off if the patient is shot under them.
        /// And the corpse is held from the moment of dispatch, so the engine cannot tidy it
        /// away before the van gets there -- which it could, before.
        ///
        /// 0.2.1 -- he lives long enough to be worked on.
        ///
        /// A week of play and not one scene ran. The log had the whole story: every call-out
        /// ended six seconds after the crew reached him with "the patient was killed", and the
        /// player had not touched him. He was brought back into arrest at 40 health, and a ped
        /// is dead at anything under a hundred -- the engine killed him again on the next tick
        /// and the flee guard did exactly what it was written for. See Crew.Floor. Health is now
        /// set as a fraction of the bar rather than of the number, in both places it was wrong,
        /// and the two exits that used to leave the log silent say why they happened.
        ///
        /// 0.2.2 -- the van is not the scene.
        ///
        /// The first scene that ever reached compressions ended one second into them: the
        /// player's crashed car was burning next to the parked ambulance, the ambulance was
        /// wrecked, and the top-of-tick guard that ended the call-out on a lost van released
        /// everyone mid-CPR. Medics do not stop working on a man because their vehicle took
        /// damage. A lost van is now noted once, with how it was lost, and the scene carries on
        /// -- only the steps that need somewhere to put him give up, and they give up on foot.
        ///
        /// 0.3.0 -- the loading, done properly.
        ///
        /// Three things were wrong and they were all the same shape: something was guessed that
        /// could have been measured or asked for.
        ///
        /// The body vanished the instant the trolley appeared, twice, 25ms apart in the log.
        /// RESURRECT_PED returns a ped blank and that includes its persistence, so from the
        /// moment the crew reached him he was ambient again -- and loading the gurney model
        /// yields, which is all the engine needs to reclaim him. He is re-held after every
        /// resurrection and every pose, and the models are now requested at dispatch so nothing
        /// yields mid-scene at all.
        ///
        /// The trolley materialised through the paramedic, because it was put down a metre
        /// towards the van, which is exactly where he was standing. It goes out to the side now,
        /// off the line he is on.
        ///
        /// And the offsets were never measurable from outside the game -- except they were.
        /// GET_MODEL_DIMENSIONS gives the bounding box, the box says where the origin sits, and
        /// the trolley now stands on its own wheels by arithmetic. The ini numbers are nudges on
        /// top of a measurement rather than the whole answer.
        ///
        /// The scene itself gained the two beats it was missing: combat@drag_ped@, the game's
        /// own paired body-lift, for getting him onto the canvas; and the shopping-trolley pose
        /// worn on the upper body over an ordinary walk, for wheeling him to the van.
        ///
        /// 0.3.1 -- the trolley comes back down.
        ///
        /// The measurement was right and the thing it was measured against was wrong. It said
        /// the gurney's origin sits at its wheels, which is true; the trolley was then welded to
        /// the medic at bone index 0 on the belief that bone 0 means the entity's own origin,
        /// between a ped's feet. For a ped bone 0 is SKEL_ROOT, which is the PELVIS -- so the
        /// wheels were planted at his waist and the frame stood at chest height, and fixedRot
        /// rolled the whole thing over every time an animation bent him.
        ///
        /// Both faults are the attachment, so the attachment is gone: while he wheels it, the
        /// trolley is placed each tick a fixed distance along his facing at the height of his
        /// feet, square to the world. A ped's position IS the ground under him, so nothing about
        /// skeletons needs to be known or guessed. And where it is first put down it is now
        /// grounded by the engine rather than given the body's height, which was standing it in
        /// the air whenever the body lay against a kerb.
        ///
        /// 0.3.2 -- the bed stays where it was put down.
        ///
        /// Take() -- the call that starts the trolley following the medic -- was made the
        /// instant the body was loaded, so the bed leapt across the pavement to wherever he
        /// happened to be standing. Watched from the street that is the gurney rising up to
        /// meet the body rather than the body being laid down on it, which is what it was
        /// reported as, in those words. Nothing about a trolley should move during a load: it
        /// is a thing with wheels standing on a road, and the man is the one who walks. So he
        /// is sent to the back of it and only picks it up once he is there.
        ///
        /// It is also laid out pointing AT the ambulance now, rather than at whatever angle the
        /// body happened to fall, so the back of it is where he stands and forward is where he
        /// is going.
        ///
        /// And the last borrowed height is gone. Every floating-prop bug in this mod has been a
        /// Z taken from something that is only usually on the floor -- first the body, then the
        /// medic. The world is asked directly now, through Crew.Ground.
        ///
        /// 0.4.0 -- nothing teleports.
        ///
        /// Five versions in a row fixed the thing in the latest screenshot and left the next one
        /// to be found the same way. This one went through the whole scene looking for the
        /// shape all of them shared, and it was the same every time: something jumping. The
        /// medic snapped onto his mark. The body flipped round as he knelt. The medic popped to
        /// a standing idle between CPR clips. The bed rose to meet the body; the body leapt from
        /// his arms onto the bed; the trolley appeared in the back of the van.
        ///
        /// Each is now a measurement or a movement. The patient anchors every scene where he
        /// already lies, turned to match the way his ragdoll actually fell, read off his bones.
        /// The medic walks to the exact mark the clip wants him on -- asked of the engine with
        /// GET_ANIM_INITIAL_OFFSET_POSITION -- and joins a scene held paused for him. Every
        /// one-shot holds its last frame. The body is carried along an arc onto the bed. The
        /// trolley rolls in through the doors.
        ///
        /// Two failures the log had been counting all along are fixed with it. The van was
        /// being deleted mid-scene because a frozen, colliding trolley was placed inside its
        /// rear every tick and the physics threw the van out of the world; the trolley no longer
        /// collides while it moves, and the medic stops short of the bumper by the van's
        /// measured length. And thirty-eight call-outs ended "it could not get there": progress
        /// is now watched rather than time, and a stuck van either lets the crew walk the last
        /// stretch or is put back on a road nearer the body.
        ///
        /// And the settings screen can stage one in front of you, because this scene was nearly
        /// untestable -- a murder, then two minutes, then a third of the time no van.
        ///
        /// 0.4.1 -- on the ground, round the doors, and a van that comes back.
        ///
        /// Both men dropped through the road the moment a scene started: the scene's origin was
        /// solved for Z the same way as X and Y, by taking the clip's initial offset away from
        /// where the patient lay, and whatever that native reports for Z is not the distance
        /// from the scene floor to the root. The origin's height is the road now, full stop.
        ///
        /// The crew stood pushing against their own rear doors: every walk was a straight line,
        /// the doors were opened a minute early and swing out a metre either side, and a man
        /// sent straight at the bumper hit one and waited out his timeout. Every walk is a
        /// navmesh walk now, the doors open only as the trolley sets off and shut before anybody
        /// is sent to a seat, and the second man is sent to the cab rather than the door's arc.
        ///
        /// And the van is being deleted mid-scene by something that is not this mod -- three
        /// times in one evening during the CPR, with nothing of ours near it and no sweeper to
        /// be found in any config file on the machine. It cannot be stopped from here. So it is
        /// noticed on the next tick and another ambulance is put back where that one stood; the
        /// crew are on foot and not looking.
        ///
        /// 0.4.2 -- flat on the canvas, by measurement.
        ///
        /// The transport pose was one of eight unlabelled death poses, picked because the
        /// dictionary was called "dead" and never checked to be on the back. It is the
        /// morgue-table pose now -- a body knocked out flat on a slab -- which is a man on a
        /// gurney with the gurney removed. And the angle and height he lies at are no longer
        /// numbers in the ini: a quarter of a second after he is laid down, his skeleton is read
        /// against the trolley's long axis and the canvas height, and the attach is corrected
        /// once for the session. The lift now logs how far each man was from where the clip
        /// wanted him when it started, and the wheeling logs if the pushing pose is refused.
        ///
        /// 0.4.3 -- nothing is remembered, and the doors go in order.
        ///
        /// The first log with numbers in it said: one scene built fourteen metres from the
        /// body, its measurements cached for the session, and every patient after it placed
        /// wrong -- including two whose own scenes were perfect. So no measurement is kept past
        /// the pose it was read in, every one is bounded before it is used, and the engine's
        /// placements are checked against where the man actually lies before they are believed.
        ///
        /// The patient lay flat, aligned, on the ground directly under the canvas: the attach
        /// was made with isPed false, so the height was honoured for a crate and not a man.
        ///
        /// And the back of the van is a sequence now: at the rear, doors open, trolley in, doors
        /// shut, both men in, and it goes when both are actually seated.
        /// </summary>
        public const string Version = "0.4.3";

        /// <summary>The word on the splash row and at the top of the log.</summary>
        public const string Name = "Code Three";

        public const string By = "spitmux";
    }
}
