using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.Display;
using Rhino.FileIO;
using Rhino.Geometry;

namespace StructuredViews
{
    /// <summary>
    /// Everything the panel does to named views and folders, free of UI so it can be scripted and tested.
    /// Views are always addressed by NamedViewId; table indices are looked up at the moment of use.
    /// </summary>
    public static class ViewOps
    {
        public static int IndexOf(RhinoDoc doc, Guid viewId)
        {
            for (int i = 0; i < doc.NamedViews.Count; i++)
            {
                if (doc.NamedViews[i].NamedViewId == viewId)
                    return i;
            }
            return -1;
        }

        /// <summary>"Name", or "Name 2", "Name 3", … when taken ("Name 2" continues as "Name 3", not "Name 2 2").</summary>
        public static string UniqueName(RhinoDoc doc, string stem)
        {
            stem = string.IsNullOrWhiteSpace(stem) ? "Named View" : stem.Trim();
            if (doc.NamedViews.FindByName(stem) < 0)
                return stem;
            var start = 2;
            var space = stem.LastIndexOf(' ');
            if (space > 0 && int.TryParse(stem.Substring(space + 1), out var number) && number >= 1)
            {
                start = number + 1;
                stem = stem.Substring(0, space);
            }
            for (int i = start; ; i++)
            {
                var name = stem + " " + i;
                if (doc.NamedViews.FindByName(name) < 0)
                    return name;
            }
        }

        #region Views

        /// <summary>
        /// Saves the active viewport of <paramref name="view"/> as a named view. A new view goes to the end of
        /// <paramref name="folder"/>; an existing name is replaced in place (it keeps its id and folder).
        /// </summary>
        public static Guid Save(RhinoDoc doc, RhinoView view, string name, Guid folder)
        {
            var existed = doc.NamedViews.FindByName(name) >= 0;
            var index = doc.NamedViews.Add(name, view.ActiveViewportID);
            if (index < 0)
                return Guid.Empty;
            var id = doc.NamedViews[index].NamedViewId;
            if (!existed)
                MoveViews(doc, new[] { id }, folder, Guid.Empty);
            Thumbnails.Set(doc, id, Thumbnails.CaptureView(view));
            return id;
        }

        /// <summary>Sets a named view to what the active viewport of <paramref name="view"/> shows now.</summary>
        public static bool Update(RhinoDoc doc, Guid viewId, RhinoView view)
        {
            var index = IndexOf(doc, viewId);
            if (index < 0 || doc.NamedViews.Add(doc.NamedViews[index].Name, view.ActiveViewportID) < 0)
                return false;
            Thumbnails.Set(doc, viewId, Thumbnails.CaptureView(view));
            return true;
        }

        /// <summary>Renames a view; false if the name is empty or taken by another view.</summary>
        public static bool Rename(RhinoDoc doc, Guid viewId, string name)
        {
            name = (name ?? "").Trim();
            var index = IndexOf(doc, viewId);
            if (index < 0 || name.Length == 0)
                return false;
            var other = doc.NamedViews.FindByName(name);
            if (other >= 0 && other != index)
                return false;
            return doc.NamedViews.Rename(index, name);
        }

        /// <summary>Copies views next to their originals ("Name copy"); returns the new ids.</summary>
        public static List<Guid> Duplicate(RhinoDoc doc, IEnumerable<Guid> viewIds)
        {
            var copies = new List<Guid>();
            foreach (var id in viewIds.ToList())
            {
                var index = IndexOf(doc, id);
                if (index < 0)
                    continue;
                var info = doc.NamedViews[index];
                var copy = AddCopy(doc, info, info.Name + " copy");
                if (copy == Guid.Empty)
                    continue;
                var structure = Structure.Build(doc, SortMode.Custom);
                var folder = structure.Store.FolderOf(id);
                var siblings = structure.ViewsIn(folder);
                var after = siblings.FindIndex(v => v.Id == id);
                var before = after >= 0 && after + 1 < siblings.Count ? siblings[after + 1].Id : Guid.Empty;
                MoveViews(doc, new[] { copy }, folder, before == copy ? Guid.Empty : before);
                Thumbnails.Set(doc, copy, Thumbnails.Get(doc, id));
                copies.Add(copy);
            }
            return copies;
        }

        public static void Delete(RhinoDoc doc, IEnumerable<Guid> viewIds)
        {
            var store = FolderStore.Load(doc);
            foreach (var id in viewIds.ToList())
            {
                var index = IndexOf(doc, id);
                if (index >= 0 && doc.NamedViews.Delete(index))
                {
                    store.Forget(id);
                    Thumbnails.Set(doc, id, null);
                }
            }
        }

