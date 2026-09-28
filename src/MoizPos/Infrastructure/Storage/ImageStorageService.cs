using MoizPos.Application.Calculations;
using MoizPos.Domain.Errors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace MoizPos.Infrastructure.Storage;

public sealed class ImageStorageOptions
{
    /// <summary>Directory, relative to the content root, holding product images.</summary>
    public string ProductImageRoot { get; init; } = "content/products";

    /// <summary>2 MB. Thousands of catalogue images must not bloat the daily backup (R7).</summary>
    public long MaxImageBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>
    /// The box a thumbnail is fitted inside, in pixels (feature 005). Lists and the counter's
    /// search results load these and never the full-size photograph.
    /// </summary>
    public int ThumbnailPixels { get; init; } = 150;

    /// <summary>
    /// Directory, relative to the content root, holding payment-proof screenshots (feature 008).
    /// Separate from product pictures on purpose: a product photo is shown to anyone browsing the
    /// catalogue, a payment proof is internal evidence, and keeping them in one folder would make
    /// it impossible to ever serve one publicly without serving the other.
    /// </summary>
    public string PaymentProofRoot { get; init; } = "content/payment-proofs";
}

public interface IImageStorageService
{
    /// <returns>The relative path to store in products.image_path.</returns>
    Task<string> SaveProductImageAsync(
        Stream content,
        string contentType,
        long lengthBytes,
        CancellationToken cancellationToken = default);

    void DeleteProductImage(string relativePath);

    /// <returns>The relative path to store in invoices.payment_proof_path.</returns>
    Task<string> SavePaymentProofAsync(
        Stream content,
        string contentType,
        long lengthBytes,
        CancellationToken cancellationToken = default);

    void DeletePaymentProof(string relativePath);
}

