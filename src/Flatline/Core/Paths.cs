using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Flatline.Core
{
    /// <summary>
    /// Where this mod reads and writes.
    ///
    /// Assembly.Location is NOT usable here. ScriptHookVDotNet shadow-copies every script into
    /// the .NET download cache before running it, so Location reports somewhere under
    /// AppData\Local\assembly\dl3 -- a folder that has never held an ini and never will. A mod
    /// that trusts it looks for its settings beside the COPY, finds nothing, and runs on
    /// built-in defaults while printing a line that reads like a missing file rather than a mod
    /// looking in the wrong place. That cost Hoodrich several days; it is not repeated here.
    ///
    /// So several candidates are tested against files we know we shipped, and the first that
    /// actually holds them wins.
    /// </summary>
    internal static class Paths
    {
        private static string _scripts;

        /// <summary>The scripts folder the game loaded this dll from.</summary>
        public static string Scripts
        {
            get
            {
                if (_scripts != null) return _scripts;

                var candidates = new List<string>();

                // SHVDN builds its script AppDomain with the scripts folder as the base.
                TryAdd(candidates, SafeGet(() => AppDomain.CurrentDomain.BaseDirectory));

                var cwd = SafeGet(Directory.GetCurrentDirectory);
                if (!string.IsNullOrEmpty(cwd))
                {
                    TryAdd(candidates, Path.Combine(cwd, "scripts"));
                    TryAdd(candidates, cwd);
                }

                // CodeBase survives a shadow copy where Location does not.
                TryAdd(candidates, SafeGet(() =>
                {
                    var code = Assembly.GetExecutingAssembly().CodeBase;
                    return string.IsNullOrEmpty(code)
                        ? null
                        : Path.GetDirectoryName(new Uri(code).LocalPath);
                }));

                TryAdd(candidates, SafeGet(() =>
                {
                    var loc = Assembly.GetExecutingAssembly().Location;
                    return string.IsNullOrEmpty(loc) ? null : Path.GetDirectoryName(loc);
                }));

                foreach (var dir in candidates)
                {
                    if (LooksLikeOurFolder(dir)) { _scripts = dir; return _scripts; }
                }

                _scripts = candidates.Count > 0 ? candidates[0] : cwd ?? ".";
                return _scripts;
            }
        }

        /// <summary>
        /// True when this folder holds the files the deploy puts down.
        ///
        /// The ini OR the icons. This mod ships no data files of its own -- the hospitals are a
        /// table in Scene.Hospitals rather than a json, for the reason set out there -- so the
        /// seal art is the only other thing that proves a folder is ours.
        /// </summary>
        private static bool LooksLikeOurFolder(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                if (File.Exists(Path.Combine(dir, "Flatline.ini"))) return true;

                var data = Path.Combine(dir, "Flatline");
                return Directory.Exists(data) &&
                       File.Exists(Path.Combine(Path.Combine(data, "icons"), "seal-face.png"));
            }
            catch
            {
                return false;
            }
        }

        private static void TryAdd(List<string> list, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;

            try
            {
                dir = Path.GetFullPath(dir.TrimEnd(Path.DirectorySeparatorChar));
                if (Directory.Exists(dir) && !list.Contains(dir)) list.Add(dir);
            }
            catch
            {
                // Unusable path; skip it.
            }
        }

        private static string SafeGet(Func<string> get)
        {
            try { return get(); }
            catch { return null; }
        }

        /// <summary>The shipped art, beside the dll. Read-only as far as we care.</summary>
        public static string Data
        {
            get
            {
                var d = Path.Combine(Scripts, "Flatline");
                EnsureDir(d);
                return d;
            }
        }

        private static string _writable;

        /// <summary>
        /// Where the log goes.
        ///
        /// The game is normally installed under Program Files, which is NOT writable by an
        /// unelevated process -- and GTA5.exe is unelevated. Reads work fine, so the ini loads,
        /// but every write silently fails: no log, with nothing on screen to say so.
        /// </summary>
        public static string Writable
        {
            get
            {
                if (_writable != null) return _writable;

                var preferred = Path.Combine(Scripts, "Flatline");

                if (IsWritable(preferred)) { _writable = preferred; return _writable; }

                var fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Flatline");

                try
                {
                    if (!Directory.Exists(fallback)) Directory.CreateDirectory(fallback);
                }
                catch
                {
                    fallback = Path.Combine(Path.GetTempPath(), "Flatline");
                    try { if (!Directory.Exists(fallback)) Directory.CreateDirectory(fallback); }
                    catch { /* nothing left to try */ }
                }

                _writable = fallback;
                return _writable;
            }
        }

        private static bool IsWritable(string dir)
        {
            try
            {
                EnsureDir(dir);

                var probe = Path.Combine(dir, ".write-probe");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureDir(string dir)
        {
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); }
            catch { /* the caller finds out when it writes */ }
        }

        public static string Ini => Path.Combine(Scripts, "Flatline.ini");
        public static string LogFile => Path.Combine(Writable, "Flatline.log");

        /// <summary>The seal art, beside the data rather than loose in scripts\.</summary>
        public static string Icons => Path.Combine(Data, "icons");
    }
}
