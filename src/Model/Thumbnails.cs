using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.Display;

namespace StructuredViews
{
    /// <summary>
    /// Named-view thumbnails as PNG bytes per document and view id. Rhino doesn't expose the thumbnails of
    /// its own panel, so these are captured here: from the active view when a view is saved or updated,
    /// otherwise by borrowing a model viewport (<see cref="ViewportState"/>). The plug-in saves them in the
    /// .3dm. A thumbnail goes stale when the model changes and is then captured again.
    /// </summary>
    public static class Thumbnails
    {
        public const int Width = 256;
        public const int Height = 160;

        sealed class Entry
        {
            public byte[] Png;
            public bool Stale;
        }

        static readonly Dictionary<uint, Dictionary<Guid, Entry>> s_docs = new Dictionary<uint, Dictionary<Guid, Entry>>();

        /// <summary>Raised with the document serial number and view id whenever a thumbnail changes.</summary>
        public static event Action<uint, Guid> Changed;

        static Dictionary<Guid, Entry> For(RhinoDoc doc, bool create)
        {
            if (!s_docs.TryGetValue(doc.RuntimeSerialNumber, out var map) && create)
                s_docs[doc.RuntimeSerialNumber] = map = new Dictionary<Guid, Entry>();
            return map;
        }

        public static byte[] Get(RhinoDoc doc, Guid viewId)
        {
            var map = For(doc, false);
            return map != null && map.TryGetValue(viewId, out var entry) ? entry.Png : null;
        }

        /// <summary>True when the view has no thumbnail or an outdated one.</summary>
        public static bool NeedsCapture(RhinoDoc doc, Guid viewId)
        {
            var map = For(doc, false);
            return map == null || !map.TryGetValue(viewId, out var entry) || entry.Stale;
        }

        public static void Set(RhinoDoc doc, Guid viewId, byte[] png)
        {
            if (viewId == Guid.Empty)
                return;
            var map = For(doc, true);
            if (png == null)
                map.Remove(viewId);
            else
                map[viewId] = new Entry { Png = png };
            Changed?.Invoke(doc.RuntimeSerialNumber, viewId);
        }

        /// <summary>Keeps the pictures but marks them for capture again (model changed).</summary>
        public static void MarkStale(RhinoDoc doc)
        {
            var map = For(doc, false);
            if (map == null)
                return;
            foreach (var entry in map.Values)
                entry.Stale = true;
        }

        /// <summary>Thumbnails of the views the document still has.</summary>
        public static List<KeyValuePair<Guid, byte[]>> Current(RhinoDoc doc)
        {
            var map = For(doc, false);
            if (map == null)
                return new List<KeyValuePair<Guid, byte[]>>();
            var ids = new HashSet<Guid>(doc.NamedViews.Select(v => v.NamedViewId));
            return map.Where(kv => ids.Contains(kv.Key)).Select(kv => new KeyValuePair<Guid, byte[]>(kv.Key, kv.Value.Png)).ToList();
        }

        public static void Forget(uint serial)
        {
            s_docs.Remove(serial);
        }

        /// <summary>Captures what <paramref name="view"/> shows now.</summary>
        public static byte[] CaptureView(RhinoView view)
        {
            var viewport = view.ActiveViewport;
            return Encode(view.CaptureToBitmap(new System.Drawing.Size(Width, Height), viewport.DisplayMode));
        }

        /// <summary>Captures a named view without leaving it on screen.</summary>
        public static byte[] CaptureNamedView(RhinoDoc doc, int index)
        {
            var view = ViewportState.ModelView(doc);
            if (view == null || index < 0 || index >= doc.NamedViews.Count)
                return null;
            using (new ViewportState(view.MainViewport))
            {
                // Restore applies the named view's display mode too, if it has one.
                doc.NamedViews.Restore(index, view.MainViewport);
                return Encode(view.CaptureToBitmap(new System.Drawing.Size(Width, Height), view.MainViewport.DisplayMode));
            }
        }

        static byte[] Encode(System.Drawing.Bitmap bitmap)
        {
            if (bitmap == null)
                return null;
            using (bitmap)
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                return stream.ToArray();
            }
        }
    }

    /// <summary>
    /// Borrows a viewport: snapshots projection, target, construction plane, title, display mode and
    /// wallpaper, and puts them back on dispose without a redraw in between, so the user never sees the
    /// borrowed state. (Push/PopViewProjection does not bring the camera back, and restoring a named view
    /// also renames the viewport and may change its wallpaper.)
    /// </summary>
    public sealed class ViewportState : IDisposable
    {
        readonly RhinoViewport _viewport;
        readonly Rhino.DocObjects.ViewportInfo _projection;
        readonly Rhino.Geometry.Point3d _target;
        readonly Rhino.DocObjects.ConstructionPlane _cplane;
        readonly string _name;
        readonly DisplayModeDescription _mode;
        readonly string _wallpaper;
        readonly bool _wallpaperGrayscale;
        readonly bool _wallpaperVisible;

        public ViewportState(RhinoViewport viewport)
        {
            _viewport = viewport;
            _projection = new Rhino.DocObjects.ViewportInfo(viewport);
            _target = viewport.CameraTarget;
            _cplane = viewport.GetConstructionPlane();
            _name = viewport.Name;
            _mode = viewport.DisplayMode;
            _wallpaper = viewport.WallpaperFilename ?? "";
            _wallpaperGrayscale = viewport.WallpaperGrayscale;
            _wallpaperVisible = viewport.WallpaperVisible;
        }

        public void Dispose()
        {
            _viewport.SetViewProjection(_projection, false);
            _viewport.SetCameraTarget(_target, false);
            _viewport.SetConstructionPlane(_cplane);
            if (_viewport.Name != _name)
                _viewport.Name = _name;
            if (_mode != null && _viewport.DisplayMode?.Id != _mode.Id)
                _viewport.DisplayMode = _mode;
            if ((_viewport.WallpaperFilename ?? "") != _wallpaper || _viewport.WallpaperGrayscale != _wallpaperGrayscale || _viewport.WallpaperVisible != _wallpaperVisible)
                _viewport.SetWallpaper(_wallpaper, _wallpaperGrayscale, _wallpaperVisible);
        }

        /// <summary>The active view if it is a model view, otherwise the first model view.</summary>
        public static RhinoView ModelView(RhinoDoc doc)
        {
            var active = doc.Views.ActiveView;
            if (active != null && !(active is RhinoPageView))
                return active;
            return doc.Views.GetViewList(ViewTypeFilter.Model).FirstOrDefault();
        }
    }
}
