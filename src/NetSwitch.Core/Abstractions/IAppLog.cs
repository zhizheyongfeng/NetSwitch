namespace NetSwitch.Core.Abstractions;

public interface IAppLog
{
    Task WriteAsync(string message, CancellationToken ct = default);
}
