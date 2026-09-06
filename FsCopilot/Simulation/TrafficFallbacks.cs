namespace FsCopilot.Simulation;

/// <summary>
/// What to spawn when the receiver does not own the title the host reported, so the object
/// still exists - on TCAS and out of the window - even if it is the wrong shape. The rule is
/// that everyone runs the same traffic pack, which makes this the exception path.
/// </summary>
public static class TrafficFallbacks
{
    public static string? For(TrafficCategory category, bool msfs2024) => category switch
    {
        TrafficCategory.Airplane => msfs2024 ? "Asobo PassiveAircraft Citation CJ4" : "Airbus A320 Neo Asobo",
        TrafficCategory.Helicopter => msfs2024 ? "Asobo PassiveAircraft Bell 407" : "Asobo Bell 407",
        _ => null   // a vehicle of unknown title is better absent than wrong
    };
}
