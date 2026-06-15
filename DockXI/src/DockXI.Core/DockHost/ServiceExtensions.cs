using DockXI.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace DockXI.DockHost;

public static class ServiceExtensions
{
    public static IServiceCollection AddDockHost(this IServiceCollection services)
    {
        services.AddSingleton<IDockPositionService, DockPositionService>();
        services.AddSingleton<PinnedItemRepository>();
        services.AddSingleton<IPinnedItemRepository>(sp => sp.GetRequiredService<PinnedItemRepository>());
        services.AddSingleton<IShortcutResolver, ShellLinkResolver>();
        return services;
    }
}
