namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Job manifest format. Users drop a .json file with this shape next to
    /// their .rvt to indicate the job is ready to be processed.
    ///
    /// IMPORTANT: property names use snake_case to match the JSON convention
    /// because System.Web.Script.Serialization.JavaScriptSerializer maps
    /// fields by exact name match. Do not rename without updating the JSON
    /// schema for users.
    /// </summary>
    public class RenderManifest
    {
        // === Ticket pointer (REQUIRED) ===
        // Absolute path to the .rvt in its real project folder.
        // The plugin opens the .rvt directly from this location, so linked models
        // and project-relative assets (like custom skyboxes) work transparently.
        public string rvt_path { get; set; }

        // === Identification ===
        public string project { get; set; }
        public string output_name { get; set; }

        // === Revit-side ===
        public string revit_view { get; set; }       // e.g. "3D Enscape" - the view to activate before rendering

        // === Enscape-side (consumed in v0.6+, validated now) ===
        public string enscape_preset { get; set; }   // e.g. "Qbiq preset" - the Visual Settings preset
        public string skybox_file { get; set; }      // resolved: project folder first, then SkyboxLibraryRoot
        public int    skybox_rotation { get; set; }  // skybox rotation in degrees (0-360, default 0)
        public string view_path_xml { get; set; }    // Enscape View Path .xml; resolved from project folder only

        // === Render output settings ===
        public string resolution { get; set; }
        public int fps { get; set; }
        public string quality { get; set; }

        // === Free-form ===
        public string notes { get; set; }

        // === Sender info (auto-populated by Create Ticket) ===
        // Used by the render server to auto-create path junctions
        // so Revit decals / linked files resolve on the render PC.
        public string sender_username    { get; set; }  // Windows username of the creator
        public string sender_dropbox_root { get; set; } // e.g. C:\Users\Eitan 3D\Q Dropbox
    }
}
