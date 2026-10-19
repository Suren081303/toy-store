using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Shop.Tests;

// Pictures made in memory for the photo tests.
public static class TestImages
{
    public static byte[] Jpeg(int width = 800, int height = 600, Color? color = null)
    {
        using var image = Filled(width, height, color ?? Color.CornflowerBlue);
        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream);
        return stream.ToArray();
    }

    // A JPEG with camera information, as a phone writes it. orientation 6 = "rotate 90 degrees to look right".
    public static byte[] JpegWithExif(int width, int height, ushort orientation)
    {
        using var image = Filled(width, height, Color.Orange);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "Test Phone");
        exif.SetValue(ExifTag.Software, "Secret App 1.0");
        exif.SetValue(ExifTag.Orientation, orientation);
        image.Metadata.ExifProfile = exif;

        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream);
        return stream.ToArray();
    }

    public static byte[] Png(int width = 800, int height = 600, Color? color = null)
    {
        using var image = Filled(width, height, color ?? Color.SeaGreen);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    // A PNG that is fully transparent.
    public static byte[] TransparentPng(int width = 40, int height = 40)
    {
        using var image = Filled(width, height, Color.Transparent);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        return stream.ToArray();
    }

    public static byte[] Webp(int width = 800, int height = 600)
    {
        using var image = Filled(width, height, Color.MediumPurple);
        using var stream = new MemoryStream();
        image.SaveAsWebp(stream, new WebpEncoder());
        return stream.ToArray();
    }

    // A picture format that is not allowed.
    public static byte[] Gif(int width = 50, int height = 50)
    {
        using var image = Filled(width, height, Color.Red);
        using var stream = new MemoryStream();
        image.SaveAsGif(stream, new GifEncoder());
        return stream.ToArray();
    }

    // Wide but tiny in bytes: a "picture bomb" shape.
    public static byte[] VeryWidePng(int width) => Png(width, 1, Color.White);

    public static byte[] Text() => System.Text.Encoding.UTF8.GetBytes("This is not a picture. <script>alert(1)</script>");

    // The start of a real JPEG, cut off.
    public static byte[] TruncatedJpeg()
    {
        var full = Jpeg(400, 300);
        return full[..(full.Length / 3)];
    }

    private static Image<Rgba32> Filled(int width, int height, Color color)
    {
        var image = new Image<Rgba32>(width, height);
        image.Mutate(x => x.BackgroundColor(color));
        return image;
    }
}
