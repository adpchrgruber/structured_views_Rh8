using System;
using System.Collections.Generic;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;

namespace StructuredViews
{
    /// <summary>Where a drag would land: into <see cref="Folder"/>, before <see cref="Before"/> (Guid.Empty = at the end).</summary>
    public sealed class DropRequest
    {
        public List<Guid> Views = new List<Guid>();
        public List<Guid> Folders = new List<Guid>();
        public Guid Folder;
        public Guid Before;
        public bool Copy;
    }

    /// <summary>
    /// The sectioned gallery. Every folder is a full-width header (chevron, name, count); its views flow
    /// underneath as an evenly spaced grid (or compact rows in list mode), then its subfolders. Nesting is
    /// shown by indenting the header and content plus a hairline guide, so thumbnails stay aligned in
    /// columns instead of stair-stepping. Top-level views come first, without a header.
    /// Drawn by hand: one control, crisp at any scale, identical on Mac and Windows, and drag-and-drop that
    /// doesn't depend on the platform's tree view.
    /// </summary>
    public sealed class ViewCanvas : Drawable
    {
        const float Pad = 10;
        const float Indent = 14;
        const float Gap = 10;
        const float HeaderHeight = 28;
        const float LabelHeight = 20;
        const float DragThreshold = 4;
        const float ThumbAspect = (float)Thumbnails.Height / Thumbnails.Width;

        #region Layout elements

        abstract class Element
        {
            public RectangleF Bounds;
        }

        sealed class HeaderElement : Element
        {
            public FolderNode Node;
            public bool Expanded;
            public float TextLeft;
            public Guid Id => Node.Folder.Id;
        }

        sealed class ViewElement : Element
        {
            public ViewEntry View;
            public RectangleF Thumb;
            public RectangleF Label;
            public SectionElement Section;
        }

        /// <summary>A run of consecutive views in one level (or an empty folder's placeholder).</summary>
        sealed class SectionElement : Element
        {
            public Guid Folder;
            public HeaderElement Header;
            public List<ViewElement> Views = new List<ViewElement>();
            public bool Placeholder;
            public RectangleF HitBounds;
            /// <summary>The item right after this run in its level (a folder), or Guid.Empty at the end.</summary>
            public Guid Next;
        }

        /// <summary>The area an open folder's contents take; a drop there that hits nothing else goes in.</summary>
        sealed class ContainerElement : Element
        {
            public Guid Folder;
            public HeaderElement Header;
        }

        #endregion

        Structure _structure;
        readonly List<HeaderElement> _headers = new List<HeaderElement>();
        readonly List<SectionElement> _sections = new List<SectionElement>();
        readonly List<ContainerElement> _containers = new List<ContainerElement>();
        readonly List<Element> _order = new List<Element>();
        readonly List<(float X, float Top, float Bottom)> _guides = new List<(float, float, float)>();
        readonly List<float> _separators = new List<float>();
        float _layoutWidth = -1;
        bool _empty;

        ViewMode _mode = ViewMode.Thumbnails;
        ThumbnailSize _size = ThumbnailSize.Medium;
        SortMode _sort = SortMode.Custom;

        Element _hover;
        Element _pressed;
        PointF _pressPoint;
        bool _pressWasSelected;
        bool _dragging;
        PointF _dragPoint;
        DropRequest _drop;
        RectangleF _dropMarker;
        HeaderElement _dropHeader;
        Guid _anchor;
        bool _swallowMouseDown;

        // Holding the button on a view or folder (without moving) renames it, like a double-click on its name.
        const double LongPressSeconds = 0.6;
        UITimer _longPress;
        Guid _longPressId;

        /// <summary>The view or folder whose name is being edited in place; its own label is not drawn.</summary>
        public Guid EditingId { get; set; }

        readonly Dictionary<(string, int, Font), string> _fitCache = new Dictionary<(string, int, Font), string>();
        readonly Font _labelFont = SystemFonts.Default(11);
        readonly Font _listFont = SystemFonts.Default(12);
        readonly Font _headerFont = SystemFonts.Bold(12);
        readonly Font _countFont = SystemFonts.Default(11);

        /// <summary>Selected view and folder ids (they never collide).</summary>
        public HashSet<Guid> Selection { get; } = new HashSet<Guid>();

        /// <summary>Collapsed folder ids; owned by the panel so it outlives a rebuild.</summary>
        public HashSet<Guid> Collapsed { get; set; } = new HashSet<Guid>();

        /// <summary>Returns the thumbnail for a view id, or null.</summary>
        public Func<Guid, Image> ImageFor { get; set; } = _ => null;

        public event Action<Guid> ViewActivated;
        /// <summary>Asks the panel to open an in-place editor: item id, whether it is a folder, editor bounds, current name.</summary>
        public event Action<Guid, bool, RectangleF, string> RenameRequested;
        public event Action<PointF> MenuRequested;
        public event Action<DropRequest> DropRequested;
        public event Action SelectionChanged;

        public ViewCanvas()
        {
            CanFocus = true;
        }

        public Structure Structure => _structure;

        float _minimumHeight;

        /// <summary>The visible height of the scroll area. The canvas is never shorter, so every click on
        /// empty space lands on it (and clears the selection) instead of on the scroll area behind it.</summary>
        public float MinimumHeight
        {
            get => _minimumHeight;
            set
            {
                if (Math.Abs(value - _minimumHeight) < 0.5f)
                    return;
                _minimumHeight = value;
                Relayout();
            }
        }

