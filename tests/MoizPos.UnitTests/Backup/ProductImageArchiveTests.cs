using System.IO.Compression;
using FluentAssertions;
using MoizPos.Infrastructure.Backup;

namespace MoizPos.UnitTests.Backup;

/// <summary>
/// Feature 005, FR-017 — product pictures must survive a restore.
///
/// <para>The nightly backup dumps the database, and the database holds only the PATH to each
/// picture. Restore that alone onto a new machine and every product comes back with a
/// placeholder where its photograph was, permanently — the files were never copied anywhere.
/// This archives them alongside the dump.</para>
/// </summary>
public sealed class ProductImageArchiveTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"moizpos-backup-{Guid.NewGuid():N}");

    private string Images => Path.Combine(_root, "images");

    private string Destination => Path.Combine(_root, "backup", "images.zip");

    public ProductImageArchiveTests()
    {
        Directory.CreateDirectory(Images);
        Directory.CreateDirectory(Path.Combine(_root, "backup"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteImage(string relativeName, string content = "pretend-jpeg")
    {
        var path = Path.Combine(Images, relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Every_picture_and_thumbnail_is_archived()
    {
        WriteImage("a.jpg");
        WriteImage("a_thumb.jpg");
        WriteImage("b.png");

        var archived = ProductImageArchive.Create(Images, Destination);

        archived.Should().Be(3);

        using var zip = ZipFile.OpenRead(Destination);

        zip.Entries.Select(e => e.FullName)
            .Should().BeEquivalentTo("a.jpg", "a_thumb.jpg", "b.png");
    }

    [Fact]
    public void The_archive_can_be_read_back()
    {
        WriteImage("a.jpg", "the-original-bytes");

        ProductImageArchive.Create(Images, Destination);

        using var zip = ZipFile.OpenRead(Destination);
        using var reader = new StreamReader(zip.GetEntry("a.jpg")!.Open());

        // A backup nobody can restore from is not a backup.
        reader.ReadToEnd().Should().Be("the-original-bytes");
    }

    [Fact]
    public void A_shop_with_no_pictures_yet_produces_no_archive()
    {
        var archived = ProductImageArchive.Create(Images, Destination);

        archived.Should().Be(0);
        File.Exists(Destination).Should().BeFalse(
            "an empty zip beside every dump would only invite someone to wonder what is wrong");
    }

    [Fact]
    public void A_missing_image_directory_is_not_a_failure()
    {
        // The directory does not exist until the first picture is uploaded. A shop that has not
        // uploaded one must still get its nightly database backup.
        var act = () => ProductImageArchive.Create(Path.Combine(_root, "never-created"), Destination);

        act.Should().NotThrow();
    }

    [Fact]
    public void Re_running_replaces_the_previous_archive()
    {
        WriteImage("a.jpg");
        ProductImageArchive.Create(Images, Destination);

        WriteImage("b.jpg");
        var archived = ProductImageArchive.Create(Images, Destination);

        archived.Should().Be(2);

        using var zip = ZipFile.OpenRead(Destination);
        zip.Entries.Should().HaveCount(2);
    }
}
