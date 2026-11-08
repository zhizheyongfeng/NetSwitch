using NetSwitch.Core.Abstractions;

namespace NetSwitch.Core.Storage;

/// <summary>
/// Minimal append-only transaction log. One file per day under the configurable log
/// directory. Logging must never break a network operation, so all I/O errors are swallowed.
/// </summary>
public sealed class FileAppLog : IAppLog
{
    private readonly IPathService _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileAppLog(IPathService paths)
    {
        _paths = paths;
    }

    public async Task WriteAsync(string message, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(
                _paths.Paths.LogDirectory,
                $"netswitch-{DateTime.Now:yyyy-MM-dd}.log");
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
            Directory.CreateDirectory(_paths.Paths.LogDirectory);
            await File.AppendAllTextAsync(path, line, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Logging is best effort.
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class NullAppLog : IAppLog
{
    public static NullAppLog Instance { get; } = new();

    public Task WriteAsync(string message, CancellationToken ct = default) => Task.CompletedTask;
}
