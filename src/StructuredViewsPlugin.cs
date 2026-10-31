using Rhino;
using Rhino.FileIO;
using Rhino.PlugIns;
using Rhino.UI;

namespace StructuredViews
{
    ///<summary>
    /// Structured Views: Rhino's Named Views panel with folders. The views stay Rhino's own named views;
    /// the folder structure lives in the document's string table (FolderStore) and the thumbnails in this
    /// plug-in's document data.
    ///</summary>
    public class StructuredViewsPlugin : PlugIn
    {
        const int DataVersion = 1;

        public StructuredViewsPlugin()
        {
            Instance = this;
        }

        ///<summary>Gets the only instance of the StructuredViewsPlugin plug-in.</summary>
        public static StructuredViewsPlugin Instance { get; private set; }

        // Load with Rhino so the panel is there in every session.
        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            StructuredViews.Settings.Attach(Settings);
            Panels.RegisterPanel(this, typeof(StructuredViewsPanel), "Structured Views", GetType().Assembly, "StructuredViews.ico", PanelType.PerDoc);
            RhinoDoc.CloseDocument += OnCloseDocument;
            return LoadReturnCode.Success;
        }

        protected override void OnShutdown()
        {
            RhinoDoc.CloseDocument -= OnCloseDocument;
        }

        static void OnCloseDocument(object sender, DocumentEventArgs e)
        {
            Thumbnails.Forget(e.DocumentSerialNumber);
        }

        protected override bool ShouldCallWriteDocument(FileWriteOptions options)
        {
            return !options.WriteGeometryOnly && !options.WriteSelectedObjectsOnly;
        }

        protected override void WriteDocument(RhinoDoc doc, BinaryArchiveWriter archive, FileWriteOptions options)
        {
            var thumbnails = Thumbnails.Current(doc);
            archive.WriteInt(DataVersion);
            archive.WriteInt(thumbnails.Count);
            foreach (var kv in thumbnails)
            {
                archive.WriteGuid(kv.Key);
                archive.WriteByteArray(kv.Value);
            }
        }

        protected override void ReadDocument(RhinoDoc doc, BinaryArchiveReader archive, FileReadOptions options)
        {
            if (archive.ReadInt() != DataVersion)
                return;
            var count = archive.ReadInt();
            for (int i = 0; i < count; i++)
            {
                var id = archive.ReadGuid();
                Thumbnails.Set(doc, id, archive.ReadByteArray());
            }
        }
    }
}
