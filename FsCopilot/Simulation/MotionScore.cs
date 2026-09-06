namespace FsCopilot.Simulation;

/// <summary>
/// Smoothness of an object's motion as the sim actually renders it, from per-frame position
/// reads: mean frame-to-frame change in displacement over mean displacement, across a rolling
/// window. Constant-speed motion is near 0; an object that stands still on some frames and
/// snaps on others is near 1 or above. The prototype scored 0.17 with this; the ATC add-on's
/// own traffic 0.56. Diagnostics only.
/// </summary>
public sealed class MotionScore
{
    private const int Window = 150;
    private const int MinFrames = 30;
    private const double MinMeanMetres = 0.005;

    private sealed class Track
    {
        public double Lat, Lon; public bool Has;
        public readonly Queue<double> Disp = new();
    }

    private readonly Dictionary<uint, Track> _tracks = new();

    public int Count => _tracks.Count;

    public void Add(uint id, double lat, double lon)
    {
        if (!_tracks.TryGetValue(id, out var t)) _tracks[id] = t = new Track();
        if (t.Has)
        {
            var dLat = (lat - t.Lat) * 111320;
            var dLon = (lon - t.Lon) * 111320 * Math.Cos(lat * Math.PI / 180);
            t.Disp.Enqueue(Math.Sqrt(dLat * dLat + dLon * dLon));
            while (t.Disp.Count > Window) t.Disp.Dequeue();
        }
        t.Lat = lat; t.Lon = lon; t.Has = true;
    }

    public void Forget(uint id) => _tracks.Remove(id);

    public string Report()
    {
        var scores = new List<double>();
        foreach (var t in _tracks.Values)
        {
            if (t.Disp.Count < MinFrames) continue;
            var d = t.Disp.ToArray();
            var mean = d.Average();
            if (mean < MinMeanMetres) continue;
            var change = 0.0;
            for (var i = 1; i < d.Length; i++) change += Math.Abs(d[i] - d[i - 1]);
            scores.Add(change / (d.Length - 1) / mean);
        }
        if (scores.Count == 0) return "smoothness: no movers measured";
        scores.Sort();
        return $"smoothness median {scores[scores.Count / 2]:0.00} over {scores.Count} movers, worst {scores[^1]:0.00}";
    }
}
