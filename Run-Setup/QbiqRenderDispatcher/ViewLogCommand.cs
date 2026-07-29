using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Diagnostics;
using System.IO;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Opens logs\dispatch.log in Notepad so the user can inspect the
    /// trace of recent renders.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ViewLogCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            string folderError;
            if (!JobQueue.EnsureFolders(out folderError))
            {
                ShowError("Cannot access queue folders", folderError);
                return Result.Cancelled;
            }

            string path = Logger.GetLogPath();

            if (!File.Exists(path))
            {
                ShowInfo("Log file not yet created",
                    "No render has run yet, so " + path + " does not exist.\n\n" +
                    "Run \"Process Next Render\" once to generate log entries.");
                return Result.Succeeded;
            }

            try
            {
                Process.Start("notepad.exe", "\"" + path + "\"");
            }
            catch (System.Exception ex)
            {
                ShowError("Could not open log file",
                    "Path: " + path + "\n\nError: " + ex.Message);
                return Result.Failed;
            }

            return Result.Succeeded;
        }

        private static void ShowInfo(string title, string content)
        {
            TaskDialog dlg = new TaskDialog("Render Dispatcher")
            {
                MainInstruction = title, MainContent = content,
                CommonButtons = TaskDialogCommonButtons.Ok
            };
            dlg.Show();
        }

        private static void ShowError(string title, string content)
        {
            TaskDialog dlg = new TaskDialog("Render Dispatcher")
            {
                MainInstruction = title, MainContent = content,
                CommonButtons = TaskDialogCommonButtons.Ok,
                MainIcon = TaskDialogIcon.TaskDialogIconError
            };
            dlg.Show();
        }
    }
}
