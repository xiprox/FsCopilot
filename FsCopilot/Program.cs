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

        // DIAGNOSTIC BUILD: Verbose from a plain double-click, and the level on every line.
        // The PR logs Debug unless --dev or --debug is given.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Console()
            .WriteTo.File(
                path: "log",
                rollingInterval: RollingInterval.Infinite,
                rollOnFileSizeLimit: false,
                shared: true,
                retainedFileCountLimit: null,
                fileSizeLimitBytes: null,
                outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                restrictedToMinimumLevel: LogEventLevel.Verbose
            )
            .CreateLogger();

        try
        {
            Log.Information("[Application] Loaded {Version} version", version);
            Log.Information("[Diag] DIAGNOSTIC BUILD {Info}; started {Utc:O} UTC, local offset {Offset}, machine {Machine}, user {User}, OS {Os}, args [{Args}]",
                Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                DateTime.UtcNow, TimeZoneInfo.Local.GetUtcOffset(DateTime.Now), Environment.MachineName,
                Environment.UserName, Environment.OSVersion, string.Join(" ", args));
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

    /// <summary>The value after <paramref name="flag"/>, or null when it is absent or last.</summary>
    private static string? Option(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static AppBuilder BuildAvaloniaApp(string[] args)
    {
        var isDev = args.Any(a => string.Equals(a, "--dev", StringComparison.OrdinalIgnoreCase));
        // var isExperimental = args.Any(a => string.Equals(a, "--experimental", StringComparison.OrdinalIgnoreCase));
        // A harness runs two instances on one machine: it needs a relay it can restart and
        // ids it can name beforehand. --relay also points the app at a self-hosted relay.
        var host = Option(args, "--relay") ?? "p2p.fscopilot.com";
        var peerId = Option(args, "--peer-id") ?? Random.String(8);
        var name = Environment.UserName;
        Log.Information("[Diag] Peer id {PeerId}, relay host {Host}, dev={Dev}", peerId, host, isDev);

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseReactiveUIWithMicrosoftDependencyResolver(
                services =>
                {
                    services.AddSingleton(new SimClient(!isDev ? "FS Copilot" : "FS Copilot DEV"));
                    var panelServer = new PanelServer();
                    services.AddSingleton(panelServer);
                    services.AddSingleton<SetupViewModel>();
                    services.AddSingleton(new Updater("http://p2p.fscopilot.com:2320"));
                    
                    if (!isDev)
                    {
                        services.AddSingleton<INetwork>(new HybridNetwork(host, peerId, name));
                        // services.AddSingleton<INetwork>(!isExperimental
                        //     ? new P2PNetwork("p2p.fscopilot.com", peerId, name)
                        //     : new HybridNetwork("p2p.fscopilot.com", peerId, name));
                        services.AddSingleton<MasterSwitch>();
                        services.AddSingleton<Coordinator>();
                        services.AddSingleton(sp => new MainViewModel(
                            peerId,
                            name,
                            sp.GetRequiredService<INetwork>(),
                            sp.GetRequiredService<SimClient>(),
                            sp.GetRequiredService<MasterSwitch>(),
                            sp.GetRequiredService<Coordinator>(),
                            sp.GetRequiredService<Updater>(),
                            sp.GetRequiredService<PanelServer>()
                        ));
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
