using Avalonia.Threading;
using AutoDev.ViewModels.Infrastructure;

namespace AutoDev.Infrastructure;

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
