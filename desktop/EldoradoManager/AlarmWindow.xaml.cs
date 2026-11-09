using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace EldoradoManager
{
    /// <summary>
    /// The island: grows out of the top edge of the screen. Springs drive the outline, so this window
    /// never resizes during an animation; only the geometry, clip and opacity change. At the critical
    /// stage it hands over to TakeoverWindow.
    /// </summary>
    public partial class AlarmWindow : Window
    {
        private enum Mode { Hidden, Pill, Open, Compact, Takeover }

        private const double OverlayWidth = 1040, OverlayHeight = 240;
        private const double OpenWidth = 760, OpenHeight = 104, OpenRadius = 36, OpenShoulder = 20;
        private const double PillWidth = 210, PillHeight = 40, PillRadius = 20;
        private const double CompactWidth = 176, CompactHeight = 46, CompactRadius = 23, CompactShoulder = 12;
        private const int OpenSeconds = 7;
        private const double RestingGlow = 0.75, CompactGlow = 0.45;
        private const int NoticeSeconds = 8;

        private readonly Spring width = Spring.Bouncy(PillWidth);
        private readonly Spring height = Spring.Bouncy(0);
        private readonly Spring radius = Spring.Bouncy(PillRadius);
        private readonly Spring shoulder = Spring.Bouncy(0);
        private readonly Spring fade = Spring.Smooth(0);
        private readonly Spring glow = Spring.Smooth(0);
        private readonly Spring mini = Spring.Smooth(0);
        private readonly Spring[] springs;
        private readonly DispatcherTimer collapse = new DispatcherTimer { Interval = TimeSpan.FromSeconds(OpenSeconds) };

        private readonly List<Alarm> alarms = new List<Alarm>();
        private readonly Dictionary<string, Stage> answered = new Dictionary<string, Stage>();
        private readonly Stopwatch clock = new Stopwatch();
        private readonly DispatcherTimer ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        private readonly SolidColorBrush glowBrush = new SolidColorBrush(Colors.Transparent);
        private readonly SolidColorBrush accentBrush = new SolidColorBrush(Colors.White);
        private readonly RotateTransform lampSpin = new RotateTransform(0, 0.5, 0.5);
        private readonly bool motion = SystemParameters.ClientAreaAnimation;
        private readonly TakeoverWindow takeover = new TakeoverWindow();

        private Config config;
        private Mode mode = Mode.Hidden;
        private Alarm current;
        private Stage? lampStage;
        private double fuseShare;
        private DateTime noticeUntil;
        private TimeSpan lastFrame;
        private bool animating;
        private string playing;
        private SoundPlayer player;
        private bool exitWhenEmpty;

        public AlarmWindow()
        {
            InitializeComponent();
            springs = new[] { width, height, radius, shoulder, fade, glow, mini };

            Glow.Fill = glowBrush;
            Dot.Fill = accentBrush;
            MiniDot.Fill = accentBrush;
            LampLight.Fill = new ImageBrush(Beacon.LampBeam) { RelativeTransform = lampSpin };
            MiniLight.Fill = LampLight.Fill;
            Countdown.Fill = accentBrush;
            MiniClock.Fill = accentBrush;
            collapse.Tick += (_, __) => Collapse();
            Root.MouseEnter += (_, __) =>
            {
                collapse.Stop();
                if (mode == Mode.Compact) Go(Mode.Open);
            };
            Root.MouseLeave += (_, __) => { if (mode == Mode.Open) collapse.Start(); };
            Fuse.Stroke = accentBrush;
            FuseGlow.Stroke = accentBrush;
            Skin.Reload();
            ApplySkin();

            takeover.Answered += () => Answer(null, null);
            takeover.OpenRequested += () => Open(null, null);
            ticker.Tick += (_, __) => Tick();
            SourceInitialized += (_, __) => KeepOutOfAltTab();

            var screen = SystemParameters.PrimaryScreenWidth;
            Width = OverlayWidth;
            Height = OverlayHeight;
            Left = (screen - OverlayWidth) / 2;
            Top = 0;
            Show();
            Hide();
        }

        /// <summary>Takes the current theme. Also runs before each new alarm, so a change needs no restart.</summary>
        public void ApplySkin()
        {
            var skin = Skin.Current;
            Body.Fill = new LinearGradientBrush(skin.IslandTop, skin.IslandBottom, 90);
            Header.Foreground = new SolidColorBrush(skin.IslandMuted);
            Summary.Foreground = new SolidColorBrush(skin.IslandInk);
            DotGlow.Opacity = skin.Soft ? 0.9 : 0;
            CountdownGlow.Opacity = skin.Soft ? 0.75 : 0;
            FuseGlow.Visibility = skin.Soft ? Visibility.Visible : Visibility.Collapsed;
            takeover.ApplySkin();
            lampStage = null;
        }

        // ---------- input ----------

        public void Listen(Config settings)
        {
            config = settings;
            Config.ShareOrder(null, "");
            new Listener(config,
                alarm => Dispatcher.BeginInvoke(new Action(() => Add(alarm))),
                id => Dispatcher.BeginInvoke(new Action(() => Remove(id)))).Start();
        }

        public void Welcome() => Add(new Alarm
        {
            Id = "welcome",
            IsNotice = true,
            Header = "Eldorado Manager is on watch",
            Text = "You'll see your next order here.",
        });

        public void PlayDemo(string scene)
        {
            exitWhenEmpty = true;
            switch (scene)
            {
                case "calm": Add(Sample(1200, 0.1)); break;
                case "hurry": Add(Sample(1200, 0.6)); break;
                case "critical": Add(Sample(1200, 0.9)); break;
                case "late": Add(Sample(1200, 1.05)); break;
                default: Add(Sample(30, 0)); break;   // walks through each stage in 30 s
            }
        }

        public static Alarm Sample(double window, double used) => new Alarm
        {
            Id = "demo",
            Header = "New order",
            Text = "13,500 V-Bucks top up, 64.90 USD, buyer nightowl_77",
            Link = "https://www.eldorado.gg/",
            Priority = 5,
            Window = window,
            Deadline = Alarm.Now + window * (1 - used),
        };

        /// <summary>Lays the island out at rest without showing it, for --snapshot.</summary>
        public FrameworkElement PreviewIsland(Alarm alarm, bool compact = false)
        {
            current = alarm;
            mode = compact ? Mode.Compact : Mode.Open;
            Paint();
            width.SnapTo(compact ? CompactWidth : OpenWidth);
            height.SnapTo(compact ? CompactHeight : OpenHeight);
            radius.SnapTo(compact ? CompactRadius : OpenRadius);
            shoulder.SnapTo(compact ? CompactShoulder : OpenShoulder);
            fade.SnapTo(compact ? 0 : 1);
            mini.SnapTo(compact ? 1 : 0);
            glow.SnapTo(compact ? CompactGlow : RestingGlow);
            Draw();
            Root.Measure(new Size(OverlayWidth, OverlayHeight));
            Root.Arrange(new Rect(0, 0, OverlayWidth, OverlayHeight));
            Root.UpdateLayout();
            return Root;
        }

        public FrameworkElement PreviewTakeover(Alarm alarm, double angle) => takeover.Preview(alarm, angle);

        private void Add(Alarm alarm)
        {
            // Same rule as the server: an answer holds until the order reaches a worse critical stage.
            if (!alarm.IsNotice && answered.TryGetValue(alarm.Id, out var stage))
            {
                if (alarm.Stage < Stage.Critical || alarm.Stage <= stage) return;
                answered.Remove(alarm.Id);
            }
            var existing = alarms.FirstOrDefault(a => a.Id == alarm.Id);
            if (existing != null)
            {
                // A repeat from the server: keep the alarm, take any newer deadline.
                existing.Deadline = alarm.Deadline ?? existing.Deadline;
                existing.Window = alarm.Window ?? existing.Window;
                ShareOrder();
                return;
            }
            alarms.Add(alarm);
            Config.Log("alarm opened: " + alarm.Id);
            Refresh();
        }

        private void Remove(string id)
        {
            answered[id] = alarms.FirstOrDefault(a => a.Id == id)?.Stage ?? Stage.Calm;
            if (alarms.RemoveAll(a => a.Id == id) > 0)
            {
                Config.Log("alarm closed: " + id);
                Refresh();
            }
        }

        private void Answer(object sender, RoutedEventArgs e)
        {
            if (current == null) return;
            var alarm = current;
            if (!alarm.IsNotice && config != null) Task.Run(() => config.PublishAck(alarm.Id));
            Remove(alarm.Id);
        }

        private void Open(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(current?.Link))
                Process.Start(new ProcessStartInfo(current.Link) { UseShellExecute = true });
        }

        // ---------- state ----------

        /// <summary>The most urgent order wins the screen. Notices wait behind orders.</summary>
        private Alarm PickCurrent() => alarms
            .OrderBy(a => a.IsNotice)
            .ThenBy(a => a.Remaining ?? double.MaxValue)
            .FirstOrDefault();

        /// <summary>A demo has no real order, so it stays out of the dashboard.</summary>
        private void ShareOrder()
        {
            if (exitWhenEmpty) return;
            var orders = alarms.Where(a => !a.IsNotice).OrderBy(a => a.Remaining ?? double.MaxValue).ToList();
            Config.ShareOrder(orders.FirstOrDefault(), orders.Count > 1 ? $"{orders.Count - 1} more waiting" : "");
        }

        private void Refresh()
        {
            ShareOrder();
            if (mode == Mode.Hidden)
            {
                Skin.Reload();
                ApplySkin();
            }
            var next = PickCurrent();
            if (next != current)
            {
                current = next;
                if (current?.IsNotice == true) noticeUntil = DateTime.UtcNow.AddSeconds(NoticeSeconds);
                if (current != null && mode == Mode.Compact) Go(Mode.Open);
            }
            UpdateSound();
            if (current == null)
            {
                Go(Mode.Hidden);
                return;
            }
            Paint();
            if (current.Stage >= Stage.Critical) Go(Mode.Takeover);
            else if (mode == Mode.Hidden || mode == Mode.Takeover) Go(Mode.Pill);
        }

        private void Tick()
        {
            if (current == null) return;
            if (current.IsNotice && DateTime.UtcNow >= noticeUntil)
            {
                Remove(current.Id);
                return;
            }
            Paint();
            if (current.Stage >= Stage.Critical) Go(Mode.Takeover);
            else if (mode == Mode.Takeover) Go(Mode.Pill);   // the deadline was extended
            UpdateSound();
        }

        private void Paint()
        {
            var alarm = current;
            var stage = alarm.Stage;
            var others = alarms.Count(a => !a.IsNotice) - (alarm.IsNotice ? 0 : 1);
            var more = others > 0 ? $"{others} more waiting" : "";

            Header.Text = string.Join(", ", new[] { alarm.Header, alarm.Price, more }.Where(s => !string.IsNullOrEmpty(s)));
            Summary.Text = alarm.Title;
            Countdown.Text = MiniClock.Text = alarm.Clock;
            Countdown.Visibility = alarm.Clock.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            AnswerButton.Content = alarm.IsNotice ? "Dismiss" : "Got it";
            OpenButton.Visibility = string.IsNullOrEmpty(alarm.Link) ? Visibility.Collapsed : Visibility.Visible;

            var accent = Skin.Current.Accent(stage);
            accentBrush.Color = accent;
            glowBrush.Color = Skin.Current.Lamp(stage);
            DotGlow.Color = accent;
            CountdownGlow.Color = accent;
            if (stage != lampStage)
            {
                var escalated = lampStage != null && stage > lampStage;
                lampStage = stage;
                SpinLamp(stage);
                if (escalated && mode == Mode.Compact) Go(Mode.Open);
            }

            var timed = alarm.Remaining != null && alarm.Window > 0;
            fuseShare = timed ? Math.Max(0, Math.Min(alarm.Remaining.Value / alarm.Window.Value, 1)) : 0;
            Fuse.Visibility = timed ? Visibility.Visible : Visibility.Collapsed;
            FuseGlow.Visibility = timed && Skin.Current.Soft ? Visibility.Visible : Visibility.Collapsed;
            BurnFuse();

            if (mode == Mode.Takeover) takeover.Paint(alarm, more);
        }

        // ---------- modes ----------

        private void Go(Mode next)
        {
            if (next == mode) return;
            var previous = mode;
            mode = next;
            collapse.Stop();

            if (previous == Mode.Takeover) takeover.Leave(Settle);
            if (previous == Mode.Hidden || previous == Mode.Takeover)
            {
                ticker.Start();
                if (next != Mode.Hidden && next != Mode.Takeover)
                {
                    width.SnapTo(PillWidth * 0.6);
                    height.SnapTo(0);
                    radius.SnapTo(PillRadius);
                    shoulder.SnapTo(0);
                    Draw();
                    Show();
                }
            }

            switch (next)
            {
                case Mode.Pill:
                    Aim(PillWidth, PillHeight, PillRadius, 14, 0);
                    Delay(230, () => { if (mode == Mode.Pill) Go(Mode.Open); });
                    break;
                case Mode.Open:
                    Aim(OpenWidth, OpenHeight, OpenRadius, OpenShoulder, 1);
                    if (!Root.IsMouseOver) collapse.Start();
                    break;
                case Mode.Compact:
                    Aim(CompactWidth, CompactHeight, CompactRadius, CompactShoulder, 0, 1);
                    break;
                case Mode.Takeover:
                    takeover.Enter(ScreenGrab.Primary(this), width.Value, height.Value, radius.Value, shoulder.Value);
                    Paint();
                    Hide();
                    return;
                case Mode.Hidden:
                    Aim(PillWidth * 0.6, 0, PillRadius, 0, 0);
                    if (previous == Mode.Takeover) return;   // the takeover shrink ends in Settle
                    break;
            }
            Animate();
        }

        /// <summary>Only an order with a clock folds away: a notice leaves by itself, and nothing else fits.</summary>
        private void Collapse()
        {
            collapse.Stop();
            if (mode == Mode.Open && current != null && !current.IsNotice && current.Clock.Length > 0) Go(Mode.Compact);
        }

        private void Aim(double w, double h, double r, double s, double content, double compact = 0)
        {
            width.Target = w;
            height.Target = h;
            radius.Target = r;
            shoulder.Target = s;
            fade.Target = content;
            mini.Target = compact;
            glow.Target = 0;
            if (!motion)
            {
                foreach (var spring in springs) spring.SnapTo(spring.Target);
            }
        }

        private static void Delay(int milliseconds, Action action)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                action();
            };
            timer.Start();
        }

        // ---------- rendering ----------

        /// <summary>The lamp turns faster as the deadline nears.</summary>
        private void SpinLamp(Stage stage)
        {
            if (!motion) return;
            var turn = stage == Stage.Calm ? 2.8 : 1.3;
            lampSpin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(turn)) { RepeatBehavior = RepeatBehavior.Forever });
        }

        /// <summary>Dash lengths count in stroke widths, so each fuse needs its own numbers.</summary>
        private void BurnFuse()
        {
            var length = width.Value + 2 * height.Value;
            Fuse.StrokeDashArray = new DoubleCollection { length * fuseShare / Fuse.StrokeThickness, length };
            FuseGlow.StrokeDashArray = new DoubleCollection { length * fuseShare / FuseGlow.StrokeThickness, length };
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

            var shapeResting = width.IsAtRest && height.IsAtRest && radius.IsAtRest && shoulder.IsAtRest;
            if (shapeResting && mode == Mode.Open) glow.Target = RestingGlow;
            if (shapeResting && mode == Mode.Compact) glow.Target = CompactGlow;
            if (springs.All(s => s.IsAtRest))
            {
                CompositionTarget.Rendering -= OnFrame;
                animating = false;
                Settle();
            }
        }

        private void Draw()
        {
            var center = OverlayWidth / 2;
            var fill = IslandShape.Build(center, width.Value, height.Value, radius.Value, shoulder.Value);
            Body.Data = fill;
            Glow.Data = fill;
            IslandClip.Clip = fill;
            var edge = IslandShape.Build(center, width.Value, height.Value, radius.Value, shoulder.Value, closed: false);
            Rim.Data = Fuse.Data = FuseGlow.Data = edge;
            Island.Opacity = fade.Value;
            Island.IsHitTestVisible = fade.Value > 0.5;
            Mini.Opacity = mini.Value;
            var edgeLight = Math.Min(Math.Max(fade.Value, mini.Value), 1);
            Rim.Opacity = Fuse.Opacity = edgeLight;
            FuseGlow.Opacity = edgeLight * 0.8;
            Glow.Opacity = glow.Value;
            Shade.Opacity = Math.Min(glow.Value * 1.2, 0.8);
            BurnFuse();
        }

        private void Settle()
        {
            if (mode != Mode.Hidden) return;
            ticker.Stop();
            Hide();
            if (exitWhenEmpty) Application.Current.Shutdown();
        }

        // ---------- sound ----------

        private void UpdateSound()
        {
            var order = alarms.Where(a => !a.IsNotice).OrderBy(a => a.Remaining ?? double.MaxValue).FirstOrDefault();
            var wanted = order == null ? null : order.Stage >= Stage.Critical ? "alarm.wav" : "chime.wav";
            if (wanted == playing) return;
            playing = wanted;
            player?.Stop();
            player?.Dispose();
            player = null;
            if (wanted == null) return;
            var settings = config ?? Config.Load() ?? new Config();   // a demo has no config of its own
            var custom = wanted == "chime.wav" ? settings.Sound : null;
            player = new SoundPlayer(Sounds.Load(wanted, settings.Volume, custom));
            player.PlayLooping();
        }

        // ---------- window flags ----------

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLong(IntPtr hwnd, int index, IntPtr value);

        private void KeepOutOfAltTab()
        {
            const int ExStyle = -20, ToolWindow = 0x80, NoActivate = 0x08000000;
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(hwnd, ExStyle).ToInt64();
            SetWindowLong(hwnd, ExStyle, new IntPtr(style | ToolWindow | NoActivate));
        }
    }
}
