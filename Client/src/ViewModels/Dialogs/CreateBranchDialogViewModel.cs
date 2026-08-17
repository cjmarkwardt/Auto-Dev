using AutoDev.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels.Dialogs;

public sealed record CreateBranchDialogResult(string Name, string Id, bool IsPublic);

public sealed partial class CreateBranchDialogViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _id = "";

    /// <summary>Off by default (private) - a private branch is meant for one user at a time and gets squashed/renamed away once merged. A public branch skips Squash/Rename entirely, keeping its full commit log forever instead of collapsing it into a single base commit, since other users may be relying on its history.</summary>
    [ObservableProperty]
    private bool _isPublic;

    /// <summary>True until the user directly edits the Id box themselves (see CreateBranchDialogWindow's code-behind, which flips this via MarkIdManuallyEdited on the Id TextBox's user-driven text change) - while true, OnNameChanged keeps re-deriving Id from Name so typing a name alone is enough for the common case.</summary>
    private bool _idFollowsName = true;

    partial void OnNameChanged(string value)
    {
        if (_idFollowsName)
        {
            Id = BranchConvention.Slugify(value);
        }

        OnPropertyChanged(nameof(CanConfirm));
    }

    partial void OnIdChanged(string value) => OnPropertyChanged(nameof(CanConfirm));

    public void MarkIdManuallyEdited() => _idFollowsName = false;

    public bool CanConfirm => Name.Trim().Length > 0 && Id.Trim().Length > 0;

    public event Action<bool>? RequestClose;

    [RelayCommand]
    private void Ok() => RequestClose?.Invoke(true);

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);
}
