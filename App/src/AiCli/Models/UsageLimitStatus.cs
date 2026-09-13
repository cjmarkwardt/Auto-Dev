namespace AutoDev.AiCli.Models;

/// <summary>One usage window (session or weekly) as reported by the CLI's own `/usage` command.</summary>
public sealed record UsagePeriodStatus(int PercentUsed, string ResetsAtDisplay, string ResetsAtFull, DateTimeOffset? ResetsAtUtc);

public sealed record UsageLimitStatus(UsagePeriodStatus? Session, UsagePeriodStatus? Week);