/// <summary>
/// Stores product images on disk and keeps only the path in the database (research.md R7).
///
/// BLOBs would bloat every <c>SELECT</c> and, more importantly, the nightly mysqldump the shop
/// depends on for recovery.
/// </summary>
public sealed class ImageStorageService : IImageStorageService
{
    private static readonly Dictionary<string, string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
    };

    private readonly ImageStorageOptions _options;
    private readonly string _contentRoot;

    public ImageStorageService(ImageStorageOptions options, string contentRoot)
    {
        _options = options;
        _contentRoot = contentRoot;
    }

    public async Task<string> SaveProductImageAsync(
        Stream content,
        string contentType,
        long lengthBytes,
        CancellationToken cancellationToken = default)
    {
        if (lengthBytes <= 0)
        {
            throw new BusinessRuleViolationException("The uploaded image is empty.");
        }

        if (lengthBytes > _options.MaxImageBytes)
        {
            var limitMb = _options.MaxImageBytes / (1024d * 1024d);

            throw new BusinessRuleViolationException(
                $"Image is too large. The limit is {limitMb:0.#} MB.");
        }

        if (!AllowedTypes.TryGetValue(contentType, out var extension))
        {
            throw new BusinessRuleViolationException(
                "Only JPEG, PNG and WebP images are accepted.");
        }

        var directory = Path.Combine(_contentRoot, _options.ProductImageRoot);
        Directory.CreateDirectory(directory);

        // A generated name, never the client's: an uploaded filename could contain path
        // separators and escape the image directory.
        var fileName = $"{Guid.NewGuid():N}{extension}";
        var fullPath = Path.Combine(directory, fileName);

        // Decoded rather than copied straight through: the content type is the client's claim,
        // and a file that is not really an image would sit in the catalogue failing to render
        // on every screen that shows it. Decoding is also what makes the thumbnail possible,
        // so it costs nothing extra.
        Image image;

        try
        {
            image = await Image.LoadAsync(content, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new BusinessRuleViolationException(
                "That file is not a readable image. Please choose a JPEG, PNG or WebP photo.");
        }

        using (image)
        {
            await image.SaveAsync(fullPath, cancellationToken);

            // Already small enough? Copy it as-is. ResizeMode.Max still scales UP to fill the
            // box, which would invent detail nobody photographed and produce a "thumbnail"
            // larger than the original.
            var box = _options.ThumbnailPixels;
            var alreadySmallEnough = image.Width <= box && image.Height <= box;

            using var thumbnail = alreadySmallEnough
                ? image.Clone(_ => { })
                : image.Clone(context => context.Resize(new ResizeOptions
                {
                    Size = new Size(box, box),

                    // Preserves the aspect ratio — a stretched phone case is harder to
                    // recognise than no picture at all.
                    Mode = ResizeMode.Max,
                }));

            var thumbnailFileName = Path.GetFileName(ProductImagePaths.ThumbnailFor(fileName)!);
            await thumbnail.SaveAsync(Path.Combine(directory, thumbnailFileName), cancellationToken);
        }

        // Forward slashes: this path is served over HTTP, not read from disk by the client.
        return $"{_options.ProductImageRoot}/{fileName}".Replace('\\', '/');
    }

    public void DeleteProductImage(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        // Both files, always. A replaced picture that leaves its thumbnail behind is invisible
        // — nothing reads it and nothing ever deletes it, so the disk fills silently.
        DeleteIfInside(_options.ProductImageRoot, relativePath);
        DeleteIfInside(_options.ProductImageRoot, ProductImagePaths.ThumbnailFor(relativePath));
    }

    public async Task<string> SavePaymentProofAsync(
        Stream content,
        string contentType,
        long lengthBytes,
        CancellationToken cancellationToken = default)
    {
        // Same limits and the same decode check as a product picture — a screenshot that is
        // not really an image is just as useless as a product photo that is not. No thumbnail:
        // a proof is opened full-size when a payment is disputed, or not at all, and never
        // appears in a list (FR-009).
        var fileName = await SaveDecodedAsync(
            _options.PaymentProofRoot, content, contentType, lengthBytes, cancellationToken);

        return $"{_options.PaymentProofRoot}/{fileName}".Replace('\\', '/');
    }

    public void DeletePaymentProof(string relativePath) =>
        DeleteIfInside(_options.PaymentProofRoot, relativePath);

    /// <summary>
    /// Validates, decodes and writes one image into <paramref name="root"/>, returning its
    /// generated file name. Shared so a second kind of upload cannot quietly acquire weaker
    /// checks than the first.
    /// </summary>
    private async Task<string> SaveDecodedAsync(
        string root,
        Stream content,
        string contentType,
        long lengthBytes,
        CancellationToken cancellationToken)
    {
        if (lengthBytes <= 0)
        {
            throw new BusinessRuleViolationException("The uploaded image is empty.");
        }

        if (lengthBytes > _options.MaxImageBytes)
        {
            var limitMb = _options.MaxImageBytes / (1024d * 1024d);

            throw new BusinessRuleViolationException(
                $"Image is too large. The limit is {limitMb:0.#} MB.");
        }

        if (!AllowedTypes.TryGetValue(contentType, out var extension))
        {
            throw new BusinessRuleViolationException(
                "Only JPEG, PNG and WebP images are accepted.");
        }

        var directory = Path.Combine(_contentRoot, root);
        Directory.CreateDirectory(directory);

        // A generated name, never the client's: an uploaded filename could contain path
        // separators and escape the directory.
        var fileName = $"{Guid.NewGuid():N}{extension}";

        Image image;

        try
        {
            image = await Image.LoadAsync(content, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new BusinessRuleViolationException(
                "That file is not a readable image. Please choose a JPEG, PNG or WebP photo.");
        }

        using (image)
        {
            await image.SaveAsync(Path.Combine(directory, fileName), cancellationToken);
        }

        return fileName;
    }

    private void DeleteIfInside(string root, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_contentRoot, normalized));
        var allowedRoot = Path.GetFullPath(Path.Combine(_contentRoot, root));

        // Refuses to delete anything outside that directory, whatever the stored path says.
        if (!fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }
}
