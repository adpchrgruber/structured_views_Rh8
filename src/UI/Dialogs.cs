using System;
using System.Collections.Generic;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using Rhino.Geometry;

namespace StructuredViews
{
    /// <summary>Named view animation settings, as in Rhino's own panel: constant time or constant speed, and a frame delay.</summary>
    public sealed class AnimationSettingsDialog : Dialog<bool>
    {
        public AnimationSettingsDialog()
        {
            Title = "Named View Animation";
            Resizable = false;
            Padding = new Padding(14);

            var animate = new CheckBox { Text = "Animate named view restoration", Checked = Settings.Animate };
            var constantTime = new RadioButton { Text = "Constant time", Checked = Settings.AnimationMode == AnimationMode.ConstantTime };
            var constantSpeed = new RadioButton(constantTime) { Text = "Constant speed", Checked = Settings.AnimationMode == AnimationMode.ConstantSpeed };
            var frames = new NumericStepper { MinValue = 1, MaxValue = 500, Value = Settings.AnimationFrames, Width = 80 };
            var speed = new NumericStepper { MinValue = 0.001, MaxValue = 1e6, DecimalPlaces = 3, Value = Settings.AnimationSpeed, Width = 80 };
            var delay = new NumericStepper { MinValue = 0, MaxValue = 1000, Value = Settings.AnimationDelay, Width = 80 };

            void Sync()
            {
                var on = animate.Checked == true;
                constantTime.Enabled = constantSpeed.Enabled = delay.Enabled = on;
                frames.Enabled = on && constantTime.Checked;
                speed.Enabled = on && constantSpeed.Checked;
            }
            animate.CheckedChanged += (s, e) => Sync();
            constantTime.CheckedChanged += (s, e) => Sync();
            constantSpeed.CheckedChanged += (s, e) => Sync();
            Sync();

            DefaultButton = new Button { Text = "OK" };
            AbortButton = new Button { Text = "Cancel" };
            DefaultButton.Click += (s, e) =>
            {
                Settings.Animate = animate.Checked == true;
                Settings.AnimationMode = constantSpeed.Checked ? AnimationMode.ConstantSpeed : AnimationMode.ConstantTime;
                Settings.AnimationFrames = (int)frames.Value;
                Settings.AnimationSpeed = speed.Value;
                Settings.AnimationDelay = (int)delay.Value;
                Close(true);
            };
            AbortButton.Click += (s, e) => Close(false);

            Content = new TableLayout
            {
                Spacing = new Size(8, 8),
                Rows =
                {
                    animate,
                    new TableRow(new TableLayout(
                        new TableRow(constantTime, frames, new Label { Text = "frames", VerticalAlignment = VerticalAlignment.Center }),
                        new TableRow(constantSpeed, speed, new Label { Text = "units per frame", VerticalAlignment = VerticalAlignment.Center }),
                        new TableRow(new Label { Text = "Delay between frames", VerticalAlignment = VerticalAlignment.Center }, delay, new Label { Text = "ms", VerticalAlignment = VerticalAlignment.Center }))
                    { Spacing = new Size(8, 6), Padding = new Padding(18, 0, 0, 0) }),
                    null,
                    new TableLayout(new TableRow(null, AbortButton, DefaultButton)) { Spacing = new Size(8, 0) },
                },
            };
        }
    }

    /// <summary>Name, camera, target and lens of one named view.</summary>
    public sealed class ViewPropertiesDialog : Dialog<bool>
    {
        readonly TextBox _name;
        readonly NumericStepper[] _location;
        readonly NumericStepper[] _target;
        readonly NumericStepper _lens;

        public string ViewName => _name.Text;
        public Point3d CameraLocation => new Point3d(_location[0].Value, _location[1].Value, _location[2].Value);
        public Point3d CameraTarget => new Point3d(_target[0].Value, _target[1].Value, _target[2].Value);
        public double LensLength => _lens.Value;

