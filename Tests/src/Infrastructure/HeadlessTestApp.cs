using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

namespace AutoDev.Tests.Infrastructure;

/// <summary>Minimal Avalonia Application for headless View-level tests (see ScriptTabViewTests) - just enough (FluentTheme) for a real control's own default template to build, without the real App's own DI-composed OnFrameworkInitializationCompleted.</summary>
public sealed class HeadlessTestApp : Application
{
    /// <inheritdoc />
    public override void Initialize() => Styles.Add(new FluentTheme());
}

/// <summary>
/// Sets up Avalonia's headless platform (no real window server, no Xvfb needed) exactly once per test process -
/// plain Avalonia.Headless rather than the Avalonia.Headless.XUnit glue package, which pulls in xunit.v3.core
/// and conflicts with this project's xunit v2 test suite. A View-level test calls EnsureInitialized() itself
/// before touching any Avalonia control.
/// </summary>
public static class TestAppBuilder
{
    private static readonly object gate = new();
    private static bool initialized;

    public static void EnsureInitialized()
    {
        lock (gate)
        {
            if (initialized)
            {
                return;
            }

            AppBuilder.Configure<HeadlessTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
            initialized = true;
        }
    }
}
