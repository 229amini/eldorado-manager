using System;
using System.Windows;
using System.Windows.Media;

namespace EldoradoManager
{
    /// <summary>
    /// The island outline: its top fuses with the screen edge, the top corners flare into concave
    /// shoulders, and the bottom corners use continuous curves instead of circular arcs.
    /// Adapted from zab1yak/dynamic-island-windows (MIT), see THIRD-PARTY-NOTICES.md.
    /// </summary>
    public static class IslandShape
    {
        private const double CornerExtent = 1.528;   // a continuous corner starts this many radii early
        private const double CornerControl = 0.822;  // keeps the curve's midpoint on a true arc
        private const double CircleKappa = 0.5523;   // cubic approximation of a quarter circle

        public static Geometry Build(double centerX, double width, double height, double radius,
                                     double shoulder, bool closed = true)
        {
            width = Math.Max(width, 0);
            height = Math.Max(height, 0);
            var s = Math.Max(0, Math.Min(shoulder, height * 0.45));
            var maxCorner = Math.Min((height - s) / CornerExtent, width / (2 * CornerExtent));
            var r = Math.Max(0, Math.Min(radius, Math.Max(maxCorner, 0)));

            var left = centerX - width / 2;
            var right = centerX + width / 2;
            var e = r * CornerExtent;
            var c = e * CornerControl;
            var k = s * CircleKappa;
            var h = height;

            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(left - s, 0), closed, closed);
                g.BezierTo(new Point(left - s + k, 0), new Point(left, s - k), new Point(left, s), true, true);
                g.LineTo(new Point(left, h - e), true, true);
                g.BezierTo(new Point(left, h - e + c), new Point(left + e - c, h), new Point(left + e, h), true, true);
                g.LineTo(new Point(right - e, h), true, true);
                g.BezierTo(new Point(right - e + c, h), new Point(right, h - e + c), new Point(right, h - e), true, true);
                g.LineTo(new Point(right, s), true, true);
                g.BezierTo(new Point(right, s - k), new Point(right + s - k, 0), new Point(right + s, 0), true, true);
            }
            geometry.Freeze();
            return geometry;
        }
    }
}
