using System.Text.RegularExpressions;

namespace AutoDev.Core.Services;

/// <summary>The shared %VAR% substitution primitive - same syntax as the old ScriptBlockParser, now used by TaskDocumentReader to resolve a task's script names and command fields.</summary>
public static partial class VariableSubstitution
{
    [GeneratedRegex(@"%([A-Za-z_][A-Za-z0-9_]*)%")]
    private static partial Regex VariableRefRegex();

    /// <exception cref="FormatException">A %VAR% reference has no matching variable.</exception>
    public static string Substitute(string text, IReadOnlyDictionary<string, string> variables) =>
        VariableRefRegex().Replace(text, match =>
        {
            var name = match.Groups[1].Value;
            if (!variables.TryGetValue(name, out var value))
            {
                throw new FormatException($"Undefined variable '%{name}%'.");
            }

            return value;
        });
}
