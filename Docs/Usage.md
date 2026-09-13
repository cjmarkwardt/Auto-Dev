# Usage

Walkthroughs of AutoDev in different situations, in place of runnable code samples - there's no
programmatic API to call, just the application itself.

## Open a workspace and make a first AI-driven change

1. From the empty state, click **Open** and pick a folder (any folder - if it isn't already a git
   repo, AutoDev initializes one transparently with an empty first commit) or **Clone** a remote
   URL.
2. Type a request into the Generate tab's input box and send it. AutoDev drives whichever AI CLI
   (Claude Code or Codex) is currently selected as a subprocess against the workspace's real files -
   the same ones the file tree and editor show - so its edits appear immediately as pending changes.
3. Once the turn finishes, review what changed (the sidebar highlights pending changes; opening a
   file shows the edit directly) and commit from the Version section's menu.

## Run a `.cs` file as a script

Right-click a `.cs` file in the tree and choose **Run** (or double-click it). Output streams live
into the Script tab exactly like a real terminal, including any prompts the script itself writes to
stdin - the Script tab's own input box sends a line back to the running process. The dropdown at the
top switches between every script that's currently running or has run before, each with its own
Running/Succeeded/Stopped/Failed status.

## Resolve a merge conflict without leaving the app

Triggering a Rebase, Merge, or a pull that lands on top of stashed local changes can produce a real
git conflict. Rather than surfacing raw conflict markers for the user to resolve by hand, AutoDev
switches to the Generate tab automatically and drives the same AI session through resolving it
(editing the conflicted files, removing the markers, staging the result) - retrying automatically
if the first attempt doesn't fully resolve it, up to a bounded number of attempts, and leaving
everything exactly as a failed attempt found it if it still can't recover.

## Apply a template

Open the title bar's Templates popup, register a template file (any Markdown file describing a
target layout/configuration) if it isn't already, and click its Apply button. This submits a
Generate request - visible in the Generate tab like any other - instructing the AI to scaffold an
empty workspace's initial structure, or restructure an already-populated one to conform, moving/
renaming/rewriting whatever's there as needed rather than only adding alongside it. The result is
left as ordinary pending changes to review before committing, exactly like a typed request would be.
