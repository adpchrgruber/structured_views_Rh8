using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.UI;
using Command = Rhino.Commands.Command;
using RhinoDialogs = Rhino.UI.Dialogs;

namespace StructuredViews
{
    /// <summary>
    /// The Structured Views panel: Rhino's Named Views panel (save, restore, update, rename, duplicate,
    /// delete, properties, copy/paste, import, list/thumbnail modes, sorting, animation settings) with
    /// folders. Rhino raises no event when named views change, so the panel compares a signature on idle.
    /// Every UI callback goes through <see cref="Safe"/>: on Mac an exception escaping an Eto handler aborts Rhino.
    /// </summary>
    [Guid("a2d88ec3-48bf-4758-b46b-ba822b0b949f")]
    public sealed class StructuredViewsPanel : Eto.Forms.Panel, IPanel
    {
        public static Guid PanelId => typeof(StructuredViewsPanel).GUID;

        // Folder expansion per document, so it survives the panel being rebuilt or re-shown.
        static readonly Dictionary<uint, HashSet<Guid>> s_collapsed = new Dictionary<uint, HashSet<Guid>>();

        readonly uint _serial;
        readonly ViewCanvas _canvas;
        readonly Scrollable _scroll;
        readonly IconButton _mode;
        readonly IconButton _restore;
        readonly IconButton _delete;
        readonly IconButton _copy;
        readonly IconButton _paste;
        readonly IconButton _duplicate;
        readonly IconButton _up;
        readonly IconButton _down;
        readonly CheckBox _autoUpdate;
        readonly CheckBox _aspectRatio;
        readonly Dictionary<Guid, Bitmap> _images = new Dictionary<Guid, Bitmap>();
        readonly HashSet<Guid> _captureFailed = new HashSet<Guid>();
        readonly Stopwatch _idleClock = Stopwatch.StartNew();
        readonly Stopwatch _sinceModelChange = Stopwatch.StartNew();
        string _signature;
        bool _dirty = true;
        bool _subscribed;

        public StructuredViewsPanel(uint documentSerialNumber)
        {
            _serial = documentSerialNumber;
            MinimumSize = new Size(260, 320);

            _canvas = new ViewCanvas { ImageFor = ImageFor };
            _canvas.ViewActivated += id => Safe("restore", () => RestoreView(id));
            _canvas.MenuRequested += p => Safe("menu", () => ShowMenu(BuildContextMenu(), _canvas, p));
            _canvas.DropRequested += d => Safe("move", () => Drop(d));
            _canvas.SelectionChanged += () => Safe("selection", UpdateToolbar);
            _canvas.KeyDown += (s, e) => Safe("key", () => OnKey(e));
            _scroll = new Scrollable { Content = _canvas, Border = BorderType.None, ExpandContentWidth = true, ExpandContentHeight = false };

            // The toolbar of Rhino's Named Views panel (minus widget, edit-by-looking and help), plus New Folder.
            var save = Tool(Icon.SaveView, "Save the current viewport as a named view", SaveView);
            var folder = Tool(Icon.NewFolder, "New folder", NewFolder);
            _restore = Tool(Icon.Restore, "Restore the selected view to the current viewport", () => RestoreView(_canvas.SelectedViews.FirstOrDefault()));
            _delete = Tool(Icon.Delete, "Delete", DeleteSelected);
            _copy = Tool(Icon.Copy, "Copy", CopySelected);
            _paste = Tool(Icon.Paste, "Paste", Paste);
            var import = Tool(Icon.Import, "Import named views from a 3dm file", ImportViews);
            _duplicate = Tool(Icon.Duplicate, "Duplicate", DuplicateSelected);
            _up = Tool(Icon.MoveUp, "Move up (custom order)", () => Nudge(-1));
            _down = Tool(Icon.MoveDown, "Move down (custom order)", () => Nudge(1));
            _mode = Tool(Icon.List, "Switch between thumbnails and list", () => Settings.ViewMode = Settings.ViewMode == ViewMode.List ? ViewMode.Thumbnails : ViewMode.List);
            var more = Tool(Icon.More, "Options", null);
            more.Click += (s, e) => Safe("menu", () => ShowMenu(OptionsMenu(), more, new PointF(0, more.Height)));
            var toolbar = new ToolStrip(
                new Control[] { save, folder, Divider(), _restore, _delete, _copy, _paste, import, Divider(), _duplicate, _up, _down },
                new Control[] { _mode, more });

            // The options Rhino's panel shows below its list.
            _autoUpdate = new CheckBox { Text = "Auto-update thumbnails" };
            _autoUpdate.CheckedChanged += (s, e) => Safe("option", () => { if (Settings.AutoUpdateThumbnails != (_autoUpdate.Checked == true)) Settings.AutoUpdateThumbnails = _autoUpdate.Checked == true; });
            _aspectRatio = new CheckBox { Text = "Restore aspect ratio" };
            _aspectRatio.CheckedChanged += (s, e) => Safe("option", () => { if (Settings.RestoreAspectRatio != (_aspectRatio.Checked == true)) Settings.RestoreAspectRatio = _aspectRatio.Checked == true; });
            var options = new StackLayout { Padding = new Padding(8, 6), Spacing = 4, Items = { _autoUpdate, _aspectRatio } };

            Content = new TableLayout { Rows = { toolbar, Rule(), new TableRow(_scroll) { ScaleHeight = true }, Rule(), options } };
            ApplySettings();
        }

