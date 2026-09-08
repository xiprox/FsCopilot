namespace FsCopilot.Audio;

using NAudio.CoreAudioApi;

/// <summary>
/// Which process is the ATC app. Known apps are found by exe name; the "Other…" list
/// is every process that owns an audio session on a render device, i.e. things that make
/// sound, rather than every process on the machine. The mixer level of a process is readable
/// through the same sessions, which is how the host compensates for it.
///
/// Everything here runs on a timer, so nothing may outlive the call that made it: a
/// <see cref="Process"/> owns a kernel handle once it is asked anything, and every audio object
/// is a COM reference. Left to the finalizer they are handle and GC churn on a machine that is
/// being watched for stutter.
/// </summary>
public static class AtcApps
{
    public static readonly string[] Known = ["BeyondATC", "SayIntentions", "Pilot2ATC", "PF3", "FSHud", "VoxATC"];

    public sealed record App(string Exe, int Pid, string Label);

    /// <summary>Known ATC apps running right now, in <see cref="Known"/> order.</summary>
    public static IReadOnlyList<App> DetectedKnown()
    {
        var oldest = OldestOf(Known);
        var found = new List<App>();
        foreach (var name in Known)
            if (oldest.TryGetValue(name, out var pid)) found.Add(new App(name, pid, name));
        return found;
    }

    /// <summary>
    /// The oldest process of that exe name, or null - a launcher's child tree is covered by
    /// include-tree capture.
    /// </summary>
    public static int? Oldest(string exeName) =>
        OldestOf([exeName]).TryGetValue(exeName, out var pid) ? pid : null;

    public static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }   // gone, or not ours to ask
    }

    /// <summary>
    /// The oldest live process id for each of the named executables. One snapshot for all of
    /// them: <c>GetProcessesByName</c> takes a full one per call, and this runs twice a second.
    /// </summary>
    private static Dictionary<string, int> OldestOf(IReadOnlyList<string> names)
    {
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var oldest = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var startedAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (!wanted.Contains(p.ProcessName) || p.HasExited) continue;
                    var start = p.StartTime;
                    if (oldest.ContainsKey(p.ProcessName) && startedAt[p.ProcessName] <= start) continue;
                    oldest[p.ProcessName] = p.Id;
                    startedAt[p.ProcessName] = start;
                }
                catch { /* access denied, or gone between the snapshot and the question */ }
            }
        }
        return oldest;
    }

    /// <summary>
    /// Processes with an audio session on any render device, by pid, excluding this one and
    /// system sounds. Enumerating every session on every endpoint is not cheap, so this is
    /// called when the user asks for the list, never on a timer.
    /// </summary>
    public static IReadOnlyList<App> AudioSessionProcesses()
    {
        var byPid = new Dictionary<int, App>();
        var self = Environment.ProcessId;
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var device in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        var pid = (int)session.GetProcessID;
                        if (pid == 0 || pid == self || byPid.ContainsKey(pid)) continue;
                        try
                        {
                            using var p = Process.GetProcessById(pid);
                            byPid[pid] = new App(p.ProcessName, pid, p.ProcessName);
                        }
                        catch { /* gone */ }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Atc] Could not enumerate audio sessions");
        }
        return byPid.Values.OrderBy(a => a.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The volume-mixer level of a process: the lowest across its sessions, and whether any is muted.</summary>
    public static (float Volume, bool Muted) SessionVolume(int pid)
    {
        var vol = 1f; var muted = false; var found = false;
        using var e = new MMDeviceEnumerator();
        foreach (var device in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    if (session.GetProcessID != (uint)pid) continue;
                    found = true;
                    var volume = session.SimpleAudioVolume;
                    vol = Math.Min(vol, volume.Volume);
                    muted |= volume.Mute;
                }
            }
        }
        return found ? (vol, muted) : (1f, false);
    }
}
