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
            IslandTop = Hex("#222327"), IslandBottom = Hex("#2B2C31"), IslandInk = Hex("#ECECEF"), IslandMuted = Hex("#9A9CA6"),
            Calm = Hex("#A3A5FF"), Hurry = Hex("#FFC27A"), Critical = Hex("#FF8B80"), Soft = true,
            Room = Hex("#6E2530"), LateRoom = Hex("#4A1820"), RoomEdge = Hex("#1A1B1F"), Beam = Hex("#A3A5FF"), Pulse = Hex("#FF8B80"),
            ClockTop = Hex("#FFFFFF"), ClockBottom = Hex("#ECECEF"), ClockGlow = Hex("#FF8B80"), ClockGlowOpacity = 0.5, ClockHot = Hex("#FFFFFF"),
            RoomInk = Hex("#FFFFFF"), RoomMuted = Hex("#F2DADC"), Vignette = 0.6, Grain = 0,
            PillFace = Hex("#5E61E6"), PillInk = Hex("#FFFFFF"), GhostInk = Hex("#FFFFFF"), GhostFace = Hex("#14FFFFFF"), GhostLine = Hex("#59FFFFFF"),
            Corner = 8, Surface = Hex("#1A1B1F"), SurfaceLine = Hex("#33343A"), Field = Hex("#222327"), FieldLine = Hex("#3A3B42"),
            Ink = Hex("#ECECEF"), Muted = Hex("#9A9CA6"), Label2 = Hex("#C9CAD1"), Signal = Hex("#5E61E6"), SignalInk = Hex("#FFFFFF"),
            Quiet = Hex("#222327"), Error = Hex("#FF8B80"),
        };

        // The critical room stays pale here, so the indigo pill and the ink ghost button keep their contrast.
        public static readonly Skin Light = new Skin
        {
            Name = "light", Label = "Light",
            IslandTop = Hex("#FFFFFF"), IslandBottom = Hex("#F5F5F7"), IslandInk = Hex("#1C1D21"), IslandMuted = Hex("#60626B"),
            Calm = Hex("#5658D8"), Hurry = Hex("#B45309"), Critical = Hex("#C8364A"), Soft = false,
            Room = Hex("#FDE5E7"), LateRoom = Hex("#FBD3D7"), RoomEdge = Hex("#F4BEC4"), Beam = Hex("#C8364A"), Pulse = Hex("#C8364A"),
            ClockTop = Hex("#C8364A"), ClockBottom = Hex("#C8364A"), ClockGlow = Hex("#C8364A"), ClockGlowOpacity = 0, ClockHot = Hex("#7F1D2A"),
            RoomInk = Hex("#1C1D21"), RoomMuted = Hex("#5B2B33"), Vignette = 0.1, Grain = 0,
            PillFace = Hex("#5658D8"), PillInk = Hex("#FFFFFF"), GhostInk = Hex("#1C1D21"), GhostFace = Hex("#00000000"), GhostLine = Hex("#1C1D21"),
            Corner = 8, Surface = Hex("#F5F5F7"), SurfaceLine = Hex("#E3E3E8"), Field = Hex("#FFFFFF"), FieldLine = Hex("#D4D4DB"),
            Ink = Hex("#1C1D21"), Muted = Hex("#60626B"), Label2 = Hex("#393A41"), Signal = Hex("#5658D8"), SignalInk = Hex("#FFFFFF"),
            Quiet = Hex("#EEEEF1"), Error = Hex("#C8364A"),
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
