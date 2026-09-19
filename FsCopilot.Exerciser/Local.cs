namespace FsCopilot.Exerciser;

using System.Diagnostics;
using System.Net.NetworkInformation;

/// <summary>
/// The two processes the exerciser would otherwise ask the pilot to start by hand, and the
/// reason it can: both are built from this solution, beside it on disk.
///
/// The rendezvous first. Peers find each other through one, and on one machine a local one
/// costs nothing while the public one costs its round trip twice - 145 ms each way through
/// p2p.fscopilot.com, which is visible in a recording. It is a separate process rather than
/// hosted here: FsCopilot.Discovery is an ASP.NET application, and an Avalonia test tool has
/// no business carrying that framework.
///
/// Then FS Copilot itself, launched with the rendezvous this window is set to, because those
/// two settings have to agree and nothing else checks that. Given --peer-id, the session
/// code is known before the app starts and the exerciser can join without anyone copying
/// anything - the flag is test-only, so a build without it just means typing the code.
/// </summary>
public static class Local
{
    private static Process? _relay;

    /// <summary>The solution directory: the nearest ancestor holding FsCopilot.sln. Found
    /// rather than counted, because how deep this assembly sits under its project is not fixed -
    /// Platforms>x64 adds a platform folder above the configuration, so this build lives at
    /// bin/x64/Debug/net9.0/win-x64 and a build without it one level higher.</summary>
    private static string Solution
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FsCopilot.sln"))) dir = dir.Parent;
            return dir?.FullName ?? AppContext.BaseDirectory;
        }
    }

    /// <summary>The newest <paramref name="file"/> anywhere under that project's bin tree, or
    /// null. Searched for the same reason: the runtime identifier is in the path and differs per
    /// project - FsCopilot is win-x64, FsCopilot.Discovery is pinned linux-x64 because that is
    /// what the server runs - so spelling the path out gets it wrong for one of them.</summary>
    private static string? Built(string project, string file)
    {
        var bin = Path.Combine(Solution, project, "bin");
        if (!Directory.Exists(bin)) return null;
        try
        {
            return Directory.EnumerateFiles(bin, file, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception) { return null; }
    }

    /// <summary>Falls back to where a Debug build would put it, so a missing build is reported
    /// as a path the pilot can go and look at rather than as nothing.</summary>
    public static string FsCopilotExe =>
        Built("FsCopilot", "FsCopilot.exe")
        ?? Path.Combine(Solution, "FsCopilot", "bin", "Debug", "net9.0", "win-x64", "FsCopilot.exe");

    /// <summary>The rendezvous is up if this window started it, or if something else already
    /// holds its port - a leftover from a previous run, or one started in a terminal. Trying
    /// to start a second is how the log filled with a bind failure.</summary>
    public static bool RelayRunning => _relay is { HasExited: false } || PortHeld(3600);

    private static bool PortHeld(int port)
    {
        try
        {
            foreach (var endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners())
                if (endpoint.Port == port) return true;
        }
        catch (Exception) { /* nothing to learn from a failed enumeration */ }
        return false;
    }

    /// <summary>Starts the rendezvous if it is not already up, and waits for it to say so.
    /// Returns what to tell the pilot, or null when it is running.</summary>
    public static async Task<string?> StartRelay(Action<string> log)
    {
        if (_relay is { HasExited: false }) return null;
        if (PortHeld(3600))
        {
            log("relay: already listening on 3600; using it");
            return null;
        }

        // Building the exerciser builds a win-x64 rendezvous beside the linux one - see the
        // ProjectReference. Prefer that apphost; fall back to the runtime only for an assembly
        // that is framework-dependent, because a self-contained build for another platform
        // cannot be started here at all.
        var apphost = Built("FsCopilot.Discovery", "p2p_serv.exe");
        var portable = Runnable(Built("FsCopilot.Discovery", "p2p_serv.dll"));
        string exe, arguments;
        if (apphost != null) { exe = apphost; arguments = ""; }
        else if (portable != null) { exe = "dotnet"; arguments = $"\"{portable}\""; }
        else return "no rendezvous for this machine: dotnet build FsCopilot.Discovery " +
                    "-r win-x64 --self-contained false";

        var started = new TaskCompletionSource();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe, arguments)
            {
                WorkingDirectory = Path.GetDirectoryName(apphost ?? portable)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        // The HTTP side is only there to say "I'm fine" and is pinned high, so it cannot
        // collide with whatever else the developer is running.
        process.StartInfo.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:5399";
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (e.Data.Contains("UDP port: 3600")) started.TrySetResult();
            var line = Tidy(e.Data);
            if (line != null) log($"relay: {line}");
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) log($"relay: {Tidy(e.Data) ?? e.Data.Trim()}"); };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception e)
        {
            return $"could not start the rendezvous: {e.Message}";
        }

        _relay = process;
        var up = await Task.WhenAny(started.Task, Task.Delay(20000)) == started.Task;
        return up ? null : "the rendezvous did not come up; the port may be taken";
    }

    /// <summary>The assembly back, if the runtime here can start it: a runtimeconfig naming
    /// includedFrameworks is a self-contained build, and self-contained means built for one
    /// platform's host. The linux-x64 output fails with "hostpolicy.dll not found", which reads
    /// as a broken install rather than as the wrong platform.</summary>
    private static string? Runnable(string? dll)
    {
        if (dll == null) return null;
        try
        {
            var config = Path.ChangeExtension(dll, ".runtimeconfig.json");
            return File.Exists(config) && File.ReadAllText(config).Contains("includedFrameworks")
                ? null : dll;
        }
        catch (Exception) { return null; }
    }

    /// <summary>What is worth reading from the rendezvous. Its own format is
    /// "[time INF] Category   message", it repeats a STUN introduction every twenty
    /// seconds, and a CONNECT carries the peer's whole schema hash - 700 characters that
    /// say nothing a reader of this log needs.</summary>
    private static string? Tidy(string raw)
    {
        var line = raw.Trim();
        // A stack frame says where a failure was raised, which is the rendezvous's business
        // and not this log's. The message above it is the part worth reading.
        if (line.Length == 0 || line.Contains("INTRODUCE") || line.StartsWith("at ") ||
            line.StartsWith("---") || line.StartsWith("(")) return null;
        var close = line.IndexOf(']');
        if (close > 0 && close + 1 < line.Length) line = line[(close + 1)..].Trim();
        var schema = line.IndexOf("schema=", StringComparison.Ordinal);
        if (schema > 0) line = line[..schema].TrimEnd();
        line = string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    public static void StopRelay()
    {
        if (!RelayRunning) return;
        try { _relay!.Kill(entireProcessTree: true); } catch (Exception) { /* going anyway */ }
        _relay = null;
    }

    /// <summary>Starts FS Copilot against <paramref name="relayHost"/>, with
    /// <paramref name="peerId"/> as its session code. Returns an error, or null.</summary>
    public static bool FsCopilotRunning => Process.GetProcessesByName("FsCopilot").Length > 0;

    /// <summary>Closes a running app the way its own window's close button does, so it says
    /// goodbye to its panels and its peer before it goes. Killed only if it will not.</summary>
    public static async Task StopFsCopilot()
    {
        var running = Process.GetProcessesByName("FsCopilot");
        foreach (var process in running)
        {
            try { process.CloseMainWindow(); } catch (Exception) { /* about to be killed anyway */ }
        }
        for (var i = 0; i < 20 && running.Any(p => !p.HasExited); i++) await Task.Delay(250);
        foreach (var process in running.Where(p => !p.HasExited))
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* gone between checks */ }
        }
    }

    public static string? StartFsCopilot(string relayHost, string peerId)
    {
        if (!File.Exists(FsCopilotExe)) return $"no FS Copilot build at {FsCopilotExe}";
        // Empty means a build that takes neither flag: start it as the pilot would.
        var args = relayHost.Length == 0 || peerId.Length == 0 ? "" : $"--relay {relayHost} --peer-id {peerId}";
        try
        {
            Process.Start(new ProcessStartInfo(FsCopilotExe, args)
            {
                WorkingDirectory = Path.GetDirectoryName(FsCopilotExe)!,
                UseShellExecute = true
            });
            return null;
        }
        catch (Exception e)
        {
            return $"could not start FS Copilot: {e.Message}";
        }
    }

    /// <summary>Whether this FS Copilot build takes the flags above. They are test-only, so a
    /// build cut for the pull request may not have them, and passing them would leave the app on
    /// the wrong rendezvous with a code nobody knows. The strings live in the managed assembly,
    /// not the little apphost beside it.</summary>
    public static bool TakesTestFlags()
    {
        try
        {
            var dll = Path.ChangeExtension(FsCopilotExe, ".dll");
            var image = File.ReadAllBytes(File.Exists(dll) ? dll : FsCopilotExe);
            return HoldsLiteral(image, "--peer-id") && HoldsLiteral(image, "--relay");
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Searches the assembly image for a string literal, as raw UTF-16 bytes. Reading
    /// the file as Unicode text instead would decode from byte zero in pairs, and the literal
    /// heap packs its entries behind a length prefix rather than aligning them - so half of all
    /// literals start on an odd offset and never appear in the decoded text at all.</summary>
    private static bool HoldsLiteral(byte[] image, string literal)
    {
        var needle = Encoding.Unicode.GetBytes(literal);
        for (var i = 0; i + needle.Length <= image.Length; i++)
        {
            var j = 0;
            while (j < needle.Length && image[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }
}
