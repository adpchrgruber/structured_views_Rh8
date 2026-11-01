using System;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;

namespace StructuredViews
{
    public enum Icon { SaveView, NewFolder, Restore, Delete, Copy, Paste, Import, Duplicate, MoveUp, MoveDown, Thumbnails, List, More, Camera }

    /// <summary>Line icons drawn as paths on a 16×16 grid, so they stay sharp and follow the theme colour.</summary>
    public static class Icons
    {
        public static void Draw(Graphics g, Icon icon, RectangleF bounds, Color color)
        {
            var scale = Math.Min(bounds.Width, bounds.Height) / 16f;
            g.SaveTransform();
            g.TranslateTransform(bounds.X + (bounds.Width - 16 * scale) / 2, bounds.Y + (bounds.Height - 16 * scale) / 2);
            g.ScaleTransform(scale);
            var pen = new Pen(color, 1.3f) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            switch (icon)
            {
                case Icon.SaveView:
                    Camera(g, pen, 0, 1);
                    g.DrawLine(pen, 13.5f, 1f, 13.5f, 5f);
                    g.DrawLine(pen, 11.5f, 3f, 15.5f, 3f);
                    break;
                case Icon.Camera:
                    Camera(g, pen, 1, 0);
                    break;
                case Icon.NewFolder:
                    g.DrawPath(pen, Polyline(9f, 13f, 1.5f, 13f, 1.5f, 3.5f, 5.5f, 3.5f, 7f, 5f, 13f, 5f, 13f, 8f));
                    g.DrawLine(pen, 12.5f, 9.5f, 12.5f, 14.5f);
                    g.DrawLine(pen, 10f, 12f, 15f, 12f);
                    break;
                case Icon.Restore:
                    // Circular arrow, as in Rhino's own panel.
                    var arc = new GraphicsPath();
                    arc.AddArc(2.5f, 2.5f, 11f, 11f, 200, 290);
                    g.DrawPath(pen, arc);
                    g.DrawPath(pen, Polyline(2f, 2.8f, 2.8f, 6.4f, 6.4f, 5.6f));
                    break;
                case Icon.Delete:
                    g.DrawLine(pen, 3.5f, 3.5f, 12.5f, 12.5f);
                    g.DrawLine(pen, 12.5f, 3.5f, 3.5f, 12.5f);
                    break;
                case Icon.Copy:
                    g.DrawPath(pen, GraphicsPath.GetRoundRect(new RectangleF(5.5f, 5f, 8f, 9.5f), 1.2f));
                    g.DrawPath(pen, Polyline(10.5f, 3f, 10.5f, 1.5f, 2.5f, 1.5f, 2.5f, 11f, 3.5f, 11f));
                    break;
                case Icon.Paste:
                    g.DrawPath(pen, Polyline(10.5f, 3f, 12.5f, 3f, 12.5f, 5f));
                    g.DrawPath(pen, Polyline(5.5f, 14.5f, 2.5f, 14.5f, 2.5f, 3f, 4.5f, 3f));
                    g.DrawPath(pen, GraphicsPath.GetRoundRect(new RectangleF(4.5f, 1.5f, 6f, 3f), 1f));
                    g.DrawPath(pen, GraphicsPath.GetRoundRect(new RectangleF(7.5f, 7f, 7f, 7.5f), 1.2f));
                    break;
                case Icon.Import:
                    g.DrawPath(pen, Polyline(2f, 9.5f, 2f, 13.5f, 14f, 13.5f, 14f, 9.5f));
                    g.DrawLine(pen, 8f, 2f, 8f, 10f);
                    g.DrawPath(pen, Polyline(5f, 7f, 8f, 10f, 11f, 7f));
                    break;
                case Icon.Duplicate:
                    g.DrawPath(pen, GraphicsPath.GetRoundRect(new RectangleF(1.5f, 1.5f, 9f, 9f), 2f));
                    g.DrawPath(pen, GraphicsPath.GetRoundRect(new RectangleF(5.5f, 5.5f, 9f, 9f), 2f));
                    break;
                case Icon.MoveUp:
                    g.DrawPath(pen, Polyline(3f, 11f, 8f, 5f, 13f, 11f, 3f, 11f));
                    break;
                case Icon.MoveDown:
                    g.DrawPath(pen, Polyline(3f, 5f, 8f, 11f, 13f, 5f, 3f, 5f));
                    break;
                case Icon.Thumbnails:
                    foreach (var (x, y) in new[] { (2f, 2f), (9f, 2f), (2f, 9f), (9f, 9f) })
                        g.DrawPath(pen, GraphicsPath.GetRoundRect(new RectangleF(x, y, 5f, 5f), 1f));
                    break;
                case Icon.List:
                    foreach (var y in new[] { 3f, 8f, 13f })
                    {
                        g.DrawPath(pen, GraphicsPath.GetRoundRect(new RectangleF(1.5f, y - 1.5f, 3.5f, 3f), 0.6f));
                        g.DrawLine(pen, 7.5f, y, 14.5f, y);
                    }
                    break;
                case Icon.More:
                    foreach (var x in new[] { 3.5f, 8f, 12.5f })
                        g.FillEllipse(color, x - 1.3f, 6.7f, 2.6f, 2.6f);
                    break;
            }
            g.RestoreTransform();
        }

