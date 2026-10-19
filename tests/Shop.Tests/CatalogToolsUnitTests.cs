using System.Text;
using Shop.Core.Admin;
using Shop.Web.Uploads;

namespace Shop.Tests;

public class CsvReaderTests
{
    private static string[][] Rows(string text) => CsvReader.Parse(text).Select(r => r.ToArray()).ToArray();

    [Fact]
    public void ReadsRowsAndCells_WithAnyLineEnd_AndSkipsEmptyLines()
    {
        Assert.Equal([["a", "b", "c"], ["1", "2", "3"]], Rows("a,b,c\r\n1,2,3\r\n"));
        Assert.Equal([["a", "b"], ["1", "2"]], Rows("a,b\n1,2"));
        Assert.Equal([["a", "b"], ["1", "2"]], Rows("a,b\r1,2\r"));
        Assert.Equal([["a", "b"], ["1", "2"]], Rows("a,b\r\n\r\n   \r\n,\r\n1,2\r\n"));  // empty lines and a line of empty cells are skipped
        Assert.Empty(Rows(""));
        Assert.Empty(Rows("\r\n\r\n"));
    }

    [Fact]
    public void Quotes_CarryCommas_QuotesAndLineBreaks()
    {
        var rows = Rows("Name,Text\r\n\"Bear, big\",\"He said \"\"hi\"\"\"\r\n\"Two\r\nlines\",x\r\n");

        Assert.Equal("Bear, big", rows[1][0]);
        Assert.Equal("He said \"hi\"", rows[1][1]);
        Assert.Equal("Two\r\nlines", rows[2][0]);
        Assert.Equal(3, rows.Length);
    }

    [Fact]
    public void AByteOrderMark_IsIgnored()
    {
        Assert.Equal([["Sku", "Brand"], ["A1", "B"]], Rows("﻿Sku,Brand\r\nA1,B"));
    }

    [Theory]
    [InlineData(";")]
    [InlineData("\t")]
    public void ASemicolonOrATab_CanSeparateTheCells(string delimiter)
    {
        var text = $"Sku{delimiter}Brand{delimiter}Name\r\nA1{delimiter}B{delimiter}\"x{delimiter}y\"\r\n";

        Assert.Equal([["Sku", "Brand", "Name"], ["A1", "B", $"x{delimiter}y"]], Rows(text));
    }

    [Fact]
    public void CommasInsideAQuotedHeaderLine_DoNotDecideTheDelimiter()
    {
        // The header uses semicolons; the commas are inside quotes in a later row.
        Assert.Equal([["Sku", "Name"], ["A1", "a,b,c,d"]], Rows("Sku;Name\r\nA1;\"a,b,c,d\"\r\n"));
    }

    [Fact]
    public void ACellOfOnlySpaces_AndAMissingLastCell_AreKept()
    {
        var rows = Rows("a,b,c\r\n1,,\r\n2,x");

        Assert.Equal(["1", "", ""], rows[1]);
        Assert.Equal(["2", "x"], rows[2]);
    }

    [Fact]
    public void AnUnclosedQuote_IsReadToTheEnd_WithoutFailing()
    {
        var rows = Rows("a,b\r\n1,\"never closed\r\n2,3");

        Assert.Equal(2, rows.Length);
        Assert.StartsWith("never closed", rows[1][1]);
    }

    [Theory]
    [InlineData("'=SUM(1)", "=SUM(1)")]
    [InlineData("'+7 91", "+7 91")]
    [InlineData("'-Bear", "-Bear")]
    [InlineData("'@home", "@home")]
    [InlineData("'Just text", "'Just text")]   // an apostrophe not in front of a formula character stays
    [InlineData("Plain", "Plain")]
    [InlineData("'", "'")]
    [InlineData("", "")]
    public void Unprotect_RemovesOnlyTheWritersApostrophe(string cell, string expected)
    {
        Assert.Equal(expected, CsvReader.Unprotect(cell));
    }

