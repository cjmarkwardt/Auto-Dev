using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using AutoDev.Tests.Infrastructure;
using AutoDev.Views.Content;
using AutoDev.ViewModels.Infrastructure;

namespace AutoDev.Tests.Views.Content;

/// <summary>
/// Covers ScriptTabView's own auto-scroll behavior (see its OnScrollerScrollChanged/OnVmPropertyChanged) against
/// a real, headless-rendered Avalonia visual tree - a ViewModel-level test can't catch a bug in this logic,
/// since it lives entirely in the View's code-behind and depends on real ScrollViewer Offset/Extent/Viewport
/// values a mocked/headless-less test has no way to produce.
/// </summary>
public sealed class ScriptTabViewTests
{
    private readonly Mock<IWorkspaceMetadataStore> metadataStore = new();
    private readonly Mock<IWorkspaceScriptRunner> scriptRunner = new();
    private readonly Mock<IClipboardService> clipboardService = new();
    private readonly Mock<IUiDispatcher> dispatcher = new();

    public ScriptTabViewTests()
    {
        TestAppBuilder.EnsureInitialized();
        dispatcher.Setup(d => d.Post(It.IsAny<Action>())).Callback<Action>(action => action());
        metadataStore
            .Setup(store => store.LoadScriptRunsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    [Fact]
    public void StreamingOutputPastViewportHeight_StaysScrolledToBottom()
    {
        LiveScriptRun liveRun = new();
        scriptRunner.Setup(runner => runner.GetLiveRun("Scripts/Test.cs")).Returns(liveRun);
        scriptRunner.Setup(runner => runner.IsRunning("Scripts/Test.cs")).Returns(true);

        ScriptTabViewModel viewModel = new("/workspace", metadataStore.Object, scriptRunner.Object, clipboardService.Object, dispatcher.Object);
        viewModel.SelectScript("Scripts/Test.cs", "Test");

        ScriptTabView view = new() { DataContext = viewModel };
        Window window = new() { Content = view, Width = 300, Height = 150 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        ScrollViewer scroller = view.FindControl<ScrollViewer>("Scroller")!;

        for (int line = 0; line < 80; line++)
        {
            liveRun.AppendText($"Output line {line}\n");
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(scroller.Extent.Height > scroller.Viewport.Height);
        Assert.True(
            scroller.Offset.Y + scroller.Viewport.Height >= scroller.Extent.Height - 2.0,
            $"Expected to stay scrolled to the bottom (Offset={scroller.Offset.Y}, Viewport={scroller.Viewport.Height}, Extent={scroller.Extent.Height}).");
    }

    [Fact]
    public void SwitchingEntries_ResetsAutoScrollEvenIfThePreviousOneWasScrolledAway()
    {
        LiveScriptRun liveRunA = new();
        LiveScriptRun liveRunB = new();
        scriptRunner.Setup(runner => runner.GetLiveRun("Scripts/A.cs")).Returns(liveRunA);
        scriptRunner.Setup(runner => runner.GetLiveRun("Scripts/B.cs")).Returns(liveRunB);
        scriptRunner.Setup(runner => runner.IsRunning("Scripts/A.cs")).Returns(true);
        scriptRunner.Setup(runner => runner.IsRunning("Scripts/B.cs")).Returns(true);

        ScriptTabViewModel viewModel = new("/workspace", metadataStore.Object, scriptRunner.Object, clipboardService.Object, dispatcher.Object);
        viewModel.SelectScript("Scripts/A.cs", "A");

        ScriptTabView view = new() { DataContext = viewModel };
        Window window = new() { Content = view, Width = 300, Height = 150 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        ScrollViewer scroller = view.FindControl<ScrollViewer>("Scroller")!;

        for (int line = 0; line < 80; line++)
        {
            liveRunA.AppendText($"A line {line}\n");
            Dispatcher.UIThread.RunJobs();
        }

        // Simulate the user manually scrolling away from the bottom while reviewing A's output - the same
        // Offset change a real scroll bar drag would produce.
        scroller.Offset = new Vector(0, 0);
        Dispatcher.UIThread.RunJobs();
        Assert.True(scroller.Offset.Y + scroller.Viewport.Height < scroller.Extent.Height - 2.0);

        viewModel.SelectScript("Scripts/B.cs", "B");
        Dispatcher.UIThread.RunJobs();

        for (int line = 0; line < 80; line++)
        {
            liveRunB.AppendText($"B line {line}\n");
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(scroller.Extent.Height > scroller.Viewport.Height);
        Assert.True(
            scroller.Offset.Y + scroller.Viewport.Height >= scroller.Extent.Height - 2.0,
            $"Expected B to auto-scroll despite A having been scrolled away from (Offset={scroller.Offset.Y}, Viewport={scroller.Viewport.Height}, Extent={scroller.Extent.Height}).");
    }
}
