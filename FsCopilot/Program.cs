namespace FsCopilot;

using System.Globalization;
using System.Reflection;
using Connection;
using Microsoft.Extensions.DependencyInjection;
using Network;
using ReactiveUI.Avalonia;
using ReactiveUI.Avalonia.Splat;
using Serilog;
using Serilog.Events;
using Simulation;
using ViewModels;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        
        var isDev = args.Any(a => string.Equals(a, "--dev", StringComparison.OrdinalIgnoreCase));
        var isDebug = args.Any(a => string.Equals(a, "--debug", StringComparison.OrdinalIgnoreCase));
        var version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+')[0] ?? "unknown";

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(
                path: "log",
                rollingInterval: RollingInterval.Infinite,
                rollOnFileSizeLimit: false,
                shared: true,
                retainedFileCountLimit: null,
                fileSizeLimitBytes: null,
                outputTemplate: "[{Timestamp:HH:mm:ss.fff}] {Message:lj}{NewLine}{Exception}",
                restrictedToMinimumLevel: isDev || isDebug ? LogEventLevel.Verbose : LogEventLevel.Debug
            )
            .CreateLogger();

        try
        {
            Log.Information("[Application] Loaded {Version} version", version);
            BuildAvaloniaApp(args).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Application] Something went wrong");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp([]);

    public static AppBuilder BuildAvaloniaApp(string[] args)
    {
        var isDev = args.Any(a => string.Equals(a, "--dev", StringComparison.OrdinalIgnoreCase));
        // var isExperimental = args.Any(a => string.Equals(a, "--experimental", StringComparison.OrdinalIgnoreCase));
        var peerId = Random.String(8);
        var name = Environment.UserName;
        var trafficOptions = TrafficOptions.Parse(args);

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseReactiveUIWithMicrosoftDependencyResolver(
                services =>
                {
                    services.AddSingleton(Settings.Load());
                    services.AddSingleton(trafficOptions);
                    services.AddSingleton(new SimClient(!isDev ? "FS Copilot" : "FS Copilot DEV"));
                    services.AddSingleton(new SimTraffic(!isDev ? "FS Copilot (Traffic)" : "FS Copilot DEV (Traffic)"));
                    services.AddSingleton<SetupViewModel>();
                    services.AddSingleton(new Updater("http://p2p.fscopilot.com:2320"));
                    
                    if (!isDev)
                    {
                        services.AddSingleton<INetwork>(new HybridNetwork("p2p.fscopilot.com", peerId, name));
                        // services.AddSingleton<INetwork>(!isExperimental
                        //     ? new P2PNetwork("p2p.fscopilot.com", peerId, name)
                        //     : new HybridNetwork("p2p.fscopilot.com", peerId, name));
                        services.AddSingleton<MasterSwitch>();
                        services.AddSingleton<Coordinator>();
                        // Registers the sharing packets; constructed after Coordinator so the
                        // packet table is the same on every peer.
                        services.AddSingleton(sp =>
                        {
                            sp.GetRequiredService<Coordinator>();
                            return new ShareSwitch(peerId, sp.GetRequiredService<INetwork>());
                        });
                        services.AddSingleton<TrafficReceiver>();
                        services.AddSingleton<TrafficHost>();
                        services.AddSingleton(sp =>
                        {
                            var share = sp.GetRequiredService<ShareSwitch>();
                            sp.GetRequiredService<TrafficHost>();
                            // Until the sharing card exists: host traffic from the persisted
                            // setting, for as long as the traffic connection is up.
                            if (sp.GetRequiredService<Settings>().ShareTraffic)
                                sp.GetRequiredService<SimTraffic>().Connected
                                    .DistinctUntilChanged()
                                    .Subscribe(connected => share.Request(ShareSwitch.Feature.Traffic, connected));
                            return new MainViewModel(
                                peerId,
                                name,
                                sp.GetRequiredService<INetwork>(),
                                sp.GetRequiredService<SimClient>(),
                                sp.GetRequiredService<MasterSwitch>(),
                                sp.GetRequiredService<Coordinator>(),
                                sp.GetRequiredService<Updater>()
                            );
                        });
                    }
                    else
                    {
                        services.AddSingleton<DevelopViewModel>();
                    }
                },
                null)
            .RegisterReactiveUIViewsFromEntryAssembly()
            .WithInterFont()
            .LogToTrace();
    }
}