    [Fact]
    public void WhatTheWriterWrites_IsReadBackTheSame_IncludingHardTexts()
    {
        var header = new[] { "Sku", "Name", "Text" };
        object?[][] data =
        [
            ["A-1", "Bear, big", "He said \"hi\"\r\nsecond line"],
            ["A-2", "=1+1", "-minus"],
            ["A-3", "Հայերեն", "Русский текст"],
            ["A-4", "", "  spaces  "]
        ];

        var bytes = CsvWriter.Build(header, data.Select(r => (IReadOnlyList<object?>)r));
        var rows = Rows(Encoding.UTF8.GetString(bytes));

        Assert.Equal(["Sku", "Name", "Text"], rows[0]);
        Assert.Equal("Bear, big", rows[1][1]);
        Assert.Equal("He said \"hi\"\r\nsecond line", rows[1][2]);
        Assert.Equal("=1+1", CsvReader.Unprotect(rows[2][1]));       // protected on the way out, restored on the way in
        Assert.Equal("-minus", CsvReader.Unprotect(rows[2][2]));
        Assert.Equal("Հայերեն", rows[3][1]);
        Assert.Equal("Русский текст", rows[3][2]);
        Assert.Equal("  spaces  ", rows[4][2]);
        Assert.Equal(5, rows.Length);
    }
}

// Copying the photos of a product: real files are copied (so deleting one copy never harms the other); placeholders are shared.
public class ImageStoreCopyTests : IDisposable
{
    private readonly TempWebRoot _root = new();
    private readonly ImageStore _store;

    public ImageStoreCopyTests()
    {
        _store = new ImageStore(_root);
    }

    public void Dispose() => _root.Dispose();

    private async Task<StoredImage> SavedAsync(string folder = "products/5", bool thumb = true)
    {
        var bytes = TestImages.Jpeg(800, 600);
        var result = await _store.SaveAsync(new MemoryStream(bytes), bytes.Length, folder, thumb);
        return result.Image!;
    }

    [Fact]
    public async Task ACopy_HasNewFiles_InTheNewFolder_WithTheSameContent_AndSurvivesDeletingTheOriginal()
    {
        var original = await SavedAsync();

        var copy = await _store.CopyAsync(original, "products/9");

        Assert.NotNull(copy);
        Assert.StartsWith("/uploads/products/9/", copy!.Path);
        Assert.StartsWith("/uploads/products/9/", copy.ThumbPath);
        Assert.NotEqual(original.Path, copy.Path);
        Assert.NotEqual(original.ThumbPath, copy.ThumbPath);
        Assert.Equal(File.ReadAllBytes(_root.FullPath(original.Path)), File.ReadAllBytes(_root.FullPath(copy.Path)));
        Assert.Equal(File.ReadAllBytes(_root.FullPath(original.ThumbPath!)), File.ReadAllBytes(_root.FullPath(copy.ThumbPath!)));

        _store.Delete(original.Path, original.ThumbPath);

        Assert.False(File.Exists(_root.FullPath(original.Path)));
        Assert.True(File.Exists(_root.FullPath(copy.Path)));       // the copy is unaffected
        Assert.True(File.Exists(_root.FullPath(copy.ThumbPath!)));

        // And the other way round.
        var again = await _store.CopyAsync(copy, "products/10");
        _store.Delete(copy.Path, copy.ThumbPath);
        Assert.True(File.Exists(_root.FullPath(again!.Path)));
    }

    [Fact]
    public async Task APhotoWithoutASmallVersion_IsCopiedWithoutOne()
    {
        var original = await SavedAsync(thumb: false);

        var copy = await _store.CopyAsync(original, "products/9");

        Assert.NotNull(copy);
        Assert.Null(copy!.ThumbPath);
        Assert.True(File.Exists(_root.FullPath(copy.Path)));
    }

    [Fact]
    public async Task APlaceholderPicture_IsSharedNotCopied()
    {
        var placeholder = new StoredImage("/images/placeholders/generic-a.svg", null);

        var copy = await _store.CopyAsync(placeholder, "products/9");

        Assert.Equal(placeholder, copy);
        Assert.False(Directory.Exists(Path.Combine(_root.WebRootPath, "uploads", "products", "9")));
    }

    [Fact]
    public async Task AMissingFile_GivesNull_AndAPathOutsideTheUploadsFolder_IsNeverRead()
    {
        Assert.Null(await _store.CopyAsync(new StoredImage("/uploads/products/5/" + new string('a', 32) + ".jpg", null), "products/9"));

        // A file next to the uploads folder must not be reachable by a "../" path.
        File.WriteAllText(Path.Combine(_root.WebRootPath, "secret.txt"), "secret");
        Assert.Null(await _store.CopyAsync(new StoredImage("/uploads/../secret.txt", null), "products/9"));
        Assert.Null(await _store.CopyAsync(new StoredImage("/uploads/products/../../secret.txt", null), "products/9"));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("Products/9")]
    [InlineData("")]
    [InlineData("products//9")]
    public async Task ABadFolderName_IsRefused(string folder)
    {
        var original = await SavedAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => _store.CopyAsync(original, folder));
    }
}
