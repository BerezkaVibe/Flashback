using System;
using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Flashback;

// Writes see-through pictures for the exporter quickly. WPF's PNG encoder compresses hard and is the
// slowest part of preparing text, pictures and shapes; these files are read once by ffmpeg and
// deleted, so the fastest compression is the right trade. Rows are stored unfiltered.
internal static class FastPng
{
    internal static void Save(BitmapSource bitmap, string path)
    {
        if (bitmap.Format != PixelFormats.Bgra32) bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight, stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        // Filter byte per row, then RGBA.
        var raw = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int from = y * stride, to = y * (stride + 1) + 1;
            for (int x = 0; x < stride; x += 4)
            {
                raw[to + x] = pixels[from + x + 2]; raw[to + x + 1] = pixels[from + x + 1];
                raw[to + x + 2] = pixels[from + x]; raw[to + x + 3] = pixels[from + x + 3];
            }
        }
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw, 0, raw.Length);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        WriteBig(header, 0, width); WriteBig(header, 4, height);
        header[8] = 8; header[9] = 6; // 8 bits, RGBA
        Chunk(file, "IHDR", header, header.Length);
        Chunk(file, "IDAT", packed.GetBuffer(), (int)packed.Length);
        Chunk(file, "IEND", Array.Empty<byte>(), 0);
    }
    private static void WriteBig(byte[] b, int at, int v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }
    private static void Chunk(Stream s, string type, byte[] data, int length)
    {
        var head = new byte[8]; WriteBig(head, 0, length);
        for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        s.Write(head, 0, 8); s.Write(data, 0, length);
        uint crc = Crc(Crc(0xFFFFFFFF, head, 4, 4), data, 0, length) ^ 0xFFFFFFFF;
        var tail = new byte[4]; WriteBig(tail, 0, unchecked((int)crc)); s.Write(tail, 0, 4);
    }
    private static readonly uint[] Table = MakeTable();
    private static uint[] MakeTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; table[n] = c; }
        return table;
    }
    private static uint Crc(uint crc, byte[] data, int start, int length)
    {
        for (int i = start; i < start + length; i++) crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
