using DockXI.Contracts;
using Windows.Graphics;

namespace DockXI.Monitors;

internal sealed class RevealZoneHostStub : IRevealZoneHost
{
    public event EventHandler? PointerEntered { add { } remove { } }

    public void Show(RectInt32 physicalRect)
    {
        throw new NotImplementedException("RevealZoneHostStub — implemented in M4.");
    }

    public void Hide()
    {
        throw new NotImplementedException("RevealZoneHostStub — implemented in M4.");
    }
}
