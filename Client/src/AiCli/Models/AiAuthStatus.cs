using System.Text.Json.Serialization;

namespace AutoDev.AiCli.Models;

/// <summary>An AI provider account's login/subscription status, as reported by that provider's own CLI - the exact fields populated (e.g. OrgId/OrgName) vary by provider; anything a given provider has no concept of is left null.</summary>
public sealed record AiAuthStatus(
    [property: JsonPropertyName("loggedIn")] bool LoggedIn,
    [property: JsonPropertyName("authMethod")] string? AuthMethod,
    [property: JsonPropertyName("apiProvider")] string? ApiProvider,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("orgId")] string? OrgId,
    [property: JsonPropertyName("orgName")] string? OrgName,
    [property: JsonPropertyName("subscriptionType")] string? SubscriptionType)
{
    public static AiAuthStatus NotLoggedIn { get; } = new(false, null, null, null, null, null, null);
}