        IconButton Tool(Icon icon, string tip, Action action)
        {
            var button = new IconButton(icon, tip);
            if (action != null)
                button.Click += (s, e) => Safe(tip, action);
            return button;
        }

        static Control Divider()
        {
            var divider = new Drawable { Size = new Size(9, 18) };
            divider.Paint += (s, e) => Safe("drawing", () => e.Graphics.FillRectangle(new Color(Theme.Text, 0.15f), 4, 2, 1, 14));
            return divider;
        }

        static Control Rule()
        {
            var rule = new Drawable { Height = 1 };
            rule.Paint += (s, e) => Safe("drawing", () => e.Graphics.FillRectangle(new Color(Theme.Text, 0.1f), 0, 0, rule.Width, 1));
            return rule;
        }

        static void Safe(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("Structured Views: {0} failed: {1}", what, ex.Message);
            }
        }

        /// <summary>The panel's document. Mac has one panel per document window; Windows has one document.</summary>
        RhinoDoc Doc => (Rhino.Runtime.HostUtils.RunningOnOSX ? RhinoDoc.FromRuntimeSerialNumber(_serial) : null) ?? RhinoDoc.ActiveDoc;

        #region Live updates

        protected override void OnLoadComplete(EventArgs e)
        {
            base.OnLoadComplete(e);
            Subscribe(true);
            Rebuild();
        }

        protected override void OnUnLoad(EventArgs e)
        {
            Subscribe(false);
            base.OnUnLoad(e);
        }

        void Subscribe(bool on)
        {
            if (on == _subscribed)
                return;
            _subscribed = on;
            if (on)
            {
                RhinoApp.Idle += OnIdle;
                Command.EndCommand += OnEndCommand;
                RhinoDoc.UserStringChanged += OnUserStringChanged;
                RhinoDoc.AddRhinoObject += OnObjectEvent;
                RhinoDoc.DeleteRhinoObject += OnObjectEvent;
                RhinoDoc.ReplaceRhinoObject += OnReplaceObject;
                RhinoDoc.ModifyObjectAttributes += OnModifyAttributes;
                Thumbnails.Changed += OnThumbnailChanged;
                Settings.Changed += OnSettingsChanged;
            }
            else
            {
                RhinoApp.Idle -= OnIdle;
                Command.EndCommand -= OnEndCommand;
                RhinoDoc.UserStringChanged -= OnUserStringChanged;
                RhinoDoc.AddRhinoObject -= OnObjectEvent;
                RhinoDoc.DeleteRhinoObject -= OnObjectEvent;
                RhinoDoc.ReplaceRhinoObject -= OnReplaceObject;
                RhinoDoc.ModifyObjectAttributes -= OnModifyAttributes;
                Thumbnails.Changed -= OnThumbnailChanged;
                Settings.Changed -= OnSettingsChanged;
            }
        }

        void OnEndCommand(object sender, CommandEventArgs e) => _dirty = true;

        void OnUserStringChanged(object sender, EventArgs e) => _dirty = true;

