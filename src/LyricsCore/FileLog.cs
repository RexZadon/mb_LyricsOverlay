using System;
using System.IO;
using System.Reflection;

namespace LyricsOverlay.Core
{
    /// <summary>
    /// Tiny append-only log. Prefers a file next to the plugin DLL; if that folder isn't writable
    /// (e.g. MusicBee under Program Files) it falls back to the given folder. Never throws.
    /// </summary>
    public static class FileLog
    {
        const long MaxBytes = 1024 * 1024;
        static readonly object Gate = new object();
        static string _path;

        public static string PathInUse => _path;

        public static void Init(string fileName, string fallbackDir)
        {
            try
            {
                var dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var primary = Path.Combine(dllDir ?? ".", fileName);
                if (CanWrite(primary)) { _path = primary; return; }
                if (!string.IsNullOrEmpty(fallbackDir))
                {
                    Directory.CreateDirectory(fallbackDir);
                    _path = Path.Combine(fallbackDir, fileName);
                }
            }
            catch (Exception) { /* logging must never break the plugin */ }
        }

        /// <summary>Stops logging and deletes the log and its rolled-over ".old" copy (plugin uninstall).</summary>
        public static void CloseAndDelete()
        {
            string path;
            lock (Gate)
            {
                path = _path;
                _path = null;
            }
            if (path == null) return;
            foreach (var p in new[] { path, path + ".old" })
            {
                try { if (File.Exists(p)) File.Delete(p); }
                catch (Exception) { }
            }
        }

        public static void Info(string message) => Write("INFO ", message);

        public static void Error(string context, Exception ex) => Write("ERROR", context + ": " + ex);

        static void Write(string level, string message)
        {
            if (_path == null) return;
            try
            {
                lock (Gate)
                {
                    if (_path == null) return;
                    var fi = new FileInfo(_path);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        File.Copy(_path, _path + ".old", true);
                        File.Delete(_path);
                    }
                    File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
                }
            }
            catch (Exception) { }
        }

        static bool CanWrite(string path)
        {
            try
            {
                using (new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
