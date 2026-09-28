using FluentAssertions;
using MoizPos.Application.Calculations;
using MoizPos.Domain.Errors;
using MoizPos.Infrastructure.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace MoizPos.UnitTests.Infrastructure;

/// <summary>
/// Feature 005 — product pictures.
///
/// <para>A thumbnail is generated once, at upload, and lives at a path DERIVED from the full
/// image's path rather than in a column of its own (research.md). That makes the two
/// impossible to get out of step, but only while every read and write goes through this
/// service — which is what these tests hold in place.</para>
/// </summary>
public sealed class ImageStorageServiceTests : IDisposable
{
    private readonly string _contentRoot =
        Path.Combine(Path.GetTempPath(), $"moizpos-images-{Guid.NewGuid():N}");

    private ImageStorageService CreateService() =>
        new(new ImageStorageOptions { ProductImageRoot = "content/products" }, _contentRoot);

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
    }

    private static MemoryStream JpegOf(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        var stream = new MemoryStream();
        image.Save(stream, new JpegEncoder());
        stream.Position = 0;

        return stream;
    }

    private string FullPathOf(string relativePath) =>
        Path.Combine(_contentRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task Saving_an_image_also_writes_a_thumbnail_beside_it()
    {
        var service = CreateService();
        await using var source = JpegOf(1200, 900);

        var relativePath = await service.SaveProductImageAsync(source, "image/jpeg", source.Length);

        var thumbnailPath = ProductImagePaths.ThumbnailFor(relativePath)!;

        File.Exists(FullPathOf(relativePath)).Should().BeTrue("the full-size image is kept");
        File.Exists(FullPathOf(thumbnailPath)).Should().BeTrue(
            "a list showing hundreds of products must never download full-size photographs");
    }

    [Fact]
    public async Task The_thumbnail_fits_inside_the_target_box_and_keeps_its_shape()
    {
        var service = CreateService();
        await using var source = JpegOf(1200, 600);

        var relativePath = await service.SaveProductImageAsync(source, "image/jpeg", source.Length);

        using var thumbnail = await Image.LoadAsync(FullPathOf(ProductImagePaths.ThumbnailFor(relativePath)!));

        thumbnail.Width.Should().BeLessThanOrEqualTo(150);
        thumbnail.Height.Should().BeLessThanOrEqualTo(150);

        // 2:1 in, 2:1 out. Stretching a phone case to a square would defeat the point of
        // showing a picture at all.
        thumbnail.Width.Should().Be(150);
        thumbnail.Height.Should().Be(75);
    }

    [Fact]
    public async Task An_image_smaller_than_the_thumbnail_is_not_enlarged()
    {
        var service = CreateService();
        await using var source = JpegOf(80, 60);

        var relativePath = await service.SaveProductImageAsync(source, "image/jpeg", source.Length);

        using var thumbnail = await Image.LoadAsync(FullPathOf(ProductImagePaths.ThumbnailFor(relativePath)!));

        // Upscaling invents detail that was never photographed and only makes the file bigger.
        thumbnail.Width.Should().Be(80);
        thumbnail.Height.Should().Be(60);
    }

    [Fact]
    public async Task Deleting_an_image_removes_its_thumbnail_too()
    {
        var service = CreateService();
        await using var source = JpegOf(400, 400);

        var relativePath = await service.SaveProductImageAsync(source, "image/jpeg", source.Length);
        var thumbnailPath = ProductImagePaths.ThumbnailFor(relativePath)!;

        service.DeleteProductImage(relativePath);

        File.Exists(FullPathOf(relativePath)).Should().BeFalse();
        File.Exists(FullPathOf(thumbnailPath)).Should().BeFalse(
            "a replaced picture that leaves its thumbnail behind fills the disk with files "
            + "nothing will ever read or delete");
    }

    [Fact]
    public void Deleting_refuses_to_reach_outside_the_image_directory()
    {
        var service = CreateService();
        var outsider = Path.Combine(_contentRoot, "keep-me.txt");
        Directory.CreateDirectory(_contentRoot);
        File.WriteAllText(outsider, "not an image");

        service.DeleteProductImage("../keep-me.txt");

        File.Exists(outsider).Should().BeTrue(
            "the stored path is data, and data must never be able to name a file to delete");
    }

    [Fact]
    public async Task A_file_that_is_not_an_image_is_refused()
    {
        var service = CreateService();
        await using var notAnImage = new MemoryStream("<?php echo 1; ?>"u8.ToArray());

        var act = async () =>
            await service.SaveProductImageAsync(notAnImage, "image/jpeg", notAnImage.Length);

        // The content type is the client's claim; the bytes are the truth. Accepting this
        // would leave an unreadable file that every product list then fails to render.
        await act.Should().ThrowAsync<BusinessRuleViolationException>()
            .WithMessage("*not a readable image*");
    }

    [Theory]
    [InlineData("content/products/abc.jpg", "content/products/abc_thumb.jpg")]
    [InlineData("content/products/abc.png", "content/products/abc_thumb.png")]
    [InlineData("content/products/a.b.c.webp", "content/products/a.b.c_thumb.webp")]
    public void The_thumbnail_path_is_derived_from_the_image_path(string image, string expected)
    {
        ProductImagePaths.ThumbnailFor(image).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_product_with_no_picture_has_no_thumbnail_path(string? image)
    {
        ProductImagePaths.ThumbnailFor(image).Should().BeNull(
            "\"never had a picture\" must stay distinguishable from \"has one\"");
    }
}
