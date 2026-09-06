namespace Traffic.Sim;

/// <summary>
/// Smoothness of an object's motion as the sim actually renders it, from per-frame position
/// reads. Score = mean frame-to-frame change in displacement over mean displacement, across a
/// rolling window: constant-speed motion is near 0; an object that stands still on some
/// frames and snaps on others is near 1 or above. The same number for the ATC add-on's own
/// traffic is the baseline every injector configuration is judged against.
/// </summary>
public sealed class MotionScore
{
    public const int Window = 150;      // frames kept per object
    private const int MinFrames = 30;
    private const double MinMeanMetres = 0.005;

    private sealed class Track
    {
        public double Lat, Lon; public bool Has;
        public readonly Queue<double> Disp = new();
        public string Name = "";
    }

    private readonly Dictionary<uint, Track> _tracks = new();

    public void Add(uint id, string name, double lat, double lon)
    {
        if (!_tracks.TryGetValue(id, out var t)) _tracks[id] = t = new Track { Name = name };
        if (t.Has)
        {
            t.Disp.Enqueue(Geo.DistanceNm(t.Lat, t.Lon, lat, lon) * Geo.MetersPerNm);
            while (t.Disp.Count > Window) t.Disp.Dequeue();
        }
        t.Lat = lat; t.Lon = lon; t.Has = true;
    }

    public void Forget(uint id) => _tracks.Remove(id);

    public string Report()
    {
        var scores = new List<(double Score, string Name)>();
        foreach (var (_, t) in _tracks)
        {
            if (t.Disp.Count < MinFrames) continue;
            var d = t.Disp.ToArray();
            var mean = d.Average();
            if (mean < MinMeanMetres) continue;
            var change = 0.0;
            for (var i = 1; i < d.Length; i++) change += Math.Abs(d[i] - d[i - 1]);
            scores.Add((change / (d.Length - 1) / mean, t.Name));
        }
        if (scores.Count == 0) return "jitter: no movers measured";
        scores.Sort((a, b) => a.Score.CompareTo(b.Score));
        var median = scores[scores.Count / 2].Score;
        var worst = scores[^1];
        return $"jitter median {median:0.00} over {scores.Count} movers, worst {worst.Score:0.00} ({worst.Name})";
    }
}
