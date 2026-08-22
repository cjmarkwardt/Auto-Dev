# Task Automation

Alongside AI-driven changes, AutoDev has a small, file-defined scripting system for repeatable
scripted work (build, publish, run tests, ...) - `.task` files, shown in the file tree colored
distinctly and offered `Run`/`Stop`/`View` instead of (well, alongside) the normal file context
menu. See `Example.task` (repo root) for a real sample - right-click it in the Files sidebar and
choose Run to try it (Stop cancels it mid-run; View reopens its live/last output). Everything it
does is confined to a `build/` folder next to it, so running it is harmless and repeatable.

The task language itself - its parser and its execution engine - is **not** AutoDev's own code: it
comes from the [Markwardt.TaskRunner](https://github.com/cjmarkwardt/Task-Runner) NuGet package
(`Core/Core.csproj` in that repo), referenced directly by `Client/AutoDev.csproj`. AutoDev's own
code is everything *around* that: scheduling/tracking multiple concurrent runs across a workspace,
persisting run history, the Files sidebar's Run/Stop/View actions, the Output tab, and a
syntax colorizer for the Edit tab.

## The `.task` language

A hand-written, indentation-sensitive, Python-like syntax (no `end` keyword), fully specified by
Markwardt.TaskRunner:

```
# Top-level variables, substituted wherever {Name} appears below.
var BuildDir = build

script Build
    folder {BuildDir}
    switch {BuildDir}
    run echo "Building in $(pwd)"
    wait 1
    trace off
    run
        echo "Step 1"
        echo "Step 2"
    trace on
    . Build finished.

script Package
    after Build
    . Packaging what Build already produced.
```

- `var Name = value` - top-level variables, substituted wherever `{Name}` appears elsewhere in the
  file (`{{` escapes a literal `{`). Two special names are always available inside a script without
  being declared: `{Script}` (that script's own name) and `{Location}` (its current working
  directory, changed by `switch` - every script starts at the *workspace root*, not the `.task`
  file's own containing folder, even for one nested in a subfolder - see
  `WorkspaceTaskSchedulerService.RunAndTrackAsync`).
- `script <name>` - one script block. **Every script in a file runs concurrently** with every other
  script in that same file (they all start together; the whole `.task` run only finishes once every
  script has); instructions *within* one script run sequentially, and a failing instruction fails
  that script (logged, not thrown) without stopping any other script.
- Instructions: `run <cmd>` (or a bare `run` with an indented multi-line shell body), `.` `<text>`
  (writes straight to the script's own output, no process spawned), `wait <seconds>`,
  `after <script>` (pause until another script in the same file finishes, success or not),
  `switch <path>` (that script's own working directory), `move`/`copy`/`rename source newName`,
  `file`/`append <path> [content]`, `folder <path>`, `delete`/`clean <path>`, `read <path>` (writes
  a file's content to the output), `trace on`/`trace off` (toggles whether instruction
  announcements and `run`'s own command output reach the script's output log for the rest of the
  script - output an instruction writes explicitly, such as `.` and `read`, is unaffected). None of
  `file`/`folder`/`move`/`copy` take `overwrite`/`conditional` modifiers any more - they always
  replace/no-op as appropriate. A line whose first non-whitespace character is `#` is a comment and
  is ignored entirely, same as a blank line.
- There is no longer any way to pin a script's live output panel to a specific position (the old
  `output <col> <row>` instruction) - the Output tab always auto-arranges every script's panel into
  a square-ish grid (see `ScriptBlockGridLayout`).

`Markwardt.TaskRunner.TaskDocumentParser.Parse` turns raw text into a fully resolved
`Markwardt.TaskRunner.TaskDocument` in one pass - variable resolution (detecting unknown/circular
references) and instruction parsing together, throwing a `Markwardt.TaskRunner.TaskParseException`
with a line number on any error.

## Execution (`Markwardt.TaskRunner.TaskEngine`/`ScriptRunner`)

A `TaskEngine` wraps one parsed `TaskDocument` plus the working directory every script starts in -
`WorkspaceTaskSchedulerService` always constructs it with the workspace root, regardless of where in
the workspace the `.task` file itself lives (unlike `TaskEngine.Load`, a convenience static the
library's own standalone Runner app uses instead, which defaults to the file's containing folder).
`RunAsync` runs every script's own `ScriptRunner` concurrently (`Task.WhenAll`) and completes once
they all have - a `ScriptRunner` never throws out of `RunAsync`, even on cancellation: a failure
(or a cancelled `run`/`wait`/`after`) is caught, logged as an `Error: ...` line, and leaves that
script `Failed`, so one script's trouble never aborts its siblings or the engine's own `RunAsync`
call. Each `ScriptRunner` exposes live `Status` (`Running`/`Waiting`/`Completed`/`Failed`,
`INotifyPropertyChanged`) and `Log`/`LogText` (every instruction's own `"> ..."` announcement plus
whatever it writes), which AutoDev's Output tab binds to directly for a live run rather than
re-buffering output itself.

## Scheduling and concurrency (`IWorkspaceTaskScheduler`)

One `WorkspaceTaskSchedulerService` per workspace (via `ITaskSchedulerServiceFactory`, part of the
per-tab isolation described in [Architecture](Architecture.md#dependency-injection)). It's purely a
manual-trigger tracker/broadcaster - no polling loop:

- Only one `.task` file total ever runs at a time per workspace - `RunNowAsync` guards on a single
  `Interlocked`-driven flag (`_runInProgress`), not a per-path one, so starting a second `.task`
  file while any run (including this same file) is already in flight is a no-op; `_activeRuns` (a
  `ConcurrentDictionary` used as a set) still tracks it by path underneath, purely so
  `IsRunning(taskId)`/`GetLiveScripts(taskId)` can answer "is *this* task running" for whichever one
  is currently the sole active run, each with its own linked `CancellationTokenSource` so
  `StopRun(taskId)` only cancels that run's `TaskEngine`.
- Three events surface everything: `TaskRunStarted(TaskRef)` (fires immediately, before the file is
  even read), `TaskScriptsAvailable(TaskRef)` (fires once the file has parsed and
  `GetLiveScripts(taskId)` - the run's live `ScriptRunner`s - is ready), and
  `TaskRunCompleted(TaskRunRecord)`.
- A `TaskRunRecord` stopped by the user is marked `WasStopped` - purely AutoDev's own policy (the
  library itself has no such concept; a cancelled script is just `Failed` like any other) - tracked
  by recording that `StopRun` was actually called for that run before persisting its record. The
  Output tab applies this per script too, not just at the whole-task level: any script still
  `Failed` (i.e. hadn't reached `Completed`) when a `WasStopped` run ends shows "Stopped" instead of
  "Failed" (`ScriptPanelViewModel.ShowStopped`/`ShowFailed`, both fed by `ApplyFinal`'s own
  `wasStopped` parameter) - a script that had already completed keeps showing as succeeded either way.
- Completed runs persist via `IWorkspaceMetadataStore.AppendTaskRunAsync`, under
  `.autodev/local/task-runs/<sanitized task path>/<runId>.json`, as a `TaskRunRecord` - one
  `ScriptRunRecord` (`Name`/`Status`/`Log`) per script the document declared, or a `ParseError`
  string instead if the file never parsed at all.

`FilesSectionViewModel` is the main consumer of the start/complete events: it drives each file
tree node's "currently running" indicator (re-applied after every tree refresh, since a refresh
recreates node instances) and computes `HasRunningTasks`, which is forwarded all the way to
`GenerateTabViewModel.HasRunningTasks` - **a Claude turn can't start while any `.task` file is
running in the same workspace**, and vice versa (see
[Claude Integration](ClaudeIntegration.md#guards-against-racing-the-working-tree)), so scripted
automation and AI-driven edits never contend for the same working tree at once. `HasRunningTasks`
also reaches `WorkspaceContentViewModel` (forcing the Edit tab read-only, for *every* open file, not
just the one running) and `VersionSectionViewModel.IsInteractionBlocked` (disabling Commit/Merge/etc.
and every History tab action) - manual editing, task running, and AI working are mutually exclusive
states over one workspace's working tree, and only one of the three (with, for tasks, only one
`.task` file) is ever active at once.

## Output tab vs. Command tab

Two different, purpose-built consoles:

- **Output tab** (`OutputTabViewModel`) - a read-only, dropdown-switchable viewer over the
  scheduler above. `Entries` lists every task that's currently running or has ever run (seeded from
  persisted history); for the selected task, one `ScriptPanelViewModel` per concurrently running
  script shows its own Running/Waiting/Succeeded/Failed state (mirroring
  `Markwardt.TaskRunner.ScriptStatus` directly) and live output, auto-arranged in a square-ish grid.
  A live panel wraps and mirrors an actual `ScriptRunner`; a panel for a historical (already
  finished) run is built straight from its persisted `ScriptRunRecord` instead - either way the
  same `ApplyFinal`-populated view model, so the Output tab's own XAML doesn't need to care which.
  It can either watch a run live or browse the most recent historical run for a task that isn't
  currently running.
- **Command tab** (`CommandTabViewModel`) - a separate, general-purpose REPL-style shell console,
  entirely unrelated to `.task` files. Runs arbitrary one-off command lines rooted at a chosen
  working directory (defaulting to the workspace root; see its own "Set Command Context"/home
  controls) through the same `ICommandExecutor` backend, with output buffering and up/down history
  recall. This is the ad-hoc counterpart to the structured, file-defined `.task` automation above.

## Syntax highlighting (`TaskSyntaxColorizer`)

The Edit tab colors `.task` files with `TaskSyntaxColorizer`, an AvaloniaEdit
`DocumentColorizingTransformer` (attached to the editor's `TextView.LineTransformers` only while a
`.task` file is open, and re-run on every edit - see `EditTabView.axaml.cs`'s `UpdateLanguage`/
`OnEditorTextChanged`) rather than the older static XSHD-grammar approach: it re-parses the
document's indentation structure via `Markwardt.TaskRunner.IndentationParser` on every change and
colors each line only according to the structural role that parse actually assigns it (a
`var`/`script` keyword, an instruction label, a script/variable name, a `{Name}` insertion, a `#`
comment line) - never a plain regex/keyword-text match, so an instruction word appearing inside a
`run`'s shell body or a `file`'s content never gets miscolored as if it were a real instruction.
Ported from
Markwardt.TaskRunner's own `Runner` reference app (not part of the published library itself, since
it's an Avalonia-specific tool, but written entirely against the library's own public parsing
types) rather than duplicated from scratch.
