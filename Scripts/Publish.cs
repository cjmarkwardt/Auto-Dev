#:package Markwardt.ScriptUtilities@0.2.0
#:property TreatWarningsAsErrors=true

// Creates the GitHub Release/tag for a version (locally, since a repo ruleset blocks GITHUB_TOKEN from
// creating tags), dispatches the publish workflow, waits for it to finish, and rolls the release/tag back
// if it fails - so a failed publish never leaves one behind. File-based app (dotnet run) instead of a task
// script since this needs real control flow (polling, conditional rollback) that stays identical on any OS
// with dotnet installed, unlike relying on the local shell (sh and cmd.exe don't share syntax for this).
// Run from the repo root, e.g. `dotnet run Scripts/Publish.cs`.

using Markwardt.ScriptUtilities;
using System.Text.Json;

string version = await Script.Query("Version to publish (e.g. 1.2.3)");
if (string.IsNullOrEmpty(version))
{
    Script.Error("A version is required.");
    return 1;
}

if ((await Script.Run("gh", "release", "create", version, "--title", version, "--generate-notes")).IsFailure)
{
    return 1;
}

// Captured before dispatching (with a buffer for clock skew against GitHub's server time), not after -
// GitHub's per-second-resolution createdAt for the new run can otherwise fall before a post-dispatch
// timestamp once network round-trip latency is accounted for, permanently failing the comparison below.
DateTimeOffset dispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-30);

if ((await Script.Run("gh", "workflow", "run", "build.yml", "-f", $"version={version}")).IsFailure)
{
    return await Rollback("Failed to dispatch the publish workflow");
}

Script.Log("Waiting for the dispatched run to appear on GitHub Actions...");
long? runId = null;

for (int attempt = 0; attempt < 40 && runId is null; attempt++)
{
    await Script.Wait(3);

    if (attempt > 0 && attempt % 5 == 0)
    {
        Script.Log($"Still waiting for the run to appear... ({attempt * 3}s elapsed)");
    }

    RunResult list = await Script.Run(false, "gh", "run", "list", "--workflow=build.yml", "--event=workflow_dispatch", "--limit=5", "--json", "databaseId,createdAt");
    if (list.IsFailure)
    {
        continue;
    }

    foreach (JsonElement run in JsonDocument.Parse(list.Output).RootElement.EnumerateArray())
    {
        if (run.GetProperty("createdAt").GetDateTimeOffset() >= dispatchedAt)
        {
            runId = run.GetProperty("databaseId").GetInt64();
            break;
        }
    }
}

if (runId is null)
{
    // The workflow was already confirmed dispatched above, so it may still be running - rolling back here
    // would delete the release out from under a real in-progress run, breaking its later publish steps.
    Script.Error($"Dispatched the publish workflow for {version} but could not confirm its run - check GitHub Actions manually. The release was left in place.");
    return 1;
}

if ((await Script.Run("gh", "run", "watch", runId.Value.ToString(), "--exit-status", "--interval", "15")).IsFailure)
{
    return await Rollback("Publish workflow failed");
}

Script.Log($"Published {version}.");
return 0;

async Task<int> Rollback(string reason)
{
    Script.Error($"{reason} - rolling back release {version}.");
    await Script.Run("gh", "release", "delete", version, "--yes", "--cleanup-tag");
    return 1;
}
