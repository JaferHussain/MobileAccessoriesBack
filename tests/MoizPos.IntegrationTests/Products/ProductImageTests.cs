using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MoizPos.Application.Calculations;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace MoizPos.IntegrationTests.Products;

/// <summary>
/// Feature 005 — product pictures, over HTTP.
///
/// <para>The unit tests prove <c>ImageStorageService</c> writes and deletes both files. These
/// prove the endpoint actually calls it that way: that a real upload produces a thumbnail on
/// disk, and that REPLACING a picture leaves nothing of the old one behind. That second one
/// only fails once the disk is full, months later, which is far too late to notice.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ProductImageTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public ProductImageTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data, ErrorBody? Error);

    private sealed record ErrorBody(string Code, string Message);

    private async Task<HttpClient> ClientAsync(UserRole role)
    {
        var (_, username, password) = await _api.CreateUserAsync(role);
        var client = _api.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        var body = await login.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json);
        var token = body!.Data!.GetProperty("accessToken").GetString();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    private async Task<long> CreateProductAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync("/api/products", new
        {
            name = $"Pic {Guid.NewGuid():N}"[..20],
            categoryId = (await _api.EnsureCatalogueAsync()).CategoryId,
            brandId = (await _api.EnsureCatalogueAsync()).BrandId,
            model = "CATZ-01",
            barcode = (string?)null,
            costPrice = 800m,
            wholesalePrice = 950m,
            retailPrice = 1200m,
            salePrice = 1100m,
            quantityOnHand = 10,
            minStockThreshold = 3,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json);

        return body!.Data!.GetProperty("id").GetInt64();
    }

    private static MultipartFormDataContent JpegUpload(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        var bytes = new MemoryStream();
        image.Save(bytes, new JpegEncoder());

        var file = new ByteArrayContent(bytes.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        return new MultipartFormDataContent { { file, "file", "photo.jpg" } };
    }

    private string OnDisk(string relativePath) =>
        Path.Combine(_api.ContentRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private async Task<string> UploadAsync(HttpClient admin, long productId, int width, int height)
    {
        using var upload = JpegUpload(width, height);

        var response = await admin.PostAsync($"/api/products/{productId}/image", upload);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json);

        return body!.Data!.GetProperty("imagePath").GetString()!;
    }

    [Fact]
    public async Task Uploading_a_picture_stores_the_image_and_its_thumbnail()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var productId = await CreateProductAsync(admin);

        var imagePath = await UploadAsync(admin, productId, 1000, 800);

        File.Exists(OnDisk(imagePath)).Should().BeTrue();
        File.Exists(OnDisk(ProductImagePaths.ThumbnailFor(imagePath)!)).Should().BeTrue(
            "every list in the app reads the thumbnail, so the endpoint must produce one");
    }

    /// <summary>
    /// The gap that shipped: <c>ImageStorageService</c> writes the files, and this proves the
    /// HOST actually serves them back over HTTP. It does not — there is no static file
    /// middleware for anything outside <c>wwwroot</c> — is exactly the bug that made every
    /// upload succeed while every &lt;img&gt; for it 404'd and silently fell back to a
    /// placeholder, no matter how many products were photographed.
    /// </summary>
    [Fact]
    public async Task An_uploaded_picture_and_its_thumbnail_are_reachable_by_url()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var productId = await CreateProductAsync(admin);

        var imagePath = await UploadAsync(admin, productId, 400, 400);
        var thumbnailPath = ProductImagePaths.ThumbnailFor(imagePath)!;

        // Anonymous on purpose: this is a static file, served by the same middleware the login
        // page's own bundled assets use, not a controller action — nothing to authenticate.
        var anonymous = _api.CreateClient();

        var image = await anonymous.GetAsync($"/{imagePath}");
        var thumbnail = await anonymous.GetAsync($"/{thumbnailPath}");

        image.StatusCode.Should().Be(HttpStatusCode.OK);
        image.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
        thumbnail.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Replacing_a_picture_leaves_no_trace_of_the_old_one()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var productId = await CreateProductAsync(admin);

        var firstImage = await UploadAsync(admin, productId, 600, 600);
        var firstThumbnail = ProductImagePaths.ThumbnailFor(firstImage)!;

        var secondImage = await UploadAsync(admin, productId, 900, 400);

        secondImage.Should().NotBe(firstImage);

        File.Exists(OnDisk(firstImage)).Should().BeFalse("the replaced image must be removed");
        File.Exists(OnDisk(firstThumbnail)).Should().BeFalse(
            "its thumbnail must go with it — nothing else will ever delete it");

        File.Exists(OnDisk(secondImage)).Should().BeTrue();
        File.Exists(OnDisk(ProductImagePaths.ThumbnailFor(secondImage)!)).Should().BeTrue();
    }

    [Fact]
    public async Task A_file_that_is_not_an_image_is_refused()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var productId = await CreateProductAsync(admin);

        var file = new ByteArrayContent("not a picture"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var upload = new MultipartFormDataContent { { file, "file", "payload.jpg" } };

        var response = await admin.PostAsync($"/api/products/{productId}/image", upload);

        // The declared content type says JPEG; the bytes say otherwise, and the bytes win.
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Staff_may_not_upload_a_picture()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var productId = await CreateProductAsync(admin);

        var staff = await ClientAsync(UserRole.Staff);
        using var upload = JpegUpload(300, 300);

        var response = await staff.PostAsync($"/api/products/{productId}/image", upload);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_product_with_no_picture_reports_none()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var productId = await CreateProductAsync(admin);

        var response = await admin.GetAsync($"/api/products/{productId}");
        var body = await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json);

        // Null, not empty string: "never had a picture" is what makes the screen show a
        // placeholder instead of a broken image.
        body!.Data!.GetProperty("imagePath").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
