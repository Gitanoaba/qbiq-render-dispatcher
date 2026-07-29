using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Posts simple text messages to a Slack incoming webhook.
    /// Non-blocking: fires the request on a background thread so the render
    /// flow does not wait on network I/O. All errors are swallowed - a
    /// failed Slack post must never break the render pipeline.
    /// </summary>
    public static class SlackNotifier
    {
        /// <summary>
        /// Sends "Render started: <project>".
        /// </summary>
        public static void NotifyStarted(string project, string ticketName)
        {
            string text = ":hourglass_flowing_sand: *Render started* on the qbiq render server\n" +
                          "*Project:* " + Safe(project) + "\n" +
                          "*Ticket:* `" + Safe(ticketName) + "`";
            Send(text);
        }

        /// <summary>
        /// Sends a success message with output path. Server is now free.
        /// </summary>
        public static void NotifyCompleted(string project, string outputName, string ticketName)
        {
            string text = ":white_check_mark: *Render completed* - server is now free for the next job\n" +
                          "*Project:* " + Safe(project) + "\n" +
                          "*Output:* `" + Safe(outputName) + "`\n" +
                          "*Ticket:* `" + Safe(ticketName) + "`";
            Send(text);
        }

        /// <summary>
        /// Sends a failure message naming the step that failed. Server is free.
        /// </summary>
        public static void NotifyFailed(string project, string ticketName, string failureReason)
        {
            string text = ":x: *Render failed* - server is now free for the next job\n" +
                          "*Project:* " + Safe(project) + "\n" +
                          "*Ticket:* `" + Safe(ticketName) + "`\n" +
                          "*Reason:* " + Safe(failureReason);
            Send(text);
        }

        // -------------------------------------------------------------
        private static void Send(string text)
        {
            string url = Config.SlackWebhookUrl;
            if (string.IsNullOrEmpty(url)) return;   // Slack disabled

            // Fire-and-forget: don't block the render flow on network I/O
            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    string payload = "{\"text\":\"" + JsonEscape(text) + "\"}";
                    byte[] bytes   = Encoding.UTF8.GetBytes(payload);

                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method        = "POST";
                    req.ContentType   = "application/json";
                    req.ContentLength = bytes.Length;
                    req.Timeout       = 10000;

                    // TLS 1.2 (Slack requires it; .NET 4.8 default may pick lower)
                    try
                    {
                        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    }
                    catch { /* ignore on platforms that don't support it */ }

                    using (Stream s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    {
                        // Slack returns 200 + "ok" body when accepted; no need to read it
                    }
                }
                catch
                {
                    // Swallow all errors - Slack notification failure must never break render
                }
            });
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        private static string Safe(string s)
        {
            return string.IsNullOrEmpty(s) ? "(unknown)" : s;
        }
    }
}
