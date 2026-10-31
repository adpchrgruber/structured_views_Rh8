using System;
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
}