        void OnObjectEvent(object sender, RhinoObjectEventArgs e) => ModelChanged(e.TheObject?.Document);

        void OnReplaceObject(object sender, RhinoReplaceObjectEventArgs e) => ModelChanged(e.Document);

        void OnModifyAttributes(object sender, RhinoModifyObjectAttributesEventArgs e) => ModelChanged(e.Document);

        /// <summary>The model changed: thumbnails are redone once things have been quiet for a moment.</summary>
        void ModelChanged(RhinoDoc doc)
        {
            if (doc == null || doc.RuntimeSerialNumber != Doc?.RuntimeSerialNumber || !Settings.AutoUpdateThumbnails)
                return;
            Thumbnails.MarkStale(doc);
            _sinceModelChange.Restart();
        }

        void OnThumbnailChanged(uint serial, Guid id)
        {
            if (serial != Doc?.RuntimeSerialNumber)
                return;
            if (_images.TryGetValue(id, out var old))
            {
                old.Dispose();
                _images.Remove(id);
            }
            _canvas.Invalidate();
        }

        void OnSettingsChanged() => Safe("settings", ApplySettings);

        void OnIdle(object sender, EventArgs e)
        {
            if (_idleClock.ElapsedMilliseconds < 300)
                return;
            _idleClock.Restart();
            Safe("refresh", () =>
            {
                var doc = Doc;
                if (_dirty || Signature(doc) != _signature)
                    Rebuild();
                else if (doc != null)
                    CaptureOneThumbnail(doc);
            });
        }

        static string Signature(RhinoDoc doc)
        {
            if (doc == null)
                return "";
            var parts = new List<string> { doc.RuntimeSerialNumber.ToString() };
            foreach (var view in doc.NamedViews)
                parts.Add(view.NamedViewId.ToString("N") + view.Name);
            foreach (var entry in doc.Strings.GetEntryNames(FolderStore.Section) ?? Array.Empty<string>())
                parts.Add(entry + doc.Strings.GetValue(FolderStore.Section, entry));
            return string.Join("\n", parts);
        }

        /// <summary>
        /// Captures one missing or stale thumbnail per idle tick. Never during a command, while a mouse
        /// button is down or shortly after a model change: it borrows a viewport.
        /// </summary>
        void CaptureOneThumbnail(RhinoDoc doc)
        {
            if (Command.InCommand() || Mouse.Buttons != MouseButtons.None || _sinceModelChange.ElapsedMilliseconds < 1500)
                return;
            for (int i = 0; i < doc.NamedViews.Count; i++)
            {
                var id = doc.NamedViews[i].NamedViewId;
                if (_captureFailed.Contains(id) || !Thumbnails.NeedsCapture(doc, id))
                    continue;
                var png = Thumbnails.CaptureNamedView(doc, i);
                if (png == null)
                    _captureFailed.Add(id);
                else
                    Thumbnails.Set(doc, id, png);
                return;
            }
        }

        Image ImageFor(Guid id)
        {
            if (_images.TryGetValue(id, out var image))
                return image;
            var doc = Doc;
            var png = doc == null ? null : Thumbnails.Get(doc, id);
            if (png == null)
                return null;
            try
            {
                image = new Bitmap(png);
            }
            catch (Exception)
            {
                return null;
            }
            _images[id] = image;
            return image;
        }

        void Rebuild()
        {
            _dirty = false;
            var doc = Doc;
            _signature = Signature(doc);
            if (doc == null)
                return;
            if (!s_collapsed.TryGetValue(doc.RuntimeSerialNumber, out var collapsed))
                s_collapsed[doc.RuntimeSerialNumber] = collapsed = new HashSet<Guid>();
            _canvas.Collapsed = collapsed;
            _canvas.SetStructure(Structure.Build(doc, Settings.Sort));
            UpdateToolbar();
        }

        void ApplySettings()
        {
            _mode.Icon = Settings.ViewMode == ViewMode.List ? Icon.Thumbnails : Icon.List;
            _autoUpdate.Checked = Settings.AutoUpdateThumbnails;
            _aspectRatio.Checked = Settings.RestoreAspectRatio;
            _canvas.SetPresentation(Settings.ViewMode, Settings.ThumbnailSize, Settings.Sort);
            _dirty = true;
            UpdateToolbar();
        }

