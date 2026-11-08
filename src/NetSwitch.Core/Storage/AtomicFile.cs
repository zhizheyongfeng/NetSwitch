using System.Text;

namespace NetSwitch.Core.Storage;

public static class AtomicFile
{
    public static async Task WriteAllTextAsync(
        string path,
        string content,
        CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"Path has no directory: {path}", nameof(path));

        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(
                tempPath,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                ct).ConfigureAwait(false);

            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best effort cleanup; the original file is intact.
                }
            }
        }
    }

    public static async Task CopyAsync(
        string source,
        string destination,
        CancellationToken ct = default)
    {
        var fullDestination = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(fullDestination)
            ?? throw new ArgumentException($"Path has no directory: {destination}", nameof(destination));

        Directory.CreateDirectory(directory);

        await using var input = File.OpenRead(source);
        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullDestination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var output = File.Create(tempPath))
            {
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }

            File.Move(tempPath, fullDestination, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best effort cleanup.
                }
            }
        }
    }

    public static async Task CopyDirectoryAsync(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken ct = default)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));

        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("目标目录不能位于源目录内部。");
        }

        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            await CopyAsync(file, target, ct).ConfigureAwait(false);
        }
    }
}