        public void SetStructure(Structure structure)
        {
            _structure = structure;
            Relayout();
        }

        public void SetPresentation(ViewMode mode, ThumbnailSize size, SortMode sort)
        {
            _mode = mode;
            _size = size;
            _sort = sort;
            Relayout();
        }

        public IEnumerable<Guid> SelectedViews => _order.OfType<ViewElement>().Select(v => v.View.Id).Where(Selection.Contains);

        public IEnumerable<Guid> SelectedFolders => _headers.Select(h => h.Id).Where(Selection.Contains);

        /// <summary>Selected views and folders in the order they are shown.</summary>
        public List<Guid> SelectedItems => _order.Select(IdOf).Where(Selection.Contains).ToList();

        string _notice;

        /// <summary>A short message shown at the top of the visible area (e.g. while a file is read); null hides it.</summary>
        public string Notice
        {
            get => _notice;
            set
            {
                _notice = value;
                Invalidate();
            }
        }

        /// <summary>The folder new items go to: the selected folder, or the folder of the selected view.</summary>
        public Guid TargetFolder
        {
            get
            {
                var focus = _order.FirstOrDefault(e => Selection.Contains(IdOf(e)));
                return focus is HeaderElement h ? h.Id : focus is ViewElement v ? v.Section.Folder : Guid.Empty;
            }
        }

        public void Select(IEnumerable<Guid> ids)
        {
            Selection.Clear();
            foreach (var id in ids)
                Selection.Add(id);
            _anchor = Selection.FirstOrDefault();
            ScrollToSelection();
            SelectionChanged?.Invoke();
            Invalidate();
        }

        public void SelectAll()
        {
            Select(_order.OfType<ViewElement>().Select(v => v.View.Id));
        }

        public void ToggleFolder(Guid id, bool? expand = null)
        {
            var collapse = expand.HasValue ? !expand.Value : !Collapsed.Contains(id);
            if (collapse)
                Collapsed.Add(id);
            else
                Collapsed.Remove(id);
            Relayout();
        }

        Scrollable ScrollArea
        {
            get
            {
                for (var parent = Parent; parent != null; parent = parent.Parent)
                {
                    if (parent is Scrollable scroll)
                        return scroll;
                }
                return null;
            }
        }

        static RectangleF Inflated(RectangleF r, float dx, float dy) => new RectangleF(r.X - dx, r.Y - dy, r.Width + 2 * dx, r.Height + 2 * dy);

        static Guid IdOf(Element e) => e is HeaderElement h ? h.Id : e is ViewElement v ? v.View.Id : Guid.Empty;