        void UpdateToolbar()
        {
            var views = _canvas.SelectedViews.Count();
            var any = views + _canvas.SelectedFolders.Count() > 0;
            var custom = Settings.Sort == SortMode.Custom;
            void Enable(IconButton button, bool enabled)
            {
                if (button.Enabled == enabled)
                    return;
                button.Enabled = enabled;
                button.Invalidate();
            }
            Enable(_restore, views == 1);
            Enable(_delete, any);
            Enable(_copy, views > 0);
            Enable(_paste, ViewOps.CanPaste);
            Enable(_duplicate, views > 0);
            Enable(_up, any && custom);
            Enable(_down, any && custom);
        }

        #endregion

        #region Actions

        void Refresh(IEnumerable<Guid> select = null)
        {
            Rebuild();
            if (select != null)
                _canvas.Select(select);
        }

        void RestoreView(Guid id)
        {
            var doc = Doc;
            if (doc != null && id != Guid.Empty)
                ViewOps.Restore(doc, id, doc.Views.ActiveView?.ActiveViewport);
        }

        void RestoreFloating(Guid id)
        {
            var doc = Doc;
            if (doc != null)
                ViewOps.RestoreFloating(doc, id);
        }

        void SaveView()
        {
            var doc = Doc;
            var view = doc?.Views.ActiveView;
            if (view == null)
                return;
            var name = Prompt("Save Named View", "Name of the new named view:", ViewOps.UniqueName(doc, "Named View"));
            if (name == null)
                return;
            if (doc.NamedViews.FindByName(name) >= 0 && !Confirm($"There is already a named view called \"{name}\". Replace it?"))
                return;
            var folder = _canvas.TargetFolder;
            var id = ViewOps.Save(doc, view, name, folder);
            if (folder != Guid.Empty)
                _canvas.Collapsed.Remove(folder);
            Refresh(new[] { id });
        }

        void UpdateView(Guid id)
        {
            var doc = Doc;
            var view = doc?.Views.ActiveView;
            if (view != null && ViewOps.Update(doc, id, view))
                Refresh(new[] { id });
        }

        void NewFolder()
        {
            var doc = Doc;
            if (doc == null)
                return;
            var parent = _canvas.TargetFolder;
            var name = Prompt("New Folder", "Folder name:", "New Folder");
            if (name == null)
                return;
            var folder = FolderStore.Load(doc).AddFolder(name, parent);
            _canvas.Collapsed.Remove(parent);
            Refresh(new[] { folder.Id });
        }

        void RenameSelected()
        {
            var doc = Doc;
            if (doc == null)
                return;
            var folder = _canvas.SelectedFolders.FirstOrDefault();
            var view = _canvas.SelectedViews.FirstOrDefault();
            if (folder != Guid.Empty && view == Guid.Empty)
            {
                var store = FolderStore.Load(doc);
                var name = Prompt("Rename Folder", "New name:", store.Folders[folder].Name);
                if (name != null)
                    store.RenameFolder(folder, name);
            }
            else if (view != Guid.Empty)
            {
                var index = ViewOps.IndexOf(doc, view);
                var name = index < 0 ? null : Prompt("Rename Named View", "New name:", doc.NamedViews[index].Name);
                if (name != null && !ViewOps.Rename(doc, view, name))
                    RhinoDialogs.ShowMessage($"There is already a named view called \"{name}\".", "Structured Views");
            }
            Refresh();
        }

        void DuplicateSelected()
        {
            var doc = Doc;
            var views = _canvas.SelectedViews.ToList();
            if (doc != null && views.Count > 0)
                Refresh(ViewOps.Duplicate(doc, views));
        }

