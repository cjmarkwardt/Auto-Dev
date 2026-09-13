#:package Markwardt.ScriptUtilities@0.2.0
#:property TreatWarningsAsErrors=true

// Runs the test suite with coverage and prints a summary, mirroring what CI's Test step runs. File-based
// app (dotnet run) - run from the repo root, e.g. `dotnet run Scripts/Test.cs`.

using Markwardt.ScriptUtilities;

const string TestResultsFolder = "TestResults";

(await Script.Run(false, "dotnet", "tool", "restore")).Verify();
Script.Delete(TestResultsFolder);

(await Script.Run("dotnet", "test", "Tests/Tests.csproj", "--settings", "Tests/coverage.runsettings", "--collect:XPlat Code Coverage", "--results-directory", TestResultsFolder)).Verify();

(await Script.Run(false, "dotnet", "reportgenerator", $"-reports:{TestResultsFolder}/**/coverage.cobertura.xml", $"-targetdir:{TestResultsFolder}", "-reporttypes:TextSummary")).Verify();

Script.Log(await Script.Read(Path.Combine(TestResultsFolder, "Summary.txt")) ?? throw new InvalidOperationException("Coverage summary was not generated."));
Script.Delete(TestResultsFolder);
