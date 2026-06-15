using DockXI.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace DockXI.LaunchService;

public static class ServiceExtensions
{
    public static IServiceCollection AddLaunchService(this IServiceCollection services)
    {
        services.AddSingleton<ILaunchService, LaunchService>();
        return services;
    }
}
