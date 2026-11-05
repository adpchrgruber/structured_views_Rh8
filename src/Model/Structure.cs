using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;

namespace StructuredViews
{
    /// <summary>A view or a folder: what a folder (or the top level) contains, in one shared order.</summary>
    public interface IItem
    {
        Guid Id { get; }
        string Name { get; }
    }

    public sealed class ViewEntry : IItem
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        /// <summary>Index in the named-view table when the structure was built.</summary>
        public int Index;
        public Guid Folder;
    }

    public sealed class FolderNode : IItem
    {
        public Folder Folder;
        public int Depth;
        /// <summary>Views and subfolders in display order, mixed.</summary>
        public List<IItem> Items = new List<IItem>();
        public IEnumerable<ViewEntry> Views => Items.OfType<ViewEntry>();
        public IEnumerable<FolderNode> Children => Items.OfType<FolderNode>();
        /// <summary>Views in this folder and all folders below it.</summary>
        public int Total;
        public Guid Id => Folder.Id;
        public string Name => Folder.Name;
    }

    /// <summary>
    /// The named views of a document (or file) in their folders, in display order. Each folder and the top
    /// level hold one list of views and subfolders. Custom order uses the stored order, which views and
    /// folders share; views without one (saved by Rhino's own panel) follow at the end in table order.
    /// Files from before the shared order showed a level's views first and its folders after; they keep
    /// that look until the first reorder (see ViewOps.MixedStructure). Name order mixes views and folders.
    /// </summary>
    public sealed class Structure
    {
        public FolderStore Store { get; private set; }
        public List<IItem> RootItems { get; } = new List<IItem>();
        public Dictionary<Guid, FolderNode> FolderNodes { get; } = new Dictionary<Guid, FolderNode>();
        public int ViewCount { get; private set; }
        public IEnumerable<FolderNode> RootFolders => RootItems.OfType<FolderNode>();

        public static Structure Build(RhinoDoc doc, SortMode sort)
        {
            return Build(FolderStore.Load(doc), doc.NamedViews.Select(v => (v.NamedViewId, v.Name)).ToList(), sort);
        }

        /// <summary>Builds the structure of any set of named views (a document's, or a file's for import).</summary>
        public static Structure Build(FolderStore store, IList<(Guid Id, string Name)> views, SortMode sort)
        {
            var structure = new Structure { Store = store, ViewCount = views.Count };
            foreach (var folder in store.Folders.Values)
                structure.FolderNodes[folder.Id] = new FolderNode { Folder = folder };

            var contents = new Dictionary<Guid, List<IItem>> { [Guid.Empty] = structure.RootItems };
            foreach (var node in structure.FolderNodes.Values)
                contents[node.Id] = node.Items;
            foreach (var node in structure.FolderNodes.Values)
                contents[node.Folder.Parent].Add(node);
            for (int i = 0; i < views.Count; i++)
            {
                var view = new ViewEntry { Id = views[i].Id, Name = views[i].Name, Index = i, Folder = store.FolderOf(views[i].Id) };
                contents[view.Folder].Add(view);
            }
            foreach (var items in contents.Values)
                Sort(items, store, sort);

            void Walk(FolderNode node, int depth)
            {
                node.Depth = depth;
                foreach (var child in node.Children)
                    Walk(child, depth + 1);
                node.Total = node.Views.Count() + node.Children.Sum(c => c.Total);
            }
            foreach (var node in structure.RootFolders)
                Walk(node, 0);
            return structure;
        }

        static void Sort(List<IItem> items, FolderStore store, SortMode sort)
        {
            if (sort != SortMode.Custom)
            {
                var sign = sort == SortMode.NameDescending ? -1 : 1;
                items.Sort((a, b) =>
                {
                    var c = sign * NaturalComparer.Instance.Compare(a.Name, b.Name);
                    return c != 0 ? c : (a is FolderNode ? 0 : 1).CompareTo(b is FolderNode ? 0 : 1);
                });
                return;
            }
            // Rank 0: views and folders with a shared order; 1: views without one; 2: folders of a file that
            // has no shared order yet (they come after its views, as before).
            var mixed = store.IsMixed;
            (int Rank, double Key, int Index) Key(IItem item)
            {
                if (item is FolderNode f)
                    return (mixed ? 0 : 2, f.Folder.Order, int.MaxValue);
                var v = (ViewEntry)item;
                var order = store.OrderOf(v.Id);
                return double.IsNaN(order) ? (1, 0, v.Index) : (0, order, v.Index);
            }
            var keyed = items.Select(i => (Item: i, Key: Key(i))).ToList();
            keyed.Sort((a, b) =>
            {
                var c = a.Key.Rank.CompareTo(b.Key.Rank);
                if (c == 0) c = a.Key.Key.CompareTo(b.Key.Key);
                if (c == 0) c = a.Key.Index.CompareTo(b.Key.Index);
                return c != 0 ? c : NaturalComparer.Instance.Compare(a.Item.Name, b.Item.Name);
            });
            items.Clear();
            items.AddRange(keyed.Select(k => k.Item));
        }

        /// <summary>What a folder (Guid.Empty = top level) contains, views and subfolders, in display order.</summary>
        public List<IItem> ItemsIn(Guid folder)
        {
            return folder == Guid.Empty ? RootItems : FolderNodes.TryGetValue(folder, out var node) ? node.Items : new List<IItem>();
        }

        /// <summary>All views in display order, folders expanded.</summary>
        public IEnumerable<ViewEntry> AllViews()
        {
            IEnumerable<ViewEntry> Walk(IEnumerable<IItem> items) => items.SelectMany(i => i is FolderNode f ? Walk(f.Items) : new[] { (ViewEntry)i });
            return Walk(RootItems);
        }

        /// <summary>Every item in display order, folders expanded after themselves.</summary>
        public IEnumerable<IItem> AllItems()
        {
            IEnumerable<IItem> Walk(IEnumerable<IItem> items) => items.SelectMany(i => i is FolderNode f ? new[] { i }.Concat(Walk(f.Items)) : new[] { i });
            return Walk(RootItems);
        }
    }
}
