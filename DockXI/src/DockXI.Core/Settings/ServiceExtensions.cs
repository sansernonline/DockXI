using DockXI.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace DockXI.Settings;

public static class ServiceExtensions
{
    public static IServiceCollection AddSettings(this IServiceCollection services)
    {
        services.AddSingleton<AppSettingsStore>();
        services.AddSingleton<IAppSettingsStore>(sp => sp.GetRequiredService<AppSettingsStore>());
        services.AddSingleton<DockConfigStore>();
        services.AddSingleton<IDockConfigStore>(sp => sp.GetRequiredService<DockConfigStore>());
        return services;
    }
}
