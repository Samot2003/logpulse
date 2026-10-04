namespace LogPulse.Core.Models;

/// <summary>A dashboard user. <see cref="PasswordHash"/> is produced by ASP.NET Core's PasswordHasher.</summary>
public sealed record User
{
    public int Id { get; init; }
    public required string UserName { get; init; }
    public required string PasswordHash { get; init; }
    public required string Role { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
