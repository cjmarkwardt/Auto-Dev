using System.Text.Json;
using AutoDev.Core.Models;
using AutoDev.Core.Serialization;

namespace AutoDev.Core.Services;

public sealed class JsonSettingsService : ISettingsService
{
    private readonly string settingsFilePath;

    public JsonSettingsService()
    {
        string appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
            "AutoDev");
        Directory.CreateDirectory(appDataDir);
        settingsFilePath = Path.Combine(appDataDir, "settings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(settingsFilePath))
        {
            return new AppSettings();
        }

        try
        {
            await using FileStream stream = File.OpenRead(settingsFilePath);
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
        await using FileStream stream = File.Create(settingsFilePath);
        await JsonSerializer.SerializeAsync(stream, settings, AppJson.Options, cancellationToken);
    }
}
