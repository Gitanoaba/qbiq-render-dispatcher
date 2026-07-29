using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Generates a render ticket (.json) for the currently open .rvt and
    /// drops it into pending\. Always uses Document.PathName, so rvt_path
    /// is guaranteed correct.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CreateTicketCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument    uiDoc = uiApp.ActiveUIDocument;

            if (uiDoc == null || uiDoc.Document == null)
            {
                ShowError("No active document",
                    "Open the .rvt you want to render before clicking Create Ticket.");
                return Result.Cancelled;
            }

            Document doc = uiDoc.Document;
            string   rvt = doc.PathName;

            if (string.IsNullOrEmpty(rvt))
            {
                ShowError("Document not saved",
                    "The active document has no path on disk. Save it first (File -> Save As).");
                return Result.Cancelled;
            }

            if (!File.Exists(rvt))
            {
                ShowError("Document path invalid",
                    "Document.PathName reports:\n   " + rvt + "\n\n...but that file does not exist. Save the document first.");
                return Result.Cancelled;
            }

            string folderError;
            if (!JobQueue.EnsureFolders(out folderError))
            {
                ShowError("Cannot access queue folders", folderError);
                return Result.Cancelled;
            }

            List<string> viewNames = new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate &&
                            (v.ViewType == ViewType.ThreeD ||
                             v.ViewType == ViewType.FloorPlan ||
                             v.ViewType == ViewType.CeilingPlan ||
                             v.ViewType == ViewType.Elevation ||
                             v.ViewType == ViewType.Section))
                .Select(v => v.Name).Distinct().OrderBy(n => n).ToList();

            string activeViewName = uiDoc.ActiveView != null ? uiDoc.ActiveView.Name : "";

            CreateTicketDialog dlg = new CreateTicketDialog(rvt, viewNames, activeViewName);
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return Result.Cancelled;

            string projectFolder = Path.GetDirectoryName(rvt) ?? "";
            string ticketId      = DateTime.Now.ToString("yyyyMMddHHmmssfff");
            string projectClean  = string.IsNullOrEmpty(dlg.Project)
                ? Path.GetFileNameWithoutExtension(rvt)
                : dlg.Project;
            string outputName    = projectClean + " VIDEO_" + ticketId + ".mp4";

            string json = BuildJson(rvt, projectClean, outputName, dlg.RevitView,
                dlg.EnscapePreset, dlg.SkyboxFileName, dlg.SkyboxRotation, dlg.ViewPathXmlName, dlg.Notes);

            string ticketBaseName = SafeForFileName(projectClean) + "_" + ticketId + ".json";
            string ticketPath     = Path.Combine(Config.PendingDir, ticketBaseName);

            try
            {
                File.WriteAllText(ticketPath, json);
            }
            catch (Exception ex)
            {
                ShowError("Could not write ticket",
                    "Path: " + ticketPath + "\n\nError: " + ex.GetType().Name + "\n" + ex.Message);
                return Result.Failed;
            }

            ShowInfo("Ticket created",
                "Ticket saved to:\n   " + ticketPath + "\n\n" +
                "RVT path:       " + rvt + "\n" +
                "Project:        " + projectClean + "\n" +
                "Preset:         " + NotEmpty(dlg.EnscapePreset, "(none)") + "\n" +
                "Skybox:         " + NotEmpty(dlg.SkyboxFileName, "(none)") + "\n" +
                "View Path XML:  " + NotEmpty(dlg.ViewPathXmlName, "(none)") + "\n" +
                "Revit view:     " + NotEmpty(dlg.RevitView, "(active)") + "\n" +
                "Output target:  " + outputName + "\n\n" +
                "Click \"Process Next Render\" to render it.");

            return Result.Succeeded;
        }

        private static string BuildJson(string rvt, string project, string outputName, string revitView,
            string preset, string skyboxFile, int skyboxRotation, string viewPathXml, string notes)
        {
            // Sender info for auto-junction on the render PC
            string senderUser    = System.Environment.UserName;
            string senderDropbox = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Config.DropboxRoot, "..", ".."));

            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"rvt_path\":           \"").Append(JsonEscape(rvt)).Append("\",\n");
            sb.Append("  \"project\":            \"").Append(JsonEscape(project)).Append("\",\n");
            sb.Append("  \"output_name\":        \"").Append(JsonEscape(outputName)).Append("\",\n");
            sb.Append("  \"revit_view\":         \"").Append(JsonEscape(revitView ?? "")).Append("\",\n");
            sb.Append("  \"enscape_preset\":     \"").Append(JsonEscape(preset ?? "")).Append("\",\n");
            sb.Append("  \"skybox_file\":        \"").Append(JsonEscape(skyboxFile ?? "")).Append("\",\n");
            sb.Append("  \"skybox_rotation\":    ").Append(skyboxRotation).Append(",\n");
            sb.Append("  \"view_path_xml\":      \"").Append(JsonEscape(viewPathXml ?? "")).Append("\",\n");
            sb.Append("  \"resolution\":         \"Full HD (1920x1080)\",\n");
            sb.Append("  \"fps\":                60,\n");
            sb.Append("  \"quality\":            \"Maximum\",\n");
            sb.Append("  \"notes\":              \"").Append(JsonEscape(notes ?? "")).Append("\",\n");
            sb.Append("  \"sender_username\":    \"").Append(JsonEscape(senderUser)).Append("\",\n");
            sb.Append("  \"sender_dropbox_root\":\"").Append(JsonEscape(senderDropbox)).Append("\"\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        private static string SafeForFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "ticket";
            char[] invalid = Path.GetInvalidFileNameChars();
            char[] arr     = s.ToCharArray();
            for (int i = 0; i < arr.Length; i++)
                if (Array.IndexOf(invalid, arr[i]) >= 0) arr[i] = '_';
            return new string(arr);
        }

        private static string NotEmpty(string v, string fb) { return string.IsNullOrEmpty(v) ? fb : v; }

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
