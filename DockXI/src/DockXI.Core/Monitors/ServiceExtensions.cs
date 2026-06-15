using DockXI.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace DockXI.Monitors;

public static class ServiceExtensions
{
    // MonitorWatcher (WinUI Microsoft.UI.Windowing) was removed during the
    // WinUI → WPF migration. RevealZoneHost is supplied by the WPF shell
    // (WpfRevealZoneHost) so this registration is now empty — kept as a
    // no-op so callers don't need to be touched.
    public static IServiceCollection AddMonitors(this IServiceCollection services)
    {
        return services;
    }
}
