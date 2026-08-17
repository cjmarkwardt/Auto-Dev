# Task Automation

Alongside AI-driven changes, AutoDev has a small, file-defined scripting system for repeatable
scripted work (build, publish, run tests, ...) - `.task` files, shown in the file tree colored
distinctly and offered `Run`/`Stop`/`View` instead of (well, alongside) the normal file context
menu. See `examples/example.task` for a real sample.

## The `.task` DSL

A hand-written, indentation-sensitive, Python-like syntax (no `end` keyword):

```
var BUILD_DIR = build

script Build
    folder %BUILD_DIR% conditional
    cd %BUILD_DIR%
    run echo "Building in $(pwd)"
    wait 1
    run
        echo "Step 1"
        echo "Step 2"
    print Build finished.

script Watch For Changes
    output 2 1
    run echo "watching…"
```

- `var NAME = value` - top-level variables, substituted wherever `%NAME%` appears elsewhere in the
  file.
- `script <name>` - one script block. **Every script in a file runs concurrently** with every
  other script in that same file (they all start together; the whole `.task` run only finishes
  once every script has); commands *within* one script run sequentially and that script stops at
  its first failing command.
- Commands: `run <cmd>` (or a bare `run` with an indented multi-line shell body), `print <text>`
  (no process spawned), `wait <seconds>`, `move`/`rename`/`file`/`folder`/`delete`/`purge` (direct
  filesystem operations, each with optional `overwrite`/`conditional` modifiers), `cd <path>`
  (that script's own working directory).
- `output <column> <row>` (1-based) pins a script's live output panel to a specific cell in the
  Output tab's grid instead of letting it auto-arrange.

`Core/Services/TaskFileParser.Parse` turns raw text into an unresolved `TaskDocument` (variables
and commands still containing literal `%VAR%` text) - pure syntax parsing, throwing a
`FormatException` with a line number on any error. A separate resolution pass substitutes
variables and validates the result before it's ever executed.

## Execution (`ScriptTaskRunner`)

`Core/Services/ScriptTaskRunner.RunAsync` runs every `TaskScript` in a resolved `TaskDocument`
concurrently (`Task.WhenAll`), executing each script's commands sequentially and halting that one
script on its first failure. Filesystem commands (`Create`/`Move`/`Rename`/`Delete`/`Purge`) run
directly against `File`/`Directory` APIs in-process; `run` commands go through the same
`ICommandExecutor` shell backend the Command tab uses (`/bin/sh -c` via `CliWrap`), and a nonzero
exit code fails that command. Every step is reported through an `IProgress<ScriptOutputLine>`
callback before it runs (`"$ Run: ..."`-style trace lines), and a `ScriptBlockResult` fires once
each script finishes.

## Scheduling and concurrency (`IWorkspaceTaskScheduler`)

One `WorkspaceTaskSchedulerService` per workspace (via `ITaskSchedulerServiceFactory`, part of the
per-tab isolation described in [Architecture](Architecture.md#dependency-injection)). It's purely a
manual-trigger tracker/broadcaster - no polling loop:

- `_activeRuns` (a `ConcurrentDictionary` used as a set) prevents double-starting the *same*
  `.task` file (`RunNowAsync`'s `TryAdd` guard); different `.task` files run fully concurrently,
  each with its own linked `CancellationTokenSource` so `StopRun(taskId)` only cancels that one
  run.
- Four events surface everything: `TaskRunStarted(TaskRef)`, `TaskRunCompleted(TaskRunRecord)`,
  `ScriptTaskProgress(taskPath, blockName, line)` (one output line at a time),
  `ScriptBlockCompleted(taskPath, ScriptBlockRunRecord)`.
- Completed runs persist via `IWorkspaceMetadataStore.AppendTaskRunAsync`, under
  `.autodev/local/task-runs/<sanitized task path>/<runId>.json`.

`FilesSectionViewModel` is the main consumer of the start/complete events: it drives each file
tree node's "currently running" indicator (re-applied after every tree refresh, since a refresh
recreates node instances) and computes `HasRunningTasks`, which is forwarded all the way to
`GenerateTabViewModel.HasRunningTasks` - **a Claude turn can't start while any `.task` file is
running in the same workspace**, and vice versa (see
[Claude Integration](ClaudeIntegration.md#guards-against-racing-the-working-tree)), so scripted
automation and AI-driven edits never contend for the same working tree at once.

## Output tab vs. Command tab

Two different, purpose-built consoles:

- **Output tab** (`OutputTabViewModel`) - a read-only, dropdown-switchable viewer over the
  scheduler above. `Entries` lists every task that's currently running or has ever run (seeded from
  persisted history); for the selected task, one `ScriptBlockPanelViewModel` per concurrently
  running script shows its own Running/Succeeded/Failed/Stopped state and live output, arranged in
  a grid that honors each script's `output <col> <row>` pin. It can either watch a run live or
  browse the most recent historical run for a task that isn't currently running.
- **Command tab** (`CommandTabViewModel`) - a separate, general-purpose REPL-style shell console,
  entirely unrelated to `.task` files. Runs arbitrary one-off command lines rooted at the workspace
  directory through the same `ICommandExecutor` backend, with output buffering and up/down history
  recall. This is the ad-hoc counterpart to the structured, file-defined `.task` automation above.
