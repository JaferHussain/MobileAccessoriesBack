using System.Diagnostics;
using System.Globalization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Time;
using MoizPos.Domain.Errors;
using MySqlConnector;

namespace MoizPos.Infrastructure.Backup;

public sealed class BackupOptions
{
    /// <summary>Where dumps are written. Should sit on a different device from the database.</summary>
    public string Directory { get; init; } = "backups";

    public int RetainDays { get; init; } = 30;

    /// <summary>Local hour at which the daily backup runs (FR-045).</summary>
    public int RunAtLocalHour { get; init; } = 2;

    /// <summary>
    /// The product image directory to archive beside each dump (FR-017). Empty means the shop
    /// stores no pictures and only the database is backed up.
    /// </summary>
    public string ProductImageDirectory { get; init; } = string.Empty;

    /// <summary>
    /// Directory holding mysqldump/mysql. Empty means "already on PATH", which is the normal
    /// case on a server; a developer machine often has MySQL installed but not on PATH.
    /// </summary>
    public string ToolsDirectory { get; init; } = string.Empty;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// <inheritdoc />
public sealed class BackupService : IBackupService
{
    private const string FilePrefix = "moizpos-";
    private const string FileExtension = ".sql";

    /// <summary>Pictures ride beside the dump, sharing its timestamp so the pair is obvious.</summary>
    private const string ImageArchiveExtension = "-images.zip";

    private readonly BackupOptions _options;
    private readonly string _connectionString;
    private readonly IClock _clock;
    private readonly PeriodResolver _periods;

    public BackupService(
        BackupOptions options,
        string connectionString,
        IClock clock,
        PeriodResolver periods)
    {
        _options = options;
        _connectionString = connectionString;
        _clock = clock;
        _periods = periods;
    }

    public async Task<BackupResult> CreateAsync(CancellationToken cancellationToken = default)
    {
        var builder = new MySqlConnectionStringBuilder(_connectionString);

        System.IO.Directory.CreateDirectory(_options.Directory);

        // Named in shop-local time so "which day is this?" is obvious to the owner.
        var localNow = _periods.ToShopLocal(_clock.UtcNow);
        var fileName = $"{FilePrefix}{localNow:yyyy-MM-dd-HHmmss}{FileExtension}";
        var fullPath = Path.Combine(_options.Directory, fileName);

        var arguments = new List<string>
        {
            $"--host={builder.Server}",
            $"--port={builder.Port.ToString(CultureInfo.InvariantCulture)}",
            $"--user={builder.UserID}",
            "--single-transaction",   // a consistent snapshot without locking the shop out
            "--routines",
            "--events",
            "--set-gtid-purged=OFF",
            $"--result-file={fullPath}",
            builder.Database,
        };

        await RunToolAsync("mysqldump", arguments, builder.Password, cancellationToken);

        var info = new FileInfo(fullPath);

        if (!info.Exists || info.Length == 0)
        {
            throw new BusinessRuleViolationException(
                "The backup produced no output. Check that mysqldump can reach the database.");
        }

        // The pictures, beside the dump (FR-017). The database holds only their paths, so a
        // dump on its own restores a catalogue whose every photograph is gone.
        //
        // Deliberately after the dump has been verified, and deliberately not fatal: a backup
        // of the shop's money that succeeded must not be discarded because a picture file was
        // locked. The ledger is what cannot be reconstructed; a photograph can be retaken.
        if (!string.IsNullOrWhiteSpace(_options.ProductImageDirectory))
        {
            try
            {
                ProductImageArchive.Create(
                    _options.ProductImageDirectory,
                    Path.Combine(
                        _options.Directory,
                        $"{FilePrefix}{localNow:yyyy-MM-dd-HHmmss}{ImageArchiveExtension}"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Swallowed on purpose, per the reasoning above.
            }
        }

        return new BackupResult(fileName, info.Length, _clock.UtcNow);
    }

    public Task<IReadOnlyList<BackupFile>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!System.IO.Directory.Exists(_options.Directory))
        {
            return Task.FromResult<IReadOnlyList<BackupFile>>([]);
        }

        var files = new System.IO.DirectoryInfo(_options.Directory)
            .GetFiles($"{FilePrefix}*{FileExtension}")
            .OrderByDescending(file => file.CreationTimeUtc)
            .Select(file => new BackupFile
            {
                FileName = file.Name,
                SizeBytes = file.Length,
                CreatedAtUtc = file.CreationTimeUtc,
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<BackupFile>>(files);
    }

    public async Task RestoreAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveBackupPath(fileName);

        if (!File.Exists(fullPath))
        {
            throw new NotFoundException("Backup", fileName);
        }

        // Validate before touching the live database. Restoring a truncated dump would leave the
        // shop with neither its old data nor a working copy of the new (spec edge case).
        await AssertLooksLikeAMySqlDumpAsync(fullPath, cancellationToken);

        var builder = new MySqlConnectionStringBuilder(_connectionString);

        var arguments = new List<string>
        {
            $"--host={builder.Server}",
            $"--port={builder.Port.ToString(CultureInfo.InvariantCulture)}",
            $"--user={builder.UserID}",
            builder.Database,
        };

        await RunToolAsync("mysql", arguments, builder.Password, cancellationToken, inputFile: fullPath);
    }

    public Task<int> PruneAsync(CancellationToken cancellationToken = default)
    {
        if (!System.IO.Directory.Exists(_options.Directory))
        {
            return Task.FromResult(0);
        }

        var cutoff = _clock.UtcNow.AddDays(-_options.RetainDays);
        var removed = 0;

        // Both the dumps and the picture archives beside them: retiring only the dumps would
        // leave the zips growing without limit, which is how a backup disk quietly fills up.
        var expired = new System.IO.DirectoryInfo(_options.Directory)
            .GetFiles($"{FilePrefix}*{FileExtension}")
            .Concat(new System.IO.DirectoryInfo(_options.Directory)
                .GetFiles($"{FilePrefix}*{ImageArchiveExtension}"));

        foreach (var file in expired)
        {
            if (file.CreationTimeUtc >= cutoff)
            {
                continue;
            }

            try
            {
                file.Delete();
                removed++;
            }
            catch (IOException)
            {
                // A locked file is not worth failing the whole prune over.
            }
        }

        return Task.FromResult(removed);
    }

    /// <summary>
    /// Refuses a path that escapes the backup directory, whatever the caller passed.
    /// </summary>
    private string ResolveBackupPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(fileName)
            || fileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new BusinessRuleViolationException("That is not a valid backup file name.");
        }

