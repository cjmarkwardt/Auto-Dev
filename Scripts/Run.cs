#:package Markwardt.ScriptUtilities@0.2.0
#:property TreatWarningsAsErrors=true

// Runs the app locally via `dotnet run`. File-based app (dotnet run) - run from the repo root,
// e.g. `dotnet run Scripts/Run.cs`.

using Markwardt.ScriptUtilities;

(await Script.Run("dotnet", "run", "--project", "App/App.csproj")).Verify();
