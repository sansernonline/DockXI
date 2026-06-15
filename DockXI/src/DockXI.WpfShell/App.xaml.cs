using System;
using System.IO;
using System.Windows;
using DockXI.Contracts;
using DockXI.Diagnostics;
using DockXI.DockHost;
using DockXI.IconExtraction;
using DockXI.LaunchService;
using DockXI.Monitors;
using DockXI.Settings;
using DockXI.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DockXI.WpfShell;

public partial class App : Application
{
    private IHost? _host;

    public static IServiceProvider Services => ((App)Current)._host!.Services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DockXI", "logs");

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                // Storage — WpfStorageLocations overrides PackagedStorageLocations (MS DI: last wins).
                services.AddStorage();
                services.AddSingleton<IStorageLocations, WpfStorageLocations>();

                services.AddSettings();
                services.AddDockHost();
                services.AddIconExtraction();
                services.AddLaunchService();
                services.AddDockXILogging(logsFolder);

                // WPF-native bridges for Core abstractions that need a host-side implementation.
                services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
                services.AddMonitors();
                services.AddSingleton<IRevealZoneHost, WpfRevealZoneHost>();

                services.AddSingleton<MainDockWindow>();
            })
            .Build();

        // Bootstrap: load config from disk then push data into each domain store.
        var configStore      = _host.Services.GetRequiredService<ConfigStore>();
        var pinnedRepo       = _host.Services.GetRequiredService<PinnedItemRepository>();
        var dockConfigStore  = _host.Services.GetRequiredService<DockConfigStore>();
        var appSettingsStore = _host.Services.GetRequiredService<AppSettingsStore>();

        var doc = await configStore.LoadAsync();
        pinnedRepo.Initialize(doc.PinnedItems);
        dockConfigStore.Initialize(doc.DockConfig);
        appSettingsStore.Initialize(doc.AppSettings);

        // Wire snapshot source so every mutation triggers a debounced save.
        configStore.SetSnapshotSource(() => new DockConfigDocument
        {
            SchemaVersion   = 1,
            AppSettings     = appSettingsStore.Current,
            DockConfig      = dockConfigStore.Current,
            PinnedItems     = pinnedRepo.Snapshot(),
            MonitorBindings = [],
        });

        var window = _host.Services.GetRequiredService<MainDockWindow>();
        window.Show();

        await _host.StartAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            // Flush any pending debounced save before the process exits.
            var configStore = _host.Services.GetService<ConfigStore>();
            if (configStore is not null)
            {
                await configStore.FlushAsync();
            }

            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
