using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Dialog used by CreateTicketCommand to capture manifest fields.
    /// The .rvt path is fixed (active document) and not editable - that's the
    /// whole point: the user cannot mistype it.
    /// </summary>
    public class CreateTicketDialog : Form
    {
        public string Project          { get; private set; }
        public string EnscapePreset    { get; private set; }
        public string SkyboxFileName   { get; private set; }
        public int    SkyboxRotation   { get; private set; }
        public string ViewPathXmlName  { get; private set; }
        public string RevitView        { get; private set; }
        public string Notes            { get; private set; }

        private readonly string _rvtPath;
        private readonly string _projectFolder;
        private readonly List<string> _viewNames;

        private TextBox        _txtProject;
        private TextBox        _txtPreset;
        private ComboBox       _cboSkybox;
        private NumericUpDown  _numRotation;
        private ComboBox       _cboViewPath;
        private ComboBox       _cboRevitView;
        private TextBox        _txtNotes;

        public CreateTicketDialog(string rvtPath, List<string> revitViewNames, string activeViewName)
        {
            _rvtPath       = rvtPath;
            _projectFolder = Path.GetDirectoryName(rvtPath) ?? "";
            _viewNames     = revitViewNames ?? new List<string>();

            BuildUi(activeViewName);
        }

        private void BuildUi(string activeViewName)
        {
            this.Text            = "Create Render Ticket";
            this.StartPosition   = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MinimizeBox     = false;
            this.MaximizeBox     = false;
            this.Width           = 720;
            this.Height          = 600;
            this.Font            = new Font("Segoe UI", 9F);

            int leftCol  = 20;
            int rightCol = 200;
            int width    = 480;
            int rowH     = 26;
            int y        = 20;

            AddLabel("RVT path:", leftCol, y);
            AddInfo(_rvtPath, rightCol, y, width);
            y += rowH;

            AddLabel("Project folder:", leftCol, y);
            AddInfo(_projectFolder, rightCol, y, width);
            y += rowH + 8;

            AddLabel("Project name:", leftCol, y);
            _txtProject = new TextBox { Left = rightCol, Top = y - 3, Width = width, Text = Path.GetFileName(_projectFolder) };
            this.Controls.Add(_txtProject);
            y += rowH;

            AddLabel("Enscape preset:", leftCol, y);
            _txtPreset = new TextBox { Left = rightCol, Top = y - 3, Width = width, Text = "Qbiq preset" };
            this.Controls.Add(_txtPreset);
            y += rowH;

            AddLabel("Skybox (optional):", leftCol, y);
            _cboSkybox = new ComboBox { Left = rightCol, Top = y - 3, Width = width, DropDownStyle = ComboBoxStyle.DropDownList };
            _cboSkybox.Items.Add("(none - keep current)");
            foreach (string s in DiscoverSkyboxes()) _cboSkybox.Items.Add(s);
            _cboSkybox.SelectedIndex = 0;
            this.Controls.Add(_cboSkybox);
            y += rowH;

            AddLabel("Skybox rotation (Â°):", leftCol, y);
            _numRotation = new NumericUpDown
            {
                Left = rightCol, Top = y - 3, Width = 80,
                Minimum = 0, Maximum = 360, Value = 0, DecimalPlaces = 0
            };
            this.Controls.Add(_numRotation);
            y += rowH;

            AddLabel("View Path XML:", leftCol, y);
            _cboViewPath = new ComboBox { Left = rightCol, Top = y - 3, Width = width, DropDownStyle = ComboBoxStyle.DropDownList };
            _cboViewPath.Items.Add("(none - render will fail)");
            foreach (string xml in DiscoverViewPathXmls()) _cboViewPath.Items.Add(xml);
            _cboViewPath.SelectedIndex = _cboViewPath.Items.Count > 1 ? 1 : 0;
            this.Controls.Add(_cboViewPath);
            y += rowH;

            AddLabel("Revit view (locked):", leftCol, y);
            // Revit view is always "3D Enscape" - that's the standardized view used for renders.
            // Keep the field as a disabled ComboBox so the manifest schema stays the same and the
            // OnSaveClicked code that reads .Text still works.
            _cboRevitView = new ComboBox { Left = rightCol, Top = y - 3, Width = width, DropDownStyle = ComboBoxStyle.DropDownList };
            _cboRevitView.Items.Add("3D Enscape");
            _cboRevitView.SelectedIndex = 0;
            _cboRevitView.Enabled = false;
            this.Controls.Add(_cboRevitView);
            y += rowH + 8;

            AddLabel("Notes (optional):", leftCol, y);
            _txtNotes = new TextBox { Left = rightCol, Top = y - 3, Width = width, Height = 60, Multiline = true };
            this.Controls.Add(_txtNotes);
            y += 70;

            AddLabel("Locked:", leftCol, y);
            AddInfo("Resolution: Full HD (1920x1080)  -  FPS: 60  -  Quality: Maximum", rightCol, y, width);
            y += rowH + 16;

            Button btnCancel = new Button { Text = "Cancel", Left = rightCol + width - 200, Top = y, Width = 90, Height = 30 };
            btnCancel.Click += (s, e) => { this.DialogResult = DialogResult.Cancel; this.Close(); };
            this.Controls.Add(btnCancel);

            Button btnSave = new Button { Text = "Save Ticket", Left = rightCol + width - 100, Top = y, Width = 100, Height = 30 };
            btnSave.Click += OnSaveClicked;
            this.AcceptButton = btnSave;
            this.Controls.Add(btnSave);
        }

        private void AddLabel(string text, int x, int y)
        {
            Label lbl = new Label { Text = text, Left = x, Top = y, Width = 170, Font = new Font("Segoe UI", 9F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
            this.Controls.Add(lbl);
        }

        private void AddInfo(string text, int x, int y, int width)
        {
            Label lbl = new Label { Text = text, Left = x, Top = y, Width = width, Height = 22, ForeColor = Color.DarkSlateGray, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            this.Controls.Add(lbl);
        }


        private List<string> DiscoverSkyboxes()
        {
            List<string> result = new List<string>();
            string[] exts = new[] { ".hdr", ".exr", ".jpg", ".jpeg", ".png" };

            if (Directory.Exists(_projectFolder))
            {
                foreach (string f in Directory.GetFiles(_projectFolder))
                {
                    string ext = Path.GetExtension(f).ToLower();
                    if (Array.IndexOf(exts, ext) >= 0)
                        result.Add(Path.GetFileName(f) + "  [project folder]");
                }
            }
            if (Directory.Exists(Config.SkyboxLibraryRoot))
            {
                foreach (string f in Directory.GetFiles(Config.SkyboxLibraryRoot, "*.*", SearchOption.AllDirectories))
                {
                    string ext = Path.GetExtension(f).ToLower();
                    if (Array.IndexOf(exts, ext) >= 0)
                    {
                        string rel = f.Substring(Config.SkyboxLibraryRoot.Length).TrimStart('\\', '/');
                        result.Add(rel + "  [library]");
                    }
                }
            }
            return result;
        }

        private List<string> DiscoverViewPathXmls()
        {
            List<string> result = new List<string>();
            if (Directory.Exists(_projectFolder))
                foreach (string f in Directory.GetFiles(_projectFolder, "*.xml"))
                    result.Add(Path.GetFileName(f));
            return result;
        }

        private void OnSaveClicked(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_txtProject.Text))
            {
                MessageBox.Show(this, "Project name is required.", "Missing field", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_cboViewPath.SelectedIndex <= 0)
            {
                DialogResult ans = MessageBox.Show(this,
                    "No View Path XML selected - render will fail without one.\n\nSave anyway?",
                    "Missing View Path", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (ans != DialogResult.Yes) return;
            }

            Project       = _txtProject.Text.Trim();
            EnscapePreset = _txtPreset.Text.Trim();

            string skyRaw = _cboSkybox.SelectedIndex > 0 ? (string)_cboSkybox.SelectedItem : "";
            SkyboxFileName = StripTag(skyRaw);
            SkyboxRotation = (int)_numRotation.Value;

            string vpRaw = _cboViewPath.SelectedIndex > 0 ? (string)_cboViewPath.SelectedItem : "";
            ViewPathXmlName = StripTag(vpRaw);

            RevitView = _cboRevitView.Text.Trim();
            Notes     = _txtNotes.Text.Trim();

            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private static string StripTag(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int idx = s.IndexOf("  [");
            return idx >= 0 ? s.Substring(0, idx) : s;
        }
    }
}