        /// <summary>Restores a named view into a viewport, animated and/or with its aspect ratio per <see cref="Settings"/>.</summary>
        public static bool Restore(RhinoDoc doc, Guid viewId, RhinoViewport viewport)
        {
            var index = IndexOf(doc, viewId);
            if (index < 0 || viewport == null)
                return false;
            bool ok;
            if (Settings.RestoreAspectRatio)
                ok = doc.NamedViews.RestoreWithAspectRatio(index, viewport);
            else if (!Settings.Animate)
                ok = doc.NamedViews.Restore(index, viewport);
            else if (Settings.AnimationMode == AnimationMode.ConstantSpeed)
                ok = doc.NamedViews.RestoreAnimatedConstantSpeed(index, viewport, Settings.AnimationSpeed, Settings.AnimationDelay);
            else
                ok = doc.NamedViews.RestoreAnimatedConstantTime(index, viewport, Settings.AnimationFrames, Settings.AnimationDelay);
            doc.Views.Redraw();
            return ok;
        }

        /// <summary>Opens a new floating viewport showing the named view.</summary>
        public static RhinoView RestoreFloating(RhinoDoc doc, Guid viewId)
        {
            var index = IndexOf(doc, viewId);
            if (index < 0)
                return null;
            var name = doc.NamedViews[index].Name;
            var view = doc.Views.Add(name, DefinedViewportProjection.Perspective, new System.Drawing.Rectangle(120, 120, 720, 480), true);
            if (view == null)
                return null;
            doc.NamedViews.Restore(index, view.MainViewport);
            view.Redraw();
            return view;
        }

        /// <summary>
        /// Changes the camera of a named view by applying it to a borrowed viewport and saving that over the
        /// view (same name, so the id stays). The viewport is put back without being redrawn.
        /// </summary>
        public static bool SetCamera(RhinoDoc doc, Guid viewId, Point3d location, Point3d target, double lensLength)
        {
            var index = IndexOf(doc, viewId);
            var view = ViewportState.ModelView(doc);
            if (index < 0 || view == null || location.DistanceTo(target) < RhinoMath.ZeroTolerance)
                return false;
            var viewport = view.MainViewport;
            using (new ViewportState(viewport))
            {
                var name = doc.NamedViews[index].Name;
                doc.NamedViews.Restore(index, viewport);
                viewport.SetCameraLocations(target, location);
                if (viewport.IsPerspectiveProjection && lensLength > 0)
                    viewport.Camera35mmLensLength = lensLength;
                if (doc.NamedViews.Add(name, viewport.Id) < 0)
                    return false;
                Thumbnails.Set(doc, viewId, Encode(view));
            }
            return true;
        }

        static byte[] Encode(RhinoView view)
        {
            using (var bitmap = view.CaptureToBitmap(new System.Drawing.Size(Thumbnails.Width, Thumbnails.Height), view.MainViewport.DisplayMode))
            using (var stream = new MemoryStream())
            {
                if (bitmap == null)
                    return null;
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                return stream.ToArray();
            }
        }

        /// <summary>Adds a copy of <paramref name="info"/> under a free name based on <paramref name="name"/>.</summary>
        static Guid AddCopy(RhinoDoc doc, Rhino.DocObjects.ViewInfo info, string name)
        {
            info.Name = UniqueName(doc, name);
            var index = doc.NamedViews.Add(info);
            return index < 0 ? Guid.Empty : doc.NamedViews[index].NamedViewId;
        }

        #endregion

        #region Structure

        /// <summary>
        /// Files views in a folder (Guid.Empty = top level), placed before <paramref name="before"/>
        /// (Guid.Empty = at the end), and renumbers that folder's custom order.
        /// </summary>
        public static void MoveViews(RhinoDoc doc, IList<Guid> viewIds, Guid folder, Guid before)
        {
            var structure = Structure.Build(doc, SortMode.Custom);
            var store = structure.Store;
            if (folder != Guid.Empty && !store.Folders.ContainsKey(folder))
                folder = Guid.Empty;
            var moving = new HashSet<Guid>(viewIds);
            var order = structure.ViewsIn(folder).Select(v => v.Id).Where(id => !moving.Contains(id)).ToList();
            var at = before == Guid.Empty ? -1 : order.IndexOf(before);
            order.InsertRange(at < 0 ? order.Count : at, viewIds);
            for (int i = 0; i < order.Count; i++)
                store.SetPlace(order[i], folder, i);
        }

