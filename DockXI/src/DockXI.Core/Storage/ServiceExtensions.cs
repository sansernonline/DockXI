using DockXI.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace DockXI.Storage;

public static class ServiceExtensions
{
    public static IServiceCollection AddStorage(this IServiceCollection services)
    {
        services.AddSingleton<IStorageLocations, PackagedStorageLocations>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<IConfigStore>(sp => sp.GetRequiredService<ConfigStore>());
        return services;
    }
}
