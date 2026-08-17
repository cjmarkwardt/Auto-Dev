using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoDev.AiCli;
using AutoDev.AiCli.Models;
using AutoDev.ClaudeCli.Serialization;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;

namespace AutoDev.ClaudeCli;

/// <summary>
/// Reads session/weekly rate-limit usage via the CLI's own `/usage` slash command, sent as a plain
/// user message over the same stream-json stdin protocol used elsewhere. Verified empirically: this
/// is handled locally by the CLI (no model call - `total_cost_usd` is 0, `num_turns` is 0), so polling
/// it is free. The response is a synthetic assistant message with a human-readable text report; there
/// is no structured JSON for it, so this parses the two lines we care about out of that text.
/// </summary>
public sealed partial class ClaudeUsageService(ILogger<ClaudeUsageService> logger) : IAiUsageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new ClaudeStreamEventJsonConverter() },
    };

    public AiProvider Provider => AiProvider.Claude;

    [GeneratedRegex(@"^(?<pct>\d+)% used · resets (?<resets>.+?)(?:\s*\((?<tz>[^)]+)\))?\s*$")]
    private static partial Regex UsageLineRegex();

    public async Task<UsageLimitStatus?> GetUsageStatusAsync(CancellationToken cancellationToken = default)
    {
        BufferedCommandResult result;
        try
        {
            result = await Cli.Wrap(ClaudeCliLocator.ExecutableName)
                .WithArguments(["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--model", "sonnet"])
                .WithStandardInputPipe(PipeSource.FromString(ClaudeInputMessageWriter.UserMessage("/usage") + "\n"))
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to query claude /usage");
            return null;
        }

        string? reportText = null;
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            AiStreamEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<AiStreamEvent>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (evt is ResultEvent { Result.Length: > 0 } resultEvent)
            {
                reportText = resultEvent.Result;
            }
        }

        return reportText is null ? null : Parse(reportText);
    }

    private static UsageLimitStatus Parse(string reportText) => new(
        ExtractPeriod(reportText, "Current session: "),
        ExtractPeriod(reportText, "Current week (all models): "));

    private static UsagePeriodStatus? ExtractPeriod(string reportText, string linePrefix)
    {
        foreach (var line in reportText.Split('\n'))
        {
            if (!line.StartsWith(linePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var match = UsageLineRegex().Match(line[linePrefix.Length..].Trim());
            if (!match.Success)
            {
                return null;
            }

            var percent = int.Parse(match.Groups["pct"].Value);
            var resets = match.Groups["resets"].Value.Trim();
            var timezone = match.Groups["tz"] is { Success: true } tz ? tz.Value : null;
            var full = timezone is null ? $"Resets {resets}" : $"Resets {resets} ({timezone})";
            return new UsagePeriodStatus(percent, resets, full, TryParseResetsAtUtc(resets, timezone));
        }

        return null;
    }

    /// <summary>
    /// Best-effort: the CLI only gives us free text like "Jul 19, 8:50am" or "Jul 25, 2am" (no minutes)
    /// plus an optional IANA zone name like "America/Chicago". Generic DateTime.TryParse mis-parses both
    /// of those shapes (confirmed empirically - e.g. it silently reads "25, 2" as day/two-digit-year and
    /// drops the time entirely), so this uses explicit format strings instead. Missing year defaults to
    /// the current year, same as TryParseExact's normal behavior. Returns null (no countdown shown)
    /// rather than guessing if either step fails.
    /// </summary>
    private static readonly string[] ResetsAtFormats = ["MMM d, h:mmtt", "MMM d, htt"];

    private static DateTimeOffset? TryParseResetsAtUtc(string resets, string? timezone)
    {
        if (!DateTime.TryParseExact(resets, ResetsAtFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return null;
        }

        if (timezone is null)
        {
            return null;
        }

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), zone);
            return new DateTimeOffset(utc, TimeSpan.Zero);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }
}