        /// <summary>
        /// Moves folders under <paramref name="parent"/>, placed before <paramref name="before"/> (Guid.Empty =
        /// at the end). Folders that would end up inside themselves are skipped; returns how many moved.
        /// </summary>
        public static int MoveFolders(RhinoDoc doc, IList<Guid> folderIds, Guid parent, Guid before)
        {
            var store = FolderStore.Load(doc);
            var moved = folderIds.Where(id => store.MoveFolder(id, parent)).ToList();
            if (moved.Count == 0)
                return 0;
            var structure = Structure.Build(doc, SortMode.Custom);
            var movedSet = new HashSet<Guid>(moved);
            var order = structure.FoldersIn(parent).Select(n => n.Folder.Id).Where(id => !movedSet.Contains(id)).ToList();
            var at = before == Guid.Empty ? -1 : order.IndexOf(before);
            order.InsertRange(at < 0 ? order.Count : at, moved);
            structure.Store.SetFolderOrder(order);
            return moved.Count;
        }

        /// <summary>
        /// Moves views and folders one place up (-1) or down (+1) among their siblings in custom order. Items
        /// that are already first/last, or blocked by another selected item there, stay put.
        /// </summary>
        public static void Nudge(RhinoDoc doc, ICollection<Guid> viewIds, ICollection<Guid> folderIds, int step)
        {
            var structure = Structure.Build(doc, SortMode.Custom);
            var store = structure.Store;

            List<Guid> Shift(List<Guid> order, ICollection<Guid> moving)
            {
                var indices = Enumerable.Range(0, order.Count).Where(i => moving.Contains(order[i]));
                foreach (var i in step < 0 ? indices.ToList() : indices.Reverse().ToList())
                {
                    var j = i + step;
                    if (j >= 0 && j < order.Count && !moving.Contains(order[j]))
                        (order[i], order[j]) = (order[j], order[i]);
                }
                return order;
            }

            foreach (var folder in viewIds.Select(store.FolderOf).Distinct().ToList())
            {
                var order = Shift(structure.ViewsIn(folder).Select(v => v.Id).ToList(), viewIds);
                for (int i = 0; i < order.Count; i++)
                    store.SetPlace(order[i], folder, i);
            }
            foreach (var parent in folderIds.Where(store.Folders.ContainsKey).Select(id => store.Folders[id].Parent).Distinct().ToList())
                store.SetFolderOrder(Shift(structure.FoldersIn(parent).Select(n => n.Folder.Id).ToList(), folderIds));
        }

        #endregion

        #region Clipboard and import

        // Copies live in a File3dm: a ViewInfo read from the document is tied to its table entry and can't
        // be read any more once that view is deleted.
        static File3dm s_clipboard;

        public static bool CanPaste => s_clipboard != null && s_clipboard.NamedViews.Count > 0;

        public static int Copy(RhinoDoc doc, IEnumerable<Guid> viewIds)
        {
            var clipboard = new File3dm();
            foreach (var id in viewIds)
            {
                var index = IndexOf(doc, id);
                if (index >= 0)
                    clipboard.NamedViews.Add(doc.NamedViews[index]);
            }
            if (clipboard.NamedViews.Count > 0)
                s_clipboard = clipboard;
            return clipboard.NamedViews.Count;
        }

        /// <summary>Pastes the copied views (from any open document) at the end of <paramref name="folder"/>.</summary>
        public static List<Guid> Paste(RhinoDoc doc, Guid folder)
        {
            var added = new List<Guid>();
            if (!CanPaste)
                return added;
            foreach (var info in s_clipboard.NamedViews)
            {
                var id = AddCopy(doc, info, info.Name);
                if (id != Guid.Empty)
                    added.Add(id);
            }
            MoveViews(doc, added, folder, Guid.Empty);
            return added;
        }

        /// <summary>
        /// Writes named views and their folders to a .3dm that holds nothing else, so it can be imported
        /// here or with Rhino's own NamedView › Import. Selected folders go with everything below them; a
        /// selected view whose folder is not exported lands at the top level. Thumbnails are not exported:
        /// they are captured again after import. Returns the number of views written.
        /// </summary>
        public static int Export(RhinoDoc doc, string path, ICollection<Guid> viewIds, ICollection<Guid> folderIds)
        {
            var store = FolderStore.Load(doc);
            var folders = new HashSet<Guid>();
            foreach (var folder in folderIds.Where(store.Folders.ContainsKey))
                folders.UnionWith(store.Subtree(folder));
            var views = doc.NamedViews.Select(v => v.NamedViewId)
                .Where(id => viewIds.Contains(id) || folders.Contains(store.FolderOf(id)))
                .ToList();

            var file = new File3dm();
            file.Settings.ModelUnitSystem = doc.ModelUnitSystem;
            foreach (var id in views)
                file.NamedViews.Add(doc.NamedViews[IndexOf(doc, id)]);
            store.WriteTo(file.Strings, folders, views);
            if (!file.Write(path, 8))
                throw new IOException("Could not write " + Path.GetFileName(path) + ".");
            return views.Count;
        }

