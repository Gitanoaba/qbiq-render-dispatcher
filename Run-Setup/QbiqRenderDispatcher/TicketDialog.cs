using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// "Render Ticket" popup shown before the automation actually runs.
    /// Displays everything that's about to happen and lets the operator override
    /// preset / skybox / view path before clicking Render Now.
    ///
    /// Resolution / Compression / FPS are HARDCODED (Full HD / Maximum / 60).
    /// Output filename is auto-derived: "<project> VIDEO_<id>.mp4" inside
    /// the project folder (parent of rvt_path).
    /// </summary>
    public class TicketDialog : Form
    {
        private RenderJob _job;
        private string _projectFolder;

        // Buttons
        private Button _btnOpenVS;
        private Button _btnRender;
        private Button _btnCancel;

        // Outputs
        public string SelectedPreset       { get; private set; }
        public string SelectedSkyboxPath   { get; private set; }
        public int    SkyboxRotation       { get; private set; }
        public string SelectedViewPathFile { get; private set; }
        public string OutputAbsolutePath   { get; private set; }
        public bool   OpenVisualSettingsRequested { get; private set; }

        public TicketDialog(RenderJob job)
        {
            _job = job;
            _projectFolder = Path.GetDirectoryName(job.RvtPath) ?? "";

            BuildUI();
        }

        // ============================================================
        // UI construction
        // ============================================================
        private void BuildUI()
        {
            this.Text            = "qbiq Render Dispatcher - Confirm Render";
            this.Size            = new Size(720, 640);
            this.StartPosition   = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MinimizeBox     = false;
            this.MaximizeBox     = false;
            this.BackColor       = Color.White;

            int leftCol  = 20;
            int rightCol = 180;
            int width    = 480;
            int rowH     = 28;
            int y        = 20;

            // ----- Project info (read-only) -----
            AddSection("Job", ref y);
            AddInfoRow("Project:", _job.Manifest.project ?? "(not specified)", leftCol, rightCol, width, ref y, rowH);
            AddInfoRow("File:",    Path.GetFileName(_job.RvtPath),             leftCol, rightCol, width, ref y, rowH);
            AddInfoRow("View:",    _job.Manifest.revit_view ?? "(default)",    leftCol, rightCol, width, ref y, rowH);

            y += 8;

            // ----- Visual Settings (read-only from ticket) -----
            // These values were chosen at ticket-creation time. They're displayed
            // here as a final review, not as editable fields, so the operator
            // can't accidentally diverge from what the ticket says.
            AddSection("Visual Settings (from ticket)", ref y);

            string presetDisplay   = string.IsNullOrEmpty(_job.Manifest.enscape_preset)
                                        ? "(use current preset)"
                                        : _job.Manifest.enscape_preset;
            string skyboxDisplay   = string.IsNullOrEmpty(_job.Manifest.skybox_file)
                                        ? "(keep current)"
                                        : _job.Manifest.skybox_file;
            string viewPathDisplay = string.IsNullOrEmpty(_job.Manifest.view_path_xml)
                                        ? "(none - render will fail!)"
                                        : _job.Manifest.view_path_xml;

            AddInfoRow("Preset:",    presetDisplay,   leftCol, rightCol, width, ref y, rowH);
            AddInfoRow("Skybox:",    skyboxDisplay,   leftCol, rightCol, width, ref y, rowH);
            AddInfoRow("View Path:", viewPathDisplay, leftCol, rightCol, width, ref y, rowH);

            y += 8;

            // ----- Locked export settings -----
            AddSection("Video Export (locked)", ref y);
            AddInfoRow("Resolution:",  "Full HD (1920 x 1080)", leftCol, rightCol, width, ref y, rowH);
            AddInfoRow("Compression:", "Maximum",               leftCol, rightCol, width, ref y, rowH);
            AddInfoRow("FPS:",         "60",                    leftCol, rightCol, width, ref y, rowH);

            y += 8;

            // ----- Output -----
            AddSection("Output", ref y);

            string outputName = DeriveOutputFilename(_job.RvtPath);
            string outputPath = Path.Combine(_projectFolder, outputName);
            OutputAbsolutePath = outputPath;

            AddInfoRow("Filename:", outputName,      leftCol, rightCol, width, ref y, rowH);
            AddInfoRow("Folder:",   _projectFolder,  leftCol, rightCol, width, ref y, rowH);

            // ----- Buttons -----
            int btnY = this.ClientSize.Height - 60;

            _btnOpenVS = new Button {
                Text     = "Open Visual Settings",
                Location = new Point(leftCol, btnY),
                Size     = new Size(180, 32),
                FlatStyle = FlatStyle.System
            };
            _btnOpenVS.Click += (s, e) => {
                OpenVisualSettingsRequested = true;
                this.DialogResult = DialogResult.Retry;
                this.Close();
            };
            this.Controls.Add(_btnOpenVS);

            _btnCancel = new Button {
                Text         = "Cancel",
                Location     = new Point(this.ClientSize.Width - 220, btnY),
                Size         = new Size(90, 32),
                DialogResult = DialogResult.Cancel,
                FlatStyle    = FlatStyle.System
            };
            this.Controls.Add(_btnCancel);

            _btnRender = new Button {
                Text         = "Render Now",
                Location     = new Point(this.ClientSize.Width - 120, btnY),
                Size         = new Size(100, 32),
                FlatStyle    = FlatStyle.Flat,
                BackColor    = Color.FromArgb(0, 120, 215),
                ForeColor    = Color.White,
                Font         = new Font(this.Font, FontStyle.Bold)
            };
            _btnRender.FlatAppearance.BorderSize = 0;
            _btnRender.Click += OnRenderNow;
            this.Controls.Add(_btnRender);

            this.AcceptButton = _btnRender;
            this.CancelButton = _btnCancel;
        }

        private void AddSection(string title, ref int y)
        {
            Label l = new Label {
                Text     = title.ToUpper(),
                Location = new Point(20, y),
                Size     = new Size(660, 18),
                Font     = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(80, 80, 80)
            };
            this.Controls.Add(l);

            Panel separator = new Panel {
                Location = new Point(20, y + 19),
                Size     = new Size(660, 1),
                BackColor = Color.FromArgb(220, 220, 220)
            };
            this.Controls.Add(separator);

            y += 26;
        }

        private void AddInfoRow(string label, string value, int leftCol, int rightCol, int width, ref int y, int rowH)
        {
            Label lbl = new Label {
                Text     = label,
                Location = new Point(leftCol, y + 4),
                Size     = new Size(rightCol - leftCol, rowH),
                ForeColor = Color.FromArgb(100, 100, 100)
            };
            this.Controls.Add(lbl);

            Label val = new Label {
                Text     = value,
                Location = new Point(rightCol, y + 4),
                Size     = new Size(width, rowH),
                Font     = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            this.Controls.Add(val);

            y += rowH;
        }

        // ============================================================
        // Submit
        // ============================================================
        private void OnRenderNow(object sender, EventArgs e)
        {
            // All values come straight from the ticket's manifest - the user
            // doesn't edit them in this dialog anymore (just reviews + confirms).
            RenderManifest m = _job.Manifest;

            SelectedPreset = (m.enscape_preset ?? "").Trim();

            // Skybox: project folder first, then SkyboxLibraryRoot
            string skybox = (m.skybox_file ?? "").Trim();
            if (string.IsNullOrEmpty(skybox))
                SelectedSkyboxPath = null;
            else if (Path.IsPathRooted(skybox))
                SelectedSkyboxPath = skybox;
            else
            {
                string p = Path.Combine(_projectFolder, skybox);
                SelectedSkyboxPath = File.Exists(p)
                    ? p
                    : Path.Combine(Config.SkyboxLibraryRoot, skybox);
            }

            SkyboxRotation = m.skybox_rotation;

            // View Path XML: project folder only
            string xml = (m.view_path_xml ?? "").Trim();
            if (string.IsNullOrEmpty(xml))
                SelectedViewPathFile = null;
            else if (Path.IsPathRooted(xml))
                SelectedViewPathFile = xml;
            else
                SelectedViewPathFile = Path.Combine(_projectFolder, xml);

            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        // ============================================================
        // Static helper used elsewhere
        // ============================================================
        public static string DeriveOutputFilename(string rvtPath)
        {
            string baseName = Path.GetFileNameWithoutExtension(rvtPath);
            int lastUnderscore = baseName.LastIndexOf('_');
            if (lastUnderscore > 0 && lastUnderscore < baseName.Length - 1)
            {
                string projectName = baseName.Substring(0, lastUnderscore);
                string idPart      = baseName.Substring(lastUnderscore); // includes underscore
                return projectName + " VIDEO" + idPart + ".mp4";
            }
            return baseName + " VIDEO.mp4";
        }
    }
}
