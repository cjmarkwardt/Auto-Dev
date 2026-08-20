using Markwardt.TaskRunner;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoDev.Tests.Core.Services;

/// <summary>
/// Covers WorkspaceTaskSchedulerService's own job on top of Markwardt.TaskRunner's TaskEngine: reading a real
/// .task file from disk, tracking a run's live scripts while it's in flight, persisting a TaskRunRecord once
/// it finishes (success, a script failure, a parse failure, or a user Stop), and raising the three scheduler
/// events in order. Runs a real TaskEngine against real temp-directory .task files rather than mocking it -
/// only IWorkspaceMetadataStore is mocked, since persistence is this class's own responsibility.
/// </summary>
public sealed class WorkspaceTaskSchedulerServiceTests : IDisposable
{
    private readonly string workspacePath = Directory.CreateTempSubdirectory("autodev-scheduler-tests-").FullName;

    public void Dispose() => Directory.Delete(workspacePath, recursive: true);

    private WorkspaceTaskSchedulerService CreateScheduler(Mock<IWorkspaceMetadataStore> metadataStore)
    {
        WorkspaceTaskSchedulerService scheduler = new(workspacePath, metadataStore.Object, NullLogger<WorkspaceTaskSchedulerService>.Instance);
        scheduler.Start();
        return scheduler;
    }

    private string WriteTaskFile(string relativePath, string content)
    {
        string fullPath = Path.Combine(workspacePath, relativePath);
        File.WriteAllText(fullPath, content);
        return relativePath;
    }

    [Fact]
    public async Task RunNowAsync_SuccessfulScript_PersistsCompletedRecordAndRaisesEventsInOrder()
    {
        Mock<IWorkspaceMetadataStore> metadataStore = new();
        TaskRunRecord? persisted = null;
        metadataStore
            .Setup(store => store.AppendTaskRunAsync(workspacePath, It.IsAny<TaskRunRecord>(), It.IsAny<CancellationToken>()))
            .Callback<string, TaskRunRecord, CancellationToken>((_, record, _) => persisted = record)
            .Returns(Task.CompletedTask);

        using WorkspaceTaskSchedulerService scheduler = CreateScheduler(metadataStore);
        string path = WriteTaskFile("greet.task", "script Greeter\n    . hello\n");

        List<string> order = [];
        scheduler.TaskRunStarted += _ => order.Add("started");
        scheduler.TaskScriptsAvailable += _ => order.Add("available");
        scheduler.TaskRunCompleted += _ => order.Add("completed");

        await scheduler.RunNowAsync(new TaskRef(path, "greet"));

        Assert.Equal(["started", "available", "completed"], order);
        Assert.NotNull(persisted);
        Assert.True(persisted!.Success);
        Assert.False(persisted.WasStopped);
        Assert.Null(persisted.ParseError);
        ScriptRunRecord script = Assert.Single(persisted.Scripts);
        Assert.Equal("Greeter", script.Name);
        Assert.Equal(ScriptStatus.Completed, script.Status);
        Assert.Contains("hello", script.Log);
        Assert.False(scheduler.IsRunning(path));
    }

    [Fact]
    public async Task RunNowAsync_FailingScript_PersistsUnsuccessfulRecord()
    {
        Mock<IWorkspaceMetadataStore> metadataStore = new();
        TaskRunRecord? persisted = null;
        metadataStore
            .Setup(store => store.AppendTaskRunAsync(workspacePath, It.IsAny<TaskRunRecord>(), It.IsAny<CancellationToken>()))
            .Callback<string, TaskRunRecord, CancellationToken>((_, record, _) => persisted = record)
            .Returns(Task.CompletedTask);

        using WorkspaceTaskSchedulerService scheduler = CreateScheduler(metadataStore);
        string path = WriteTaskFile("fail.task", "script Failer\n    run exit 1\n");

        await scheduler.RunNowAsync(new TaskRef(path, "fail"));

        Assert.NotNull(persisted);
        Assert.False(persisted!.Success);
        Assert.False(persisted.WasStopped);
        ScriptRunRecord script = Assert.Single(persisted.Scripts);
        Assert.Equal(ScriptStatus.Failed, script.Status);
    }

