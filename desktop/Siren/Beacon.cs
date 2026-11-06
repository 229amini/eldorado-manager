using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Siren
{
    /// <summary>
    /// Light drawn once at startup. WPF has no conic gradient, so the rotating beam is a bitmap: the GPU
    /// only spins it, and the soft edges cost nothing per frame.
    /// </summary>
    public static class Beacon
    {
        private static readonly Dictionary<Color, BitmapSource> RoomBeams = new Dictionary<Color, BitmapSource>();

        public static readonly BitmapSource LampBeam = MakeBeam(128, Colors.White, 0.9);   // wide, or a dot this small reads as a no-entry sign
        public static readonly BitmapSource Grain = MakeGrain(256);

        public static BitmapSource RoomBeam(Color color)
        {
            if (!RoomBeams.TryGetValue(color, out var beam)) RoomBeams[color] = beam = MakeBeam(768, color, 0.2);
            return beam;
        }

        /// <summary>Two opposite lobes along the x axis, as in a rotating warning lamp.</summary>
        private static BitmapSource MakeBeam(int size, Color color, double spread)
        {
            var pixels = new byte[size * size * 4];
            var noise = new Random(7);
            var half = size / 2.0;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = (x + 0.5 - half) / half;
                    var dy = (y + 0.5 - half) / half;
                    var reach = Math.Sqrt(dx * dx + dy * dy);
                    if (reach >= 1) continue;
                    var offAxis = Math.Asin(Math.Abs(Math.Sin(Math.Atan2(dy, dx))));
                    var core = Math.Exp(-Math.Pow(offAxis / spread, 2));
                    var haze = 0.22 * Math.Exp(-Math.Pow(offAxis / 0.55, 2));
                    var strength = (core + haze) / 1.22 * Math.Pow(1 - reach, 1.1);
                    // Dither by under one level, or a soft gradient this dark shows bands.
                    var alpha = Clamp(strength * 255 + noise.NextDouble() - 0.5);
                    var i = (y * size + x) * 4;
                    pixels[i] = (byte)(color.B * alpha / 255);
                    pixels[i + 1] = (byte)(color.G * alpha / 255);
                    pixels[i + 2] = (byte)(color.R * alpha / 255);
                    pixels[i + 3] = alpha;
                }
            }
            return Freeze(size, pixels);
        }

        private static BitmapSource MakeGrain(int size)
        {
            var pixels = new byte[size * size * 4];
            var noise = new Random(11);
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var alpha = (byte)noise.Next(256);
                var level = noise.Next(2) == 0 ? (byte)0 : alpha;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = level;
                pixels[i + 3] = alpha;
            }
            return Freeze(size, pixels);
        }

        private static byte Clamp(double value) => (byte)Math.Max(0, Math.Min(255, Math.Round(value)));

        private static BitmapSource Freeze(int size, byte[] pixels)
        {
            var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
            image.Freeze();
            return image;
        }
    }
}
