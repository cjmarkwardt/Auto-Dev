# Claude Integration

AutoDev doesn't use an SDK or HTTP API for Claude - it shells out to the `claude` CLI binary as a
subprocess and speaks its `stream-json` protocol over stdin/stdout. This keeps AutoDev itself thin:
authentication, tool execution, and conversation state are all the CLI's problem, not AutoDev's.

## `ClaudeCli/` - the subprocess bridge

| File | Role |
|---|---|
| `ClaudeSessionClient.cs` / `IClaudeSessionClient.cs` | One long-lived, multi-turn `claude` subprocess per Generate-tab session |
| `ClaudeUsageService.cs` | One-shot `/usage` queries for the header's rate-limit indicators |
| `ClaudeAuthService.cs` | `claude auth status` / `claude auth login` |
| `ClaudeCliLocator.cs` | Resolves the executable name (`"claude"`, PATH-based) |
| `ClaudeSystemPromptGuidance.cs` | A constant appended via `--append-system-prompt` to every session |
| `Models/` | `ClaudeStreamEvent`, `ContentBlock`, `UsageSnapshot`, `ImageAttachment`, ... |
| `Serialization/` | Hand-written polymorphic JSON reader/writer for the stream-json protocol |

### Process invocation

```
claude -p --input-format stream-json --output-format stream-json --verbose
       --model <sonnet|opus|haiku> [--effort <low|medium|high|xhigh|max>]
       --permission-mode bypassPermissions
       --disallowedTools Task
       --append-system-prompt "<ClaudeSystemPromptGuidance.Text>"
       --session-id <new guid>          (or --resume <existing session id>)
```

`--permission-mode bypassPermissions` runs Claude headless/unattended with every tool
auto-approved - there's no human in the loop to answer a permission prompt, so AutoDev's own system
prompt guidance tells Claude to use an isolated Xvfb display rather than the user's real one for
anything GUI-related.

