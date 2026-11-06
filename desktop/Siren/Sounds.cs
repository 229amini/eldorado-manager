using System.IO;
using System.Text;

namespace Siren
{
    /// <summary>SoundPlayer has no volume control, so the samples are scaled before it reads them.</summary>
    public static class Sounds
    {
        /// <param name="name">A built-in sound, used unless a readable custom file is given.</param>
        /// <param name="volume">0 to 100.</param>
        public static Stream Load(string name, int volume, string custom = null)
        {
            byte[] wave;
            if (!string.IsNullOrEmpty(custom) && File.Exists(custom))
            {
                wave = File.ReadAllBytes(custom);
            }
            else
            {
                using (var resource = typeof(Sounds).Assembly.GetManifestResourceStream(name))
                using (var copy = new MemoryStream())
                {
                    resource.CopyTo(copy);
                    wave = copy.ToArray();
                }
            }
            Scale(wave, volume / 100.0);
            return new MemoryStream(wave);
        }

        /// <summary>Walks the RIFF chunks. Only 16-bit PCM is scaled; any other file plays as it is.</summary>
        private static void Scale(byte[] wave, double gain)
        {
            if (wave.Length < 12 || Encoding.ASCII.GetString(wave, 0, 4) != "RIFF") return;
            var pcm16 = false;
            for (var at = 12; at + 8 <= wave.Length;)
            {
                var chunk = Encoding.ASCII.GetString(wave, at, 4);
                var size = System.BitConverter.ToInt32(wave, at + 4);
                if (chunk == "fmt " && at + 24 <= wave.Length)
                {
                    pcm16 = System.BitConverter.ToInt16(wave, at + 8) == 1 && System.BitConverter.ToInt16(wave, at + 22) == 16;
                }
                else if (chunk == "data" && pcm16)
                {
                    var end = System.Math.Min(at + 8 + size, wave.Length - 1);
                    for (var i = at + 8; i + 1 <= end; i += 2)
                    {
                        var sample = (short)(System.BitConverter.ToInt16(wave, i) * gain);
                        wave[i] = (byte)sample;
                        wave[i + 1] = (byte)(sample >> 8);
                    }
                    return;
                }
                at += 8 + size + (size & 1);
            }
        }
    }
}
