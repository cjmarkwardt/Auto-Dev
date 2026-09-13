#:package Markwardt.ScriptUtilities@0.2.0
#:property TreatWarningsAsErrors=true

// Verifies that everything is checked for formatting and consistency: applies formatting fixes and
// regenerates the coverage badge, leaving any changes staged in the working tree for review. Run this
// before every commit, and again before running Publish. File-based app (dotnet run) - run from the
// repo root, e.g. `dotnet run Scripts/Verify.cs`.

using Markwardt.ScriptUtilities;

(await Script.Run("dotnet", "format")).Verify();
(await Script.Run(false, "dotnet", "tool", "restore")).Verify();
Script.Delete("Tests/TestResults");

(await Script.Run("dotnet", "test", "Tests/Tests.csproj", "--configuration", "Debug", "--settings", "Tests/coverage.runsettings", "--collect:XPlat Code Coverage")).Verify();

(await Script.Run(false, "dotnet", "reportgenerator", "-reports:Tests/TestResults/**/coverage.cobertura.xml", "-targetdir:.github/badges", "-reporttypes:Badges")).Verify();
Script.Delete("Tests/TestResults");

Script.Log("Verification complete - review any formatting/badge changes and commit and push them yourself if there are any.");
