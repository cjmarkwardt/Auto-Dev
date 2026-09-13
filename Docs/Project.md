# Project

Documentation for this repository's own tooling and workflow.

## Scripts

`Scripts/` holds file-based C# apps (`dotnet run Scripts/<Name>.cs`) for automated repository
actions.

- `Scripts/Run.cs` - runs the app locally (`dotnet run --project App/App.csproj`) without needing
  to remember the project path.
- `Scripts/Test.cs` - runs the test suite with coverage collection and prints a summary.
- `Scripts/Verify.cs` - applies formatting fixes and regenerates the coverage badge.
- `Scripts/Publish.cs` - cuts a release (see Publishing below). A manual, human-only action.

## Publishing

Run `Scripts/Publish.cs` locally to cut a release:

1. It prompts for the version to publish (e.g. `1.2.3`) - `App/App.csproj` carries no `<Version>` of
   its own, so this is what actually gets built and published.
2. It creates the GitHub Release (and its underlying tag) for that version locally via `gh release
   create` - done locally because a repo ruleset blocks the default `GITHUB_TOKEN` from creating tags.
3. It dispatches `build.yml`'s `workflow_dispatch` trigger with the version as input. The workflow
   verifies the dispatcher has Admin permission on the repo, refuses to run from anything but `main`,
   publishes self-contained single-file executables for Windows and Linux (x64), and uploads them as
   release assets.
4. It waits for that run to finish, rolling the release/tag back if the workflow fails, so a failed
   publish never leaves one behind. If it can't confirm the run happened at all, it leaves the release
   in place instead, rather than risk deleting one that's still running.

## Workflows

- `.github/workflows/build.yml` - builds, verifies formatting (`dotnet format --verify-no-changes`,
  never applies fixes), and runs tests on every push/PR to `main`; its `publish` job (see Publishing
  above) only runs on `workflow_dispatch`, gated on the dispatcher having Admin permission on the repo
  and the run being on `main`.
- `.github/workflows/codeql.yml` - CodeQL security analysis on push/PR to `main` and a weekly schedule.
