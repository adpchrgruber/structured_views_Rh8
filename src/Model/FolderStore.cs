using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino;

namespace StructuredViews
{
    /// <summary>A folder of named views. <see cref="Parent"/> is Guid.Empty at the top level.</summary>
    public sealed class Folder
    {
        public Guid Id;
        public Guid Parent;
        public double Order;
        public string Name;
    }

    /// <summary>
    /// Folders and the place (folder + custom order) of every named view, kept in the document's string
    /// table so they are saved in the .3dm and survive opening the file without the plug-in:
    ///   "f:&lt;folder id&gt;" = "&lt;parent id or empty&gt;|&lt;order&gt;|&lt;name&gt;"
    ///   "v:&lt;named view id&gt;" = "&lt;folder id or empty&gt;|&lt;order&gt;"
    /// Views are keyed by ViewInfo.NamedViewId, which survives rename, update and save. A view without an
    /// entry, or whose folder is gone, sits at the top level in table order.
    /// </summary>
    public sealed class FolderStore
    {
        public const string Section = "StructuredViews";
        const string FolderPrefix = "f:";
        const string ViewPrefix = "v:";

        struct Place
        {
            public Guid Folder;
            public double Order;
        }

        readonly RhinoDoc _doc;
        readonly Dictionary<Guid, Place> _views = new Dictionary<Guid, Place>();

        public Dictionary<Guid, Folder> Folders { get; } = new Dictionary<Guid, Folder>();

        FolderStore(RhinoDoc doc)
        {
            _doc = doc;
        }

        public static FolderStore Load(RhinoDoc doc)
        {
            var store = new FolderStore(doc);
            foreach (var entry in doc.Strings.GetEntryNames(Section) ?? Array.Empty<string>())
                store.Parse(entry, doc.Strings.GetValue(Section, entry));
            store.Repair();
            return store;
        }

        /// <summary>Reads the folder structure of another file (for import), without a document.</summary>
        public static FolderStore Load(Rhino.FileIO.File3dm file)
        {
            var store = new FolderStore(null);
            foreach (var entry in file.Strings.GetEntryNames(Section) ?? Array.Empty<string>())
                store.Parse(entry, file.Strings.GetValue(Section, entry));
            store.Repair();
            return store;
        }

        void Parse(string entry, string value)
        {
            value ??= "";
            if (entry.StartsWith(FolderPrefix) && Guid.TryParse(entry.Substring(FolderPrefix.Length), out var id))
            {
                var parts = value.Split(new[] { '|' }, 3);
                if (parts.Length < 2)
                    return;
                Guid.TryParse(parts[0], out var parent);
                // Older entries were "parent|name".
                var hasOrder = parts.Length == 3 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                Folders[id] = new Folder
                {
                    Id = id,
                    Parent = parent,
                    Order = hasOrder ? double.Parse(parts[1], CultureInfo.InvariantCulture) : 0,
                    Name = hasOrder ? parts[2] : value.Substring(parts[0].Length + 1),
                };
            }
            else if (entry.StartsWith(ViewPrefix) && Guid.TryParse(entry.Substring(ViewPrefix.Length), out var viewId))
            {
                var parts = value.Split('|');
                Guid.TryParse(parts[0], out var folder);
                var order = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var o) ? o : double.NaN;
                _views[viewId] = new Place { Folder = folder, Order = order };
            }
        }

        // A missing parent or a cycle (hand-edited strings, partial import) puts the folder at the top.
        void Repair()
        {
            foreach (var folder in Folders.Values)
            {
                if (folder.Parent != Guid.Empty && (!Folders.ContainsKey(folder.Parent) || IsInside(folder.Parent, folder.Id)))
                    folder.Parent = Guid.Empty;
            }
        }

        #region Queries

        /// <summary>The folder a named view is filed in, or Guid.Empty for the top level.</summary>
        public Guid FolderOf(Guid viewId)
        {
            return _views.TryGetValue(viewId, out var place) && Folders.ContainsKey(place.Folder) ? place.Folder : Guid.Empty;
        }

        /// <summary>The custom order of a view, or NaN when it has none (then table order applies).</summary>
        public double OrderOf(Guid viewId)
        {
            return _views.TryGetValue(viewId, out var place) ? place.Order : double.NaN;
        }

        public IEnumerable<Folder> ChildFolders(Guid parent)
        {
            return Folders.Values.Where(f => f.Parent == parent);
        }

        /// <summary>True if <paramref name="folder"/> is <paramref name="ancestor"/> or lies somewhere below it.</summary>
        public bool IsInside(Guid folder, Guid ancestor)
        {
            var seen = new HashSet<Guid>();
            for (var id = folder; id != Guid.Empty && seen.Add(id); id = Folders.TryGetValue(id, out var f) ? f.Parent : Guid.Empty)
            {
                if (id == ancestor)
                    return true;
            }
            return false;
        }

        public string PathOf(Guid folder)
        {
            var names = new List<string>();
            var seen = new HashSet<Guid>();
            for (var id = folder; id != Guid.Empty && seen.Add(id) && Folders.TryGetValue(id, out var f); id = f.Parent)
                names.Insert(0, f.Name);
            return string.Join(" / ", names);
        }

