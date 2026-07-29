using Autodesk.Revit.UI;
using System;
using System.Reflection;
using System.Threading;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Entry point for the Revit add-in (render-server build).
    /// Adds the "Render Server" ribbon tab and starts the auto-poll loop.
    /// The loop fires an ExternalEvent every 30 s when a ready ticket is
    /// detected so AutoRenderHandler can process it without any dialogs.
    /// </summary>
    public class App : IExternalApplication
    {
        private static ExternalEvent     _autoRenderEvent;
        private static AutoRenderHandler _autoRenderHandler;
        private static Timer             _pollTimer;
        private const  int               PollIntervalSeconds = 30;

        public Result OnStartup(UIControlledApplication application)
        {
            const string TabName      = "Render Server";
            const string QueuePanel   = "Queue";
            const string TicketsPanel = "Tickets";

            try { application.CreateRibbonTab(TabName); }
            catch (Exception) { /* already exists */ }

            RibbonPanel panel = application.CreateRibbonPanel(TabName, QueuePanel);

            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            // --- Button 1: GO RENDER SERVER (manual fallback) ---
            PushButtonData manualBtn = new PushButtonData(
                name:         "ProcessNextRender",
                text:         "GO RENDER" + Environment.NewLine + "SERVER",
                assemblyName: assemblyPath,
                className:    "QbiqRenderDispatcher.ProcessNextCommand");

            manualBtn.ToolTip         = "Process the oldest ticket in pending\\";
            manualBtn.LongDescription =
                "Manual trigger: picks the oldest .json ticket in pending\\, opens the .rvt, " +
                "applies Enscape settings and triggers video export. " +
                "The server also does this automatically every 30 s.";
            try { manualBtn.LargeImage = RibbonIcons.RenderServer(32); manualBtn.Image = RibbonIcons.RenderServer(16); } catch { }

            panel.AddItem(manualBtn);

            // --- Button 2: View Log ---
            PushButtonData viewLogBtn = new PushButtonData(
                name:         "ViewLog",
                text:         "View" + Environment.NewLine + "Log",
                assemblyName: assemblyPath,
                className:    "QbiqRenderDispatcher.ViewLogCommand");

            viewLogBtn.ToolTip         = "Open the dispatcher log file in Notepad";
            viewLogBtn.LongDescription =
                "Opens logs\\dispatch.log in Notepad. Every step of every render " +
                "is logged with timestamps - useful for diagnosing failures.";
            try { viewLogBtn.LargeImage = RibbonIcons.ViewLog(32); viewLogBtn.Image = RibbonIcons.ViewLog(16); } catch { }

            panel.AddItem(viewLogBtn);

            // --- Button 3: Create Ticket ---
            RibbonPanel ticketsPanel = application.CreateRibbonPanel(TabName, TicketsPanel);

            PushButtonData createBtn = new PushButtonData(
                name:         "CreateTicket",
                text:         "Create" + Environment.NewLine + "Ticket",
                assemblyName: assemblyPath,
                className:    "QbiqRenderDispatcher.CreateTicketCommand");

            createBtn.ToolTip         = "Create a render ticket for the currently open .rvt";
            createBtn.LongDescription =
                "Generates a .json ticket using the active document's real path. " +
                "Auto-detects skybox files, view path .xml files and Revit views. " +
                "Drops the ticket into pending\\ ready to render.";
            try { createBtn.LargeImage = RibbonIcons.CreateTicket(32); createBtn.Image = RibbonIcons.CreateTicket(16); } catch { }

            ticketsPanel.AddItem(createBtn);

            // â”€â”€ Auto-poll: ExternalEvent + background timer â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // ExternalEvent lets a background timer request execution on the
            // Revit main thread without blocking or polling from C#.
            _autoRenderHandler = new AutoRenderHandler();
            _autoRenderEvent   = ExternalEvent.Create(_autoRenderHandler);

            // Timer fires on a ThreadPool thread every PollIntervalSeconds.
            // It calls _autoRenderEvent.Raise() when a ready job is found.
            _pollTimer = new Timer(OnPollTimer, null,
                TimeSpan.FromSeconds(PollIntervalSeconds),
                TimeSpan.FromSeconds(PollIntervalSeconds));

            Logger.Info("App", "Auto-poll started: every " + PollIntervalSeconds + "s. " +
                               "Dropbox queue: " + Config.PendingDir);

            return Result.Succeeded;
        }

        /// <summary>
        /// Fires on a ThreadPool thread every PollIntervalSeconds.
        /// If a ready job exists and no render is running, raises the ExternalEvent
        /// so Revit schedules AutoRenderHandler.Execute on the main thread.
        /// </summary>
        private static void OnPollTimer(object state)
        {
            try
            {
                if (RenderCoordinator.IsBusy) return;

                if (!System.IO.Directory.Exists(Config.PendingDir)) return;

                if (JobQueue.CountReadyJobs() > 0)
                {
                    Logger.Info("AutoPoll", "Job(s) found in pending\\ â€” raising ExternalEvent.");
                    _autoRenderEvent?.Raise();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("AutoPoll", "Timer callback threw: " + ex.Message);
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try { _pollTimer?.Dispose(); } catch { }
            return Result.Succeeded;
        }
    }
}
