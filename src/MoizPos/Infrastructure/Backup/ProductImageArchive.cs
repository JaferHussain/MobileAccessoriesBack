using System.IO.Compression;

namespace MoizPos.Infrastructure.Backup;

/// <summary>
/// Copies the shop's product pictures into the nightly backup (FR-017).
///
/// <para>The database stores only the PATH to each picture, never the bytes — deliberately, so
/// the dump stays small enough to take every night. The consequence is that a database-only
/// backup restores a catalogue in which every photograph is gone for good. This closes that
/// gap: the pictures travel beside the dump, in a zip named after it.</para>
///
/// <para>Kept out of <see cref="BackupService"/> itself so it can be tested without a running
/// MySQL and without shelling out to <c>mysqldump</c>.</para>
/// </summary>
public static class ProductImageArchive
{
    /// <summary>
    /// Archives every file under <paramref name="imageDirectory"/> into
    /// <paramref name="destinationPath"/>.
    /// </summary>
    /// <returns>How many files were archived. Zero means no archive was written.</returns>
    public static int Create(string imageDirectory, string destinationPath)
    {
        if (!Directory.Exists(imageDirectory))
        {
            // Nothing has been uploaded yet. The database backup still matters, so this is a
            // normal state, not a failure.
            return 0;
        }

        var files = Directory.GetFiles(imageDirectory, "*", SearchOption.AllDirectories);

        if (files.Length == 0)
        {
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        using var archive = ZipFile.Open(destinationPath, ZipArchiveMode.Create);

        foreach (var file in files)
        {
            // Stored relative, so a restore can unpack straight over the image directory
            // whatever it is called on the new machine.
            var entryName = Path.GetRelativePath(imageDirectory, file).Replace('\\', '/');

            archive.CreateEntryFromFile(file, entryName, CompressionLevel.Fastest);
        }

        return files.Length;
    }
}
