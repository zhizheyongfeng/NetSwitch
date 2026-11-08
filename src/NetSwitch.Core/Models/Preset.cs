namespace NetSwitch.Core.Models;

public sealed record Preset(
    string Id,
    string Name,
    string? Notes,
    DesiredState Desired,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public Preset Touch(DateTimeOffset now) => this with { UpdatedAt = now };
}