        var root = Path.GetFullPath(_options.Directory);
        var candidate = Path.GetFullPath(Path.Combine(root, fileName));

        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessRuleViolationException("That is not a valid backup file name.");
        }

        return candidate;
    }

    /// <summary>
    /// A cheap sanity check: a real mysqldump starts with its own header comment. This catches a
    /// truncated download or the wrong file entirely before any data is overwritten.
    /// </summary>
    private static async Task AssertLooksLikeAMySqlDumpAsync(
        string fullPath, CancellationToken cancellationToken)
    {
        var info = new FileInfo(fullPath);

        if (info.Length == 0)
        {
            throw new BusinessRuleViolationException(
                "That backup file is empty. Nothing was restored.");
        }

        using var reader = new StreamReader(fullPath);

        var header = await reader.ReadLineAsync(cancellationToken);

        // A real dump opens with its own SQL comment, e.g.
        //   -- MySQL dump 10.13  Distrib 8.0.40, for Win64 (x86_64)
        // Requiring the comment marker as well as the phrase stops arbitrary text that merely
        // mentions "mysql dump" from being fed to the client.
        var looksLikeADump =
            header is not null
            && header.StartsWith("--", StringComparison.Ordinal)
            && header.Contains("MySQL dump", StringComparison.OrdinalIgnoreCase);

        if (!looksLikeADump)
        {
            throw new BusinessRuleViolationException(
                "That file does not look like a MySQL backup. Nothing was restored.");
        }
    }

    private async Task RunToolAsync(
        string tool,
        IReadOnlyList<string> arguments,
        string password,
        CancellationToken cancellationToken,
        string? inputFile = null)
    {
        var executable = string.IsNullOrWhiteSpace(_options.ToolsDirectory)
            ? tool
            : Path.Combine(_options.ToolsDirectory, tool);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardError = true,
            RedirectStandardInput = inputFile is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The password goes via the environment, never the command line: process arguments are
        // readable by any other user on the machine.
        startInfo.Environment["MYSQL_PWD"] = password ?? string.Empty;

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new BusinessRuleViolationException(
                $"Could not run '{tool}'. Install the MySQL client tools, or set " +
                $"Backup:ToolsDirectory to the folder containing them. ({ex.Message})");
        }

        if (inputFile is not null)
        {
            await using var input = File.OpenRead(inputFile);
            await input.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
            process.StandardInput.Close();
        }

        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);

            throw new BusinessRuleViolationException(
                $"'{tool}' did not finish within {_options.Timeout.TotalMinutes:0} minutes.");
        }

        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new BusinessRuleViolationException(
                $"'{tool}' failed (exit code {process.ExitCode}). {error.Trim()}");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone.
        }
    }
}
