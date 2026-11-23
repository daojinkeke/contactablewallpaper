using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Lively.Packaging
{
    /// <summary>
    /// 无外部依赖的缩略图占位生成器（深空底色 + 暖橙光晕，1280x720）。
    /// 界面/预览缩略图允许后补，这里只保证 Lively 库有可读的封面。
    /// </summary>
    internal static class PlaceholderThumbnail
    {
        public static void Write(string path, int width = 1280, int height = 720)
        {
            var raw = BuildPixels(width, height);
            var compressed = Deflate(raw);

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using var file = File.Create(path);
            file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

            var ihdr = new byte[13];
            WriteInt32(ihdr, 0, width);
            WriteInt32(ihdr, 4, height);
            ihdr[8] = 8; //bit depth
            ihdr[9] = 2; //color type: truecolor
            ihdr[10] = 0;
            ihdr[11] = 0;
            ihdr[12] = 0;
            WriteChunk(file, "IHDR", ihdr);
            WriteChunk(file, "IDAT", compressed);
            WriteChunk(file, "IEND", Array.Empty<byte>());
        }

        private static byte[] BuildPixels(int width, int height)
        {
            var stride = width * 3 + 1;
            var raw = new byte[stride * height];
            var centerX = width * 0.5;
            var centerY = height * 0.46;

            for (var y = 0; y < height; y++)
            {
                var row = y * stride;
                raw[row] = 0; //filter: none
                for (var x = 0; x < width; x++)
                {
                    var t = (x / (double)width) * 0.55 + (y / (double)height) * 0.45;
                    var dx = (x - centerX) / width;
                    var dy = (y - centerY) / height;
                    var distance = Math.Sqrt(dx * dx + dy * dy);
                    var glow = Math.Max(0, 1 - distance * 3.2);
                    glow *= glow;

                    var r = Lerp(7, 26, t) + glow * 170;
                    var g = Lerp(11, 39, t) + glow * 96;
                    var b = Lerp(24, 64, t) + glow * 34;

                    var index = row + 1 + x * 3;
                    raw[index] = Clamp(r);
                    raw[index + 1] = Clamp(g);
                    raw[index + 2] = Clamp(b);
                }
            }
            return raw;
        }

        private static byte Clamp(double value) => (byte)Math.Max(0, Math.Min(255, (int)value));

        private static double Lerp(double from, double to, double t) => from + (to - from) * t;

        private static byte[] Deflate(byte[] raw)
        {
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw, 0, raw.Length);
            }
            return output.ToArray();
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var length = new byte[4];
            WriteInt32(length, 0, data.Length);
            stream.Write(length, 0, 4);

            var typeBytes = Encoding.ASCII.GetBytes(type);
            stream.Write(typeBytes, 0, typeBytes.Length);
            stream.Write(data, 0, data.Length);

            var crc = Crc32.Compute(typeBytes, data);
            var crcBytes = new byte[4];
            WriteInt32(crcBytes, 0, unchecked((int)crc));
            stream.Write(crcBytes, 0, 4);
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static class Crc32
        {
            private static readonly uint[] Table = CreateTable();

            private static uint[] CreateTable()
            {
                var table = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    var c = i;
                    for (var k = 0; k < 8; k++)
                        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    table[i] = c;
                }
                return table;
            }

            public static uint Compute(byte[] first, byte[] second)
            {
                var crc = 0xFFFFFFFFu;
                foreach (var b in first)
                    crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
                foreach (var b in second)
                    crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
                return crc ^ 0xFFFFFFFFu;
            }
        }
    }
}
