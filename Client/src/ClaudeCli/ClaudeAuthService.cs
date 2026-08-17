using System.Text.Json;
using AutoDev.AiCli;
using AutoDev.AiCli.Models;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;

namespace AutoDev.ClaudeCli;

public sealed class ClaudeAuthService(ILogger<ClaudeAuthService> logger) : IAiAuthService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public AiProvider Provider => AiProvider.Claude;

    public async Task<AiAuthStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Cli.Wrap(ClaudeCliLocator.ExecutableName)
                .WithArguments(["auth", "status"])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return AiAuthStatus.NotLoggedIn;
            }

            return JsonSerializer.Deserialize<AiAuthStatus>(result.StandardOutput, JsonOptions)
                   ?? AiAuthStatus.NotLoggedIn;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read claude auth status");
            return AiAuthStatus.NotLoggedIn;
        }
    }

    public Task LoginAsync(CancellationToken cancellationToken = default) =>
        Cli.Wrap(ClaudeCliLocator.ExecutableName)
            .WithArguments(["auth", "login"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(cancellationToken);
}
