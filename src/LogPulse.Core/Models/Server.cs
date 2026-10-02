namespace LogPulse.Core.Models;

/// <summary>A monitored machine that runs the LogPulse agent.</summary>
public sealed record Server(int Id, string Name, DateTimeOffset RegisteredAt, DateTimeOffset LastSeenAt);
