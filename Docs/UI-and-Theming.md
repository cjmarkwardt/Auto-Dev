# UI & Theming

## Dialogs

`ViewModels/Infrastructure/IDialogService` is the seam that keeps every other view model
Avalonia-free - anything needing a native window goes through it:

```csharp
Task<string?> PickFolderAsync();
Task<string?> ShowInputDialogAsync(string title, string label, string initialValue = "", bool requireValue = false);
Task<bool> ShowConfirmDialogAsync(string title, string message, string confirmLabel = "Delete", bool isDestructive = true);
Task<CreateTagDialogResult?> ShowCreateTagDialogAsync();
Task<SquashDialogResult?> ShowSquashDialogAsync(IReadOnlyList<string> branches, Func<string, Task<string>> defaultMessageProvider);
Task<RebaseDialogResult?> ShowRebaseDialogAsync(IReadOnlyList<string> branches, Func<string, Task<string>> defaultMessageProvider);
Task<MergeDialogResult?> ShowMergeDialogAsync(IReadOnlyList<string> branches, Func<string, Task<string>> defaultMessageProvider);
Task ShowMessageDialogAsync(string title, string message);
```

`Infrastructure/AvaloniaDialogService` implements it: each `Show*DialogAsync` builds the matching
`ViewModels/Dialogs/*ViewModel`, wraps it in the matching `Views/Dialogs/*Window`, sets
`DataContext`, and awaits `window.ShowDialog<T>(OwnerWindow)`.

Seven dialog view models cover every modal in the app:

- **`InputDialogViewModel`** - single text field. `RequireValue: true` hides Cancel and blocks the
  native close button/Escape entirely, so confirming a non-blank value is the only way out (used
  e.g. for naming a new file, or a new branch's name - see VersionSectionViewModel.BranchAsync -
  where blank isn't a meaningful answer).
- **`ConfirmDialogViewModel`** - yes/no, with a configurable `ConfirmLabel` (default `"Delete"`)
  and `IsDestructive` (default `true`, drives red-button styling).
- **`CreateTagDialogViewModel`** - `FullName`, `Id`. `Id` auto-derives from `FullName` via this
  view model's own `Slugify` helper until the user hand-edits it, at which point it stops following
  (`MarkIdManuallyEdited`, called from the view's code-behind on the first manual keystroke into
  the Id field).
- **`SquashDialogViewModel`** - a base-branch `ComboBox` (`Branches`/`SelectedBranch`) plus a
  `Message` field whose default is re-fetched (`messageProvider`, an async delegate the caller
  supplies) every time the selected branch changes - see VersionSectionViewModel.SquashAsync.
- **`RebaseDialogViewModel`** - the same shape as `SquashDialogViewModel` (an onto-branch picker
  plus a `SquashMessage` field with the same re-fetched default) - a rebase always squashes first,
  so there's no separate toggle - see VersionSectionViewModel.RebaseAsync.
- **`MergeDialogViewModel`** - same shape again (a target-branch picker plus a message field), for
  Merge's own conditional squash-if-more-than-one-commit - see VersionSectionViewModel.MergeAsync.
- **`MessageDialogViewModel`** - `Title`, `Message`, a single OK button - the popup a failed git
  action shows instead of a persistent inline label (see VersionSectionViewModel/
  HistoryTabViewModel, which route every failure message through
  `IDialogService.ShowMessageDialogAsync` now).

All seven follow the same shape: `[ObservableProperty]` fields (`MessageDialogViewModel`'s
`Title`/`Message` are plain `required init` properties instead, since it has nothing to edit),
`event Action<bool>? RequestClose` (`Action?` with no bool for `MessageDialogViewModel` - there's
only one way to close it), and a Confirm/Cancel `[RelayCommand]` pair (`MessageDialogViewModel` has
only the one `OkCommand`).

`Infrastructure/DialogWindowExtensions.DisableMinimize` sets `CanMinimize = false` on these small
modal windows - a minimized owned dialog would otherwise leave the app with nothing clickable to
bring it back.

## Theming

Merged into `App.axaml` in this order:

```xml
<ResourceInclude Source="avares://AutoDev/Styles/VsCodeColors.axaml" />
<ResourceInclude Source="avares://AutoDev/Styles/Icons.axaml" />
...
<StyleInclude Source="avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml" />
<StyleInclude Source="avares://AutoDev/Styles/ControlStyles.axaml" />
```

Split by concern rather than one monolithic file:

- **`VsCodeColors.axaml`** - a VS Code Dark+-derived palette, exposed purely as named
  `SolidColorBrush` resources: `EditorBackgroundBrush`, `SideBarBackgroundBrush`,
  `ActivityBarBackgroundBrush`, `HeaderBackgroundBrush`, `TabActiveBackgroundBrush`/
  `TabInactiveBackgroundBrush`, `AccentBrush`/`AccentHoverBrush`/`TabActiveAccentBrush`,
  `TaskFileBrush` (only accented while that `.task` file is actively running),
  `TextPrimaryBrush`/`TextMutedBrush`, `BorderSubtleBrush`, `HoverBackgroundBrush`,
  `SelectionBackgroundBrush`, `DangerBrush`, `UsageCriticalBrush`, `SuccessBrush`,
  `ChatFinalTextBrush` (deliberately its own key, not a reuse of `AccentBrush`, so repointing one
  doesn't recolor the other). Also defines a raw `SystemAccentColor` `Color` (not a brush), which
  Avalonia's `FluentTheme` needs to derive its own accent palette.
- **`Icons.axaml`** - flat monochrome line-icon `Geometry` resources only (no brushes/styles) -
  `FileIconGeometry`, `FolderIconGeometry`, `CloneIconGeometry`, `EditIconGeometry`, window-chrome
  icons, etc. - each bounding-box-normalized so `Stretch="Uniform"` scales every icon consistently
  through a shared-size `Path`.
- **`ControlStyles.axaml`** - the actual `Style Selector="..."` rules restyling built-in Avalonia
  controls: `Window`, `TextBlock`/`SelectableTextBlock`, `Button` (plus `.accent`/`.danger`/
  `.iconButton`/`.active`/`.sidebarAction`/`.sidebarHeading` classes), `ToggleButton`,
  `TabControl`/`TabItem` (including the `PART_SelectedPipe` underline), `TreeViewItem`, `TextBox`,
  `ListBox`, and a few semantic classes like `TextBlock.usageText.critical` and
  `TextBlock.tabTitle.aiWorking`.

**Convention**: views reference brushes and icons exclusively by resource key
(`{DynamicResource TextMutedBrush}`, `{StaticResource FolderIconGeometry}`) - there are no literal
hex colors or inline path geometry scattered through `.axaml` files. Follow the same pattern for
anything new: add a named resource to the relevant `Styles/*.axaml` file rather than hardcoding a
value in a view.

## Converters

`Converters/EnumEqualsConverter.cs` is currently the only one: a singleton `IValueConverter`
comparing a bound enum's `ToString()` against `ConverterParameter` (case-insensitive) to produce a
bool, used for "which of several mutually-exclusive sections should show" `IsVisible` bindings.
One-way only.