    [Fact]
    public async Task RunNowAsync_InvalidSyntax_PersistsParseErrorRecordWithoutScripts()
    {
        Mock<IWorkspaceMetadataStore> metadataStore = new();
        TaskRunRecord? persisted = null;
        metadataStore
            .Setup(store => store.AppendTaskRunAsync(workspacePath, It.IsAny<TaskRunRecord>(), It.IsAny<CancellationToken>()))
            .Callback<string, TaskRunRecord, CancellationToken>((_, record, _) => persisted = record)
            .Returns(Task.CompletedTask);

        using WorkspaceTaskSchedulerService scheduler = CreateScheduler(metadataStore);
        string path = WriteTaskFile("broken.task", "not a valid top-level line\n");

        bool scriptsAvailableRaised = false;
        scheduler.TaskScriptsAvailable += _ => scriptsAvailableRaised = true;

        await scheduler.RunNowAsync(new TaskRef(path, "broken"));

        Assert.False(scriptsAvailableRaised);
        Assert.NotNull(persisted);
        Assert.False(persisted!.Success);
        Assert.NotNull(persisted.ParseError);
        Assert.Empty(persisted.Scripts);
    }

    /// <summary>
    /// Assertions never run directly inside a scheduler-invoked event handler in these tests - the scheduler
    /// wraps its own run loop in a broad try/catch (see WorkspaceTaskSchedulerService.RunAndTrackAsync) that
    /// would silently swallow an assertion failure thrown from inside one of its event invocations instead of
    /// letting it fail the test, so handlers below only ever capture state, asserted on afterward.
    /// </summary>
    [Fact]
    public async Task StopRun_CancelsInFlightRun_PersistsStoppedRecord()
    {
        Mock<IWorkspaceMetadataStore> metadataStore = new();
        TaskRunRecord? persisted = null;
        metadataStore
            .Setup(store => store.AppendTaskRunAsync(workspacePath, It.IsAny<TaskRunRecord>(), It.IsAny<CancellationToken>()))
            .Callback<string, TaskRunRecord, CancellationToken>((_, record, _) => persisted = record)
            .Returns(Task.CompletedTask);

        using WorkspaceTaskSchedulerService scheduler = CreateScheduler(metadataStore);
        string path = WriteTaskFile("slow.task", "script Slow\n    wait 30\n");

        bool stopAccepted = false;
        scheduler.TaskScriptsAvailable += _ => stopAccepted = scheduler.StopRun(path);
        await scheduler.RunNowAsync(new TaskRef(path, "slow"));

        Assert.True(stopAccepted);
        Assert.NotNull(persisted);
        Assert.True(persisted!.WasStopped);
        Assert.False(persisted.Success);
    }

    [Fact]
    public async Task GetLiveScripts_WhileWaitingOnAfter_ReportsWaitingStatus()
    {
        Mock<IWorkspaceMetadataStore> metadataStore = new();
        metadataStore
            .Setup(store => store.AppendTaskRunAsync(workspacePath, It.IsAny<TaskRunRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using WorkspaceTaskSchedulerService scheduler = CreateScheduler(metadataStore);
        string path = WriteTaskFile("chain.task", "script First\n    wait 0.2\nscript Second\n    after First\n    . done\n");

        IReadOnlyList<ScriptRunner>? liveScripts = null;
        var sawWaiting = false;
        scheduler.TaskScriptsAvailable += task =>
        {
            liveScripts = scheduler.GetLiveScripts(task.Path);
            var second = liveScripts?.SingleOrDefault(s => s.Name == "Second");
            if (second is not null)
            {
                second.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ScriptRunner.Status) && second.Status == ScriptStatus.Waiting)
                    {
                        sawWaiting = true;
                    }
                };
            }
        };

        await scheduler.RunNowAsync(new TaskRef(path, "chain"));

        Assert.NotNull(liveScripts);
        Assert.Contains(liveScripts!, s => s.Name == "Second");
        Assert.True(sawWaiting);
    }
}