        static void Camera(Graphics g, Pen pen, float dx, float dy)
        {
            g.DrawPath(pen, GraphicsPath.GetRoundRect(new RectangleF(1.5f + dx, 5f + dy, 11f, 8f), 1.5f));
            g.DrawPath(pen, Polyline(4.5f + dx, 5f + dy, 5.5f + dx, 3.3f + dy, 8.5f + dx, 3.3f + dy, 9.5f + dx, 5f + dy));
            g.DrawEllipse(pen, 4.8f + dx, 6.8f + dy, 4.4f, 4.4f);
        }

        static IGraphicsPath Polyline(params float[] xy)
        {
            var path = new GraphicsPath();
            for (int i = 2; i + 1 < xy.Length; i += 2)
                path.AddLine(xy[i - 2], xy[i - 1], xy[i], xy[i + 1]);
            return path;
        }
    }

    /// <summary>
    /// A toolbar that never clips: the left group flows and wraps onto more rows when the panel is narrow,
    /// and the right group stays right-aligned (on its own row if it no longer fits).
    /// </summary>
    public sealed class ToolStrip : PixelLayout
    {
        const int Pad = 4;
        const int Gap = 1;
        const int RowHeight = 24;
        const int RowGap = 2;
        readonly Control[] _left;
        readonly Control[] _right;

        public ToolStrip(Control[] left, Control[] right)
        {
            _left = left;
            _right = right;
            foreach (var control in left.Concat(right))
                Add(control, 0, 0);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Arrange();
        }

        protected override void OnLoadComplete(EventArgs e)
        {
            base.OnLoadComplete(e);
            Arrange();
        }

        void Arrange()
        {
            var width = Math.Max(Width, 60);
            int x = Pad, y = 3;
            foreach (var control in _left)
            {
                var w = control.Width;
                if (x + w > width - Pad && x > Pad)
                {
                    x = Pad;
                    y += RowHeight + RowGap;
                }
                Move(control, x, y + (RowHeight - control.Height) / 2);
                x += w + Gap;
            }
            var rightWidth = _right.Sum(c => c.Width + Gap) - Gap;
            if (x + 8 + rightWidth > width - Pad)
                y += RowHeight + RowGap;
            var rx = width - Pad - rightWidth;
            foreach (var control in _right)
            {
                Move(control, rx, y + (RowHeight - control.Height) / 2);
                rx += control.Width + Gap;
            }
            var height = y + RowHeight + 3;
            if (Height != height)
                Height = height;
        }
    }

    /// <summary>A flat icon button in the style of Rhino's panel toolbars: no bezel, a soft fill on hover.</summary>
    public sealed class IconButton : Drawable
    {
        Icon _icon;
        bool _checked;
        bool _hover;
        bool _pressed;

        public event EventHandler<EventArgs> Click;

        public IconButton(Icon icon, string toolTip)
        {
            _icon = icon;
            ToolTip = toolTip;
            Size = new Size(24, 24);
            CanFocus = false;
        }

        public Icon Icon
        {
            get => _icon;
            set { _icon = value; Invalidate(); }
        }

        /// <summary>Shown as a toggled-on state (highlight colour).</summary>
        public bool Checked
        {
            get => _checked;
            set { _checked = value; Invalidate(); }
        }

        protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }

        protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); _hover = false; _pressed = false; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Buttons == MouseButtons.Primary && Enabled)
            {
                _pressed = true;
                Invalidate();
                e.Handled = true;
            }
        }

        // A second click within the double-click time arrives as a double-click (on Mac even right after a
        // click on another control), so it counts as a press too: quick repeated clicks must not get lost.
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var click = _pressed && new RectangleF(Size).Contains(e.Location);
            _pressed = false;
            Invalidate();
            if (click)
                Click?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            try
            {
                Draw(e.Graphics);
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.WriteLine("Structured Views: drawing failed: " + ex.Message);
            }
        }

        void Draw(Graphics g)
        {
            g.AntiAlias = true;
            var text = Theme.Text;
            var box = new RectangleF(1, 1, Width - 2, Height - 2);
            if (_checked)
                g.FillPath(new Color(Theme.Accent, 0.22f), GraphicsPath.GetRoundRect(box, 5));
            if (_pressed)
                g.FillPath(new Color(text, 0.16f), GraphicsPath.GetRoundRect(box, 5));
            else if (_hover && Enabled)
                g.FillPath(new Color(text, 0.08f), GraphicsPath.GetRoundRect(box, 5));
            var color = !Enabled ? new Color(text, 0.3f) : _checked ? Theme.Accent : new Color(text, 0.85f);
            Icons.Draw(g, _icon, new RectangleF((Width - 16) / 2f, (Height - 16) / 2f, 16, 16), color);
        }
    }
}
