using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DockXI.Diagnostics;

public static class LoggingSetup
{
    public static IServiceCollection AddDockXILogging(
        this IServiceCollection services,
        string? logsFolder = null)
    {
        // Single InMemoryLogStore shared by the in-app Log Viewer UI.
        var memoryStore = new InMemoryLogStore();
        services.AddSingleton(memoryStore);

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddDebug();
            builder.AddProvider(new InMemoryLogProvider(memoryStore));
            if (!string.IsNullOrWhiteSpace(logsFolder))
            {
                builder.AddProvider(new FileLoggerProvider(logsFolder));
            }
        });
        return services;
    }
}
