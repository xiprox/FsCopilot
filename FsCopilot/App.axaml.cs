namespace FsCopilot;

using System.Reflection;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Connection;
using Network;
using Simulation;
using Splat;
using ViewModels;
using Views;

public class App : Application
{
    private readonly CancellationTokenSource _appCts = new();

    // How long the way out waits for the peer departure to reach the wire. Bounded like
    // PanelServer.ShutdownGrace and for the same reason: a wedged socket must not hold the
    // window open. On a working link the departure leaves well inside this.
    private static readonly TimeSpan DisconnectGrace = TimeSpan.FromMilliseconds(500);
    
    public static readonly string Version =
        Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+')[0] ?? "unknown";
    
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) =>
            {
                _appCts.Cancel();

                // Before the sockets drop: tell pointer-synced panels this was a quit, not
                // a fault. They cannot tell from the close alone and would warn the pilot.
                Locator.Current.GetService<PanelServer>()?.Shutdown();

                // Same shape as the goodbye above, for the same reason: the departure is
                // queued, and the peer only learns this was a quit rather than an outage if
                // the process lives long enough to send it.
                var net = Locator.Current.GetService<INetwork>();
                net?.Disconnect();
                net?.DrainDisconnect(DisconnectGrace);

                Locator.Current.GetService<MasterSwitch>()?.TakeControl();
            };
            
            var args = desktop.Args ?? [];
            var dev = args.Contains("--dev", StringComparer.OrdinalIgnoreCase);
            var skipInstall = args.Contains("--skip-install", StringComparer.OrdinalIgnoreCase);
            
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();

            if (!skipInstall && Installer.RequiresInstallation)
            {
                var vm = Locator.Current.GetService<SetupViewModel>()!;
                var setup = new SetupWindow { DataContext = vm };

                vm.Completed += () =>
                {
                    CreateWindow(desktop, dev);
                    setup.Close();
                };

                desktop.MainWindow = setup;
            }
            else
            {
                CreateWindow(desktop, dev);
                _ = CheckForUpdatesAsync(Locator.Current.GetService<Updater>()!, _appCts.Token);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void CreateWindow(IClassicDesktopStyleApplicationLifetime desktop, bool dev)
    {
        var window = desktop.MainWindow = !dev 
            ? new MainWindow { DataContext = Locator.Current.GetService<MainViewModel>() }
            : new DevelopWindow { DataContext = Locator.Current.GetService<DevelopViewModel>() };
        window.Show();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }

    private async Task CheckForUpdatesAsync(Updater updater, CancellationToken ct)
    {
        var release = await updater.CheckForUpdateAsync(Version, ct);
        if (release is null)
            return;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: not null } desktop)
        {
            var dialog = new UpdateAvailableWindow(new UpdateAvailableViewModel(Version, release.TagName, release.HtmlUrl));

            await dialog.ShowDialog(desktop.MainWindow);
        }
    }
}