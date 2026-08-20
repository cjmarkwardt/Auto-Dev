using System.Text.Json;
using AutoDev.AiCli;
using AutoDev.AiCli.Models;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;

namespace AutoDev.CodexCli;

/// <summary>
/// Codex's CLI has no scriptable equivalent of `claude auth status`'s structured JSON - `codex login
/// status` only prints a plain sentence ("Logged in using ChatGPT"), and there's no non-interactive way to
/// ask it for the signed-in account's email/plan. Both are embedded as claims in the id_token JWT the CLI
/// already persists to ~/.codex/auth.json once signed in, so this reads that file directly and decodes the
/// token's payload (never verifying its signature - display-only, the same trust boundary as reading any
/// other already-authenticated CLI's own local state).
/// </summary>
public sealed class CodexAuthService(ILogger<CodexAuthService> logger) : IAiAuthService
{
    public AiProvider Provider => AiProvider.Codex;

    public bool IsInstalled => CodexCliLocator.IsInstalled;

    public async Task<AiAuthStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var authFilePath = Path.Combine(CodexHomeDirectory(), "auth.json");
            if (!File.Exists(authFilePath))
            {
                return AiAuthStatus.NotLoggedIn;
            }

            await using var stream = File.OpenRead(authFilePath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            if (!root.TryGetProperty("tokens", out var tokens) || !tokens.TryGetProperty("id_token", out var idTokenEl))
            {
                return AiAuthStatus.NotLoggedIn;
            }

            var idToken = idTokenEl.GetString();
            if (string.IsNullOrEmpty(idToken) || DecodeJwtPayload(idToken) is not { } payload)
            {
                return AiAuthStatus.NotLoggedIn;
            }

            var email = payload.TryGetProperty("email", out var emailEl) ? emailEl.GetString() : null;
            string? planType = null;
            if (payload.TryGetProperty("https://api.openai.com/auth", out var authClaims) &&
                authClaims.TryGetProperty("chatgpt_plan_type", out var planEl))
            {
                planType = planEl.GetString();
            }

            return new AiAuthStatus(true, "chatgpt", "openai", email, null, null, FormatPlan(planType));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read codex auth status");
            return AiAuthStatus.NotLoggedIn;
        }
    }

    public Task LoginAsync(CancellationToken cancellationToken = default) =>
        Cli.Wrap(CodexCliLocator.ExecutableName)
            .WithArguments(["login"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(cancellationToken);

    private static string CodexHomeDirectory() =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } codexHome
            ? codexHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    private static JsonElement? DecodeJwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        var payloadBytes = Base64UrlDecode(parts[1]);
        using var document = JsonDocument.Parse(payloadBytes);
        return document.RootElement.Clone();
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        var padding = padded.Length % 4;
        if (padding > 0)
        {
            padded += new string('=', 4 - padding);
        }

        return Convert.FromBase64String(padded);
    }

    private static string? FormatPlan(string? planType) => planType switch
    {
        null or "" => null,
        "free" => "Free",
        "plus" => "Plus",
        "pro" => "Pro",
        "team" => "Team",
        "enterprise" => "Enterprise",
        var other => char.ToUpperInvariant(other[0]) + other[1..],
    };
}
