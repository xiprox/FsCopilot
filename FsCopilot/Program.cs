namespace FsCopilot;

using System.Globalization;
using System.Reflection;
using Audio;
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
    /// <summary>
    /// The relay and STUN host. The fork speaks relay protocol v2 (see <see cref="RelayNetwork"/>),
    /// which upstream's <c>p2p.fscopilot.com</c> does not, so this must be a relay built from this
    /// tree; <c>--relay host</c> overrides it for a local one.
    /// </summary>
    private const string RelayHost = "p2p.fscopilot.com";   // TODO: the fork's own relay, once deployed

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
        // Development switches for the network: `--relay host` to use a relay of your own (the
        // one-machine bed runs one on localhost), `--no-direct` to force every link through it.
        var relayHost = RelayHost;
        var relayArg = Array.FindIndex(args, a => string.Equals(a, "--relay", StringComparison.OrdinalIgnoreCase));
        if (relayArg >= 0 && relayArg + 1 < args.Length) relayHost = args[relayArg + 1];
        var direct = !args.Any(a => string.Equals(a, "--no-direct", StringComparison.OrdinalIgnoreCase));
        if (relayHost != RelayHost) Log.Warning("[Application] Relay host overridden: {Host}", relayHost);

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
                        services.AddSingleton<INetwork>(new HybridNetwork(relayHost, peerId, name, direct));
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
                        services.AddSingleton<AtcHost>();
                        services.AddSingleton<AtcReceiver>();
                        services.AddSingleton<ShareViewModel>();
                        services.AddSingleton(sp =>
                        {
                            sp.GetRequiredService<ShareSwitch>();
                            sp.GetRequiredService<TrafficHost>();
                            return new MainViewModel(
                                peerId,
                                name,
                                sp.GetRequiredService<INetwork>(),
                                sp.GetRequiredService<SimClient>(),
                                sp.GetRequiredService<MasterSwitch>(),
                                sp.GetRequiredService<Coordinator>(),
                                sp.GetRequiredService<Updater>(),
                                sp.GetRequiredService<ShareViewModel>()
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