        void DeleteSelected()
        {
            var doc = Doc;
            var views = _canvas.SelectedViews.ToList();
            var folders = _canvas.SelectedFolders.ToList();
            if (doc == null || views.Count + folders.Count == 0)
                return;
            var store = FolderStore.Load(doc);
            var parts = new List<string>();
            if (views.Count > 0)
                parts.Add(views.Count == 1 ? $"the named view \"{doc.NamedViews[ViewOps.IndexOf(doc, views[0])].Name}\"" : $"{views.Count} named views");
            if (folders.Count > 0)
                parts.Add(folders.Count == 1 ? $"the folder \"{store.Folders[folders[0]].Name}\"" : $"{folders.Count} folders");
            var message = "Delete " + string.Join(" and ", parts) + "?";
            if (folders.Count > 0)
                message += "\n\nThe contents of a deleted folder are kept and move up one level.";
            if (!Confirm(message))
                return;
            ViewOps.Delete(doc, views);
            store = FolderStore.Load(doc);
            foreach (var folder in folders)
                store.DeleteFolder(folder);
            Refresh(Array.Empty<Guid>());
        }

        void ShowProperties(Guid id)
        {
            var doc = Doc;
            var index = doc == null ? -1 : ViewOps.IndexOf(doc, id);
            if (index < 0)
                return;
            var info = doc.NamedViews[index];
            var viewport = info.Viewport;
            var dialog = new ViewPropertiesDialog(info.Name, viewport.CameraLocation, viewport.TargetPoint, viewport.Camera35mmLensLength,
                viewport.IsPerspectiveProjection, doc.GetUnitSystemName(true, false, false, true));
            if (!dialog.ShowModal(this))
                return;
            var tolerance = doc.ModelAbsoluteTolerance;
            if (dialog.CameraLocation.DistanceTo(viewport.CameraLocation) > tolerance || dialog.CameraTarget.DistanceTo(viewport.TargetPoint) > tolerance
                || Math.Abs(dialog.LensLength - viewport.Camera35mmLensLength) > 1e-3)
            {
                if (!ViewOps.SetCamera(doc, id, dialog.CameraLocation, dialog.CameraTarget, dialog.LensLength))
                    RhinoDialogs.ShowMessage("The camera and target must be different points.", "Structured Views");
            }
            if (dialog.ViewName.Trim() != info.Name && !ViewOps.Rename(doc, id, dialog.ViewName))
                RhinoDialogs.ShowMessage($"There is already a named view called \"{dialog.ViewName.Trim()}\".", "Structured Views");
            Refresh(new[] { id });
        }

        void CopySelected()
        {
            var doc = Doc;
            if (doc != null)
                ViewOps.Copy(doc, _canvas.SelectedViews);
            UpdateToolbar();
        }

        void Nudge(int step)
        {
            var doc = Doc;
            if (doc == null || Settings.Sort != SortMode.Custom)
                return;
            var views = _canvas.SelectedViews.ToList();
            var folders = _canvas.SelectedFolders.ToList();
            ViewOps.Nudge(doc, views, folders, step);
            Refresh(views.Concat(folders).ToList());
        }

        void Paste()
        {
            var doc = Doc;
            if (doc == null || !ViewOps.CanPaste)
                return;
            var folder = _canvas.TargetFolder;
            _canvas.Collapsed.Remove(folder);
            Refresh(ViewOps.Paste(doc, folder));
        }

        void ImportViews()
        {
            var doc = Doc;
            if (doc == null)
                return;
            var dialog = new Eto.Forms.OpenFileDialog { Title = "Import Named Views", Filters = { new FileFilter("Rhino 3D Models", ".3dm") } };
            if (dialog.ShowDialog(this) != DialogResult.Ok)
                return;
            Guid folder;
            try
            {
                folder = ViewOps.Import(doc, dialog.FileName, _canvas.TargetFolder);
            }
            catch (Exception ex)
            {
                RhinoDialogs.ShowMessage(ex.Message, "Structured Views");
                return;
            }
            if (folder == Guid.Empty)
                RhinoDialogs.ShowMessage("That file has no named views.", "Structured Views");
            else
                Refresh(new[] { folder });
        }

        void MoveSelected(Guid folder)
        {
            Drop(new DropRequest { Views = _canvas.SelectedViews.ToList(), Folders = _canvas.SelectedFolders.ToList(), Folder = folder });
        }

        void Drop(DropRequest drop)
        {
            var doc = Doc;
            if (doc == null)
                return;
            var views = drop.Views;
            if (drop.Copy && views.Count > 0)
                views = ViewOps.Duplicate(doc, views);
            if (views.Count > 0)
                ViewOps.MoveViews(doc, views, drop.Folder, drop.Before);
            if (drop.Folders.Count > 0 && ViewOps.MoveFolders(doc, drop.Folders, drop.Folder, drop.Before) < drop.Folders.Count)
                RhinoApp.WriteLine("Structured Views: a folder can't move into itself or one of its subfolders.");
            _canvas.Collapsed.Remove(drop.Folder);
            Refresh(views.Concat(drop.Folders).ToList());
        }

