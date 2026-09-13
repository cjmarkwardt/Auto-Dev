# Running Scripts

Alongside AI-driven changes, AutoDev can run a `.cs` file directly as a single-file app - `.cs`
files are shown in the file tree with a distinct runnable-file icon and offered `Run`/`Stop`/`View`
instead of (well, alongside) the normal file context menu; double-clicking one runs it the same way
Run does (Stop cancels it mid-run; View reopens its live/last output).

## Execution (`dotnet run --file`)

Running a script shells out to the .NET SDK's own file-based-app support - no custom language,
parser, or execution engine of AutoDev's own: `WorkspaceScriptRunnerService` invokes
`dotnet run --file <full path>` via CliWrap, with the workspace root as the working directory
(regardless of where in the workspace the `.cs` file itself lives, so `{Directory.GetCurrentDirectory()}`
inside the script matches every other workspace-relative action AutoDev takes) and streams its
stdout/stderr live. `--file` (rather than passing the path as a bare positional argument) is what
makes this work unambiguously even when the workspace root already contains a project/solution file,
which `dotnet run` would otherwise try to run instead.

`LiveScriptRun` is the live counterpart to a persisted `ScriptRunRecord` - `IsRunning` and
`OutputText` (`INotifyPropertyChanged`). `OutputText` carries *only* the process's own real
stdout/stderr, byte for byte, plus (see "Sending input" below) the local echo of whatever was typed
back to it - no AutoDev-authored announcement, prefix, or other commentary of any kind ever gets
mixed in, so the Script tab reads exactly like a plain console window observing this one process,
start to finish. The Script tab binds directly to this while a run is in flight, and to the
persisted `ScriptRunRecord` once it finishes - see below.

stdout/stderr are read as raw decoded text chunks (`WithStandardOutputPipe`/`WithStandardErrorPipe`,
each given a `PipeTarget.Create` handing back the process's own real output `Stream`, pumped through
a `StreamReader` into `LiveScriptRun.AppendText`) - deliberately **not** CliWrap's own line-based
`ListenAsync`/`StandardOutputCommandEvent`, which only surfaces text once a newline actually arrives.
A script that prompts via `Console.Write("...: ")` with no trailing newline (so the line the user
types appears right after the prompt, rather than on its own line - a real example:
`Markwardt.ScriptUtilities.Script.Query`) would otherwise sit there invisibly waiting: the prompt
itself stays buffered in CliWrap's own line reader until some later newline-terminated output
happens to flush it out, merged together with whatever unrelated text triggered that flush - which
looks exactly like the script hanging, followed by garbled output once it doesn't. `AppendText`
appends exactly what it's given with no line-boundary assumption of its own, so a prompt like that
appears the instant the process actually writes it.

Stopping a run (`WorkspaceScriptRunnerService.StopRun`) cancels the linked `CancellationTokenSource`
CliWrap's `ExecuteAsync` is running against, which forcefully kills the `dotnet` process (and, by
extension, whatever it spawned) rather than just detaching from it - verified empirically, since
`dotnet run` itself launches a child host process that a merely-graceful cancellation wouldn't reach.

### Sending input (`LiveScriptRun.SendInputAsync`)

The command is also given a `WithStandardInputPipe(PipeSource.Create(...))` - CliWrap hands the
process's own real stdin `Stream` to that delegate once the process starts, which
`LiveScriptRun.AttachStandardInput` captures for later. A script that calls `Console.ReadLine()` (or
reads stdin directly) would otherwise hang forever, since nothing else in AutoDev ever supplies it
input - the Script tab's own input box (visible for as long as the script is running, not gated on
whether it happens to be blocked on a read at that exact moment, since there's no way to know that
from the outside) writes a line of UTF-8 text plus a trailing newline to that stream on Enter/Send,
via `LiveScriptRun.SendInputAsync`. Piping straight to a subprocess's stdin, unlike a real terminal,
never echoes what was typed on its own, so `SendInputAsync` stands in for that itself: the typed
text is appended to `OutputText` verbatim, with no prefix of any kind, exactly where the cursor
already was (immediately after an unterminated prompt, on the very same line), followed by a newline
for the Enter that submitted it - indistinguishable from what actually typing it into a real terminal
would have shown. A silent no-op if the process has already exited or its stdin pipe is otherwise
unusable.

The pipe delegate itself stays suspended on `Task.Delay(Timeout.Infinite, pipeCancellationToken)`
for as long as the process is alive, rather than returning immediately after capturing the stream -
CliWrap cancels `pipeCancellationToken` on its own once the process exits, which is what actually
closes the pipe; returning early instead would close the process's stdin the instant the script
started, before any input could ever be sent.

