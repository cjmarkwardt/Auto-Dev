# API

AutoDev has no library API of its own to consume - it's a desktop application, and its primary
surface is the application window itself. This document covers the *design and flow* of that
surface - how the pieces fit together and the order things happen in - the same role this document
would play for a library's public types, but for the UI a user actually drives.

## Shape

A single top-level window (`MainWindow`) holds at most one open workspace at a time
(`MainShellViewModel.Workspace`). Opening a workspace replaces whatever was open before rather than
adding a tab for it; working on two workspaces side by side means launching a second, fully
independent AutoDev window (the title bar's own "new instance" button). Each open workspace owns
four pieces of UI, wired together but otherwise independent: a sidebar (git status/actions, plus
the file tree), an always-visible text/markdown/hex editor, a set of switchable content tabs
(Generate, History, Script, Command), and an `F1` file/content search overlay. There's no separate
"project" or "session" concept beyond the git repository itself - a workspace *is* a folder AutoDev
has opened, versioned or not.

## Flow

1. **Open a workspace** - a folder picker, a clone, or a recent-workspace entry all funnel through
   the same path: resolve/create the folder, initialize AutoDev's own per-workspace metadata
   folder, and hand off to git initialization if the folder isn't already a repo (a brand-new empty
   commit is created automatically, so there's always a valid branch to target from the start).
2. **Target a branch, tag, or commit** - the sidebar's Version section shows whatever's currently
   checked out; only a branch is ever editable, so switching to a tag or a detached commit puts the
   editor and every mutating action into a read-only, historical-snapshot state.
3. **Make a change** - either by hand in the editor (autosaved, debounced), or by describing it to
   the AI in the Generate tab, which drives a real `claude`/`codex` CLI subprocess against the
   working tree directly (the same files the editor and file tree show) rather than an isolated
   sandbox.
4. **Review and commit** - the sidebar shows pending changes at a glance; the History tab browses
   the branch/tag timeline and offers every mutating git action (rebase, merge, squash, ...) as a
   right-click menu, each targeting a specific point in that timeline rather than always "the
   current branch."
5. **Run or verify** - a `.cs` file can be run directly as a single-file script from the file tree,
   with its live output and exit status shown in the Script tab; the Command tab is a general-purpose
   shell console for anything else.

Manual editing, a running `.cs` script, an in-flight AI turn, and a git action in progress are
mutually exclusive over one workspace's working tree - only one of the four is ever actively
mutating it at a time, and the others lock accordingly for as long as it runs.

## Templates

A registered template (a single Markdown file describing a target layout/configuration) can be
applied to any open workspace, empty or not, from the title bar's Templates popup. Applying one
submits a real Generate request instructing the AI to scaffold (an empty workspace) or restructure
(a non-empty one) the workspace to conform - it progresses through the Generate tab exactly like a
typed message, and leaves the result as ordinary pending changes for the user to review and commit,
the same as any other AI-driven turn.
