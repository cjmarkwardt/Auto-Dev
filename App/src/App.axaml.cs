using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AutoDev.Infrastructure;
using AutoDev.AiCli;
using AutoDev.ClaudeCli;
using AutoDev.CodexCli;
using AutoDev.Core.Services;
using AutoDev.ViewModels;
using AutoDev.ViewModels.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AutoDev;

public partial class App : Application
{
    private ServiceProvider? services;
    private MainWindowViewModel? mainWindowViewModel;
    private bool shutdownConfirmed;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            services = BuildServiceProvider();
            mainWindowViewModel = services.GetRequiredService<MainWindowViewModel>();

            MainWindow window = new MainWindow { DataContext = mainWindowViewModel };
            desktop.MainWindow = window;

            desktop.ShutdownRequested += OnShutdownRequested;

            // Backstop for any exit path that never reaches OnShutdownRequested at all - a caught
            // termination signal (SIGTERM, not the unstoppable SIGKILL) or an unhandled-exception crash
            // still raises ProcessExit. DisposeServices is safe to call twice (ServiceProvider.Dispose
            // itself is idempotent), so there's no harm in this firing in addition to the normal path below.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeServices();

            _ = mainWindowViewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (shutdownConfirmed || mainWindowViewModel is null)
        {
            return;
        }

        e.Cancel = true;
        await mainWindowViewModel.ShutdownAsync();
        shutdownConfirmed = true;

        // Disposes every registered singleton that implements IDisposable, notably SoundService - see its
        // own doc comment for why a ding's underlying player process otherwise survives past this app's own
        // lifetime. Done here, on the one path that's guaranteed to run for a normal window close, rather
        // than only in the ProcessExit backstop above.
        DisposeServices();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void DisposeServices() => services?.Dispose();

    private static ServiceProvider BuildServiceProvider()
    {
        ServiceCollection services = new ServiceCollection();

        services.AddLogging(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Information));

        // Core
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<IWorkspaceMetadataStore, WorkspaceMetadataStore>();
        services.AddSingleton<IWorkspaceService, WorkspaceService>();
        services.AddSingleton<ITemplateService, TemplateService>();
        services.AddSingleton<IFileTreeService, FileTreeService>();
        services.AddSingleton<IExternalOpenService, ExternalOpenService>();
        services.AddSingleton<INewInstanceService, NewInstanceService>();
        services.AddSingleton<IWorkspaceFileWatcherFactory, WorkspaceFileWatcherFactory>();
        services.AddSingleton<IUsageAggregatorService, UsageAggregatorService>();
        services.AddSingleton<IScriptRunnerServiceFactory, ScriptRunnerServiceFactory>();
        services.AddSingleton<ICommandExecutor, CommandExecutor>();
        services.AddSingleton<IGitService, GitService>();
        services.AddSingleton<IVersioningServiceFactory, VersioningServiceFactory>();
        services.AddSingleton<ISoundService, SoundService>();

        // AI provider bridge - one IAiAuthService/IAiUsageService registration per provider (HeaderViewModel/
        // AuthGateViewModel pick the one matching IAiProviderSelectionService.CurrentProvider via
        // IEnumerable<T>), routed through the shared AiSessionClientFactory for session clients.
        services.AddSingleton<IAiProviderSelectionService, AiProviderSelectionService>();
        services.AddSingleton<IAiAuthService, ClaudeAuthService>();
        services.AddSingleton<IAiUsageService, ClaudeUsageService>();
        services.AddSingleton<ClaudeSessionClientFactory>();
        services.AddSingleton<IAiAuthService, CodexAuthService>();
        services.AddSingleton<IAiUsageService, CodexUsageService>();
        services.AddSingleton<CodexSessionClientFactory>();
        services.AddSingleton<IAiSessionClientFactory, AiSessionClientFactory>();

        // App infrastructure
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<IDialogService, AvaloniaDialogService>();
        services.AddSingleton<IClipboardService, AvaloniaClipboardService>();

        // ViewModels
        services.AddSingleton<IWorkspaceFactory, WorkspaceFactory>();
        services.AddSingleton<HeaderViewModel>();
        services.AddSingleton<AuthGateViewModel>();
        services.AddSingleton<MainShellViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
