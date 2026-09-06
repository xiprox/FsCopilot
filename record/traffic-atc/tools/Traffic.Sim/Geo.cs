namespace Traffic.Sim;

public static class Geo
{
    public const double MetersPerNm = 1852.0;
    public const uint MaxRadiusMeters = 200_000; // SDK cap on RequestDataOnSimObjectType

    /// <summary>Great-circle distance in nautical miles.</summary>
    public static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371000.0 / MetersPerNm;
        var p1 = lat1 * Math.PI / 180; var p2 = lat2 * Math.PI / 180;
        var dp = (lat2 - lat1) * Math.PI / 180; var dl = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        return 2 * r * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
