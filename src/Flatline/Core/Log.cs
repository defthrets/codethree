using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Flatline.Core
{
    internal enum LogLevel
    {
        Error = 0,
        Warn = 1,
        Info = 2,
        Debug = 3
    }

    /// <summary>
    /// File logger for Flatline.log.
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
    /// The file names are a separate matter: Flatline.dll, Flatline.ini, Flatline.log and the
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
        /// </summary>
        public const string Version = "0.2.0";

        /// <summary>The word on the splash row and at the top of the log.</summary>
        public const string Name = "Flatline";

        public const string By = "spitmux";
    }
}
