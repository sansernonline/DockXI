using Windows.Graphics;

namespace DockXI.Contracts;

public interface IRevealZoneHost
{
    void Show(RectInt32 physicalRect);

    void Hide();

    event EventHandler PointerEntered;
}
