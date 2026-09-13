using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels.Dialogs;

public sealed record GitIdentityDialogResult(string Name, string Email);

/// <summary>Prompts for the name/email git needs to make a commit - shown by VersionSectionViewModel.RunBusyAsync the moment it finds neither is configured (see IGitService.HasUserIdentityConfiguredAsync), before the action that needed one ever actually runs.</summary>
public sealed partial class GitIdentityDialogViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _email = "";

    public bool CanConfirm => Name.Trim().Length > 0 && Email.Trim().Length > 0;

    public event Action<bool>? RequestClose;

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanConfirm));

    partial void OnEmailChanged(string value) => OnPropertyChanged(nameof(CanConfirm));

    [RelayCommand]
    private void Ok() => RequestClose?.Invoke(true);

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);
}
