using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Siren
{
    /// <summary>
    /// The critical stage: the island keeps growing until it fills the screen, the room goes dark, and
    /// a beam sweeps it from above. The numerals turn white where the beam crosses them.
    /// </summary>
    public partial class TakeoverWindow : Window
    {
        private const double LampY = -90;
        private const double Turn = 2.4;   // seconds: two lobes per turn, so the beam passes once per siren wail

        private readonly Spring width = new Spring(0, 170, 24);
        private readonly Spring height = new Spring(0, 170, 24);
        private readonly Spring radius = new Spring(0, 170, 24);
        private readonly Spring shoulder = new Spring(0, 170, 24);
        private readonly Spring heat = new Spring(0, 90, 19, 0.004);
        private readonly Spring reveal = new Spring(0, 60, 15.5, 0.004);
        private readonly Spring[] springs;
        private readonly Stopwatch clock = new Stopwatch();
        private readonly GradientStop roomCenter = new GradientStop(Colors.Black, 0);
        private readonly GradientStop roomEdge = new GradientStop(Colors.Black, 1);
        private readonly RotateTransform beamSpin = new RotateTransform(90);
        private readonly RotateTransform hotSpin = new RotateTransform(90);
        private readonly ImageBrush hotMask = new ImageBrush
        {
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
            TileMode = TileMode.None,
        };
        private readonly bool motion = SystemParameters.ClientAreaAnimation;
        private readonly double beamSize;

        private Color room = Skin.Current.Room;
        private TimeSpan lastFrame;
        private bool animating;
        private Action left;

        public TakeoverWindow()
        {
            InitializeComponent();
            springs = new[] { width, height, radius, shoulder, heat, reveal };
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            KeyDown += (_, e) => { if (e.Key == Key.Enter || e.Key == Key.Space) Answered?.Invoke(); };

            Body.Fill = new RadialGradientBrush(new GradientStopCollection { roomCenter, roomEdge })
            {
                Center = new Point(0.5, 0.42),
                GradientOrigin = new Point(0.5, 0.42),
                RadiusX = 0.72,
                RadiusY = 0.8,
            };
            Grain.Fill = new ImageBrush(Beacon.Grain)
            {
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 256, 256),
                TileMode = TileMode.Tile,
            };

            beamSize = 2.2 * Math.Sqrt(Width * Width + Height * Height);
            Beam.Width = Beam.Height = beamSize;
            Canvas.SetLeft(Beam, Width / 2 - beamSize / 2);
            Canvas.SetTop(Beam, LampY - beamSize / 2);
            beamSpin.CenterX = beamSpin.CenterY = beamSize / 2;
            Beam.RenderTransform = beamSpin;

            Pulse.Width = Width;
            Pulse.Height = Height;
            Lamp.Width = Width * 0.8;
            Lamp.Height = 460;
            Canvas.SetLeft(Lamp, Width * 0.1);
            Canvas.SetTop(Lamp, -230);

            hotMask.Transform = hotSpin;
            Hot.OpacityMask = hotMask;
            ApplySkin();
        }

        public void ApplySkin()
        {
            var skin = Skin.Current;
            var beam = Beacon.RoomBeam(skin.Beam);
            Beam.Source = beam;
            hotMask.ImageSource = beam;
            room = skin.Room;
            Pulse.Fill = new RadialGradientBrush(new GradientStopCollection
            {
                new GradientStop(Alpha(skin.Pulse, 0x70), 0),
                new GradientStop(Alpha(skin.Pulse, 0), 1),
            })
            {
                Center = new Point(0.5, 0.45),
                GradientOrigin = new Point(0.5, 0.45),
                RadiusX = 0.7,
                RadiusY = 0.75,
            };
            Lamp.Fill = new RadialGradientBrush(new GradientStopCollection
            {
                new GradientStop(Alpha(skin.Beam, 0xA6), 0),
                new GradientStop(Alpha(skin.Beam, 0x33), 0.45),
                new GradientStop(Alpha(skin.Beam, 0), 1),
            });
            Clock.Fill = new LinearGradientBrush(skin.ClockTop, skin.ClockBottom, 90);
            ClockGlow.Fill = new SolidColorBrush(skin.ClockGlow);
            ClockGlow.Opacity = skin.ClockGlowOpacity;
            ClockHot.Fill = new SolidColorBrush(skin.ClockHot);
            Brand.Foreground = OrderTitle.Foreground = new SolidColorBrush(skin.RoomInk);
            State.Foreground = Detail.Foreground = new SolidColorBrush(skin.RoomMuted);
            Grain.Opacity = skin.Grain;
        }

        private static Color Alpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

        public event Action Answered;
        public event Action OpenRequested;

        public void Enter(BitmapSource desktop, double w, double h, double r, double s)
        {
            left = null;
            Shot.Source = desktop;
            Shot.Visibility = Visibility.Visible;
            width.SnapTo(w);
            height.SnapTo(h);
            radius.SnapTo(r);
            shoulder.SnapTo(s);
            heat.SnapTo(0);
            reveal.SnapTo(0);
            AimOpen();
            if (!motion)
            {
                foreach (var spring in springs) spring.SnapTo(spring.Target);
            }
            else
            {
                Spin();
            }
            Draw();
            Show();
            UpdateLayout();
            AlignHot();
            Activate();
            Focus();
            Animate();
        }

        public void Leave(Action done)
        {
            left = done;
            width.Target = 120;
            height.Target = 0;
            radius.Target = 20;
            shoulder.Target = 0;
            heat.Target = 0;
            reveal.Target = 0;
            Shot.Visibility = Visibility.Visible;
            if (!motion)
            {
                foreach (var spring in springs) spring.SnapTo(spring.Target);
            }
            Animate();
        }

        public void Paint(Alarm alarm, string more)
        {
            Clock.Text = ClockGlow.Text = ClockHot.Text = alarm.Clock;
            OrderTitle.Text = alarm.Title;
            Detail.Text = string.Join(", ", new[] { alarm.Price, alarm.Buyer, more }.Where(s => !string.IsNullOrEmpty(s)));
            OpenButton.Visibility = string.IsNullOrEmpty(alarm.Link) ? Visibility.Collapsed : Visibility.Visible;

            var late = alarm.Stage == Stage.Late;
            State.Text = late ? "Past the deadline" : "Delivery deadline is close";
            room = late ? Skin.Current.LateRoom : Skin.Current.Room;
            if (!animating) Draw();
        }

        /// <summary>Lays the window out at rest with the beam at a set angle, for --snapshot.</summary>
        public FrameworkElement Preview(Alarm alarm, double angle)
        {
            AimOpen();
            foreach (var spring in springs) spring.SnapTo(spring.Target);
            Shot.Visibility = Visibility.Collapsed;
            Pulse.Opacity = 0.7;
            Paint(alarm, "");
            Draw();
            Room.Measure(new Size(Width, Height));
            Room.Arrange(new Rect(0, 0, Width, Height));
            Room.UpdateLayout();
            AlignHot();
            beamSpin.Angle = hotSpin.Angle = angle;
            return Room;
        }

        private void OnAnswer(object sender, RoutedEventArgs e) => Answered?.Invoke();
        private void OnOpen(object sender, RoutedEventArgs e) => OpenRequested?.Invoke();

        private void AimOpen()
        {
            width.Target = Width + 80;
            height.Target = Height + 80;
            radius.Target = 0;
            shoulder.Target = 0;
            heat.Target = 1;
            reveal.Target = 1;
        }

        private void Spin()
        {
            var turn = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(Turn))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            beamSpin.BeginAnimation(RotateTransform.AngleProperty, turn);
            hotSpin.BeginAnimation(RotateTransform.AngleProperty, turn);
            Pulse.BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, 1, TimeSpan.FromSeconds(Turn / 4))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }

        /// <summary>Puts the mask's beam on top of the room's beam, in Hot's own coordinates.</summary>
        private void AlignHot()
        {
            var origin = Hot.TranslatePoint(new Point(0, 0), Room);
            var x = Width / 2 - origin.X;
            var y = LampY - origin.Y;
            hotMask.Viewport = new Rect(x - beamSize / 2, y - beamSize / 2, beamSize, beamSize);
            hotSpin.CenterX = x;
            hotSpin.CenterY = y;
        }

        private void Animate()
        {
            if (animating) return;
            animating = true;
            clock.Restart();
            lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnFrame;
        }

        private void OnFrame(object sender, EventArgs e)
        {
            var now = clock.Elapsed;
            var seconds = Math.Min((now - lastFrame).TotalSeconds, 0.05);
            lastFrame = now;
            foreach (var spring in springs) spring.Step(seconds);
            Draw();
            if (!springs.All(s => s.IsAtRest)) return;

            CompositionTarget.Rendering -= OnFrame;
            animating = false;
            if (left != null)
            {
                beamSpin.BeginAnimation(RotateTransform.AngleProperty, null);
                hotSpin.BeginAnimation(RotateTransform.AngleProperty, null);
                Pulse.BeginAnimation(OpacityProperty, null);
                Hide();
                Shot.Source = null;
                var done = left;
                left = null;
                done();
            }
            else
            {
                Shot.Visibility = Visibility.Collapsed;   // fully covered: drop the picture of the desktop
            }
        }

        private void Draw()
        {
            Body.Data = IslandShape.Build(Width / 2, width.Value, height.Value, radius.Value, shoulder.Value);
            var t = Math.Max(0, Math.Min(heat.Value, 1));
            roomCenter.Color = Scale(room, t);
            roomEdge.Color = Scale(Skin.Current.RoomEdge, t);
            var shown = Math.Max(0, Math.Min(reveal.Value, 1));
            Info.Opacity = shown;
            Light.Opacity = shown;
            Vignette.Opacity = shown * Skin.Current.Vignette;
        }

        private static Color Scale(Color color, double t) =>
            Color.FromRgb((byte)(color.R * t), (byte)(color.G * t), (byte)(color.B * t));
    }
}
