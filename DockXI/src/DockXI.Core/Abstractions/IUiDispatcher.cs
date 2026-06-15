namespace DockXI.Contracts;

/// <summary>
/// Marshals a callback onto the UI thread. The WPF shell supplies
/// WpfUiDispatcher which wraps System.Windows.Threading.Dispatcher.
/// </summary>
public interface IUiDispatcher
{
    void Enqueue(Action action);
}
