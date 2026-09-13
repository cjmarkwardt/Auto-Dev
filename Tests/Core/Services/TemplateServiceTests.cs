namespace Tests.Core.Services;

public sealed class TemplateServiceTests
{
    private static (TemplateService Service, AppSettings Settings) CreateService()
    {
        var settings = new AppSettings();
        var mock = new Mock<ISettingsService>();
        mock.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => settings);
        mock.Setup(s => s.SaveAsync(It.IsAny<AppSettings>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return (new TemplateService(mock.Object), settings);
    }

    [Fact]
    public async Task RegisterTemplateAsyncAddsPathOnlyOnce()
    {
        var (service, settings) = CreateService();
        var path = Path.GetFullPath("template.md");

        await service.RegisterTemplateAsync(path);
        await service.RegisterTemplateAsync(path);

        Assert.Single(settings.TemplatePaths);
    }

    [Fact]
    public async Task UnregisterTemplateAsyncRemovesPath()
    {
        var (service, settings) = CreateService();
        var path = Path.GetFullPath("template.md");
        await service.RegisterTemplateAsync(path);

        await service.UnregisterTemplateAsync(path);

        Assert.Empty(settings.TemplatePaths);
    }

    [Fact]
    public async Task GetRegisteredTemplatesAsyncFiltersOutMissingFiles()
    {
        var (service, _) = CreateService();
        var tempFile = Path.GetTempFileName();
        try
        {
            await service.RegisterTemplateAsync(tempFile);
            await service.RegisterTemplateAsync("/nonexistent/path/template.md");

            var templates = await service.GetRegisteredTemplatesAsync();

            var template = Assert.Single(templates);
            Assert.Equal(Path.GetFullPath(tempFile), template.Path);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ReadTemplateContentAsyncReadsFreshEveryTimeRatherThanCaching()
    {
        var (service, _) = CreateService();
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "first");
            Assert.Equal("first", await service.ReadTemplateContentAsync(tempFile));

            await File.WriteAllTextAsync(tempFile, "second");
            Assert.Equal("second", await service.ReadTemplateContentAsync(tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