        void RefreshThumbnails()
        {
            var doc = Doc;
            if (doc == null)
                return;
            Thumbnails.MarkStale(doc);
            _captureFailed.Clear();
            _sinceModelChange.Restart();
        }

        static string Prompt(string title, string message, string text)
        {
            if (!RhinoDialogs.ShowEditBox(title, message, text, false, out var result))
                return null;
            result = (result ?? "").Trim();
            return result.Length == 0 ? null : result;
        }

        static bool Confirm(string message)
        {
            return RhinoDialogs.ShowMessage(message, "Structured Views", ShowMessageButton.YesNo, ShowMessageIcon.Question) == ShowMessageResult.Yes;
        }

        #endregion

        #region Menus and keys

        static void ShowMenu(ContextMenu menu, Control relativeTo, PointF location)
        {
            // An explicit location: without one Eto Mac needs a current mouse event or key window.
            menu.Show(relativeTo, location);
        }

        static ButtonMenuItem Item(string text, Action action, bool enabled = true)
        {
            var item = new ButtonMenuItem { Text = text, Enabled = enabled };
            item.Click += (s, e) => Safe(text, action);
            return item;
        }

        static CheckMenuItem Check(string text, bool on, Action action)
        {
            var item = new CheckMenuItem { Text = text, Checked = on };
            item.Click += (s, e) => Safe(text, action);
            return item;
        }

        static SubMenuItem Sub(string text, params MenuItem[] items)
        {
            var sub = new SubMenuItem { Text = text };
            sub.Items.AddRange(items);
            return sub;
        }

        ContextMenu BuildContextMenu()
        {
            var doc = Doc;
            var views = _canvas.SelectedViews.ToList();
            var folders = _canvas.SelectedFolders.ToList();
            var one = views.Count == 1 && folders.Count == 0 ? views[0] : Guid.Empty;
            var menu = new ContextMenu();
            if (views.Count + folders.Count > 0)
            {
                menu.Items.Add(Sub("Restore",
                    Item("To Current Viewport", () => RestoreView(one), one != Guid.Empty),
                    Item("To New Floating Viewport", () => RestoreFloating(one), one != Guid.Empty)));
                menu.Items.Add(Item("Update to Current Viewport", () => UpdateView(one), one != Guid.Empty));
                menu.Items.Add(new SeparatorMenuItem());
                menu.Items.Add(Item("Rename…", RenameSelected, views.Count + folders.Count == 1));
                menu.Items.Add(Item("Duplicate", DuplicateSelected, views.Count > 0));
                menu.Items.Add(Item("Delete…", DeleteSelected));
                menu.Items.Add(Item("Properties…", () => ShowProperties(one), one != Guid.Empty));
                menu.Items.Add(new SeparatorMenuItem());
                menu.Items.Add(Item("Copy", CopySelected, views.Count > 0));
            }
            menu.Items.Add(Item("Paste", Paste, ViewOps.CanPaste));
            if (doc != null && views.Count + folders.Count > 0)
                menu.Items.Add(MoveToMenu(doc, folders));
            menu.Items.Add(new SeparatorMenuItem());
            menu.Items.Add(Item("Save Current Viewport as Named View…", SaveView));
            menu.Items.Add(Item("New Folder…", NewFolder));
            menu.Items.Add(Item("Import Named Views from 3dm File…", ImportViews));
            menu.Items.Add(new SeparatorMenuItem());
            menu.Items.Add(Sub("View Mode", ViewModeItems()));
            menu.Items.Add(Sub("Sort By", SortItems()));
            menu.Items.Add(Item("Animation Settings…", ShowAnimationSettings));
            return menu;
        }

