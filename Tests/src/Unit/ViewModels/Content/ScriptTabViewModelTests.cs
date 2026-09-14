using AutoDev.ViewModels.Infrastructure;

namespace AutoDev.Tests.ViewModels.Content;

/// <summary>Covers ScriptTabViewModel.RemoveEntry - removing a dropdown entry (script or task), clearing SelectedEntry when it was the one removed, deleting its persisted history so it doesn't reappear, and refusing to remove one that's currently running.</summary>
public sealed class ScriptTabViewModelTests
{
    private readonly string workspacePath = "/workspace";
    private readonly Mock<IWorkspaceMetadataStore> metadataStore = new();
    private readonly Mock<IWorkspaceScriptRunner> scriptRunner = new();
    private readonly Mock<IClipboardService> clipboardService = new();
    private readonly Mock<IUiDispatcher> dispatcher = new();

    public ScriptTabViewModelTests()
    {
        dispatcher.Setup(d => d.Post(It.IsAny<Action>())).Callback<Action>(action => action());

        // Selecting an entry that isn't currently live fires a detached LoadMostRecentRunAsync load - given an
        // explicit empty result here so it resolves cleanly rather than relying on Moq's own unconfigured
        // default-value behavior for a Task<List<T>> return.
        metadataStore
            .Setup(store => store.LoadScriptRunsAsync(workspacePath, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private ScriptTabViewModel CreateViewModel() =>
        new(workspacePath, metadataStore.Object, scriptRunner.Object, clipboardService.Object, dispatcher.Object);

    [Fact]
    public void RemoveEntry_StandaloneScript_RemovesFromEntriesAndDeletesHistory()
    {
        ScriptTabViewModel viewModel = CreateViewModel();
        viewModel.SelectScript("Scripts/Build.cs", "Build");
        ScriptEntry entry = Assert.Single(viewModel.Entries);

        viewModel.RemoveEntryCommand.Execute(entry);

        Assert.Empty(viewModel.Entries);
        Assert.Null(viewModel.SelectedEntry);
        metadataStore.Verify(store => store.DeleteScriptRuns(workspacePath, "Scripts/Build.cs"), Times.Once);
        metadataStore.Verify(store => store.DeleteTaskRuns(workspacePath, It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void RemoveEntry_Task_RemovesFromEntriesAndDeletesTaskHistory()
    {
        ScriptTabViewModel viewModel = CreateViewModel();
        viewModel.SelectTask("group.task", "group", [new ScriptRef("a.cs", "a"), new ScriptRef("b.cs", "b")]);
        ScriptEntry entry = Assert.Single(viewModel.Entries);

        viewModel.RemoveEntryCommand.Execute(entry);

        Assert.Empty(viewModel.Entries);
        Assert.Null(viewModel.SelectedEntry);
        metadataStore.Verify(store => store.DeleteTaskRuns(workspacePath, "group.task"), Times.Once);
        metadataStore.Verify(store => store.DeleteScriptRuns(workspacePath, It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void RemoveEntry_NotCurrentlySelected_LeavesSelectionUntouched()
    {
        ScriptTabViewModel viewModel = CreateViewModel();
        viewModel.SelectScript("Scripts/A.cs", "A");
        viewModel.SelectScript("Scripts/B.cs", "B");
        ScriptEntry entryA = viewModel.Entries.First(e => e.Id == "Scripts/A.cs");

        viewModel.RemoveEntryCommand.Execute(entryA);

        Assert.DoesNotContain(entryA, viewModel.Entries);
        Assert.NotNull(viewModel.SelectedEntry);
        Assert.Equal("Scripts/B.cs", viewModel.SelectedEntry!.Id);
    }

    [Fact]
    public void CanRemoveEntry_WhileRunning_ReturnsFalse()
    {
        scriptRunner.Setup(runner => runner.IsRunning("Scripts/Build.cs")).Returns(true);

        ScriptTabViewModel viewModel = CreateViewModel();
        viewModel.SelectScript("Scripts/Build.cs", "Build");
        ScriptEntry entry = Assert.Single(viewModel.Entries);

        Assert.False(viewModel.RemoveEntryCommand.CanExecute(entry));
    }
}
