using AutoDev.Core.Services;

namespace AutoDev.AiCli;

/// <summary>The app-wide currently-selected AI provider (see the title bar's provider switcher) - a single global choice shared by every workspace tab's Generate session, not a per-workspace setting.</summary>
public interface IAiProviderSelectionService
{
    /// <summary>AiProvider.Claude until <see cref="InitializeAsync"/> has loaded the persisted choice (if any).</summary>
    AiProvider CurrentProvider { get; }

    /// <summary>Raised whenever CurrentProvider changes - GenerateTabViewModel tears down any live session (a Claude session id can't be resumed by Codex or vice versa) and HeaderViewModel refreshes the account/usage display shown for the newly-selected provider.</summary>
    event Action<AiProvider>? ProviderChanged;

    /// <summary>Loads the persisted provider choice - call once at app startup before anything reads CurrentProvider for the first time.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task SetProviderAsync(AiProvider provider, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAiProviderSelectionService" />
public sealed class AiProviderSelectionService(ISettingsService settingsService) : IAiProviderSelectionService
{
    public AiProvider CurrentProvider { get; private set; } = AiProvider.Claude;

    public event Action<AiProvider>? ProviderChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.LoadAsync(cancellationToken);
        CurrentProvider = Enum.TryParse<AiProvider>(settings.AiProvider, out var parsed) ? parsed : AiProvider.Claude;
    }

    public async Task SetProviderAsync(AiProvider provider, CancellationToken cancellationToken = default)
    {
        if (provider == CurrentProvider)
        {
            return;
        }

        CurrentProvider = provider;

        var settings = await settingsService.LoadAsync(cancellationToken);
        settings.AiProvider = provider.ToString();
        await settingsService.SaveAsync(settings, cancellationToken);

        ProviderChanged?.Invoke(provider);
    }
}
