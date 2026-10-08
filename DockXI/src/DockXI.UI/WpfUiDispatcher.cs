using System.Windows;
using DockXI.Contracts;

namespace DockXI.UI;

internal sealed class WpfUiDispatcher : IUiDispatcher
{
    public void Enqueue(Action action) =>
        Application.Current.Dispatcher.BeginInvoke(action);
}
