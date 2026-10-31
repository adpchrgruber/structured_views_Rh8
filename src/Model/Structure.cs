using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;

namespace StructuredViews
{
    public sealed class ViewEntry
    {
        public Guid Id;
        public string Name;
        /// <summary>Index in doc.NamedViews when the structure was built.</summary>
        public int Index;
        public Guid Folder;
    }

    public sealed class FolderNode
    {
        public Folder Folder;
        public int Depth;
        public List<ViewEntry> Views = new List<ViewEntry>();
        public List<FolderNode> Children = new List<FolderNode>();
        /// <summary>Views in this folder and all folders below it.</summary>
        public int Total;
    }

    /// <summary>
    /// The named views of a document arranged in their folders, in display order. Custom order puts views
    /// with an explicit order first (by that order), then the rest in table order, so views saved by
    /// Rhino's own panel show up at the end of their folder.
    /// </summary>
    public sealed class Structure
    {
        public FolderStore Store { get; private set; }
        public List<ViewEntry> RootViews { get; } = new List<ViewEntry>();
        public List<FolderNode> RootFolders { get; } = new List<FolderNode>();
        public Dictionary<Guid, FolderNode> FolderNodes { get; } = new Dictionary<Guid, FolderNode>();
        public int ViewCount { get; private set; }

        public static Structure Build(RhinoDoc doc, SortMode sort)
        {
            var structure = new Structure { Store = FolderStore.Load(doc) };
            var store = structure.Store;

            List<FolderNode> AddFolders(Guid parent, int depth)
            {
                var nodes = new List<FolderNode>();
                foreach (var folder in SortFolders(store.ChildFolders(parent), sort))
                {
                    var node = new FolderNode { Folder = folder, Depth = depth };
                    structure.FolderNodes[folder.Id] = node;
                    nodes.Add(node);
                    node.Children = AddFolders(folder.Id, depth + 1);
                }
                return nodes;
            }
            structure.RootFolders.AddRange(AddFolders(Guid.Empty, 0));

            var all = new List<ViewEntry>();
            int index = 0;
            foreach (var info in doc.NamedViews)
            {
                var id = info.NamedViewId;
                all.Add(new ViewEntry { Id = id, Name = info.Name, Index = index++, Folder = store.FolderOf(id) });
            }
            structure.ViewCount = all.Count;
            foreach (var view in SortViews(all, store, sort))
            {
                if (view.Folder != Guid.Empty && structure.FolderNodes.TryGetValue(view.Folder, out var node))
                    node.Views.Add(view);
                else
                    structure.RootViews.Add(view);
            }

            int Count(FolderNode node) => node.Total = node.Views.Count + node.Children.Sum(Count);
            foreach (var node in structure.RootFolders)
                Count(node);
            return structure;
        }

        static IEnumerable<ViewEntry> SortViews(IEnumerable<ViewEntry> views, FolderStore store, SortMode sort)
        {
            switch (sort)
            {
                case SortMode.NameAscending:
                    return views.OrderBy(v => v.Name, NaturalComparer.Instance);
                case SortMode.NameDescending:
                    return views.OrderByDescending(v => v.Name, NaturalComparer.Instance);
                default:
                    return views
                        .Select(v => new { View = v, Order = store.OrderOf(v.Id) })
                        .OrderBy(x => double.IsNaN(x.Order) ? 1 : 0)
                        .ThenBy(x => double.IsNaN(x.Order) ? 0 : x.Order)
                        .ThenBy(x => x.View.Index)
                        .Select(x => x.View);
            }
        }

        static IEnumerable<Folder> SortFolders(IEnumerable<Folder> folders, SortMode sort)
        {
            switch (sort)
            {
                case SortMode.NameAscending:
                    return folders.OrderBy(f => f.Name, NaturalComparer.Instance);
                case SortMode.NameDescending:
                    return folders.OrderByDescending(f => f.Name, NaturalComparer.Instance);
                default:
                    return folders.OrderBy(f => f.Order).ThenBy(f => f.Name, NaturalComparer.Instance);
            }
        }

        /// <summary>The views directly in a folder (Guid.Empty = top level), in display order.</summary>
        public List<ViewEntry> ViewsIn(Guid folder)
        {
            return folder == Guid.Empty ? RootViews : FolderNodes.TryGetValue(folder, out var node) ? node.Views : new List<ViewEntry>();
        }

        /// <summary>The folders directly in a folder (Guid.Empty = top level), in display order.</summary>
        public List<FolderNode> FoldersIn(Guid parent)
        {
            return parent == Guid.Empty ? RootFolders : FolderNodes.TryGetValue(parent, out var node) ? node.Children : new List<FolderNode>();
        }

        /// <summary>All views in display order, folders expanded.</summary>
        public IEnumerable<ViewEntry> AllViews()
        {
            IEnumerable<ViewEntry> Walk(FolderNode node) => node.Views.Concat(node.Children.SelectMany(Walk));
            return RootViews.Concat(RootFolders.SelectMany(Walk));
        }
    }
}
