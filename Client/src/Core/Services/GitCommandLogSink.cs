namespace AutoDev.Core.Services;

/// <summary>
/// Ambient (AsyncLocal-flowed) sink for the git command log the busy overlay shows live - GitService's own
/// RunAsync writes to Current, if set, after every command it runs. AsyncLocal (rather than a persistent event
/// subscription on the shared IGitService singleton) is deliberate: VersionSectionViewModel.RunBusyAsync sets
/// Current only for the duration of a single busy action, and it flows automatically into everything that
/// action awaits - no subscription to leak across a workspace tab closing/reopening over the app's lifetime.
/// </summary>
public static class GitCommandLogSink
{
    private static readonly AsyncLocal<Action<string>?> current = new();

    public static Action<string>? Current
    {
        get => current.Value;
        set => current.Value = value;
    }
}
