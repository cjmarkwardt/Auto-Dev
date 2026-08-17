using AutoDev.Core.Models;

namespace AutoDev.Core.Services;

/// <summary>
/// The single place a .task file's own text content is turned into something ready to run or preview:
/// parses it (see TaskFileParser), then resolves every script name and command field's %VAR% references
/// (see VariableSubstitution) against the document's own variables, eagerly for the whole document up front -
/// an undefined variable anywhere fails before any script starts rather than partway through a run after
/// some side effects already happened.
/// </summary>
public static class TaskDocumentReader
{
    /// <exception cref="FormatException">Invalid .task syntax, an undefined %VAR% reference, or two scripts resolving to the same name.</exception>
    public static TaskDocument ParseAndResolve(string scriptText)
    {
        if (string.IsNullOrWhiteSpace(scriptText))
        {
            return new TaskDocument();
        }

        var doc = TaskFileParser.Parse(scriptText);

        var variables = new Dictionary<string, string>();
        foreach (var variable in doc.Variables)
        {
            variables[variable.Name] = variable.Value;
        }

        var seenNames = new HashSet<string>();
        var scripts = new List<TaskScript>();
        foreach (var script in doc.Scripts)
        {
            var name = VariableSubstitution.Substitute(script.Name, variables);
            if (!seenNames.Add(name))
            {
                throw new FormatException($"Duplicate script name '{name}'.");
            }

            scripts.Add(new TaskScript
            {
                Name = name,
                Row = script.Row,
                Column = script.Column,
                Commands = script.Commands.Select(c => ResolveCommand(c, variables)).ToList(),
            });
        }

        return new TaskDocument { Variables = doc.Variables, Scripts = scripts };
    }

    private static TaskCommand ResolveCommand(TaskCommand command, IReadOnlyDictionary<string, string> variables) => new()
    {
        Instruction = command.Instruction,
        Seconds = command.Seconds,
        Command = VariableSubstitution.Substitute(command.Command, variables),
        Target = VariableSubstitution.Substitute(command.Target, variables),
        Destination = VariableSubstitution.Substitute(command.Destination, variables),
        Copy = command.Copy,
        Overwrite = command.Overwrite,
        Name = VariableSubstitution.Substitute(command.Name, variables),
        EntryKind = command.EntryKind,
        Conditional = command.Conditional,
        Content = VariableSubstitution.Substitute(command.Content, variables),
        Path = VariableSubstitution.Substitute(command.Path, variables),
    };
}
