using System.Text.Json;
using AutoDev.Core.Models;
using AutoDev.Core.Serialization;

namespace AutoDev.Core.Services;

public sealed class JsonSettingsService : ISettingsService
{
    private readonly string _settingsFilePath;

    public JsonSettingsService()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
            "AutoDev");
        Directory.CreateDirectory(appDataDir);
        _settingsFilePath = Path.Combine(appDataDir, "settings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsFilePath))
        {
            return new AppSettings();
        }

        try
        {
            await using var stream = File.OpenRead(_settingsFilePath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, AppJson.Options, cancellationToken)
                   ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await using var stream = File.Create(_settingsFilePath);
        await JsonSerializer.SerializeAsync(stream, settings, AppJson.Options, cancellationToken);
    }
}