## Scheduling and concurrency (`IWorkspaceScriptRunner`)

One `WorkspaceScriptRunnerService` per workspace (via `IScriptRunnerServiceFactory`, part of the
per-workspace isolation `IWorkspaceFactory` gives every service that needs its own instance per
open workspace). It's purely a manual-trigger tracker/broadcaster - no polling loop:

- Only one `.cs` file total ever runs at a time per workspace - `RunNowAsync` guards on a single
  `Interlocked`-driven flag (`_runInProgress`), not a per-path one, so starting a second `.cs` file
  while any run (including this same file) is already in flight is a no-op; `_activeRuns` (a
  `ConcurrentDictionary` used as a set) still tracks it by path underneath, purely so
  `IsRunning(scriptId)`/`GetLiveRun(scriptId)` can answer "is *this* script running" for whichever
  one is currently the sole active run, each with its own linked `CancellationTokenSource` so
  `StopRun(scriptId)` only cancels that run's process.
- Two events surface everything: `ScriptRunStarted(ScriptRef)` (fires once the run's `LiveScriptRun`
  is already registered - see `RunAndTrackAsync`'s own comment on why that ordering matters for a
  subscriber that immediately calls `GetLiveRun`) and `ScriptRunCompleted(ScriptRunRecord)`.
- A `ScriptRunRecord` stopped by the user is marked `WasStopped` - purely AutoDev's own policy (a
  killed process has no exit code of its own to distinguish this) - tracked by recording that
  `StopRun` was actually called for that run before persisting its record. The Script tab uses this
  to show "Stopped" instead of "Failed" in the header (`ScriptTabViewModel.ShowStopped`/`ShowFailed`).
- Completed runs persist via `IWorkspaceMetadataStore.AppendScriptRunAsync`, under
  `.autodev/local/script-runs/<sanitized script path>/<runId>.json`, as a `ScriptRunRecord`
  (`FilePath`/`FileName`/`Success`/`WasStopped`/`ExitCode`/`Output`).

`FilesSectionViewModel` is the main consumer of the start/complete events: it drives each file
tree node's "currently running" indicator (re-applied after every tree refresh, since a refresh
recreates node instances) and computes `HasRunningScripts`, which is forwarded all the way to
`GenerateTabViewModel.HasRunningScripts` - **a Claude turn can't start while any `.cs` file is
running in the same workspace**, and vice versa, so script runs and AI-driven edits never contend
for the same working tree at once. `HasRunningScripts` also
reaches `WorkspaceContentViewModel` (forcing the Edit tab read-only, for *every* open file, not just
the one running) and `VersionSectionViewModel.IsInteractionBlocked` (disabling Commit/Merge/etc. and
every History tab action) - manual editing, script running, and AI working are mutually exclusive
states over one workspace's working tree, and only one of the three (with, for scripts, only one
`.cs` file) is ever active at once.

## Script tab vs. Command tab

Two different, purpose-built consoles:

- **Script tab** (`ScriptTabViewModel`) - a dropdown-switchable viewer over the runner above, with
  an input box for talking back to a script that's still running (see above). `Entries` lists every
  script that's currently running or has ever run (seeded from persisted history); for the selected
  script, a single output panel shows its own Running/Succeeded/Stopped/Failed state and
  live/historical output text. A live view attaches to the run's own `LiveScriptRun.OutputText`; a
  historical (already finished) view is populated straight from its persisted
  `ScriptRunRecord.Output` instead - either way the same `OutputText` property, so the Script tab's
  own XAML doesn't need to care which. It can either watch a run live or browse the most recent
  historical run for a script that isn't currently running. A dedicated Copy icon button
  (`ScriptTabViewModel.CopyOutputCommand`) copies the whole of `OutputText` to the clipboard
  unconditionally, rather than depending on the `SelectableTextBlock` displaying it: that control's
  own built-in select-all-then-copy can end up with Copy disabled after enough live text updates
  have gone by while it held a selection (a script's own output streaming in updates `OutputText` -
  and so the control's bound `Text` - continuously while it runs), so a reliable, always-available
  copy path can't depend on it.
- **Command tab** (`CommandTabViewModel`) - a separate, general-purpose REPL-style shell console,
  entirely unrelated to `.cs` scripts. Runs arbitrary one-off command lines rooted at a chosen
  working directory (defaulting to the workspace root; see its own "Set Command Context"/home
  controls) through `ICommandExecutor` - the same CliWrap-based subprocess pattern
  `WorkspaceScriptRunnerService` uses for `dotnet run --file`, just for arbitrary commands instead of
  one fixed one - with output buffering and up/down history recall. This is the ad-hoc counterpart
  to running a specific `.cs` file above.
