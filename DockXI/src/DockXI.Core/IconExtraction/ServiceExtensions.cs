using DockXI.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace DockXI.IconExtraction;

public static class ServiceExtensions
{
    public static IServiceCollection AddIconExtraction(this IServiceCollection services)
    {
        services.AddSingleton<IIconExtractor, IconExtractor>();
        return services;
    }
}
