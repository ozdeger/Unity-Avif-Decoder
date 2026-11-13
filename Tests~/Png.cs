using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AvifDecoding.Tests
{
    // Reads the 8-bit, non-interlaced grey, RGB and RGBA PNG files that avifdec writes, as RGB, top row first
    public static class Png
    {
        public static (int Width, int Height, byte[] Rgb) ReadRgb(string path)
        {
            var data = File.ReadAllBytes(path);
            int width = 0, height = 0, channels = 0;
            var idat = new MemoryStream();
            for (var pos = 8; pos < data.Length;)
            {
                var length = ReadInt32(data, pos);
                var type = Encoding.ASCII.GetString(data, pos + 4, 4);
                if (type == "IHDR")
                {
                    width = ReadInt32(data, pos + 8);
                    height = ReadInt32(data, pos + 12);
                    if (data[pos + 16] != 8 || data[pos + 20] != 0)
                        throw new NotSupportedException($"{path}: only 8-bit non-interlaced PNG files are read");
                    channels = data[pos + 17] switch
                    {
                        0 => 1,
                        2 => 3,
                        6 => 4,
                        _ => throw new NotSupportedException($"{path}: colour type {data[pos + 17]}"),
                    };
                }
                else if (type == "IDAT")
                {
                    idat.Write(data, pos + 8, length);
                }

                pos += 12 + length;
            }

            var stride = width * channels;
            var raw = new byte[(stride + 1) * height];
            idat.Position = 0;
            using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
            {
                var read = 0;
                while (read < raw.Length)
                {
                    var n = zlib.Read(raw, read, raw.Length - read);
                    if (n == 0)
                        throw new InvalidDataException($"{path}: truncated image data");
                    read += n;
                }
            }

            var pixels = new byte[stride * height];
            for (var y = 0; y < height; y++)
            {
                var filter = raw[y * (stride + 1)];
                for (var x = 0; x < stride; x++)
                {
                    int a = x >= channels ? pixels[(y * stride) + x - channels] : 0;
                    int b = y > 0 ? pixels[((y - 1) * stride) + x] : 0;
                    int c = x >= channels && y > 0 ? pixels[((y - 1) * stride) + x - channels] : 0;
                    int value = raw[(y * (stride + 1)) + 1 + x];
                    value += filter switch
                    {
                        0 => 0,
                        1 => a,
                        2 => b,
                        3 => (a + b) / 2,
                        4 => Paeth(a, b, c),
                        _ => throw new InvalidDataException($"{path}: filter {filter}"),
                    };
                    pixels[(y * stride) + x] = (byte)value;
                }
            }

            var rgb = new byte[width * height * 3];
            for (var i = 0; i < width * height; i++)
            {
                for (var k = 0; k < 3; k++)
                    rgb[(i * 3) + k] = pixels[(i * channels) + (channels == 1 ? 0 : k)];
            }

            return (width, height, rgb);
        }

        // Reads 8 and 16-bit, non-interlaced grey, grey and alpha, RGB and RGBA PNG files as RGBA, top row first: grey is
        // copied to red, green and blue, and a missing alpha is the largest value
        public static (int Width, int Height, ushort[] Rgba, int Max, bool HasAlpha) ReadRgba(string path)
        {
            var data = File.ReadAllBytes(path);
            int width = 0, height = 0, channels = 0, depth = 0;
            var idat = new MemoryStream();
            for (var pos = 8; pos < data.Length;)
            {
                var length = ReadInt32(data, pos);
                var type = Encoding.ASCII.GetString(data, pos + 4, 4);
                if (type == "IHDR")
                {
                    width = ReadInt32(data, pos + 8);
                    height = ReadInt32(data, pos + 12);
                    depth = data[pos + 16];
                    if ((depth != 8 && depth != 16) || data[pos + 20] != 0)
                        throw new NotSupportedException($"{path}: only 8 and 16-bit non-interlaced PNG files are read");
                    channels = data[pos + 17] switch
                    {
                        0 => 1,
                        2 => 3,
                        4 => 2,
                        6 => 4,
                        _ => throw new NotSupportedException($"{path}: colour type {data[pos + 17]}"),
                    };
                }
                else if (type == "IDAT")
                {
                    idat.Write(data, pos + 8, length);
                }

                pos += 12 + length;
            }

            var bytesPerSample = depth / 8;
            var bpp = channels * bytesPerSample;
            var stride = width * bpp;
            var raw = new byte[(stride + 1) * height];
            idat.Position = 0;
            using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
            {
                var read = 0;
                while (read < raw.Length)
                {
                    var n = zlib.Read(raw, read, raw.Length - read);
                    if (n == 0)
                        throw new InvalidDataException($"{path}: truncated image data");
                    read += n;
                }
            }

            var bytes = new byte[stride * height];
            for (var y = 0; y < height; y++)
            {
                var filter = raw[y * (stride + 1)];
                for (var x = 0; x < stride; x++)
                {
                    int a = x >= bpp ? bytes[(y * stride) + x - bpp] : 0;
                    int b = y > 0 ? bytes[((y - 1) * stride) + x] : 0;
                    int c = x >= bpp && y > 0 ? bytes[((y - 1) * stride) + x - bpp] : 0;
                    int value = raw[(y * (stride + 1)) + 1 + x];
                    value += filter switch
                    {
                        0 => 0,
                        1 => a,
                        2 => b,
                        3 => (a + b) / 2,
                        4 => Paeth(a, b, c),
                        _ => throw new InvalidDataException($"{path}: filter {filter}"),
                    };
                    bytes[(y * stride) + x] = (byte)value;
                }
            }

            var max = (1 << depth) - 1;
            var rgba = new ushort[width * height * 4];
            for (var i = 0; i < width * height; i++)
            {
                int Sample(int channel) => bytesPerSample == 1 ? bytes[(i * bpp) + channel] : (bytes[(i * bpp) + (2 * channel)] << 8) | bytes[(i * bpp) + (2 * channel) + 1];
                var grey = channels <= 2;
                for (var k = 0; k < 3; k++)
                    rgba[(i * 4) + k] = (ushort)Sample(grey ? 0 : k);
                rgba[(i * 4) + 3] = (ushort)(channels == 2 ? Sample(1) : channels == 4 ? Sample(3) : max);
            }

            return (width, height, rgba, max, channels == 2 || channels == 4);
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        private static int ReadInt32(byte[] data, int offset) => (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
    }
}
