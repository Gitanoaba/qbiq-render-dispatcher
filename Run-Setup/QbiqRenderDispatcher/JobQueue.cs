using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Ticket-model job queue: each job is a .json file in pending\ that
    /// references the .rvt by absolute path (rvt_path field).
    ///
    /// The .rvt itself stays in its real project folder; only the .json
    /// (a few KB) is in the queue. Linked models and project-relative
    /// assets like custom skyboxes are reachable from the .rvt's own folder.
    /// </summary>
    public static class JobQueue
    {
        /// <summary>
        /// Ensures the queue subfolders exist. Creates them if missing.
        /// Returns true if the root is accessible and folders are ready.
        /// </summary>
        public static bool EnsureFolders(out string error)
        {
            error = null;

            try
            {
                if (!Directory.Exists(Config.DropboxRoot))
                {
                    error = "Dropbox root folder not found:\n" + Config.DropboxRoot +
                            "\n\nMake sure Dropbox is installed and synced on this machine, " +
                            "and that the path in Config.cs matches your local Dropbox layout.";
                    return false;
                }

                foreach (string sub in Config.AllSubfolders)
                {
                    if (!Directory.Exists(sub))
                    {
                        Directory.CreateDirectory(sub);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                error = "Could not prepare queue folders: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Returns the next ready job (oldest .json with a valid rvt_path
        /// pointing to an existing .rvt file). Returns null if no ready job.
        /// </summary>
        public static RenderJob PickNextJob()
        {
            FileInfo[] jsonFiles = new DirectoryInfo(Config.PendingDir)
                .GetFiles("*.json")
                .OrderBy(fi => fi.LastWriteTimeUtc)
                .ToArray();

            foreach (FileInfo jsonFi in jsonFiles)
            {
                RenderManifest manifest;
                if (!TryParseManifest(jsonFi.FullName, out manifest))
                    continue;

                if (manifest == null || string.IsNullOrEmpty(manifest.rvt_path))
                    continue;

                // Remap path from ticket-creator machine to this machine
                string remappedRvt = Config.RemapPath(manifest.rvt_path);

                if (!File.Exists(remappedRvt))
                    continue;

                return new RenderJob
                {
                    RvtPath      = remappedRvt,
                    ManifestPath = jsonFi.FullName,
                    Manifest     = manifest
                };
            }

            return null;
        }

        /// <summary>
        /// Counts every .json file in the pending folder regardless of validity.
        /// </summary>
        public static int CountPendingTickets()
        {
            return Directory.GetFiles(Config.PendingDir, "*.json").Length;
        }

        /// <summary>
        /// Counts how many tickets are valid (parseable JSON + rvt_path exists).
        /// </summary>
        public static int CountReadyJobs()
        {
            int count = 0;
            foreach (string jsonPath in Directory.GetFiles(Config.PendingDir, "*.json"))
            {
                RenderManifest manifest;
                if (!TryParseManifest(jsonPath, out manifest)) continue;
                if (manifest == null || string.IsNullOrEmpty(manifest.rvt_path)) continue;
                if (!File.Exists(Config.RemapPath(manifest.rvt_path))) continue;
                count++;
            }
            return count;
        }

        /// <summary>
        /// Returns a list of human-readable reasons why pending tickets are not ready.
        /// Useful for displaying diagnostics when CountReadyJobs() returns less than CountPendingTickets().
        /// </summary>
        public static List<string> GetInvalidTicketReasons()
        {
            List<string> reasons = new List<string>();

            foreach (string jsonPath in Directory.GetFiles(Config.PendingDir, "*.json"))
            {
                string fileName = Path.GetFileName(jsonPath);
                RenderManifest manifest;

                if (!TryParseManifest(jsonPath, out manifest))
                {
                    reasons.Add(fileName + ": invalid JSON or unreadable file");
                    continue;
                }

                if (manifest == null)
                {
                    reasons.Add(fileName + ": empty manifest");
                    continue;
                }

                if (string.IsNullOrEmpty(manifest.rvt_path))
                {
                    reasons.Add(fileName + ": missing 'rvt_path' field");
                    continue;
                }

                if (!File.Exists(manifest.rvt_path))
                {
                    reasons.Add(fileName + ": rvt_path does not exist on disk -> " + manifest.rvt_path);
                    continue;
                }
            }

            return reasons;
        }

        private static bool TryParseManifest(string jsonPath, out RenderManifest manifest)
        {
            manifest = null;
            string json;
            try { json = File.ReadAllText(jsonPath); }
            catch { return false; }

            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                manifest = serializer.Deserialize<RenderManifest>(json);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// A picked job ready to be processed (resolved .rvt path + manifest).
    /// </summary>
    public class RenderJob
    {
        public string RvtPath;        // absolute path to the .rvt (from manifest.rvt_path)
        public string ManifestPath;   // absolute path to the .json in pending\
        public RenderManifest Manifest;
    }
}
