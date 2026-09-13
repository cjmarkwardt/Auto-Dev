using AutoDev.Core.Models;

namespace AutoDev.Core.Services;

public sealed class TemplateService(ISettingsService settingsService) : ITemplateService
{
    public async Task<IReadOnlyList<WorkspaceTemplate>> GetRegisteredTemplatesAsync(CancellationToken cancellationToken = default)
    {
        AppSettings settings = await settingsService.LoadAsync(cancellationToken);
        return [.. settings.TemplatePaths
            .Where(File.Exists)
            .Select(p => new WorkspaceTemplate { Path = p })];
    }

    public async Task RegisterTemplateAsync(string path, CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(path);
        AppSettings settings = await settingsService.LoadAsync(cancellationToken);
        if (settings.TemplatePaths.Any(p => string.Equals(Path.GetFullPath(p), fullPath, StringComparison.Ordinal)))
        {
            return;
        }

        settings.TemplatePaths.Add(fullPath);
        await settingsService.SaveAsync(settings, cancellationToken);
    }

    public async Task UnregisterTemplateAsync(string path, CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(path);
        AppSettings settings = await settingsService.LoadAsync(cancellationToken);
        settings.TemplatePaths.RemoveAll(p => string.Equals(Path.GetFullPath(p), fullPath, StringComparison.Ordinal));
        await settingsService.SaveAsync(settings, cancellationToken);
    }

    public Task<string> ReadTemplateContentAsync(string path, CancellationToken cancellationToken = default) =>
        File.ReadAllTextAsync(path, cancellationToken);

    public string GetDefaultTemplatesDirectory()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
            "AutoDev", "Templates");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
