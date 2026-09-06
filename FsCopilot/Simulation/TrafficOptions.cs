namespace FsCopilot.Simulation;

using System.Globalization;

/// <summary>
/// Developer switches for traffic, from the command line. <c>--traffic-offset nm,deg</c> shifts
/// every received object, so a second instance on the same machine can receive into the same
/// sim without spawning copies inside the originals; <c>--traffic-ground</c> shares ground
/// vehicles too.
/// </summary>
public sealed record TrafficOptions(double OffsetNm, double OffsetDeg, bool Ground, bool Debug, double ShadowMetres)
{
    public static readonly TrafficOptions Default = new(0, 0, false, false, 0);

    /// <summary>Prefixed to the tail of objects a receiver in offset mode creates; no real callsign starts with it.</summary>
    public const string CopyMarker = "~";

    /// <summary>Received objects are displaced from the originals: the same-sim test bed.</summary>
    public bool HasOffset => OffsetNm != 0 || ShadowMetres != 0;

    public static TrafficOptions Parse(string[] args)
    {
        var options = Default;
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--traffic-ground", StringComparison.OrdinalIgnoreCase))
                options = options with { Ground = true };
            else if (string.Equals(args[i], "--debug", StringComparison.OrdinalIgnoreCase))
                options = options with { Debug = true };   // per-frame read-back score in the receiver's stats
            else if (string.Equals(args[i], "--traffic-shadow", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length
                     && double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var metres))
                options = options with { ShadowMetres = metres };   // copies follow this far behind the originals
            else if (string.Equals(args[i], "--traffic-offset", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                var parts = args[++i].Split(',');
                if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var nm))
                {
                    var deg = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
                    options = options with { OffsetNm = nm, OffsetDeg = deg };
                }
            }
        }
        if (options != Default) Log.Information("[Traffic] Options: offset {Nm} nm at {Deg} deg, shadow {Shadow} m, ground {Ground}, debug {Debug}", options.OffsetNm, options.OffsetDeg, options.ShadowMetres, options.Ground, options.Debug);
        return options;
    }

    /// <summary>Displace a position: by the fixed offset, and/or backwards along its heading.</summary>
    public void Apply(ref double lat, ref double lon, double headingDeg)
    {
        if (!HasOffset) return;
        var cosLat = Math.Cos(lat * Math.PI / 180);
        if (OffsetNm != 0)
        {
            var brg = OffsetDeg * Math.PI / 180;
            lat += OffsetNm / 60.0 * Math.Cos(brg);
            lon += OffsetNm / 60.0 * Math.Sin(brg) / cosLat;
        }
        if (ShadowMetres != 0)
        {
            var h = headingDeg * Math.PI / 180;
            lat -= ShadowMetres * Math.Cos(h) / 111320.0;
            lon -= ShadowMetres * Math.Sin(h) / (111320.0 * cosLat);
        }
    }
}
