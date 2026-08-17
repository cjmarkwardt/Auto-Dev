using AutoDev.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels.Dialogs;

public sealed record CreateTagDialogResult(string FullName, string Id);

public sealed partial class CreateTagDialogViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _fullName = "";

    [ObservableProperty]
    private string _id = "";

    /// <summary>True until the user directly edits the Id box themselves (see CreateTagDialogWindow's code-behind, which flips this via MarkIdManuallyEdited on the Id TextBox's user-driven text change) - while true, OnFullNameChanged keeps re-deriving Id from FullName so typing a full name alone is enough for the common case. Mirrors CreateBranchDialogViewModel's identical Name/Id relationship.</summary>
    private bool _idFollowsFullName = true;

    partial void OnFullNameChanged(string value)
    {
        if (_idFollowsFullName)
        {
            Id = BranchConvention.Slugify(value);
        }

        OnPropertyChanged(nameof(CanConfirm));
    }

    partial void OnIdChanged(string value) => OnPropertyChanged(nameof(CanConfirm));

    public void MarkIdManuallyEdited() => _idFollowsFullName = false;

    public bool CanConfirm => FullName.Trim().Length > 0 && Id.Trim().Length > 0;

    public event Action<bool>? RequestClose;

    [RelayCommand]
    private void Ok() => RequestClose?.Invoke(true);

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);
}
