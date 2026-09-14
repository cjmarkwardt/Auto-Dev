using Microsoft.Extensions.Logging.Abstractions;

namespace AutoDev.Tests.Core.Services;

/// <summary>
/// Covers WorkspaceScriptRunnerService's own job: running a real .cs file via `dotnet run --file` against a
/// real temp-directory workspace, tracking a run's live output while it's in flight, persisting a
/// ScriptRunRecord once it finishes (success, a non-zero exit, a compile error, or a user Stop), and raising
/// the two runner events in order. Only IWorkspaceMetadataStore is mocked, since persistence is this class's
/// own responsibility - everything else is a real subprocess, so these tests are slower than average
/// (a few hundred ms each for the actual `dotnet run` round trip).
/// </summary>
public sealed class WorkspaceScriptRunnerServiceTests : IDisposable
{
    private readonly string workspacePath = Directory.CreateTempSubdirectory("autodev-scriptrunner-tests-").FullName;

    public void Dispose() => Directory.Delete(workspacePath, recursive: true);

    private WorkspaceScriptRunnerService CreateRunner(Mock<IWorkspaceMetadataStore> metadataStore)
    {
        WorkspaceScriptRunnerService runner = new(workspacePath, metadataStore.Object, NullLogger<WorkspaceScriptRunnerService>.Instance);
        runner.Start();
        return runner;
    }

    private string WriteScriptFile(string relativePath, string content)
    {
        string fullPath = Path.Combine(workspacePath, relativePath);
        File.WriteAllText(fullPath, content);
        return relativePath;
    }

    [Fact]
    public async Task RunNowAsync_SuccessfulScript_PersistsCompletedRecordAndRaisesEventsInOrder()
    {
        Mock<IWorkspaceMetadataStore> metadataStore = new();
        ScriptRunRecord? persisted = null;
        metadataStore
            .Setup(store => store.AppendScriptRunAsync(workspacePath, It.IsAny<ScriptRunRecord>(), It.IsAny<CancellationToken>()))
            .Callback<string, ScriptRunRecord, CancellationToken>((_, record, _) => persisted = record)
            .Returns(Task.CompletedTask);

        using WorkspaceScriptRunnerService runner = CreateRunner(metadataStore);
        string path = WriteScriptFile("greet.cs", "Console.WriteLine(\"hello\");");

        List<string> order = [];
        runner.ScriptRunStarted += _ => order.Add("started");
        runner.ScriptRunCompleted += _ => order.Add("completed");

        await runner.RunNowAsync(new ScriptRef(path, "greet"));

        Assert.Equal(["started", "completed"], order);
        Assert.NotNull(persisted);
        Assert.True(persisted!.Success);
        Assert.False(persisted.WasStopped);
        Assert.Equal(0, persisted.ExitCode);
        Assert.Contains("hello", persisted.Output);
        Assert.False(runner.IsRunning(path));
        Assert.Null(runner.GetLiveRun(path));
    }

    [Fact]
    public async Task RunNowAsync_NonZeroExitCode_PersistsUnsuccessfulRecord()
    {
        Mock<IWorkspaceMetadataStore> metadataStore = new();
        ScriptRunRecord? persisted = null;
        metadataStore
            .Setup(store => store.AppendScriptRunAsync(workspacePath, It.IsAny<ScriptRunRecord>(), It.IsAny<CancellationToken>()))
            .Callback<string, ScriptRunRecord, CancellationToken>((_, record, _) => persisted = record)
            .Returns(Task.CompletedTask);

        using WorkspaceScriptRunnerService runner = CreateRunner(metadataStore);
        string path = WriteScriptFile("fail.cs", "Environment.Exit(3);");

        await runner.RunNowAsync(new ScriptRef(path, "fail"));

        Assert.NotNull(persisted);
        Assert.False(persisted!.Success);
        Assert.False(persisted.WasStopped);
        Assert.Equal(3, persisted.ExitCode);
    }

