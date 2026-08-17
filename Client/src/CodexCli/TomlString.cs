namespace AutoDev.CodexCli;

/// <summary>Escapes a string for embedding as a TOML basic string value in a `codex -c key="value"` override - see CodexSessionClient's developer_instructions/model_reasoning_effort overrides.</summary>
internal static class TomlString
{
    public static string Quote(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