        public ViewPropertiesDialog(string name, Point3d location, Point3d target, double lens, bool perspective, string units)
        {
            Title = "Named View Properties";
            Resizable = false;
            Padding = new Padding(14);
            MinimumSize = new Size(340, 0);

            NumericStepper Number(double value) => new NumericStepper { DecimalPlaces = 3, MinValue = -1e12, MaxValue = 1e12, Value = value, Width = 92 };
            _name = new TextBox { Text = name };
            _location = new[] { Number(location.X), Number(location.Y), Number(location.Z) };
            _target = new[] { Number(target.X), Number(target.Y), Number(target.Z) };
            _lens = new NumericStepper { DecimalPlaces = 1, MinValue = 1, MaxValue = 10000, Value = lens, Width = 92, Enabled = perspective };

            Label Caption(string text) => new Label { Text = text, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            TableRow Xyz(string caption, NumericStepper[] values) => new TableRow(Caption(caption), values[0], values[1], values[2]);

            DefaultButton = new Button { Text = "OK" };
            AbortButton = new Button { Text = "Cancel" };
            DefaultButton.Click += (s, e) => Close(true);
            AbortButton.Click += (s, e) => Close(false);

            Content = new TableLayout
            {
                Spacing = new Size(8, 10),
                Rows =
                {
                    new TableLayout(new TableRow(Caption("Name"), new TableCell(_name, true))) { Spacing = new Size(8, 0) },
                    new TableLayout(
                        new TableRow(null, Centered("X"), Centered("Y"), Centered("Z")),
                        Xyz("Camera (" + units + ")", _location),
                        Xyz("Target (" + units + ")", _target),
                        new TableRow(Caption("Lens length (mm)"), _lens, perspective ? null : new Label { Text = "parallel", VerticalAlignment = VerticalAlignment.Center }))
                    { Spacing = new Size(6, 6) },
                    new TableLayout(new TableRow(null, AbortButton, DefaultButton)) { Spacing = new Size(8, 0) },
                },
            };
        }

        static Label Centered(string text) => new Label { Text = text, TextAlignment = TextAlignment.Center };
    }

    /// <summary>
    /// What to import from a .3dm: the file's folders and named views with checkboxes (a folder's box
    /// switches everything inside it), where to put them, and what to do with names that already exist.
    /// </summary>
    public sealed class ImportDialog : Dialog<bool>
    {
        sealed class Node : TreeGridItem
        {
            public Guid Id;
            public bool IsFolder;
            public string Name;
            public bool Checked;
            public string Label => IsFolder ? Name + "  (" + Views().Count() + ")" : Name;

            public IEnumerable<Node> Views()
            {
                foreach (Node child in Children)
                {
                    if (!child.IsFolder)
                        yield return child;
                    else
                        foreach (var view in child.Views())
                            yield return view;
                }
            }

            public void SetChecked(bool value)
            {
                Checked = value;
                foreach (Node child in Children)
                    child.SetChecked(value);
            }
        }

        readonly TreeGridItemCollection _roots = new TreeGridItemCollection();
        readonly RadioButton _newFolder;
        readonly RadioButton _replace;

        /// <summary>Source ids of the checked views, or null when everything is checked (then empty folders come too).</summary>
        public ICollection<Guid> Views { get; private set; }
        public bool IntoNewFolder => _newFolder.Checked;
        public bool Replace => _replace.Checked;

