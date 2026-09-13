using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoDev.Core.Serialization;

public static class AppJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
