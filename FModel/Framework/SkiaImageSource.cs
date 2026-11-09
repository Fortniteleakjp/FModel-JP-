using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
using SkiaSharp;

namespace FModel.Framework;

/// <summary>
/// SkiaSharp bitmaps to WPF images, straight from the pixels
/// encoding to PNG and decoding it back (what the previews used to do) costs far more than the copy
/// </summary>
public static class SkiaImageSource
{
    /// <summary>
    /// a frozen image usable from any thread, null for an empty bitmap
    /// </summary>
    /// <param name="ignoreAlpha">show the color channels only, like the "disable alpha channel" option</param>
    public static BitmapSource ToBitmapSource(this SKBitmap bitmap, bool ignoreAlpha = false)
    {
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0) return null;

        try
        {
            var source = CreateFromPixels(bitmap, ignoreAlpha);
            source.Freeze();
            return source;
        }
        catch (Exception e)
        {
            Log.Debug(e, "Failed to copy the pixels of a {ColorType} bitmap, encoding it instead", bitmap.ColorType);
            return CreateFromEncoded(bitmap);
        }
    }

    private static BitmapSource CreateFromPixels(SKBitmap bitmap, bool ignoreAlpha)
    {
        var alphaType = bitmap.AlphaType == SKAlphaType.Premul ? SKAlphaType.Premul : SKAlphaType.Unpremul;
        var format = ignoreAlpha || bitmap.AlphaType == SKAlphaType.Opaque
            ? PixelFormats.Bgr32 // the alpha byte is ignored
            : alphaType == SKAlphaType.Premul ? PixelFormats.Pbgra32 : PixelFormats.Bgra32;

        if (bitmap.ColorType == SKColorType.Bgra8888)
        {
            return BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, format, null,
                bitmap.GetPixels(), bitmap.RowBytes * bitmap.Height, bitmap.RowBytes);
        }

        // Rgba8888, Gray8, ... converted by Skia while reading
        var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, alphaType);
        var pixels = new byte[info.BytesSize];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var pixmap = bitmap.PeekPixels();
            if (pixmap is null || !pixmap.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes))
                throw new InvalidOperationException($"Skia can't convert {bitmap.ColorType} to {SKColorType.Bgra8888}");
        }
        finally
        {
            handle.Free();
        }

        return BitmapSource.Create(info.Width, info.Height, 96, 96, format, null, pixels, info.RowBytes);
    }

    private static BitmapSource CreateFromEncoded(SKBitmap bitmap)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        if (data is null) return null;

        using var stream = new MemoryStream(data.ToArray(), false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