        #region Layout

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Math.Abs(Width - _layoutWidth) > 0.5f)
                Relayout();
        }

        void Relayout()
        {
            _headers.Clear();
            _sections.Clear();
            _containers.Clear();
            _order.Clear();
            _guides.Clear();
            _separators.Clear();
            _hover = null;
            _layoutWidth = Width;
            var width = Math.Max(120f, Width);
            float y = Pad;
            _empty = _structure == null || _structure.RootItems.Count == 0;
            if (!_empty)
                LayoutLevel(Guid.Empty, null, _structure.RootItems, 0, width, ref y);
            // Only what is on screen stays selected (deleted items, and items in collapsed folders, drop out),
            // so Delete or Move never act on something out of sight.
            var visible = new HashSet<Guid>(_order.Select(IdOf));
            if (Selection.RemoveWhere(id => !visible.Contains(id)) > 0)
                SelectionChanged?.Invoke();
            var height = (int)Math.Ceiling(Math.Max(Math.Max(y + Pad, 60), _minimumHeight));
            if (Height != height)
                Height = height;
            Invalidate();
        }

        /// <summary>
        /// Lays out one level in its order: consecutive views form a grid (or rows), a folder ends the run
        /// and the views after it start a new one. At the top level a hairline sets runs and folders apart.
        /// </summary>
        void LayoutLevel(Guid folder, HeaderElement header, List<IItem> items, int depth, float width, ref float y)
        {
            for (int i = 0; i < items.Count;)
            {
                if (items[i] is FolderNode node)
                {
                    LayoutFolder(node, width, ref y);
                    i++;
                    continue;
                }
                var run = items.Skip(i).TakeWhile(item => item is ViewEntry).Cast<ViewEntry>().ToList();
                i += run.Count;
                if (depth == 0 && _order.Count > 0)
                    Separator(ref y);
                var section = LayoutViews(folder, header, run, depth, width, ref y);
                section.Next = i < items.Count ? items[i].Id : Guid.Empty;
            }
        }

        void Separator(ref float y)
        {
            y += 4;
            _separators.Add(y);
            y += 4;
        }

        void LayoutFolder(FolderNode node, float width, ref float y)
        {
            if (node.Depth == 0 && _order.Count > 0)
                Separator(ref y);
            var expanded = !Collapsed.Contains(node.Folder.Id);
            var header = new HeaderElement
            {
                Node = node,
                Expanded = expanded,
                TextLeft = Pad + node.Depth * Indent,
                Bounds = new RectangleF(Pad / 2, y, width - Pad, HeaderHeight),
            };
            _headers.Add(header);
            _order.Add(header);
            y += HeaderHeight;
            if (!expanded)
                return;

            var top = y;
            if (node.Items.Count == 0)
            {
                var left = Pad + (node.Depth + 1) * Indent;
                var placeholder = new SectionElement
                {
                    Folder = node.Id,
                    Header = header,
                    Placeholder = true,
                    Bounds = new RectangleF(left, y, width - Pad - left, 24),
                };
                placeholder.HitBounds = new RectangleF(0, y, width, 24);
                _sections.Add(placeholder);
                y += 28;
            }
            else
                LayoutLevel(node.Id, header, node.Items, node.Depth + 1, width, ref y);
            _containers.Add(new ContainerElement { Folder = node.Id, Header = header, Bounds = new RectangleF(0, top, width, Math.Max(y - top, 6)) });
            _guides.Add((Pad + node.Depth * Indent + 4.5f, top, y - 4));
        }

        SectionElement LayoutViews(Guid folder, HeaderElement header, List<ViewEntry> views, int depth, float width, ref float y)
        {
            var section = new SectionElement { Folder = folder, Header = header };
            _sections.Add(section);
            var left = Pad + depth * Indent;
            var available = Math.Max(40f, width - left - Pad);
            var top = y;
            if (views.Count > 0)
                y += 4;

            if (_mode == ViewMode.Thumbnails)
            {
                var minWidth = _size == ThumbnailSize.Small ? 84f : _size == ThumbnailSize.Large ? 190f : 124f;
                var columns = Math.Max(1, (int)((available + Gap) / (minWidth + Gap)));
                var tileWidth = (available - (columns - 1) * Gap) / columns;
                var thumbHeight = tileWidth * ThumbAspect;
                for (int i = 0; i < views.Count; i++)
                {
                    var x = left + (i % columns) * (tileWidth + Gap);
                    var ty = y + (i / columns) * (thumbHeight + LabelHeight + Gap);
                    AddView(section, views[i], new RectangleF(x, ty, tileWidth, thumbHeight),
                        new RectangleF(x, ty + thumbHeight + 3, tileWidth, LabelHeight - 3),
                        new RectangleF(x, ty, tileWidth, thumbHeight + LabelHeight));
                }
                var rows = (views.Count + columns - 1) / columns;
                y += rows * (thumbHeight + LabelHeight) + Math.Max(0, rows - 1) * Gap;
            }
            else if (_mode == ViewMode.List)
            {
                var rowHeight = _size == ThumbnailSize.Small ? 22f : _size == ThumbnailSize.Large ? 40f : 28f;
                var thumbHeight = rowHeight - 6;
                var thumbWidth = thumbHeight / ThumbAspect;
                for (int i = 0; i < views.Count; i++)
                {
                    var ry = y + i * rowHeight;
                    AddView(section, views[i], new RectangleF(left + 3, ry + 3, thumbWidth, thumbHeight),
                        new RectangleF(left + thumbWidth + 10, ry, available - thumbWidth - 12, rowHeight),
                        new RectangleF(left, ry, available, rowHeight));
                }
                y += views.Count * rowHeight;
            }
            else
            {
                // Names only: compact text rows, no thumbnail.
                const float rowHeight = 22f;
                for (int i = 0; i < views.Count; i++)
                {
                    var ry = y + i * rowHeight;
                    AddView(section, views[i], RectangleF.Empty,
                        new RectangleF(left + 8, ry, available - 12, rowHeight),
                        new RectangleF(left, ry, available, rowHeight));
                }
                y += views.Count * rowHeight;
            }
            if (views.Count > 0)
                y += 8;
            section.Bounds = new RectangleF(left, top, available, y - top);
            section.HitBounds = new RectangleF(0, top, width, y - top);
            return section;
        }

        void AddView(SectionElement section, ViewEntry view, RectangleF thumb, RectangleF label, RectangleF bounds)
        {
            var element = new ViewElement { View = view, Section = section, Thumb = thumb, Label = label, Bounds = bounds };
            section.Views.Add(element);
            _order.Add(element);
        }

        #endregion

        #region Painting

        static Color Text => Theme.Text;
        static Color Faint(float alpha) => new Color(Text, alpha);
        static Color Accent => Theme.Accent;

        // Painting is guarded too: on Mac an exception escaping a draw callback aborts Rhino.
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            try
            {
                Draw(e);
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.WriteLine("Structured Views: drawing failed: " + ex.Message);
            }
        }

        void Draw(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.AntiAlias = true;
            g.ImageInterpolation = ImageInterpolation.High;
            var clip = e.ClipRectangle;
            var focused = HasFocus;

            if (_empty)
            {
                PaintEmptyState(g);
                if (!string.IsNullOrEmpty(_notice))
                    PaintNotice(g);
                return;
            }

            foreach (var y in _separators)
                g.FillRectangle(Faint(0.1f), Pad / 2, y, Width - Pad, 1);
            foreach (var (x, top, bottom) in _guides)
                g.FillRectangle(Faint(0.12f), x, top, 1, Math.Max(0, bottom - top));

            foreach (var header in _headers)
            {
                if (header.Bounds.Intersects(clip))
                    PaintHeader(g, header, focused);
            }
            foreach (var section in _sections)
            {
                if (!section.Bounds.Intersects(clip))
                    continue;
                if (section.Placeholder)
                {
                    var text = _dropHeader == section.Header && _dragging ? "Drop to move here" : "Empty folder";
                    g.DrawText(_labelFont, Faint(0.4f), section.Bounds.X + 2, section.Bounds.Y + (24 - _labelFont.LineHeight) / 2, text);
                }
                foreach (var view in section.Views)
                {
                    if (!view.Bounds.Intersects(clip))
                        continue;
                    if (_mode == ViewMode.Thumbnails)
                        PaintTile(g, view, focused);
                    else
                        PaintRow(g, view, focused);
                }
            }

            if (_dragging && _drop != null && _dropMarker.Width + _dropMarker.Height > 0)
            {
                g.FillPath(Accent, GraphicsPath.GetRoundRect(_dropMarker, 1));
                var horizontal = _dropMarker.Width > _dropMarker.Height;
                var cap = horizontal ? new PointF(_dropMarker.Left, _dropMarker.MiddleY) : new PointF(_dropMarker.MiddleX, _dropMarker.Top);
                g.FillEllipse(Accent, cap.X - 3, cap.Y - 3, 6, 6);
            }
            if (_dragging)
                PaintDragBadge(g);
            if (!string.IsNullOrEmpty(_notice))
                PaintNotice(g);
        }

        /// <summary>A pill at the top of the visible area, over the content.</summary>
        void PaintNotice(Graphics g)
        {
            var scroll = ScrollArea;
            var top = (scroll?.ScrollPosition.Y ?? 0) + 8f;
            var text = Fit(_labelFont, _notice, Width - 4 * Pad);
            var size = _labelFont.MeasureString(text);
            var box = new RectangleF((Width - size.Width) / 2 - 12, top, size.Width + 24, size.Height + 10);
            g.FillPath(new Color(Accent, 0.95f), GraphicsPath.GetRoundRect(box, box.Height / 2));
            g.DrawText(_labelFont, Theme.AccentText, box.X + 12, box.Y + 5, text);
        }

        void PaintEmptyState(Graphics g)
        {
            var title = "No named views";
            var hint = "Save the current viewport with the camera button.";
            var w = Width;
            Icons.Draw(g, Icon.Camera, new RectangleF(w / 2 - 14, 22, 28, 28), Faint(0.3f));
            var ts = _headerFont.MeasureString(title);
            g.DrawText(_headerFont, Faint(0.7f), (w - ts.Width) / 2, 58, title);
            var hs = Fit(_labelFont, hint, w - 2 * Pad);
            g.DrawText(_labelFont, Faint(0.45f), (w - _labelFont.MeasureString(hs).Width) / 2, 78, hs);
        }

        void PaintHeader(Graphics g, HeaderElement header, bool focused)
        {
            var b = header.Bounds;
            var selected = Selection.Contains(header.Id);
            if (_dragging && _dropHeader == header)
            {
                g.FillPath(new Color(Accent, 0.22f), GraphicsPath.GetRoundRect(b, 5));
                g.DrawPath(new Pen(Accent, 1), GraphicsPath.GetRoundRect(b, 5));
            }
            else if (selected)
                g.FillPath(focused ? new Color(Accent, 0.3f) : Faint(0.12f), GraphicsPath.GetRoundRect(b, 5));
            else if (_hover == header)
                g.FillPath(Faint(0.06f), GraphicsPath.GetRoundRect(b, 5));

            // Chevron: right when collapsed, down when open.
            var cx = header.TextLeft + 4.5f;
            var cy = b.MiddleY;
            var chevron = new GraphicsPath();
            if (header.Expanded)
                chevron.AddLines(new PointF(cx - 4, cy - 2), new PointF(cx + 4, cy - 2), new PointF(cx, cy + 3));
            else
                chevron.AddLines(new PointF(cx - 2, cy - 4), new PointF(cx + 3, cy), new PointF(cx - 2, cy + 4));
            chevron.CloseFigure();
            g.FillPath(Faint(0.55f), chevron);

            var count = header.Node.Total.ToString();
            var countWidth = _countFont.MeasureString(count).Width;
            var nameLeft = header.TextLeft + 15;
            var name = Fit(_headerFont, header.Node.Folder.Name, b.Right - 10 - countWidth - nameLeft - 8);
            if (header.Id != EditingId)
                g.DrawText(_headerFont, Text, nameLeft, cy - _headerFont.LineHeight / 2, name);
            g.DrawText(_countFont, Faint(0.5f), b.Right - 10 - countWidth, cy - _countFont.LineHeight / 2, count);
        }

        void PaintTile(Graphics g, ViewElement view, bool focused)
        {
            var selected = Selection.Contains(view.View.Id);
            var thumb = view.Thumb;
            var frame = GraphicsPath.GetRoundRect(thumb, 4);
            var image = ImageFor(view.View.Id);
            if (image != null)
            {
                g.SaveTransform();
                g.SetClip(frame);
                g.DrawImage(image, thumb);
                g.ResetClip();
                g.RestoreTransform();
            }
            else
            {
                g.FillPath(Faint(0.06f), frame);
                var s = Math.Min(24f, thumb.Height * 0.4f);
                Icons.Draw(g, Icon.Camera, new RectangleF(thumb.MiddleX - s / 2, thumb.MiddleY - s / 2, s, s), Faint(0.25f));
            }
            g.DrawPath(new Pen(Faint(_hover == view && !selected ? 0.4f : 0.14f), 1), frame);
            if (selected)
                g.DrawPath(new Pen(focused ? Accent : Faint(0.45f), 2.5f), GraphicsPath.GetRoundRect(Inflated(thumb, 1.5f, 1.5f), 5.5f));

            if (view.View.Id == EditingId)
                return;
            var label = Fit(_labelFont, view.View.Name, view.Label.Width - 8);
            var size = _labelFont.MeasureString(label);
            var tx = view.Label.MiddleX - size.Width / 2;
            var ty = view.Label.Y + (view.Label.Height - size.Height) / 2;
            if (selected && focused)
            {
                g.FillPath(Accent, GraphicsPath.GetRoundRect(new RectangleF(tx - 5, ty - 1, size.Width + 10, size.Height + 2), (size.Height + 2) / 2));
                g.DrawText(_labelFont, Theme.AccentText, tx, ty, label);
            }
            else
                g.DrawText(_labelFont, selected ? Text : Faint(0.85f), tx, ty, label);
        }

        void PaintRow(Graphics g, ViewElement view, bool focused)
        {
            var selected = Selection.Contains(view.View.Id);
            var row = view.Bounds;
            if (selected)
                g.FillPath(focused ? Accent : Faint(0.14f), GraphicsPath.GetRoundRect(row, 5));
            else if (_hover == view)
                g.FillPath(Faint(0.06f), GraphicsPath.GetRoundRect(row, 5));
            if (_mode == ViewMode.List)
            {
                var frame = GraphicsPath.GetRoundRect(view.Thumb, 2);
                var image = ImageFor(view.View.Id);
                if (image != null)
                {
                    g.SaveTransform();
                    g.SetClip(frame);
                    g.DrawImage(image, view.Thumb);
                    g.ResetClip();
                    g.RestoreTransform();
                }
                else
                    g.FillPath(Faint(0.08f), frame);
                g.DrawPath(new Pen(selected && focused ? new Color(Theme.AccentText, 0.4f) : Faint(0.14f), 1), frame);
            }
            if (view.View.Id == EditingId)
                return;
            var label = Fit(_listFont, view.View.Name, view.Label.Width);
            g.DrawText(_listFont, selected && focused ? Theme.AccentText : Text, view.Label.X, view.Label.MiddleY - _listFont.LineHeight / 2, label);
        }

        void PaintDragBadge(Graphics g)
        {
            var count = _drop?.Views.Count + _drop?.Folders.Count ?? 0;
            var first = _order.FirstOrDefault(e => Selection.Contains(IdOf(e)));
            var text = count > 1 ? $"{count} items" : first is HeaderElement h ? h.Node.Folder.Name : first is ViewElement v ? v.View.Name : "";
            if (_drop?.Copy == true)
                text = "+ " + text;
            text = Fit(_labelFont, text, 180);
            var size = _labelFont.MeasureString(text);
            var box = new RectangleF(_dragPoint.X + 14, _dragPoint.Y + 10, size.Width + 14, size.Height + 6);
            g.FillPath(new Color(Accent, 0.92f), GraphicsPath.GetRoundRect(box, box.Height / 2));
            g.DrawText(_labelFont, Theme.AccentText, box.X + 7, box.Y + 3, text);
        }

        /// <summary>Shortens text with an ellipsis to fit a width; cached per text, width and font.</summary>
        string Fit(Font font, string text, float width)
        {
            text ??= "";
            var key = (text, (int)width, font);
            if (_fitCache.TryGetValue(key, out var fitted))
                return fitted;
            if (_fitCache.Count > 4000)
                _fitCache.Clear();
            fitted = text;
            if (width <= 0)
                fitted = "";
            else if (font.MeasureString(text).Width > width)
            {
                int lo = 0, hi = text.Length;
                while (lo < hi)
                {
                    var mid = (lo + hi + 1) / 2;
                    if (font.MeasureString(text.Substring(0, mid).TrimEnd() + "…").Width <= width)
                        lo = mid;
                    else
                        hi = mid - 1;
                }
                fitted = lo == 0 ? "…" : text.Substring(0, lo).TrimEnd() + "…";
            }
            return _fitCache[key] = fitted;
        }

        #endregion

        #region Hit testing

        Element HitTest(PointF p)
        {
            foreach (var header in _headers)
            {
                if (header.Bounds.Contains(p))
                    return header;
            }
            foreach (var section in _sections)
            {
                if (!section.Bounds.Contains(p))
                    continue;
                foreach (var view in section.Views)
                {
                    if (_mode == ViewMode.Thumbnails ? view.Thumb.Contains(p) || view.Label.Contains(p) : view.Bounds.Contains(p))
                        return view;
                }
            }
            return null;
        }

        #endregion

        #region Mouse

        static bool IsContextClick(MouseEventArgs e)
        {
            return e.Buttons == MouseButtons.Alternate
                   || (e.Buttons == MouseButtons.Primary && e.Modifiers == Keys.Control && Rhino.Runtime.HostUtils.RunningOnOSX);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            // Eto (Mac) follows a double-click with a mouse-down for the same click; when the double-click
            // already did something (rename, restore, open/close), that mouse-down must not run.
            if (_swallowMouseDown)
            {
                _swallowMouseDown = false;
                return;
            }
            base.OnMouseDown(e);
            Focus();
            var hit = HitTest(e.Location);
            var id = IdOf(hit);
            var modifiers = e.Modifiers;
            // Not marked handled: on Mac that starts Eto's own mouse-capture loop, which can swallow the next
            // click elsewhere in the panel. The native view still gets drags outside its bounds.

            if (IsContextClick(e))
            {
                if (hit != null && !Selection.Contains(id))
                    SelectOnly(id);
                else if (hit == null && Selection.Count > 0)
                    SelectOnly(Guid.Empty);
                MenuRequested?.Invoke(e.Location);
                return;
            }
            if (e.Buttons != MouseButtons.Primary)
                return;

            if (hit is HeaderElement header && e.Location.X < header.TextLeft + 14)
            {
                ToggleFolder(header.Id);
                return;
            }

            _pressed = hit;
            _pressPoint = e.Location;
            _pressWasSelected = hit != null && Selection.Contains(id);
            if (hit != null && (modifiers & (Keys.Shift | Application.Instance.CommonModifier | Keys.Alt)) == 0)
                StartLongPress(id);
            if (hit == null)
            {
                if ((modifiers & (Keys.Shift | Application.Instance.CommonModifier)) == 0)
                    SelectOnly(Guid.Empty);
            }
            else if ((modifiers & Application.Instance.CommonModifier) != 0)
            {
                if (!Selection.Remove(id))
                    Selection.Add(id);
                _anchor = id;
                SelectionChanged?.Invoke();
            }
            else if ((modifiers & Keys.Shift) != 0 && _anchor != Guid.Empty)
            {
                SelectRange(_anchor, id);
            }
            else if (!_pressWasSelected)
            {
                SelectOnly(id);
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_pressed != null && e.Buttons == MouseButtons.Primary)
            {
                if (!_dragging && (Math.Abs(e.Location.X - _pressPoint.X) > DragThreshold || Math.Abs(e.Location.Y - _pressPoint.Y) > DragThreshold))
                {
                    StopLongPress();
                    _dragging = Selection.Contains(IdOf(_pressed));
                }
                if (_dragging)
                {
                    _dragPoint = e.Location;
                    UpdateDrop(e.Location, (e.Modifiers & Keys.Alt) != 0);
                    AutoScroll(e.Location);
                    Invalidate();
                    return;
                }
            }
            var hover = HitTest(e.Location);
            if (hover != _hover)
            {
                _hover = hover;
                ToolTip = hover is ViewElement v ? v.View.Name : hover is HeaderElement h ? $"{h.Node.Folder.Name} ({h.Node.Total})" : null;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            StopLongPress();
            var pressed = _pressed;
            _pressed = null;
            if (_dragging)
            {
                _dragging = false;
                var drop = _drop;
                _drop = null;
                _dropHeader = null;
                Invalidate();
                if (drop != null)
                    DropRequested?.Invoke(drop);
                return;
            }
            // A plain click on an already selected item narrows the selection to it.
            if (pressed != null && _pressWasSelected && (e.Modifiers & (Keys.Shift | Application.Instance.CommonModifier)) == 0 && Selection.Count > 1)
                SelectOnly(IdOf(pressed));
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != null && !_dragging)
            {
                _hover = null;
                Invalidate();
            }
        }

        // A real double-click lands on what its first click selected. A quick second click on something else
        // (or on a chevron, which doesn't select) arrives as a double-click too and is left to the mouse-down
        // that follows it, as a plain click.
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            var hit = HitTest(e.Location);
            var onChevron = hit is HeaderElement h && e.Location.X < h.TextLeft + 14;
            if (hit == null || onChevron || IsContextClick(e) || !Selection.Contains(IdOf(hit)))
                return;
            StopLongPress();
            _swallowMouseDown = true;
            if (hit is ViewElement view)
            {
                if (NameBounds(view).Contains(e.Location))
                    BeginRename(view.View.Id);
                else
                    ViewActivated?.Invoke(view.View.Id);
            }
            else if (hit is HeaderElement header)
            {
                if (NameBounds(header).Contains(e.Location))
                    BeginRename(header.Id);
                else
                    ToggleFolder(header.Id);
            }
        }

        #region Renaming

        void StartLongPress(Guid id)
        {
            StopLongPress();
            _longPressId = id;
            _longPress = new UITimer { Interval = LongPressSeconds };
            _longPress.Elapsed += (s, e) =>
            {
                var target = _longPressId;
                StopLongPress();
                try
                {
                    // Still held on the same item and not dragging: rename it.
                    if (!_dragging && _pressed != null && IdOf(_pressed) == target && Mouse.Buttons == MouseButtons.Primary)
                    {
                        _pressed = null;
                        BeginRename(target);
                    }
                }
                catch (Exception ex)
                {
                    Rhino.RhinoApp.WriteLine("Structured Views: rename failed: " + ex.Message);
                }
            };
            _longPress.Start();
        }

        void StopLongPress()
        {
            if (_longPress == null)
                return;
            _longPress.Stop();
            _longPress.Dispose();
            _longPress = null;
        }

        /// <summary>Opens the in-place editor for a view or folder (the menu, or a double-click on its name).</summary>
        public void BeginRename(Guid id)
        {
            var element = _order.FirstOrDefault(el => IdOf(el) == id);
            if (element == null)
                return;
            Select(new[] { id });
            if (element is HeaderElement header)
            {
                var left = header.TextLeft + 11;
                var box = new RectangleF(left, header.Bounds.MiddleY - 11, header.Bounds.Right - 30 - left, 22);
                RenameRequested?.Invoke(id, true, box, header.Node.Folder.Name);
            }
            else if (element is ViewElement view)
            {
                var box = _mode == ViewMode.Thumbnails
                    ? new RectangleF(view.Label.X, view.Label.MiddleY - 11, view.Label.Width, 22)
                    : new RectangleF(view.Label.X - 4, view.Bounds.MiddleY - 11, view.Bounds.Right - view.Label.X + 2, 22);
                RenameRequested?.Invoke(id, false, box, view.View.Name);
            }
        }

        /// <summary>Where a view's name is drawn (the part a double-click renames).</summary>
        RectangleF NameBounds(ViewElement view)
        {
            if (_mode == ViewMode.Thumbnails)
            {
                var w = _labelFont.MeasureString(Fit(_labelFont, view.View.Name, view.Label.Width - 8)).Width;
                return new RectangleF(view.Label.MiddleX - w / 2 - 6, view.Label.Y - 2, w + 12, view.Label.Height + 4);
            }
            var width = _listFont.MeasureString(Fit(_listFont, view.View.Name, view.Label.Width)).Width;
            return new RectangleF(view.Label.X - 4, view.Bounds.Y, width + 12, view.Bounds.Height);
        }

        /// <summary>Where a folder's name is drawn in its header.</summary>
        RectangleF NameBounds(HeaderElement header)
        {
            var left = header.TextLeft + 15;
            var width = _headerFont.MeasureString(header.Node.Folder.Name).Width;
            return new RectangleF(left - 4, header.Bounds.Y, Math.Min(width + 12, header.Bounds.Right - left), header.Bounds.Height);
        }

        #endregion

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        void SelectOnly(Guid id)
        {
            Selection.Clear();
            if (id != Guid.Empty)
                Selection.Add(id);
            _anchor = id;
            SelectionChanged?.Invoke();
            Invalidate();
        }

        void SelectRange(Guid from, Guid to)
        {
            var ids = _order.Select(IdOf).ToList();
            int a = ids.IndexOf(from), b = ids.IndexOf(to);
            if (a < 0 || b < 0)
            {
                SelectOnly(to);
                return;
            }
            Selection.Clear();
            for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                Selection.Add(ids[i]);
            SelectionChanged?.Invoke();
        }

        void AutoScroll(PointF p)
        {
            var scroll = ScrollArea;
            if (scroll == null)
                return;
            var visible = scroll.VisibleRect;
            var position = scroll.ScrollPosition;
            if (p.Y < visible.Top + 24)
                scroll.ScrollPosition = new Point(position.X, Math.Max(0, position.Y - 14));
            else if (p.Y > visible.Bottom - 24)
                scroll.ScrollPosition = new Point(position.X, Math.Min(Math.Max(0, Height - visible.Height), position.Y + 14));
        }

        void ScrollToSelection()
        {
            var first = _order.FirstOrDefault(e => Selection.Contains(IdOf(e)));
            var scroll = ScrollArea;
            if (first == null || scroll == null)
                return;
            var visible = scroll.VisibleRect;
            if (first.Bounds.Top < visible.Top)
                scroll.ScrollPosition = new Point(0, (int)Math.Max(0, first.Bounds.Top - Pad));
            else if (first.Bounds.Bottom > visible.Bottom)
                scroll.ScrollPosition = new Point(0, (int)Math.Max(0, first.Bounds.Bottom - visible.Height + Pad));
        }

        #endregion

        #region Drop targets

        void UpdateDrop(PointF p, bool copy)
        {
            var drop = new DropRequest
            {
                Views = SelectedViews.ToList(),
                Folders = SelectedFolders.ToList(),
                Copy = copy,
            };
            _dropMarker = RectangleF.Empty;
            _dropHeader = null;
            var store = _structure.Store;
            var custom = _sort == SortMode.Custom;

            var header = _headers.FirstOrDefault(h => Inflated(h.Bounds, 0, 2).Contains(p));
            if (header != null)
            {
                // Top or bottom edge of a header: before or after that folder in its level (custom order);
                // the middle: into the folder.
                var fraction = (p.Y - header.Bounds.Top) / header.Bounds.Height;
                var parent = header.Node.Folder.Parent;
                if (custom && fraction < 0.3f)
                {
                    drop.Folder = parent;
                    drop.Before = header.Id;
                    _dropMarker = new RectangleF(header.TextLeft, header.Bounds.Top - 1, header.Bounds.Right - header.TextLeft, 2);
                }
                else if (custom && fraction > 0.7f && !header.Expanded)
                {
                    var level = _structure.ItemsIn(parent);
                    var at = level.FindIndex(i => i.Id == header.Id);
                    drop.Folder = parent;
                    drop.Before = at >= 0 && at + 1 < level.Count ? level[at + 1].Id : Guid.Empty;
                    _dropMarker = new RectangleF(header.TextLeft, header.Bounds.Bottom - 1, header.Bounds.Right - header.TextLeft, 2);
                }
                else
                {
                    drop.Folder = header.Id;
                    _dropHeader = header;
                }
            }
            else
            {
                var section = _sections.LastOrDefault(s => s.HitBounds.Contains(p));
                var container = _containers.LastOrDefault(c => c.Bounds.Contains(p));
                if (section != null)
                {
                    drop.Folder = section.Folder;
                    _dropHeader = section.Header;
                    if (custom && section.Views.Count > 0)
                    {
                        // Between views of the run; after the last one means right before the next item.
                        var index = InsertIndex(section, p);
                        var moving = new HashSet<Guid>(drop.Views.Concat(drop.Folders));
                        drop.Before = section.Views.Skip(index).Select(v => v.View.Id).Concat(new[] { section.Next }).FirstOrDefault(id => !moving.Contains(id));
                        _dropMarker = MarkerFor(section, index);
                        _dropHeader = null;
                    }
                }
                else if (container != null)
                {
                    drop.Folder = container.Folder;
                    _dropHeader = container.Header;
                }
                else
                    drop.Folder = Guid.Empty;
            }

            // No folder into itself or below itself, and nothing that changes nothing.
            var valid = drop.Folders.All(f => drop.Folder == Guid.Empty || !store.IsInside(drop.Folder, f));
            if (drop.Before != Guid.Empty && drop.Folders.Contains(drop.Before))
                valid = false;
            _drop = valid ? drop : null;
            if (!valid)
            {
                _dropMarker = RectangleF.Empty;
                _dropHeader = null;
            }
        }

        int InsertIndex(SectionElement section, PointF p)
        {
            var views = section.Views;
            if (_mode != ViewMode.Thumbnails)
            {
                var rowHeight = views[0].Bounds.Height;
                return Math.Max(0, Math.Min(views.Count, (int)Math.Round((p.Y - views[0].Bounds.Top) / rowHeight)));
            }
            // The tile whose row contains the point, then before/after by its horizontal middle.
            var rowTop = views.Where(v => v.Bounds.Top <= p.Y + Gap / 2).Select(v => v.Bounds.Top).DefaultIfEmpty(views[0].Bounds.Top).Max();
            var row = views.Select((v, i) => (v, i)).Where(t => Math.Abs(t.v.Bounds.Top - rowTop) < 0.5f).ToList();
            foreach (var (v, i) in row)
            {
                if (p.X < v.Bounds.MiddleX)
                    return i;
            }
            return row.Last().i + 1;
        }

        RectangleF MarkerFor(SectionElement section, int index)
        {
            var views = section.Views;
            if (_mode != ViewMode.Thumbnails)
            {
                var y = index < views.Count ? views[index].Bounds.Top : views[views.Count - 1].Bounds.Bottom;
                return new RectangleF(section.Bounds.Left, y - 1, section.Bounds.Width, 2);
            }
            var t = index < views.Count ? views[index].Thumb : views[views.Count - 1].Thumb;
            var x = index < views.Count ? t.Left - Gap / 2 : t.Right + Gap / 2;
            return new RectangleF(x - 1, t.Top, 2, t.Height);
        }

        #endregion

        #region Keyboard

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled)
                return;
            var current = _order.FirstOrDefault(el => IdOf(el) == _anchor) ?? _order.FirstOrDefault(el => Selection.Contains(IdOf(el)));
            switch (e.KeyData)
            {
                case Keys.Up:
                case Keys.Down:
                    MoveSelection(current, e.KeyData == Keys.Down ? 1 : -1, vertical: true);
                    e.Handled = true;
                    break;
                case Keys.Left:
                    if (current is HeaderElement open && open.Expanded)
                        ToggleFolder(open.Id, false);
                    else
                        MoveSelection(current, -1, vertical: false);
                    e.Handled = true;
                    break;
                case Keys.Right:
                    if (current is HeaderElement closed && !closed.Expanded)
                        ToggleFolder(closed.Id, true);
                    else
                        MoveSelection(current, 1, vertical: false);
                    e.Handled = true;
                    break;
                case Keys.Escape when _dragging:
                    _dragging = false;
                    _drop = null;
                    _pressed = null;
                    Invalidate();
                    e.Handled = true;
                    break;
                case Keys.Escape when Selection.Count > 0:
                    SelectOnly(Guid.Empty);
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>Arrow keys: left/right step through items in order; up/down move by a row of the grid.</summary>
        void MoveSelection(Element from, int step, bool vertical)
        {
            if (_order.Count == 0)
                return;
            Element next;
            if (from == null)
                next = step > 0 ? _order[0] : _order[_order.Count - 1];
            else if (vertical && _mode == ViewMode.Thumbnails && from is ViewElement view)
            {
                // Same column in the next/previous row of this section, else leave the section.
                var section = view.Section.Views;
                var target = section
                    .Where(v => step > 0 ? v.Bounds.Top > view.Bounds.Top + 1 : v.Bounds.Top < view.Bounds.Top - 1)
                    .OrderBy(v => Math.Abs(v.Bounds.Top - view.Bounds.Top))
                    .ThenBy(v => Math.Abs(v.Bounds.Left - view.Bounds.Left))
                    .FirstOrDefault();
                next = target ?? Neighbour(step > 0 ? (Element)section.Last() : section.First(), step);
            }
            else
                next = Neighbour(from, step);
            if (next != null)
                Select(new[] { IdOf(next) });
        }

        Element Neighbour(Element from, int step)
        {
            var i = _order.IndexOf(from) + step;
            return i >= 0 && i < _order.Count ? _order[i] : from;
        }

        #endregion
    }
}
