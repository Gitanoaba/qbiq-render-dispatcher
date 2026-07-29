using System;
using System.IO;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Central configuration for the dispatcher.
    /// All paths and tunable settings live here.
    /// </summary>
    public static class Config
    {
        // Root Dropbox path for this machine, resolved at runtime from the current Windows user.
        // Works on any server PC regardless of username â€” no manual edits needed.
        private static string UserDropbox
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Q Dropbox");
            }
        }

        // Root folder of the render queue.
        public static string DropboxRoot
        {
            get { return Path.Combine(UserDropbox, @"3D Projects\#_RenderServer"); }
        }

        // Root folder of the skybox library.
        public static string SkyboxLibraryRoot
        {
            get { return Path.Combine(UserDropbox, @"3D Materials\Skybox Library\Skybox Library V2"); }
        }

        // Slack incoming webhook URL. Empty = notifications disabled.
        public static readonly string SlackWebhookUrl =
            @"";

        // Path remapping: rewrite any C:\Users\<anyone>\Q Dropbox\ prefix from a ticket
        // to this machine's local Q Dropbox path so files resolve correctly.
        public static string RemapLocalPrefix
        {
            get { return UserDropbox; }
        }

        /// <summary>
        /// Rewrites a path from the ticket-creator machine to this machine.
        /// Matches any Windows username so it works regardless of which PC sent the ticket.
        /// </summary>
        public static string RemapPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;

            // Match ANY "C:\Users\<username>\Q Dropbox\" prefix
            var m = System.Text.RegularExpressions.Regex.Match(p,
                @"^C:\\Users\\[^\\]+\\Q Dropbox\\",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
                return RemapLocalPrefix + "\\" + p.Substring(m.Length);

            return p;
        }

        // Subfolders, computed off DropboxRoot.
        public static string PendingDir    { get { return Path.Combine(DropboxRoot, "pending");     } }
        public static string InProgressDir { get { return Path.Combine(DropboxRoot, "in-progress"); } }
        public static string CompletedDir  { get { return Path.Combine(DropboxRoot, "completed");   } }
        public static string ProcessedDir  { get { return Path.Combine(DropboxRoot, "processed");   } }
        public static string FailedDir     { get { return Path.Combine(DropboxRoot, "failed");      } }
        public static string LogsDir       { get { return Path.Combine(DropboxRoot, "logs");        } }

        public static string[] AllSubfolders
        {
            get
            {
                return new string[]
                {
                    PendingDir, InProgressDir, CompletedDir,
                    ProcessedDir, FailedDir, LogsDir
                };
            }
        }
    }
}