        SubMenuItem MoveToMenu(RhinoDoc doc, List<Guid> movingFolders)
        {
            var store = FolderStore.Load(doc);
            var move = Sub("Move to Folder", Item("Top Level", () => MoveSelected(Guid.Empty)));
            foreach (var folder in store.Folders.Values.Select(f => new { f.Id, Path = store.PathOf(f.Id) }).OrderBy(f => f.Path, NaturalComparer.Instance))
            {
                var id = folder.Id;
                move.Items.Add(Item(folder.Path, () => MoveSelected(id), !movingFolders.Any(f => store.IsInside(id, f))));
            }
            return move;
        }

        MenuItem[] ViewModeItems() => new MenuItem[]
        {
            Check("Thumbnails", Settings.ViewMode == ViewMode.Thumbnails, () => Settings.ViewMode = ViewMode.Thumbnails),
            Check("List", Settings.ViewMode == ViewMode.List, () => Settings.ViewMode = ViewMode.List),
            new SeparatorMenuItem(),
            Check("Small", Settings.ThumbnailSize == ThumbnailSize.Small, () => Settings.ThumbnailSize = ThumbnailSize.Small),
            Check("Medium", Settings.ThumbnailSize == ThumbnailSize.Medium, () => Settings.ThumbnailSize = ThumbnailSize.Medium),
            Check("Large", Settings.ThumbnailSize == ThumbnailSize.Large, () => Settings.ThumbnailSize = ThumbnailSize.Large),
        };

        MenuItem[] SortItems() => new MenuItem[]
        {
            Check("Custom Order", Settings.Sort == SortMode.Custom, () => Settings.Sort = SortMode.Custom),
            Check("Name Ascending", Settings.Sort == SortMode.NameAscending, () => Settings.Sort = SortMode.NameAscending),
            Check("Name Descending", Settings.Sort == SortMode.NameDescending, () => Settings.Sort = SortMode.NameDescending),
        };

        ContextMenu OptionsMenu()
        {
            var menu = new ContextMenu();
            menu.Items.Add(Sub("View Mode", ViewModeItems()));
            menu.Items.Add(Sub("Sort By", SortItems()));
            menu.Items.Add(new SeparatorMenuItem());
            menu.Items.Add(Check("Animate Restore", Settings.Animate, () => Settings.Animate = !Settings.Animate));
            menu.Items.Add(Item("Animation Settings…", ShowAnimationSettings));
            menu.Items.Add(new SeparatorMenuItem());
            menu.Items.Add(Item("Refresh Thumbnails", RefreshThumbnails));
            menu.Items.Add(Item("Expand All Folders", () => SetAllFolders(true)));
            menu.Items.Add(Item("Collapse All Folders", () => SetAllFolders(false)));
            return menu;
        }

        void SetAllFolders(bool expand)
        {
            var structure = _canvas.Structure;
            if (structure == null)
                return;
            if (expand)
                _canvas.Collapsed.Clear();
            else
                _canvas.Collapsed.UnionWith(structure.FolderNodes.Keys);
            Rebuild();
        }

        void ShowAnimationSettings()
        {
            new AnimationSettingsDialog().ShowModal(this);
        }

        void OnKey(KeyEventArgs e)
        {
            var command = Application.Instance.CommonModifier;
            var views = _canvas.SelectedViews.ToList();
            var key = e.KeyData;
            if (key == Keys.Enter && views.Count == 1)
                RestoreView(views[0]);
            else if (key == Keys.Delete || key == Keys.Backspace)
                DeleteSelected();
            else if (key == Keys.F2)
                RenameSelected();
            else if (key == (command | Keys.D))
                DuplicateSelected();
            else if (key == (command | Keys.C))
                CopySelected();
            else if (key == (command | Keys.V))
                Paste();
            else if (key == (command | Keys.A))
                _canvas.SelectAll();
            else
                return;
            e.Handled = true;
        }

        #endregion

        #region IPanel

        public void PanelShown(uint documentSerialNumber, ShowPanelReason reason) => _dirty = true;

        public void PanelHidden(uint documentSerialNumber, ShowPanelReason reason)
        {
        }

        public void PanelClosing(uint documentSerialNumber, bool onCloseDocument)
        {
            if (!onCloseDocument)
                return;
            Subscribe(false);
            s_collapsed.Remove(documentSerialNumber);
            foreach (var image in _images.Values)
                image.Dispose();
            _images.Clear();
        }

        #endregion
    }
}
