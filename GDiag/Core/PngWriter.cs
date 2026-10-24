using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace GDiag
{
    // Minimal RGBA PNG encoder for "--screenshot", so no imaging library is needed.
    public static class PngWriter
    {
        // rgba is bottom-up (as glReadPixels returns it)
        public static void Save(string path, byte[] rgba, int width, int height)
        {
            using var file = File.Create(path);
            file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            var header = new byte[13];
            WriteInt(header, 0, width);
            WriteInt(header, 4, height);
            header[8] = 8;  // bit depth
            header[9] = 6;  // RGBA
            Chunk(file, "IHDR", header);

            using var raw = new MemoryStream();
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                int stride = width * 4;
                for (int y = height - 1; y >= 0; y--)
                {
                    z.WriteByte(0); // no filter
                    z.Write(rgba, y * stride, stride);
                }
            }
            Chunk(file, "IDAT", raw.ToArray());
            Chunk(file, "IEND", Array.Empty<byte>());
        }

        static void Chunk(Stream s, string type, byte[] data)
        {
            var length = new byte[4];
            WriteInt(length, 0, data.Length);
            s.Write(length);
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes);
            s.Write(data);
            uint crc = Crc(Crc(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
            var crcBytes = new byte[4];
            WriteInt(crcBytes, 0, (int)crc);
            s.Write(crcBytes);
        }

        static void WriteInt(byte[] b, int o, int v)
        {
            b[o] = (byte)(v >> 24);
            b[o + 1] = (byte)(v >> 16);
            b[o + 2] = (byte)(v >> 8);
            b[o + 3] = (byte)v;
        }

        static readonly uint[] Table = MakeTable();

        static uint[] MakeTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                }
                table[n] = c;
            }
            return table;
        }

        static uint Crc(uint crc, byte[] data)
        {
            foreach (byte b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }
            return crc;
        }
    }
}