        /// <summary>Exports every named view and folder of the document.</summary>
        public static int ExportAll(RhinoDoc doc, string path)
        {
            var store = FolderStore.Load(doc);
            return Export(doc, path, doc.NamedViews.Select(v => v.NamedViewId).ToList(), store.Folders.Keys.ToList());
        }

        public static File3dm ReadFile(string path)
        {
            return File3dm.Read(path) ?? throw new IOException("Could not read " + Path.GetFileName(path) + ".");
        }

        public sealed class ImportOptions
        {
            /// <summary>Ids (in the source file) of the views to import; null imports everything, empty folders included.</summary>
            public ICollection<Guid> Views;
            /// <summary>Into a new folder named after the file (inside the target), or straight into the target folder.</summary>
            public bool IntoNewFolder = true;
            /// <summary>Replace views with the same name (they keep their id and folder) instead of adding "Name 2".</summary>
            public bool Replace;
        }

        /// <summary>
        /// Imports named views from a .3dm, keeping the file's own folders (if it was organised with this
        /// plug-in). Only the folders that hold imported views are created. Returns the folder that received
        /// them (Guid.Empty = top level), or null if nothing was imported.
        /// </summary>
        public static Guid? Import(RhinoDoc doc, File3dm file, string fileName, Guid target, ImportOptions options)
        {
            var views = file.NamedViews.Where(v => options.Views == null || options.Views.Contains(v.NamedViewId)).ToList();
            if (views.Count == 0)
                return null;

            var source = FolderStore.Load(file);
            var store = FolderStore.Load(doc);
            if (!store.Folders.ContainsKey(target))
                target = Guid.Empty;
            // Views that replace one of the same name keep that view's place, so they need no folders.
            bool Replaces(Rhino.DocObjects.ViewInfo info) => options.Replace && doc.NamedViews.FindByName(info.Name) >= 0;
            var added = views.Where(v => !Replaces(v)).ToList();

            // Only the folders the added views live in, plus, for Import All, the file's empty folders.
            var needed = new HashSet<Guid>();
            void WithParents(Guid folder)
            {
                for (var f = folder; f != Guid.Empty && source.Folders.ContainsKey(f) && needed.Add(f); f = source.Folders[f].Parent)
                {
                }
            }
            foreach (var view in added)
                WithParents(source.FolderOf(view.NamedViewId));
            if (options.Views == null)
            {
                var holding = new HashSet<Guid>();
                foreach (var view in views)
                {
                    for (var f = source.FolderOf(view.NamedViewId); f != Guid.Empty && holding.Add(f); f = source.Folders[f].Parent)
                    {
                    }
                }
                foreach (var folder in source.Folders.Keys.Where(f => !holding.Contains(f)))
                    WithParents(folder);
            }
            var top = options.IntoNewFolder && (added.Count > 0 || needed.Count > 0) ? store.AddFolder(fileName, target).Id : target;

            // Recreate the folders level by level in their custom order, so siblings keep their order.
            var folderMap = new Dictionary<Guid, Guid> { [Guid.Empty] = top };
            void CopyFolders(Guid sourceParent)
            {
                foreach (var folder in source.ChildFolders(sourceParent).Where(f => needed.Contains(f.Id)).OrderBy(f => f.Order).ThenBy(f => f.Name, NaturalComparer.Instance))
                {
                    folderMap[folder.Id] = store.AddFolder(folder.Name, folderMap[sourceParent]).Id;
                    CopyFolders(folder.Id);
                }
            }
            CopyFolders(Guid.Empty);

            var appended = new List<(Guid Id, double Order)>();
            foreach (var info in views)
            {
                var sourceId = info.NamedViewId;
                if (Replaces(info))
                {
                    var id = doc.NamedViews[doc.NamedViews.FindByName(info.Name)].NamedViewId;
                    if (doc.NamedViews.Add(info) >= 0)
                        Thumbnails.Set(doc, id, null);
                    continue;
                }
                var copy = AddCopy(doc, info, info.Name);
                if (copy == Guid.Empty)
                    continue;
                var folder = folderMap.TryGetValue(source.FolderOf(sourceId), out var mapped) ? mapped : top;
                if (folder == top && !options.IntoNewFolder)
                    appended.Add((copy, source.OrderOf(sourceId)));
                else
                    store.SetPlace(copy, folder, source.OrderOf(sourceId));
            }
            // Views that land in an existing folder go to its end, in the file's order.
            if (appended.Count > 0)
                MoveViews(doc, appended.OrderBy(v => double.IsNaN(v.Order) ? double.MaxValue : v.Order).Select(v => v.Id).ToList(), top, Guid.Empty);
            return top;
        }

        #endregion
    }
}
