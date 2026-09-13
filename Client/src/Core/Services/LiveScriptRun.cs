using System.ComponentModel;
using System.Text;

namespace AutoDev.Core.Services;

/// <summary>One .cs file's currently in-flight run, updated as `dotnet run --file`'s stdout/stderr streams in - the live counterpart to a persisted ScriptRunRecord, bound to directly by a viewer (the Script tab) rather than the viewer re-buffering its output itself. Built and mutated exclusively by WorkspaceScriptRunnerService.</summary>
public sealed class LiveScriptRun : INotifyPropertyChanged
{
    private readonly StringBuilder output = new();
    private Stream? standardInput;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>False once `dotnet run` has exited (or been killed) - see WorkspaceScriptRunnerService.RunAndTrackAsync.</summary>
    public bool IsRunning { get; private set; } = true;

    public string OutputText => output.ToString();

    /// <summary>
    /// Appends text exactly as given - no added newline, no line-boundary assumption of any kind. Called with
    /// whatever raw chunk of decoded text `dotnet run`'s stdout/stderr streams produced next (see
    /// WorkspaceScriptRunnerService.PumpAsync), which may be a partial line with no trailing newline at all
    /// (e.g. an interactive prompt written via Console.Write, deliberately left unterminated so the read it's
    /// prompting for appears on the same line) - appending only ever on a newline boundary would leave a
    /// prompt like that invisible until some later line happened to flush it into view, well after it was
    /// actually written and the script was already sitting there blocked waiting to read it.
    /// </summary>
    internal void AppendText(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        output.Append(text);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OutputText)));
    }

    /// <summary>Captures the process's own real stdin stream once CliWrap's PipeSource hands it over - see WorkspaceScriptRunnerService.RunAndTrackAsync. Null until then, so SendInputAsync is a no-op for the brief window before the process has actually started.</summary>
    internal void AttachStandardInput(Stream stream) => standardInput = stream;

    internal void MarkFinished()
    {
        IsRunning = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
    }

    /// <summary>
    /// Writes a line of text straight to the running process's own stdin - for a script that calls
    /// Console.ReadLine() (or reads stdin directly) and would otherwise hang forever, since nothing else in
    /// AutoDev ever supplies input to it. Echoed into OutputText first (only once the write itself succeeds) -
    /// piping directly to a subprocess's stdin, unlike a real terminal, never echoes what was typed on its
    /// own, so this stands in for the terminal's own local echo: the typed text appended exactly as given,
    /// right where the cursor already was (immediately after an unterminated prompt, on the very same line,
    /// with no prefix of any kind), followed by a newline for the Enter that submitted it - indistinguishable
    /// from what actually typing it into a real terminal would have shown. A silent no-op if the process has
    /// already exited or its stdin pipe is otherwise unusable - there is no other output to report that
    /// against.
    /// </summary>
    public async Task SendInputAsync(string text)
    {
        if (standardInput is null)
        {
            return;
        }

        try
        {
            await standardInput.WriteAsync(Encoding.UTF8.GetBytes(text + Environment.NewLine));
            await standardInput.FlushAsync();
            AppendText(text + Environment.NewLine);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
