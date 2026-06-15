using Microsoft.Extensions.DependencyInjection;

namespace DockXI.Composition;

public static class ServiceExtensions
{
    public static IServiceCollection AddComposition(this IServiceCollection services)
    {
        // WPF shell uses XAML Storyboard animations — no WinUI 3 compositor needed.
        return services;
    }
}
