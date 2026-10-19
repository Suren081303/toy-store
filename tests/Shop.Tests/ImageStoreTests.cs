using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Shop.Core.Admin;
using Shop.Web.Uploads;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Shop.Tests;

// An environment whose web root is a throw-away folder, so no test writes into the real wwwroot.
public sealed class TempWebRoot : IWebHostEnvironment, IDisposable
{
    public TempWebRoot()
    {
        WebRootPath = Path.Combine(Path.GetTempPath(), "shop-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(WebRootPath);
    }

    public string WebRootPath { get; set; }

    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

    public string ApplicationName { get; set; } = "Shop.Tests";

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public string EnvironmentName { get; set; } = "Development";

    public string FullPath(string webPath) => Path.Combine(WebRootPath, webPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    public void Dispose()
    {
        if (Directory.Exists(WebRootPath))
        {
            Directory.Delete(WebRootPath, recursive: true);
        }
    }
}

public class ImageStoreTests : IDisposable
{
    private readonly TempWebRoot _root = new();
    private readonly ImageStore _store;

    public ImageStoreTests()
    {
        _store = new ImageStore(_root);
    }

    public void Dispose() => _root.Dispose();

    private Task<ImageStoreResult> SaveAsync(byte[] bytes, string folder = "products/5", bool thumb = true)
    {
        return _store.SaveAsync(new MemoryStream(bytes), bytes.Length, folder, thumb);
    }

    private Image LoadStored(string webPath) => Image.Load(_root.FullPath(webPath));

    // ---- Good pictures ----

    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("webp")]
    public async Task AllowedFormats_AreStoredAsJpeg_WithASmallVersion(string format)
    {
        var bytes = format switch
        {
            "jpeg" => TestImages.Jpeg(2000, 1500),
            "png" => TestImages.Png(2000, 1500),
            _ => TestImages.Webp(2000, 1500)
        };

        var result = await SaveAsync(bytes);

        Assert.Null(result.ErrorKey);
        var stored = result.Image!;
        Assert.StartsWith("/uploads/products/5/", stored.Path);
        Assert.EndsWith(".jpg", stored.Path);
        Assert.EndsWith("_t.jpg", stored.ThumbPath);

        using var full = LoadStored(stored.Path);
        Assert.Equal(1000, full.Width);   // scaled down to fit 1000 px
        Assert.Equal(750, full.Height);   // same shape
        using var thumb = LoadStored(stored.ThumbPath!);
        Assert.Equal(400, thumb.Width);
        Assert.Equal(300, thumb.Height);
        Assert.Equal("JPEG", full.Metadata.DecodedImageFormat!.Name);
    }

    [Fact]
    public async Task ASmallPicture_IsNotEnlarged()
    {
        var result = await SaveAsync(TestImages.Jpeg(300, 200));

        using var full = LoadStored(result.Image!.Path);
        using var thumb = LoadStored(result.Image.ThumbPath!);
        Assert.Equal((300, 200), (full.Width, full.Height));
        Assert.Equal((300, 200), (thumb.Width, thumb.Height));
    }

    [Fact]
    public async Task WithoutAThumbnail_NoSmallFileIsMade()
    {
        var result = await SaveAsync(TestImages.Jpeg(), "categories", thumb: false);

        Assert.Null(result.Image!.ThumbPath);
        Assert.StartsWith("/uploads/categories/", result.Image.Path);
        Assert.Single(Directory.GetFiles(Path.Combine(_root.WebRootPath, "uploads", "categories")));
    }

    [Fact]
    public async Task EveryUpload_GetsANewRandomName_NotTheUploadersName()
    {
        var bytes = TestImages.Jpeg();

        var a = (await SaveAsync(bytes)).Image!;
        var b = (await SaveAsync(bytes)).Image!;

        Assert.NotEqual(a.Path, b.Path);
        Assert.Matches(@"^/uploads/products/5/[0-9a-f]{32}\.jpg$", a.Path);
    }

    // ---- Cleaning ----

    [Fact]
    public async Task CameraInformation_IsRemoved()
    {
        var withExif = TestImages.JpegWithExif(400, 300, orientation: 1);
        using (var original = Image.Load(withExif))
        {
            Assert.NotNull(original.Metadata.ExifProfile); // the test picture really has it
        }

        var result = await SaveAsync(withExif);

        using var stored = LoadStored(result.Image!.Path);
        Assert.Null(stored.Metadata.ExifProfile);
        Assert.Null(stored.Metadata.IccProfile);
    }

    [Fact]
    public async Task ARotationNote_IsApplied_ToThePicture()
    {
        // Wider than tall, but the phone said "rotate 90 degrees": it must come out taller than wide.
        var result = await SaveAsync(TestImages.JpegWithExif(400, 200, orientation: 6));

        using var stored = LoadStored(result.Image!.Path);
        Assert.Equal((200, 400), (stored.Width, stored.Height));
    }

    [Fact]
    public async Task ATransparentPicture_BecomesWhite_NotBlack()
    {
        var result = await SaveAsync(TestImages.TransparentPng());

        using var stored = LoadStored(result.Image!.Path).CloneAs<Rgba32>();
        var pixel = stored[20, 20];
        Assert.True(pixel is { R: > 240, G: > 240, B: > 240 }, $"Expected white, got {pixel}.");
    }

    // ---- Refused files ----

    [Fact]
    public async Task ATextFile_IsRefused_NothingIsWritten()
    {
        var result = await SaveAsync(TestImages.Text());

        Assert.Null(result.Image);
        Assert.Equal("Admin_Img_NotImage", result.ErrorKey);
        Assert.False(Directory.Exists(Path.Combine(_root.WebRootPath, "uploads")));
    }

    [Fact]
    public async Task EmptyAndRandomBytes_AreRefused()
    {
        Assert.Equal("Admin_Img_NotImage", (await SaveAsync([])).ErrorKey);
        Assert.Equal("Admin_Img_NotImage", (await SaveAsync(new byte[2000])).ErrorKey);
        Assert.Equal("Admin_Img_NotImage", (await SaveAsync(Enumerable.Range(0, 3000).Select(i => (byte)(i * 31 % 251)).ToArray())).ErrorKey);
    }

    [Fact]
    public async Task AGif_IsRefused_BecauseOnlyJpegPngAndWebpAreAllowed()
    {
        var result = await SaveAsync(TestImages.Gif());

        Assert.Equal("Admin_Img_NotImage", result.ErrorKey);
    }

    [Fact]
    public async Task ACutOffPicture_IsRefused_InEveryFormat()
    {
        foreach (var whole in new[] { TestImages.Jpeg(400, 300), TestImages.Png(400, 300), TestImages.Webp(400, 300) })
        {
            var result = await SaveAsync(whole[..(whole.Length / 2)]);

            // Refused either as "damaged" (the file starts well but ends too early) or as "not a picture" (the cut is in the header).
            Assert.Null(result.Image);
            Assert.Contains(result.ErrorKey, new[] { "Admin_Img_Damaged", "Admin_Img_NotImage" });
        }

        Assert.False(Directory.Exists(Path.Combine(_root.WebRootPath, "uploads")));
    }

    [Fact]
    public async Task APictureWithTrailingZeroPadding_IsStillAccepted()
    {
        var padded = TestImages.Jpeg().Concat(new byte[16]).ToArray();

        Assert.Null((await SaveAsync(padded)).ErrorKey);
    }

    [Fact]
    public async Task AFileOver5MB_IsRefused_ByItsDeclaredSize()
    {
        var result = await _store.SaveAsync(new MemoryStream(TestImages.Jpeg()), ImageRules.MaxBytes + 1, "products/5", true);

        Assert.Equal("Admin_Img_TooBig", result.ErrorKey);
    }

    [Fact]
    public async Task AStreamThatLiesAboutItsSize_IsStillStopped()
    {
        // Says it is small, but really holds more than 5 MB.
        var big = new byte[ImageRules.MaxBytes + 10];
        Array.Copy(TestImages.Jpeg(), big, 100);

        var result = await _store.SaveAsync(new MemoryStream(big), 1000, "products/5", true);

        Assert.Equal("Admin_Img_TooBig", result.ErrorKey);
    }

    [Fact]
    public async Task APictureWiderThanTheLimit_IsRefused_BeforeItIsDecoded()
    {
        var bytes = TestImages.VeryWidePng(ImageRules.MaxSourceDimension + 1);
        Assert.True(bytes.Length < 100_000, "The test file should be small in bytes.");

        var result = await SaveAsync(bytes);

        Assert.Equal("Admin_Img_TooLarge", result.ErrorKey);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("products/../../x")]
    [InlineData("/etc")]
    [InlineData("Products/5")]
    [InlineData("")]
    [InlineData("a b")]
    public async Task ABadFolderName_IsRejected(string folder)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => SaveAsync(TestImages.Jpeg(), folder));
    }

    // ---- Deleting ----

    [Fact]
    public async Task Delete_RemovesTheStoredFiles()
    {
        var stored = (await SaveAsync(TestImages.Jpeg())).Image!;
        Assert.True(File.Exists(_root.FullPath(stored.Path)));

        _store.Delete(stored.Path, stored.ThumbPath);

        Assert.False(File.Exists(_root.FullPath(stored.Path)));
        Assert.False(File.Exists(_root.FullPath(stored.ThumbPath!)));
    }

    [Fact]
    public void Delete_NeverTouchesFilesOutsideTheUploadsFolder()
    {
        // A placeholder picture and a file next to the uploads folder.
        Directory.CreateDirectory(Path.Combine(_root.WebRootPath, "images", "placeholders"));
        var placeholder = _root.FullPath("/images/placeholders/toy.svg");
        File.WriteAllText(placeholder, "<svg/>");
        var secret = _root.FullPath("/appsettings.json");
        File.WriteAllText(secret, "{}");
        Directory.CreateDirectory(Path.Combine(_root.WebRootPath, "uploads"));

        _store.Delete(
            "/images/placeholders/toy.svg",          // not in uploads
            "/uploads/../appsettings.json",          // climbs out of uploads
            "/uploads/products/../../appsettings.json",
            "uploads/x.jpg",                         // no leading slash: not a web path
            "",
            null);

        Assert.True(File.Exists(placeholder));
        Assert.True(File.Exists(secret));
    }

    [Fact]
    public void Delete_OfAMissingFile_IsSilent()
    {
        _store.Delete("/uploads/products/9/not-there.jpg");
    }
}
