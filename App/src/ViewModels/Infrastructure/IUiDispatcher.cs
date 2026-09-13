namespace AutoDev.ViewModels.Infrastructure;

/// <summary>Seam so ViewModels can marshal work onto the UI thread without referencing Avalonia directly.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}
