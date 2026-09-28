namespace MoizPos.Application.Calculations;

/// <summary>
/// Where a product's thumbnail lives, given where its full-size picture lives.
///
/// <para>The thumbnail has no column of its own. Its path is DERIVED from
/// <c>products.image_path</c> by inserting <c>_thumb</c> before the extension, so the two can
/// never drift apart — there is no second value to forget to update. Every reader, on either
/// side of the wire, computes it the same way from the one stored fact.</para>
///
/// <para>Pure and dependency-free on purpose: the frontend applies the identical rule, and a
/// rule that lives in one testable place is a rule two codebases can actually share.</para>
/// </summary>
public static class ProductImagePaths
{
    public const string ThumbnailSuffix = "_thumb";

    /// <summary>
    /// The thumbnail path for a stored image path, or <c>null</c> when there is no picture.
    /// </summary>
    /// <remarks>
    /// Null in, null out. "Never had a picture" and "has one" are different facts, and only
    /// the first should render a placeholder.
    /// </remarks>
    public static string? ThumbnailFor(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        var trimmed = imagePath.Trim();
        var extension = Path.GetExtension(trimmed);

        // Path.GetExtension takes the LAST dot, so "a.b.c.webp" keeps "a.b.c" — a filename
        // with dots in it must not lose everything after the first one.
        var withoutExtension = trimmed[..^extension.Length];

        return $"{withoutExtension}{ThumbnailSuffix}{extension}";
    }
}
