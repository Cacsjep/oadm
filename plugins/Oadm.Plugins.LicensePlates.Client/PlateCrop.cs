using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>
/// A plate crop as the Log shows it. ALPV's crops end in a column of magenta pixels (every crop of ALPV 3.1.7 on the test
/// camera); a last column that is magenta from top to bottom is left out, every other picture shows as it is.
/// </summary>
public static class PlateCrop
{
    /// <summary>The picture of a crop's JPEG bytes, without a magenta last column.</summary>
    public static IImage Decode(byte[] jpeg)
    {
        using var stream = new MemoryStream(jpeg);
        var bitmap = new Bitmap(stream);
        var size = bitmap.PixelSize;
        if (size.Width < 2 || size.Height < 1)
        {
            return bitmap;
        }

        // The last column as 32-bit pixels (the decoder's own format; R and B swap places between Rgba and Bgra, which
        // magenta does not mind).
        var column = new byte[size.Height * 4];
        var handle = GCHandle.Alloc(column, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(size.Width - 1, 0, 1, size.Height), handle.AddrOfPinnedObject(), column.Length, 4);
        }
        finally
        {
            handle.Free();
        }

        return bitmap.Format is { } format && (format == PixelFormat.Bgra8888 || format == PixelFormat.Rgba8888) && IsMagentaColumn(column)
            ? new CroppedBitmap(bitmap, new PixelRect(0, 0, size.Width - 1, size.Height))
            : bitmap;
    }

    /// <summary>Whether every 32-bit pixel (Rgba or Bgra) of a column is magenta: red and blue high, green clearly lower.</summary>
    public static bool IsMagentaColumn(ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length < 4)
        {
            return false;
        }

        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            byte first = pixels[i], green = pixels[i + 1], third = pixels[i + 2];
            if (first < 200 || third < 200 || green > 170)
            {
                return false;
            }
        }

        return true;
    }
}
