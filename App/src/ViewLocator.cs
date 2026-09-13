using Avalonia.Controls;
using Avalonia.Controls.Templates;
using AutoDev.ViewModels;

namespace AutoDev;

public sealed class ViewLocator : IDataTemplate
{
    public Control Build(object? param)
    {
        if (param is null)
        {
            return new TextBlock { Text = "(null view model)" };
        }

        string name = param.GetType().FullName!
            .Replace("AutoDev.ViewModels", "AutoDev.Views")
            .Replace("ViewModel", "View");

        Type? type = Type.GetType(name);
        if (type is not null && Activator.CreateInstance(type) is Control control)
        {
            return control;
        }

        return new TextBlock { Text = $"View not found: {name}" };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