    [Fact]
    public async Task RunNowAsync_CompileError_PersistsUnsuccessfulRecordWithBuildOutput()
    {
        Mock<IWorkspaceMetadataStore> metadataStore = new();
        ScriptRunRecord? persisted = null;
        metadataStore
            .Setup(store => store.AppendScriptRunAsync(workspacePath, It.IsAny<ScriptRunRecord>(), It.IsAny<CancellationToken>()))
            .Callback<string, ScriptRunRecord, CancellationToken>((_, record, _) => persisted = record)
            .Returns(Task.CompletedTask);

        using WorkspaceScriptRunnerService runner = CreateRunner(metadataStore);
        string path = WriteScriptFile("bad.cs", "this is not valid csharp");

        await runner.RunNowAsync(new ScriptRef(path, "bad"));

        Assert.NotNull(persisted);
        Assert.False(persisted!.Success);
        Assert.NotEqual(0, persisted.ExitCode);
        Assert.Contains("error", persisted.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StopRun_CancelsInFlightRun_PersistsStoppedRecord()
    {
        Mock<IWorkspaceMetadataStore> metadataStore = new();
        ScriptRunRecord? persisted = null;
        TaskCompletionSource runStartedSignal = new();
        metadataStore
            .Setup(store => store.AppendScriptRunAsync(workspacePath, It.IsAny<ScriptRunRecord>(), It.IsAny<CancellationToken>()))
            .Callback<string, ScriptRunRecord, CancellationToken>((_, record, _) => persisted = record)
            .Returns(Task.CompletedTask);

        using WorkspaceScriptRunnerService runner = CreateRunner(metadataStore);
        string path = WriteScriptFile("sleeper.cs", "Console.WriteLine(\"running\");\nawait Task.Delay(TimeSpan.FromSeconds(30));");

        runner.ScriptRunStarted += _ => runStartedSignal.TrySetResult();
        Task runTask = runner.RunNowAsync(new ScriptRef(path, "sleeper"));

        await runStartedSignal.Task;
        while (runner.GetLiveRun(path) is not { OutputText.Length: > 0 })
        {
            await Task.Delay(20);
        }

        Assert.True(runner.StopRun(path));
        await runTask;

        Assert.NotNull(persisted);
        Assert.False(persisted!.Success);
        Assert.True(persisted.WasStopped);
        Assert.Null(persisted.ExitCode);
        Assert.False(runner.IsRunning(path));
    }

    [Fact]
    public async Task GetLiveRun_ScriptPromptsWithUnterminatedConsoleWrite_ShowsPromptBeforeInputIsSent()
    {
        // Regression test: CliWrap's own line-based ListenAsync/StandardOutputCommandEvent only surfaces text
        // once a newline actually arrives, which left a `Console.Write("...: ")` prompt (no trailing
        // newline - real-world example: Markwardt.ScriptUtilities.Script.Query) invisible until some later
        // newline-terminated line happened to flush it, merged together with whatever unrelated text
        // triggered that flush. WorkspaceScriptRunnerService now pumps raw decoded chunks instead (see
        // PumpAsync), so the prompt must show up immediately, well before any input is ever sent.
        Mock<IWorkspaceMetadataStore> metadataStore = new();
        metadataStore
            .Setup(store => store.AppendScriptRunAsync(workspacePath, It.IsAny<ScriptRunRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using WorkspaceScriptRunnerService runner = CreateRunner(metadataStore);
        string path = WriteScriptFile("prompter.cs", "Console.Write(\"Version to publish (e.g. 1.2.3): \");\nvar line = Console.ReadLine();");

        Task runTask = runner.RunNowAsync(new ScriptRef(path, "prompter"));

        try
        {
            // Bounded rather than an unconditional poll loop - the whole point being tested is that this
            // prompt becomes visible with nothing further ever written after it (the script just sits
            // blocked on Console.ReadLine()), so a regression here would otherwise hang this test forever
            // instead of failing it cleanly.
            LiveScriptRun? liveRun = null;
            for (int attempt = 0; attempt < 100 && (liveRun is null || !liveRun.OutputText.Contains("Version to publish")); attempt++)
            {
                await Task.Delay(20);
                liveRun = runner.GetLiveRun(path);
            }

            Assert.NotNull(liveRun);
            Assert.Contains("Version to publish (e.g. 1.2.3): ", liveRun.OutputText);
        }
        finally
        {
            // The script is otherwise still sitting blocked on Console.ReadLine() forever - stopping it
            // (rather than answering it via SendInputAsync, already covered by a separate test) guarantees
            // this cleans up regardless of whether the assertion above passed.
            runner.StopRun(path);
            await runTask;
        }
    }

    [Fact]
    public async Task SendInputAsync_WhileScriptIsBlockedOnConsoleReadLine_DeliversLineAndEchoesIt()
    {
        Mock<IWorkspaceMetadataStore> metadataStore = new();
        ScriptRunRecord? persisted = null;
        metadataStore
            .Setup(store => store.AppendScriptRunAsync(workspacePath, It.IsAny<ScriptRunRecord>(), It.IsAny<CancellationToken>()))
            .Callback<string, ScriptRunRecord, CancellationToken>((_, record, _) => persisted = record)
            .Returns(Task.CompletedTask);

        using WorkspaceScriptRunnerService runner = CreateRunner(metadataStore);
        string path = WriteScriptFile("echoer.cs", "Console.WriteLine(\"ready\");\nvar line = Console.ReadLine();\nConsole.WriteLine(\"got: \" + line);");

        Task runTask = runner.RunNowAsync(new ScriptRef(path, "echoer"));

        LiveScriptRun? liveRun;
        while ((liveRun = runner.GetLiveRun(path)) is null || !liveRun.OutputText.Contains("ready"))
        {
            await Task.Delay(20);
        }

        await liveRun.SendInputAsync("hello");
        await runTask;

        Assert.NotNull(persisted);
        Assert.True(persisted!.Success);
        // No "< " prefix and no other AutoDev-authored commentary anywhere - SendInputAsync's echo (standing
        // in for a real terminal's own local echo) reads exactly like the script's own console session, start
        // to finish.
        Assert.Equal("ready\nhello\ngot: hello\n", persisted.Output);
    }

    [Fact]
    public async Task GetLiveRun_AlreadyRegisteredWhenScriptRunStartedFires_NeverRacesSubscribers()
    {
        Mock<IWorkspaceMetadataStore> metadataStore = new();
        metadataStore
            .Setup(store => store.AppendScriptRunAsync(workspacePath, It.IsAny<ScriptRunRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using WorkspaceScriptRunnerService runner = CreateRunner(metadataStore);
        string path = WriteScriptFile("greet.cs", "Console.WriteLine(\"hello\");");

        // A ScriptRunStarted subscriber (e.g. ScriptTabViewModel) must be able to call GetLiveRun immediately
        // and get something back, not null - see WorkspaceScriptRunnerService.RunAndTrackAsync's own comment
        // on why the live run is registered before this event fires rather than after.
        LiveScriptRun? observedDuringRun = null;
        runner.ScriptRunStarted += _ => observedDuringRun = runner.GetLiveRun(path);

        await runner.RunNowAsync(new ScriptRef(path, "greet"));

        Assert.NotNull(observedDuringRun);
    }

    [Fact]
    public async Task RunTaskNowAsync_SingleBatch_RunsEveryScriptConcurrentlyAndPersistsSuccessfulRecord()
    {
        Mock<IWorkspaceMetadataStore> metadataStore = new();
        TaskRunRecord? persisted = null;
        metadataStore
            .Setup(store => store.AppendScriptRunAsync(workspacePath, It.IsAny<ScriptRunRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        metadataStore
            .Setup(store => store.AppendTaskRunAsync(workspacePath, It.IsAny<TaskRunRecord>(), It.IsAny<CancellationToken>()))
            .Callback<string, TaskRunRecord, CancellationToken>((_, record, _) => persisted = record)
            .Returns(Task.CompletedTask);

        using WorkspaceScriptRunnerService runner = CreateRunner(metadataStore);
        string pathA = WriteScriptFile("a.cs", "Console.WriteLine(\"a\");");
        string pathB = WriteScriptFile("b.cs", "Console.WriteLine(\"b\");");

        List<string> order = [];
        runner.TaskRunStarted += _ => order.Add("task-started");
        runner.TaskRunCompleted += _ => order.Add("task-completed");

        await runner.RunTaskNowAsync(
            new TaskRef("group.task", "group"),
            [[new ScriptRef(pathA, "a"), new ScriptRef(pathB, "b")]]);

        Assert.Equal(["task-started", "task-completed"], order);
        Assert.NotNull(persisted);
        Assert.True(persisted!.Success);
        Assert.False(persisted.WasStopped);
        Assert.Equal([pathA, pathB], persisted.Scripts.Select(s => s.Path));
        Assert.False(runner.IsTaskRunning("group.task"));
    }

    [Fact]
    public async Task RunTaskNowAsync_ScriptFailsInFirstBatch_SkipsSecondBatchAndPersistsUnsuccessfulRecord()
    {
        Mock<IWorkspaceMetadataStore> metadataStore = new();
        TaskRunRecord? persisted = null;
        metadataStore
            .Setup(store => store.AppendScriptRunAsync(workspacePath, It.IsAny<ScriptRunRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        metadataStore
            .Setup(store => store.AppendTaskRunAsync(workspacePath, It.IsAny<TaskRunRecord>(), It.IsAny<CancellationToken>()))
            .Callback<string, TaskRunRecord, CancellationToken>((_, record, _) => persisted = record)
            .Returns(Task.CompletedTask);

        using WorkspaceScriptRunnerService runner = CreateRunner(metadataStore);
        string failing = WriteScriptFile("fail.cs", "Environment.Exit(1);");
        string neverRun = WriteScriptFile("second.cs", "Console.WriteLine(\"should not run\");");

        await runner.RunTaskNowAsync(
            new TaskRef("group.task", "group"),
            [[new ScriptRef(failing, "fail")], [new ScriptRef(neverRun, "second")]]);

        Assert.NotNull(persisted);
        Assert.False(persisted!.Success);
        Assert.False(persisted.WasStopped);
        Assert.False(runner.IsRunning(neverRun));
        metadataStore.Verify(store => store.AppendScriptRunAsync(workspacePath, It.Is<ScriptRunRecord>(r => r.FilePath == neverRun), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StopTask_CancelsEveryRunningChildScript_PersistsStoppedTaskRecord()
    {
        Mock<IWorkspaceMetadataStore> metadataStore = new();
        TaskRunRecord? persisted = null;
        List<ScriptRunRecord> persistedScripts = [];
        TaskCompletionSource taskStartedSignal = new();

        // Mirrors the real WorkspaceMetadataStore's own cancellation-sensitivity (JsonSerializer.SerializeAsync
        // throws immediately given an already-cancelled token) - a plain unconditional Returns(Task.CompletedTask)
        // here would never reproduce the regression this test guards against: StopTask cancelling the very same
        // token this call was scoped against, silently skipping ScriptRunCompleted for every child and leaving
        // HasRunningScripts/IsRunning stuck forever (see RunAndTrackAsync's own doc comment).
        metadataStore
            .Setup(store => store.AppendScriptRunAsync(workspacePath, It.IsAny<ScriptRunRecord>(), It.IsAny<CancellationToken>()))
            .Returns<string, ScriptRunRecord, CancellationToken>((_, record, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                persistedScripts.Add(record);
                return Task.CompletedTask;
            });
        metadataStore
            .Setup(store => store.AppendTaskRunAsync(workspacePath, It.IsAny<TaskRunRecord>(), It.IsAny<CancellationToken>()))
            .Callback<string, TaskRunRecord, CancellationToken>((_, record, _) => persisted = record)
            .Returns(Task.CompletedTask);

        using WorkspaceScriptRunnerService runner = CreateRunner(metadataStore);
        string pathA = WriteScriptFile("sleeper-a.cs", "Console.WriteLine(\"running a\");\nawait Task.Delay(TimeSpan.FromSeconds(30));");
        string pathB = WriteScriptFile("sleeper-b.cs", "Console.WriteLine(\"running b\");\nawait Task.Delay(TimeSpan.FromSeconds(30));");

        List<ScriptRunRecord> completedScripts = [];
        runner.TaskRunStarted += _ => taskStartedSignal.TrySetResult();
        runner.ScriptRunCompleted += completedScripts.Add;
        Task runTask = runner.RunTaskNowAsync(
            new TaskRef("group.task", "group"),
            [[new ScriptRef(pathA, "sleeper-a"), new ScriptRef(pathB, "sleeper-b")]]);

        await taskStartedSignal.Task;
        while (runner.GetLiveRun(pathA) is not { OutputText.Length: > 0 } || runner.GetLiveRun(pathB) is not { OutputText.Length: > 0 })
        {
            await Task.Delay(20);
        }

        Assert.True(runner.StopTask("group.task"));
        await runTask;

        Assert.NotNull(persisted);
        Assert.False(persisted!.Success);
        Assert.True(persisted.WasStopped);
        Assert.False(runner.IsRunning(pathA));
        Assert.False(runner.IsRunning(pathB));
        Assert.False(runner.IsTaskRunning("group.task"));

        // Both children must still persist their own record and fire ScriptRunCompleted, marked WasStopped -
        // not silently dropped by the token reuse this test guards against.
        Assert.Equal(2, completedScripts.Count);
        Assert.Equal(2, persistedScripts.Count);
        Assert.All(completedScripts, record => Assert.True(record.WasStopped));
        Assert.All(completedScripts, record => Assert.False(record.Success));
    }

    [Fact]
    public async Task RunTaskNowAsync_WhileAnotherScriptIsRunning_IsNoOp()
    {
        Mock<IWorkspaceMetadataStore> metadataStore = new();
        metadataStore
            .Setup(store => store.AppendScriptRunAsync(workspacePath, It.IsAny<ScriptRunRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using WorkspaceScriptRunnerService runner = CreateRunner(metadataStore);
        string sleeper = WriteScriptFile("sleeper.cs", "await Task.Delay(TimeSpan.FromSeconds(30));");
        string other = WriteScriptFile("other.cs", "Console.WriteLine(\"should not run\");");

        TaskCompletionSource runStartedSignal = new();
        runner.ScriptRunStarted += _ => runStartedSignal.TrySetResult();
        Task runTask = runner.RunNowAsync(new ScriptRef(sleeper, "sleeper"));
        await runStartedSignal.Task;

        await runner.RunTaskNowAsync(new TaskRef("group.task", "group"), [[new ScriptRef(other, "other")]]);

        Assert.False(runner.IsTaskRunning("group.task"));
        Assert.False(runner.IsRunning(other));

        runner.StopRun(sleeper);
        await runTask;
    }
}