**`--disallowedTools Task` keeps the whole session to exactly one process.** Without it,
`bypassPermissions` would let Claude spawn its own subagents via the `Task` tool - additional work
happening outside the one subprocess AutoDev is watching and reflecting in the UI. AutoDev's own
design is that exactly one live `claude` process does all the work for a session - every turn
(including any AI-assisted conflict-resolution turn - see
[Version Control](VersionControl.md#ai-assisted-conflict-resolution)) and every interjection (see
[Interjections](#interjections) below) is sent into that same subprocess, never a second one.
Disallowing `Task` at the process level is what makes "this session is exactly one process" hold
regardless of what Claude itself decides to do mid-turn, rather than just being true of AutoDev's
own code path.

### Streaming and parsing

`ReadOutputLoopAsync` reads stdout line by line, deserializes each as a `ClaudeStreamEvent` via a
hand-rolled `JsonConverter<T>` (the discriminator shape genuinely differs per event type - nested
under `subtype` for `"system"`, flat for `"assistant"`/`"result"`), and writes it to an unbounded
`System.Threading.Channels.Channel<ClaudeStreamEvent>`. Consumers read via
`IClaudeSessionClient.ReadAllEventsAsync()` (`IAsyncEnumerable`).

Recognized event types: `SystemInitEvent`, `AssistantMessageEvent` (holds `ContentBlock[]` -
text/tool_use/tool_result/image/unknown), `UserEchoEvent` (the CLI's own echo of tool results),
`ResultEvent` (final text, `TotalCostUsd`, per-model token usage), and a catch-all
`UnknownStreamEvent` so an unrecognized event type (e.g. a rate-limit notice) is ignored rather than
throwing.

Sending is one JSON object per line on stdin
(`ClaudeInputMessageWriter.UserMessage`/`UserMessageWithAttachments`), matching the Anthropic
Messages API's content-block shape.

`ClaudeUsageService` reuses the same protocol but as a one-shot buffered invocation: it pipes
`/usage` in as a user message to a fresh process, and regex-parses the CLI's plain-text usage
report (there's no structured JSON form of it) for the "Current session"/"Current week" percentage
and reset-time lines. `HeaderViewModel` polls this every 60 seconds for the header's
`Session X% / Week Y%` indicators (turning red at ≥90%) - unrelated to, and a separate service
from, `IUsageAggregatorService`, which instead rolls up cumulative token/cost usage across every
session/run this app instance has driven, for internal accounting.

## The Generate tab (`GenerateTabViewModel`)

### One session per branch

`SwitchSessionAsync(sessionKey)` is called whenever the workspace's targeted branch changes
(`sessionKey` is the branch id, or `null` while detached at a tag/commit - see
[Architecture](Architecture.md#read-only-editing-and-per-tab-layout)). Each session key has its
own independent conversation, request history, and unsent draft, all persisted under
`.autodev/local/` (`generate-sessions.json` maps session key → Claude session id, so switching
back later resumes the same conversation via `--resume`). Switching away disposes the live
subprocess for the outgoing session (if any) - nothing is lost, since everything is already
persisted to disk and to the CLI's own on-disk transcript.

### Turn lifecycle

Sending while idle starts a brand-new `GenerateRequestViewModel` (`Working` status, added to
`Requests`, capped at the last 10 per session), lazily starts the subprocess if this is the
session's first turn (`EnsureClientStarted`, a no-op if one's already running), and sends the
message.

As `AssistantMessageEvent`s stream in, text accumulates into a buffer AND live-replaces the
request's own `Output` with whichever text block arrived most recently
(`CaptureActiveRequestOutput`) - the output section shows Claude's latest words as they arrive,
each new segment replacing the last, rather than staying empty until the turn fully finishes. The
latest `tool_use` block separately updates `GenerateRequestViewModel.CurrentAction` with a friendly
one-liner (`DescribeToolUse`: "Reading Foo.cs", "Running: npm test", "Searching for \"...\"", etc.,
falling back to `"Using {ToolName}"` for anything unrecognized) for the status box above it.

On `ResultEvent`: `Output` is set from the event's own clean final text (preferred over the
streamed buffer, which mixes in intermediate narration like "let me check that" ahead of the real
reply), `Status → Completed` (or `Cancelled` if the user had clicked Cancel - see below), a ding
plays, and `NormalTurnCompleted` fires.

Three buttons cover stopping a turn, each doing something different:

- **Stop** (`StopAsync`) kills the subprocess outright rather than waiting for it to wind down,
  marks the request `Cancelled` with whatever partial output had streamed, and a fresh subprocess
  starts on the next Send. This is the old `CancelAsync` behavior, renamed - see Cancel below for
  what took its name over.
- **Cancel** (`CancelAsync`) doesn't kill anything - it sends Claude an ordinary interjection ("Stop
  what you're currently doing and revert any changes you've made so far during this turn.") and
  returns immediately, the same as any other message sent mid-turn (see Interjections below). The
  turn keeps running - still shown Working, whatever tool Claude reaches for next to actually
  revert things - until its own `ResultEvent` eventually arrives, at which point `Handle()` sees
  `_cancelRequested` and finalizes the request `Cancelled` instead of `Completed`. Lets Claude use
  its own tools to clean up properly instead of being cut off mid-edit.
- **Pause** (`PauseAsync`) kills the subprocess exactly like Stop, but captures the live session id
  first (`_client.SessionId`, persisted via `PersistSessionIdAsync` - the same mechanism
  `RestartClientForSettingsChange` uses for a model/effort change) and marks the request `Paused`
  instead of `Cancelled`/`Completed`. `NormalTurnCompleted` is deliberately **not** raised - the
  workspace stays exactly as locked as it was while genuinely working (see
  `VersionSectionViewModel.IsAiWorking`/`IsAiPaused`, driven by the new `TurnPaused`/`TurnResumed`
  events instead). Persisted immediately, so a Paused request survives an app restart -
  `SwitchSessionAsync`'s loader restores it as the active request again (rather than coercing it to
  `Cancelled` the way a stale `Working` status is) and re-raises `NormalTurnStarted`/`TurnPaused` to
  re-lock the workspace.
- **Resume** (`ResumeAsync`) flips the same request back to `Working`, starts a fresh subprocess
  resuming that captured session id, and sends "Continue from where you left off." - still the same
  request/turn no matter how many times it's paused and resumed, never a new `GenerateRequestViewModel`.

Closing the app or workspace mid-turn is treated exactly like an explicit Pause rather than losing
the turn to a silent Cancel, however it happens: `DisposeAsync` marks a still-`Working` active
request `Paused` and persists immediately (a normal clean close reaches this); an unclean
crash/kill never gets the chance, so the request is left sitting on disk exactly as `SendAsync`
first persisted it - `Working` - and `SwitchSessionAsync`'s loader coerces that stale `Working`
status to `Paused` on the next load instead (rather than `Cancelled`, which only ever applies to a
genuinely finished/stopped turn). Either way, reopening the workspace shows the same "AI is paused"
locked state Resume can pick back up, never a `Working` request nothing is actually working on.

### Recovering from a stream that ends without a `ResultEvent`

`ResultEvent` is the only thing that normally finishes a turn, so anything that stops the CLI's
stdout being read further - the process exiting/crashing, or a single stream-json line failing to
parse - used to leave a request stuck showing `Working` forever (`GenerateRequestViewModel.IsWorking`
reads the request's own `Status`, not `IsSending`), with a manual Stop the only way out. Two
things guard against that now:

- `ClaudeSessionClient`'s per-line parse catches *any* exception, not just `JsonException` - the
  hand-written converter uses `JsonElement.GetProperty` (throws `KeyNotFoundException`, not
  `JsonException`) on fields it expects to always be present, so an unexpected-but-valid-JSON line
  shape could otherwise escape a narrower catch and kill the read loop for the rest of the
  process's life - not just that one line.
- `ReadLoopAsync`'s tail always runs once the stream ends, success or failure alike:
  `FinalizeAbandonedTurn()` finalizes whatever turn was still in flight exactly like `StopAsync`
  would (flushes the streamed-text buffer into `Output`, marks the request `Cancelled`, resolves a
  pending `RunAutomatedTurnAsync` call with `false`) - a safe no-op if a `ResultEvent` already
  finished things normally. It's guarded by a reference-equality check against the current
  `_client`, since a stale loop's tail can be scheduled after `SwitchSessionAsync`/
  `RestartClientForSettingsChange` has already moved on to a brand-new client and turn.

### Interjections

`CanSend()` is deliberately **not** gated on `IsSending` - sending while a turn is already in
flight is treated as an *interjection*, not a new turn: the text is appended to the active
request's `Input` (visible immediately) and sent to the CLI as a plain follow-up message, which
Claude Code picks up alongside its next tool result rather than making the user wait for the
current turn to finish. It's sent as ordinary text, not the interactive CLI's `/btw` prefix - that
convention only exists in the interactive terminal and is rejected outright over the stream-json
protocol AutoDev actually drives the CLI with; a plain message sent mid-turn is picked up as an
interjection just as well with no special prefix needed.

Because an interjection can produce its own `ResultEvent` before Claude has actually gotten to
addressing it, `_pendingTurnCount` tracks how many outstanding messages (the initial send, plus one
per interjection) still need their own `ResultEvent` - the request is only marked `Completed`
(ding included) once that count reaches zero, so the ding/completion can't fire while Claude is
still about to keep working on an interjected follow-up.

The one thing an interjection can't do is start a second, independent Claude process - it's always
sent into the same live subprocess the active turn is already using (see
[Process invocation](#process-invocation) above, including why `--disallowedTools Task` also stops
Claude itself from spawning subagents mid-turn), so "one input → one process working" still holds;
only the strict "must fully finish before the next input" ordering is relaxed.

### Automated turns

`RunAutomatedTurnAsync(instruction, visible)` drives an exchange through the exact same live
session and subprocess as a normal turn (so it shares full conversational context), but awaits
completion instead of returning immediately - used by
[Version Control's conflict-resolution loop](VersionControl.md#ai-assisted-conflict-resolution).
It never creates or touches a request card; `visible: true` (the loop's case) captures the reply
into `LastAssistantText` instead, `visible: false` into `LastHiddenTurnText` (currently unused
infrastructure - no caller passes `false` today).

### Model and effort

```csharp
public IReadOnlyList<string> AvailableModels { get; } = ["sonnet", "opus", "haiku"];
public IReadOnlyList<string> AvailableEfforts { get; } = ["default", "low", "medium", "high", "xhigh", "max"];
```

These map straight to `claude --model`/`--effort` (`"default"` omits `--effort` entirely, leaving
the CLI's own default). Changing either mid-conversation tears down the live subprocess and starts
a fresh one on the next send - keeping the same session id, so `--resume` picks the conversation
back up under the new settings rather than restarting it. Both selectors are disabled while a turn
is in flight, for the same reason changing them wouldn't apply mid-response anyway.

Permission mode is **not** user-selectable - it's hardcoded to `bypassPermissions` in
`ClaudeSessionClient`, since there's no interactive terminal for a permission prompt to go to.

### Guards against racing the working tree

`IsVersionActionBusy` (mirrored from `VersionSectionViewModel.IsBusy`) and `HasRunningTasks`
(mirrored from `FilesSectionViewModel.HasRunningTasks`, itself driven by
[the task scheduler](TaskAutomation.md)) both gate `CanSend()`. The rationale is symmetric with why
an in-progress AI turn locks the Version section and Edit tab: a Claude turn's tool calls (file
edits, `git` commands via Bash) and a plain git action or a `.task` script both mutate the same
working tree, so only one of "the user", "a version action", "a task run", or "Claude" is ever
allowed to be actively changing it at a time.
