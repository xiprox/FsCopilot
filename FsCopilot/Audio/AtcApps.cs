namespace FsCopilot.Audio;

using NAudio.CoreAudioApi;

/// <summary>
/// Which process is the ATC app. Known apps are found by exe name; the "Other…" list
/// is every process that owns an audio session on a render device, i.e. things that make
/// sound, rather than every process on the machine. The mixer level of a process is readable
/// through the same sessions, which is how the host compensates for it.
/// </summary>
public static class AtcApps
{
    public static readonly string[] Known = ["BeyondATC", "SayIntentions", "Pilot2ATC", "PF3", "FSHud", "VoxATC"];

    public sealed record App(string Exe, int Pid, string Label);

    /// <summary>Known ATC apps running right now, in <see cref="Known"/> order.</summary>
    public static IReadOnlyList<App> DetectedKnown()
    {
        var found = new List<App>();
        foreach (var name in Known)
        {
            var p = Oldest(name);
            if (p is not null) found.Add(new App(name, p.Id, name));
        }
        return found;
    }

    /// <summary>The oldest process of that exe name - a launcher's child tree is covered by include-tree capture.</summary>
    public static Process? Oldest(string exeName)
    {
        Process? oldest = null;
        var oldestStart = DateTime.MaxValue;
        foreach (var p in Process.GetProcessesByName(exeName))
        {
            try
            {
                if (p.HasExited) continue;
                var start = p.StartTime;
                if (start < oldestStart) { oldestStart = start; oldest = p; }
            }
            catch { /* access denied or gone */ }
        }
        return oldest;
    }

    /// <summary>Processes with an audio session on any render device, by pid, excluding this one and system sounds.</summary>
    public static IReadOnlyList<App> AudioSessionProcesses()
    {
        var byPid = new Dictionary<int, App>();
        var self = Environment.ProcessId;
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                var sessions = d.AudioSessionManager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    var pid = (int)sessions[i].GetProcessID;
                    if (pid == 0 || pid == self || byPid.ContainsKey(pid)) continue;
                    try
                    {
                        var p = Process.GetProcessById(pid);
                        byPid[pid] = new App(p.ProcessName, pid, p.ProcessName);
                    }
                    catch { /* gone */ }
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
        foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            var sessions = d.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.GetProcessID != (uint)pid) continue;
                found = true;
                vol = Math.Min(vol, s.SimpleAudioVolume.Volume);
                muted |= s.SimpleAudioVolume.Mute;
            }
        }
        return found ? (vol, muted) : (1f, false);
    }
}
