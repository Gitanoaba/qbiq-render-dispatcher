using System;
using System.IO;
using System.Threading;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Verbose logger that appends timestamped lines to logs\dispatch.log.
    /// Used by the entire render flow to leave a paper trail of every step
    /// taken so we can debug failures after the fact.
    ///
    /// Non-blocking: all writes happen on the calling thread but errors are
    /// swallowed so a logging failure can never break the render.
    /// </summary>
    public static class Logger
    {
        private static readonly object _lock = new object();

        public static string GetLogPath()
        {
            return Path.Combine(Config.LogsDir, "dispatch.log");
        }

        /// <summary>
        /// Writes a section header to make scanning the log easier.
        /// </summary>
        public static void Section(string title)
        {
            AppendRaw("");
            AppendRaw("================================================================");
            AppendRaw(" " + title + "  -  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            AppendRaw("================================================================");
        }

        public static void Info(string step, string message)
        {
            Append("INFO ", step, message);
        }

        public static void Warn(string step, string message)
        {
            Append("WARN ", step, message);
        }

        public static void Error(string step, string message)
        {
            Append("ERROR", step, message);
        }

        public static void Step(string name)
        {
            AppendRaw("");
            AppendRaw("--- " + name + " ---");
        }

        // -----------------------------------------------------------
        private static void Append(string level, string step, string message)
        {
            string ts = DateTime.Now.ToString("HH:mm:ss.fff");
            int    tid = Thread.CurrentThread.ManagedThreadId;
            string line = ts + "  T" + tid + "  " + level + "  " + (step ?? "") + " | " + (message ?? "");
            AppendRaw(line);
        }

        private static void AppendRaw(string line)
        {
            try
            {
                lock (_lock)
                {
                    if (!Directory.Exists(Config.LogsDir))
                        Directory.CreateDirectory(Config.LogsDir);
                    File.AppendAllText(GetLogPath(), line + Environment.NewLine);
                }
            }
            catch
            {
                // swallow - log failure must never break the render flow
            }
        }
    }
}