        public ImportDialog(Rhino.FileIO.File3dm file, string fileName, string targetName, bool checkAll)
        {
            Title = checkAll ? "Import All Named Views" : "Import Selected Named Views";
            Resizable = true;
            Padding = new Padding(14);
            MinimumSize = new Size(420, 520);

            // The file's structure, as Structured Views stores it.
            var source = FolderStore.Load(file);
            var folderNodes = new Dictionary<Guid, Node>();
            void AddFolders(Guid parent, TreeGridItemCollection into)
            {
                foreach (var folder in source.ChildFolders(parent).OrderBy(f => f.Order).ThenBy(f => f.Name, NaturalComparer.Instance))
                {
                    var node = new Node { Id = folder.Id, IsFolder = true, Name = folder.Name, Expanded = true };
                    folderNodes[folder.Id] = node;
                    AddFolders(folder.Id, node.Children);
                    into.Add(node);
                }
            }
            var loose = new List<Node>();
            AddFolders(Guid.Empty, _roots);
            var views = file.NamedViews
                .Select((v, i) => new { v.NamedViewId, v.Name, Index = i, Order = source.OrderOf(v.NamedViewId) })
                .OrderBy(x => double.IsNaN(x.Order) ? double.MaxValue : x.Order)
                .ThenBy(x => x.Index);
            foreach (var view in views)
            {
                var node = new Node { Id = view.NamedViewId, Name = view.Name };
                if (folderNodes.TryGetValue(source.FolderOf(view.NamedViewId), out var folder))
                    folder.Children.Insert(folder.Children.OfType<Node>().Count(n => !n.IsFolder), node);
                else
                    loose.Add(node);
            }
            for (int i = 0; i < loose.Count; i++)
                _roots.Insert(i, loose[i]);
            foreach (Node node in _roots)
                node.SetChecked(checkAll);

            // Names first: the tree indents its first column, so the checkboxes get a column of their own on
            // the right, where they all line up instead of being pushed (and clipped) by the indentation.
            var tree = new TreeGridView { ShowHeader = false, Height = 280, DataStore = _roots };
            tree.Columns.Add(new GridColumn { DataCell = new TextBoxCell { Binding = Binding.Delegate<Node, string>(n => n.Label) }, Expand = true });
            tree.Columns.Add(new GridColumn
            {
                DataCell = new CheckBoxCell { Binding = Binding.Delegate<Node, bool?>(n => n.Checked, (n, v) => n.SetChecked(v == true)) },
                Editable = true,
                Width = 32,
            });
            tree.CellEdited += (s, e) => tree.ReloadData();

            Button Small(string text, bool value)
            {
                var button = new Button { Text = text };
                button.Click += (s, e) =>
                {
                    foreach (Node node in _roots)
                        node.SetChecked(value);
                    tree.ReloadData();
                };
                return button;
            }

            _newFolder = new RadioButton { Text = "In a new folder \"" + fileName + "\"", Checked = true };
            var intoTarget = new RadioButton(_newFolder) { Text = "Into " + targetName };
            var keepBoth = new RadioButton { Text = "Keep both (the import gets a number)", Checked = true };
            _replace = new RadioButton(keepBoth) { Text = "Replace the existing view" };

            DefaultButton = new Button { Text = "Import" };
            AbortButton = new Button { Text = "Cancel" };
            DefaultButton.Click += (s, e) =>
            {
                var all = _roots.OfType<Node>().SelectMany(n => n.IsFolder ? n.Views() : new[] { n }).ToList();
                var picked = all.Where(n => n.Checked).ToList();
                if (picked.Count == 0)
                {
                    MessageBox.Show(this, "Tick the views to import.", "Structured Views");
                    return;
                }
                Views = picked.Count == all.Count ? null : new HashSet<Guid>(picked.Select(n => n.Id));
                Close(true);
            };
            AbortButton.Click += (s, e) => Close(false);

            // A heading on the left: a label next to an empty cell that takes the rest of the width.
            Control Heading(string text) => new TableLayout(new TableRow(new Label { Text = text, Font = SystemFonts.Bold() }, null));
            Content = new TableLayout
            {
                Spacing = new Size(8, 8),
                Rows =
                {
                    Heading(fileName + ".3dm"),
                    new TableRow(tree) { ScaleHeight = true },
                    new TableLayout(new TableRow(Small("Select All", true), Small("Select None", false), null)) { Spacing = new Size(6, 0) },
                    Heading("Put the views"),
                    _newFolder,
                    intoTarget,
                    Heading("If a view with the same name exists"),
                    keepBoth,
                    _replace,
                    new TableLayout(new TableRow(null, AbortButton, DefaultButton)) { Spacing = new Size(8, 0) },
                },
            };
        }
    }
}
