using System.Text.RegularExpressions;
using Shop.Core.Admin;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Shop.Web.Uploads;

// Saves product and category photos in wwwroot/uploads, or in the folder named by the setting Uploads:Folder (so photos can live
// outside the deployed files and survive a new deployment).
// What the uploader controls (file name, extension, content type, metadata) is never used: the content is checked,
// the picture is decoded and written again as a clean JPEG under a random name.
public partial class ImageStore : IImageStore
{
    private const int JpegQuality = 82;

    private readonly string _uploadsRoot;

    public ImageStore(IWebHostEnvironment environment, IConfiguration? configuration = null)
    {
        _uploadsRoot = UploadsRoot(environment, configuration);
    }

    // The folder on disk behind the web address /uploads.
    public static string UploadsRoot(IWebHostEnvironment environment, IConfiguration? configuration)
    {
        var folder = configuration?["Uploads:Folder"];

        return Path.GetFullPath(string.IsNullOrWhiteSpace(folder) ? Path.Combine(environment.WebRootPath, "uploads") : folder);
    }

    public async Task<ImageStoreResult> SaveAsync(Stream content, long length, string folder, bool makeThumb)
    {
        if (!SafeFolder().IsMatch(folder))
        {
            throw new ArgumentException("The folder name may contain only lower-case letters, digits, '-', '_' and '/'.", nameof(folder));
        }

        if (length <= 0)
        {
            return Refused("Admin_Img_NotImage");
        }

        if (length > ImageRules.MaxBytes)
        {
            return Refused("Admin_Img_TooBig");
        }

        // Read at most the limit plus one byte, so a stream that lies about its length cannot fill the memory.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, 81920);
        if (buffer.Length == 0)
        {
            return Refused("Admin_Img_NotImage");
        }

        if (buffer.Length > ImageRules.MaxBytes)
        {
            return Refused("Admin_Img_TooBig");
        }

        // 1. Look at the headers only (cheap): is it a JPEG, PNG or WebP, and how big is the picture?
        buffer.Position = 0;
        ImageInfo info;

        // A file that is not a picture makes ImageSharp throw one of these two. That is exactly "refuse this upload",
        // so they are turned into a message for the admin. Any other exception is a real fault and is not caught.
        try
        {
            info = await Image.IdentifyAsync(buffer);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException)
        {
            return Refused("Admin_Img_NotImage");
        }

        var format = info.Metadata.DecodedImageFormat;
        if (format is not (JpegFormat or PngFormat or WebpFormat))
        {
            return Refused("Admin_Img_NotImage");
        }

        // A file that was cut off is decoded "politely" by the library (the missing part turns grey), so the end of the file
        // is checked here: every real JPEG, PNG and WebP has a proper ending.
        buffer.TryGetBuffer(out var bytes);
        if (!LooksComplete(format, bytes.AsSpan()))
        {
            return Refused("Admin_Img_Damaged");
        }

        if (info.Width <= 0 || info.Height <= 0
            || info.Width > ImageRules.MaxSourceDimension || info.Height > ImageRules.MaxSourceDimension
            || (long)info.Width * info.Height > ImageRules.MaxSourcePixels)
        {
            return Refused("Admin_Img_TooLarge");
        }

        // 2. Decode, clean up, scale, save.
        buffer.Position = 0;
        Image image;
        try
        {
            image = await Image.LoadAsync(buffer);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException)
        {
            return Refused("Admin_Img_NotImage");
        }

        using (image)
        {
            // Phones store "rotate me" in the metadata: apply it, then drop all metadata (camera, place, time).
            image.Mutate(x => x.AutoOrient());
            image.Metadata.ExifProfile = null;
            image.Metadata.IccProfile = null;
            image.Metadata.XmpProfile = null;
            image.Metadata.IptcProfile = null;

            // A transparent PNG/WebP becomes white behind the picture (a JPEG has no transparency).
            image.Mutate(x => x.BackgroundColor(Color.White));

            var directory = Path.Combine(_uploadsRoot, folder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(directory);

            var name = Guid.NewGuid().ToString("N");
            var fullName = name + ".jpg";
            var thumbName = name + "_t.jpg";

            await SaveJpegAsync(image, ImageRules.MaxFullSize, Path.Combine(directory, fullName));
            if (makeThumb)
            {
                await SaveJpegAsync(image, ImageRules.MaxThumbSize, Path.Combine(directory, thumbName));
            }

            var webFolder = "/uploads/" + folder;
            return new ImageStoreResult(new StoredImage($"{webFolder}/{fullName}", makeThumb ? $"{webFolder}/{thumbName}" : null), null);
        }
    }

    public void Delete(params string?[] paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("/uploads/", StringComparison.Ordinal))
            {
                continue;
            }

            // The resolved path must stay inside the uploads folder ("/uploads/../appsettings.json" is refused).
            var relative = path["/uploads/".Length..].Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(_uploadsRoot, relative));
            if (!full.StartsWith(_uploadsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(full))
            {
                File.Delete(full);
            }
        }
    }

    public async Task<StoredImage?> CopyAsync(StoredImage source, string folder)
    {
        if (!SafeFolder().IsMatch(folder))
        {
            throw new ArgumentException("The folder name may contain only lower-case letters, digits, '-', '_' and '/'.", nameof(folder));
        }

        // A placeholder picture is one file for all products that use it: nothing to copy.
        if (!source.Path.StartsWith("/uploads/", StringComparison.Ordinal))
        {
            return source;
        }

        var full = ResolveInsideUploads(source.Path);
        if (full is null || !File.Exists(full))
        {
            return null;
        }

        var directory = Path.Combine(_uploadsRoot, folder.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);

        var name = Guid.NewGuid().ToString("N");
        var webFolder = "/uploads/" + folder;
        var newPath = $"{webFolder}/{name}{Path.GetExtension(full)}";
        await CopyFileAsync(full, Path.Combine(directory, name + Path.GetExtension(full)));

        string? newThumb = null;
        var thumbFull = source.ThumbPath is null ? null : ResolveInsideUploads(source.ThumbPath);
        if (thumbFull is not null && File.Exists(thumbFull))
        {
            newThumb = $"{webFolder}/{name}_t{Path.GetExtension(thumbFull)}";
            await CopyFileAsync(thumbFull, Path.Combine(directory, name + "_t" + Path.GetExtension(thumbFull)));
        }

        return new StoredImage(newPath, newThumb);
    }

    // The file on disk behind a web path /uploads/..., or null when the path would leave the uploads folder.
    private string? ResolveInsideUploads(string webPath)
    {
        if (!webPath.StartsWith("/uploads/", StringComparison.Ordinal))
        {
            return null;
        }

        var relative = webPath["/uploads/".Length..].Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_uploadsRoot, relative));

        return full.StartsWith(_uploadsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static async Task CopyFileAsync(string from, string to)
    {
        await using var source = File.OpenRead(from);
        await using var target = File.Create(to);
        await source.CopyToAsync(target);
    }

    // Saves a copy of the picture scaled down to fit in a square of the given size (a smaller picture is not enlarged).
    private static async Task SaveJpegAsync(Image image, int maxSize, string file)
    {
        using var copy = image.Clone(x =>
        {
            if (image.Width > maxSize || image.Height > maxSize)
            {
                x.Resize(new ResizeOptions { Size = new Size(maxSize, maxSize), Mode = ResizeMode.Max });
            }
        });

        await copy.SaveAsJpegAsync(file, new JpegEncoder { Quality = JpegQuality });
    }

    // JPEG: ends with the "end of image" marker FF D9 (zero padding after it is allowed).
    // PNG: ends with the IEND chunk. WebP: the size in the RIFF header matches the file.
    private static bool LooksComplete(IImageFormat format, ReadOnlySpan<byte> data)
    {
        if (format is JpegFormat)
        {
            var end = data.Length;
            while (end > 0 && data[end - 1] == 0)
            {
                end--;
            }

            return end >= 2 && data[end - 2] == 0xFF && data[end - 1] == 0xD9;
        }

        if (format is PngFormat)
        {
            ReadOnlySpan<byte> iend = [0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];
            return data.Length >= iend.Length && data[^iend.Length..].SequenceEqual(iend);
        }

        // WebP: "RIFF", then the size of everything after these 8 bytes (little-endian).
        if (data.Length < 12)
        {
            return false;
        }

        var size = (long)data[4] | ((long)data[5] << 8) | ((long)data[6] << 16) | ((long)data[7] << 24);
        return size + 8 == data.Length || size + 9 == data.Length; // a padding byte may follow an odd size
    }

    private static ImageStoreResult Refused(string errorKey) => new(null, errorKey);

    [GeneratedRegex("^[a-z0-9_-]+(/[a-z0-9_-]+)*$")]
    private static partial Regex SafeFolder();
}
