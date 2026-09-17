namespace FsCopilot.Exerciser;

using Avalonia;
using Serilog;

/*
 * The exerciser: the other pilot, on this machine, beside a running FS Copilot.
 *
 * It joins the app's session as a peer, so what it exercises is the shipping build's own
 * wire, session state machine and panel channel rather than a test mode inside the app.
 * Pointer forwarding is the first feature page; the next feature adds a page.
 *
 * Test-only, and not part of what the app ships: one project, dropped by dropping its
 * commit. FsCopilot's InternalsVisibleTo for this assembly goes with it.
 */
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Options.Current = Options.Parse(args);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File("exerciser-log", shared: true,
                outputTemplate: "[{Timestamp:HH:mm:ss.fff}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .StartWithClassicDesktopLifetime(args);
    }
}
