using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace SellerDesk
{
    /// <summary>
    /// The chosen theme, for the windows drawn in WPF. The dashboard draws its own in Web\style.css, so a
    /// colour change here usually belongs there too. Each theme is a value set, never a separate window.
    /// </summary>
    public sealed class Skin
    {
        public static readonly Skin Dark = new Skin
        {
            Name = "dark", Label = "Dark",
            IslandTop = Hex("#272939"), IslandBottom = Hex("#2F3144"), IslandInk = Hex("#ECEEF6"), IslandMuted = Hex("#A6ABC2"),
            Calm = Hex("#9A9BFF"), Hurry = Hex("#FFC27A"), Critical = Hex("#FF9A8F"), Soft = true,
            Room = Hex("#7A2B45"), LateRoom = Hex("#521C30"), RoomEdge = Hex("#1D1E2B"), Beam = Hex("#9A9BFF"), Pulse = Hex("#FF9A8F"),
            ClockTop = Hex("#FFFFFF"), ClockBottom = Hex("#ECEEF6"), ClockGlow = Hex("#FF9A8F"), ClockGlowOpacity = 0.5, ClockHot = Hex("#FFFFFF"),
            RoomInk = Hex("#FFFFFF"), RoomMuted = Hex("#F4DCE3"), Vignette = 0.6, Grain = 0,
            PillFace = Hex("#9A9BFF"), PillInk = Hex("#17172E"), GhostInk = Hex("#FFFFFF"), GhostFace = Hex("#14FFFFFF"), GhostLine = Hex("#59FFFFFF"),
            Corner = 10, Surface = Hex("#1D1E2B"), SurfaceLine = Hex("#33364A"), Field = Hex("#272939"), FieldLine = Hex("#3B3E54"),
            Ink = Hex("#ECEEF6"), Muted = Hex("#A6ABC2"), Label2 = Hex("#CDD1E2"), Signal = Hex("#9A9BFF"), SignalInk = Hex("#17172E"),
            Quiet = Hex("#272939"), Error = Hex("#FF9A8F"),
        };

        // The critical room stays pale here, so the dark pill and ghost buttons keep their contrast.
        public static readonly Skin Light = new Skin
        {
            Name = "light", Label = "Light",
            IslandTop = Hex("#FFFFFF"), IslandBottom = Hex("#F5F4FA"), IslandInk = Hex("#25263C"), IslandMuted = Hex("#5F6279"),
            Calm = Hex("#5A58D6"), Hurry = Hex("#B45309"), Critical = Hex("#C5354E"), Soft = false,
            Room = Hex("#FDE4E8"), LateRoom = Hex("#FBD2DA"), RoomEdge = Hex("#F4BCC7"), Beam = Hex("#C5354E"), Pulse = Hex("#C5354E"),
            ClockTop = Hex("#C5354E"), ClockBottom = Hex("#C5354E"), ClockGlow = Hex("#C5354E"), ClockGlowOpacity = 0, ClockHot = Hex("#7F1D30"),
            RoomInk = Hex("#25263C"), RoomMuted = Hex("#5B2B38"), Vignette = 0.1, Grain = 0,
            PillFace = Hex("#25263C"), PillInk = Hex("#FFFFFF"), GhostInk = Hex("#25263C"), GhostFace = Hex("#00000000"), GhostLine = Hex("#25263C"),
            Corner = 10, Surface = Hex("#F5F4FA"), SurfaceLine = Hex("#E6E5F0"), Field = Hex("#FFFFFF"), FieldLine = Hex("#D3D2E2"),
            Ink = Hex("#25263C"), Muted = Hex("#5F6279"), Label2 = Hex("#3A3C55"), Signal = Hex("#5A58D6"), SignalInk = Hex("#FFFFFF"),
            Quiet = Hex("#EEEDF7"), Error = Hex("#C5354E"),
        };

        public static readonly Skin[] All = { Dark, Light };

        public static Skin Current { get; private set; } = Dark;

        public string Name, Label;
        public Color IslandTop, IslandBottom, IslandInk, IslandMuted, Calm, Hurry, Critical;
        public Color? Halo;      // the light under the island, when it can't be the stage colour
        public bool Soft;        // false: flat, so no glow on the lamp or the clock
        public Color Room, LateRoom, RoomEdge, Beam, Pulse, ClockTop, ClockBottom, ClockGlow, ClockHot, RoomInk, RoomMuted;
        public double ClockGlowOpacity, Vignette, Grain, Corner;
        public Color PillFace, PillInk, GhostInk, GhostFace, GhostLine;
        public Color Surface, SurfaceLine, Field, FieldLine, Ink, Muted, Label2, Signal, SignalInk, Quiet, Error;

        public Color Accent(Stage stage) => stage == Stage.Calm ? Calm : stage == Stage.Hurry ? Hurry : Critical;

        public Color Lamp(Stage stage) => Halo ?? Accent(stage);

        /// <summary>Picks up a theme changed from the dashboard or Settings since this window last drew.</summary>
        public static void Reload() => Use(Config.Load()?.Theme);

        public static void Use(string name)
        {
            Current = All.FirstOrDefault(s => s.Name == name) ?? Dark;
            var resources = Application.Current?.Resources;
            if (resources == null) return;
            var skin = Current;
            resources["SkinPillFace"] = Brush(skin.PillFace);
            resources["SkinPillInk"] = Brush(skin.PillInk);
            resources["SkinGhostInk"] = Brush(skin.GhostInk);
            resources["SkinGhostFace"] = Brush(skin.GhostFace);
            resources["SkinGhostLine"] = Brush(skin.GhostLine);
            resources["SkinCorner"] = new CornerRadius(skin.Corner);
            resources["SkinSurface"] = Brush(skin.Surface);
            resources["SkinSurfaceLine"] = Brush(skin.SurfaceLine);
            resources["SkinField"] = Brush(skin.Field);
            resources["SkinFieldLine"] = Brush(skin.FieldLine);
            resources["SkinInk"] = Brush(skin.Ink);
            resources["SkinMuted"] = Brush(skin.Muted);
            resources["SkinLabel"] = Brush(skin.Label2);
            resources["SkinSignal"] = Brush(skin.Signal);
            resources["SkinSignalInk"] = Brush(skin.SignalInk);
            resources["SkinQuiet"] = Brush(skin.Quiet);
            resources["SkinError"] = Brush(skin.Error);
        }

        private static Color Hex(string text) => (Color)ColorConverter.ConvertFromString(text);

        private static SolidColorBrush Brush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