        #endregion

        #region Changes

        public Folder AddFolder(string name, Guid parent)
        {
            parent = Folders.ContainsKey(parent) ? parent : Guid.Empty;
            var siblings = ChildFolders(parent).ToList();
            var folder = new Folder
            {
                Id = Guid.NewGuid(),
                Parent = parent,
                Order = siblings.Count == 0 ? 0 : siblings.Max(f => f.Order) + 1,
                Name = Clean(name, "Folder"),
            };
            Folders[folder.Id] = folder;
            WriteFolder(folder);
            return folder;
        }

        public void RenameFolder(Guid id, string name)
        {
            if (!Folders.TryGetValue(id, out var folder))
                return;
            folder.Name = Clean(name, folder.Name);
            WriteFolder(folder);
        }

        /// <summary>Moves a folder under another one; refuses to move a folder into itself or below itself.</summary>
        public bool MoveFolder(Guid id, Guid newParent)
        {
            if (!Folders.TryGetValue(id, out var folder))
                return false;
            if (newParent != Guid.Empty && (!Folders.ContainsKey(newParent) || IsInside(newParent, id)))
                return false;
            if (folder.Parent != newParent)
            {
                folder.Parent = newParent;
                var siblings = ChildFolders(newParent).Where(f => f.Id != id).ToList();
                folder.Order = siblings.Count == 0 ? 0 : siblings.Max(f => f.Order) + 1;
                WriteFolder(folder);
            }
            return true;
        }

        /// <summary>Writes the given sibling order (0, 1, 2, …) for folders that share a parent.</summary>
        public void SetFolderOrder(IList<Guid> ordered)
        {
            for (int i = 0; i < ordered.Count; i++)
            {
                if (Folders.TryGetValue(ordered[i], out var folder) && folder.Order != i)
                {
                    folder.Order = i;
                    WriteFolder(folder);
                }
            }
        }

        /// <summary>Removes a folder. Its views and subfolders move up into its parent.</summary>
        public void DeleteFolder(Guid id)
        {
            if (!Folders.TryGetValue(id, out var folder))
                return;
            foreach (var child in ChildFolders(id).ToList())
            {
                child.Parent = folder.Parent;
                WriteFolder(child);
            }
            foreach (var view in _views.Where(kv => kv.Value.Folder == id).Select(kv => kv.Key).ToList())
                SetPlace(view, folder.Parent, double.NaN);
            Folders.Remove(id);
            _doc?.Strings.Delete(Section, FolderPrefix + id.ToString("D"));
        }

        /// <summary>Files a view in a folder (Guid.Empty = top level) with an optional custom order (NaN = none).</summary>
        public void SetPlace(Guid viewId, Guid folder, double order)
        {
            if (viewId == Guid.Empty)
                return;
            if (!Folders.ContainsKey(folder))
                folder = Guid.Empty;
            var key = ViewPrefix + viewId.ToString("D");
            if (folder == Guid.Empty && double.IsNaN(order))
            {
                if (_views.Remove(viewId))
                    _doc?.Strings.Delete(Section, key);
                return;
            }
            if (_views.TryGetValue(viewId, out var current) && current.Folder == folder && current.Order.Equals(order))
                return;
            _views[viewId] = new Place { Folder = folder, Order = order };
            var value = (folder == Guid.Empty ? "" : folder.ToString("D"))
                        + (double.IsNaN(order) ? "" : "|" + order.ToString("R", CultureInfo.InvariantCulture));
            _doc?.Strings.SetString(Section, key, value);
        }

        public void SetFolder(Guid viewId, Guid folder)
        {
            SetPlace(viewId, folder, FolderOf(viewId) == folder ? OrderOf(viewId) : double.NaN);
        }

        /// <summary>Drops the entry of a view that no longer exists.</summary>
        public void Forget(Guid viewId)
        {
            if (_views.Remove(viewId))
                _doc?.Strings.Delete(Section, ViewPrefix + viewId.ToString("D"));
        }

        void WriteFolder(Folder folder)
        {
            var parent = folder.Parent == Guid.Empty ? "" : folder.Parent.ToString("D");
            _doc?.Strings.SetString(Section, FolderPrefix + folder.Id.ToString("D"),
                parent + "|" + folder.Order.ToString("R", CultureInfo.InvariantCulture) + "|" + folder.Name);
        }

        static string Clean(string name, string fallback)
        {
            name = (name ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return name.Length == 0 ? fallback : name;
        }

        #endregion
    }

    /// <summary>Orders "View 2" before "View 10", ignoring case.</summary>
    public sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new NaturalComparer();

        public int Compare(string a, string b)
        {
            a ??= "";
            b ??= "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    var na = a.Substring(si, i - si).TrimStart('0');
                    var nb = b.Substring(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length)
                        return na.Length.CompareTo(nb.Length);
                    var c = string.CompareOrdinal(na, nb);
                    if (c != 0)
                        return c;
                }
                else
                {
                    var c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                    if (c != 0)
                        return c;
                    i++;
                    j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}
