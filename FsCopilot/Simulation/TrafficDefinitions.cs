namespace FsCopilot.Simulation;

using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;

/// <summary>
/// SimConnect data definitions for AI traffic, on the <see cref="Connection.SimTraffic"/>
/// connection. The host reads <see cref="ObjectState"/> and <see cref="ObjectIdentity"/>; the
/// receiver writes <see cref="DriveState"/>, <see cref="Appearance"/> and <see cref="EngineState"/>.
/// One id table for both sides, since they never run on the same connection at once.
/// </summary>
internal enum TrafficDef : uint
{
    State = 1,
    Identity,
    Livery,
    Foreign,
    Measure,
    Drive = 10,
    Appearance,
    Eng1,
    Eng2,
    Eng3,
    Eng4
}

internal enum TrafficReq : uint
{
    Identity = 10,
    Livery,
    Liveries,
    Foreign = 20,
    // The poll's request id carries which poll asked, because the reply carries nothing else:
    // SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE has no send id and no timestamp, so a straggler from
    // an abandoned poll is otherwise indistinguishable from a reply to the one that replaced it.
    // Layout: PollBase + generation * PollTypes + type, generations cycling.
    PollBase = 100,
    // Per-object requests carry the host's u16 object index above these bases.
    CreateBase = 1000,
    ReleaseBase = 70000,
    RemoveBase = 140000,
    MeasureBase = 210000
}

/// <summary>Per-frame read-back of an injected object's position; the debug smoothness score.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MeasureProbe
{
    public double Lat, Lon;
    public int FreezeLatLon, FreezeAlt, FreezeAtt;

    public static void Define(SimConnect sim, Enum def)
    {
        ObjectState.Add(sim, def, "PLANE LATITUDE", "Degrees");
        ObjectState.Add(sim, def, "PLANE LONGITUDE", "Degrees");
        ObjectState.AddInt(sim, def, "IS LATITUDE LONGITUDE FREEZE ON", "Bool");
        ObjectState.AddInt(sim, def, "IS ALTITUDE FREEZE ON", "Bool");
        ObjectState.AddInt(sim, def, "IS ATTITUDE FREEZE ON", "Bool");
        sim.RegisterDataDefineStruct<MeasureProbe>(def);
    }
}

/// <summary>
/// Everything the host samples per object on every poll. One definition for every object type:
/// the fields that do not apply to a ground vehicle read as zero and cost nothing.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ObjectState
{
    public double Lat;          // degrees
    public double Lon;          // degrees
    public double Alt;          // feet
    public double Pitch;        // degrees
    public double Bank;         // degrees
    public double HeadingTrue;  // degrees
    public double VelBodyX;     // ft/s
    public double VelBodyY;     // ft/s
    public double VelBodyZ;     // ft/s
    public double RotX;         // rad/s
    public double RotY;         // rad/s
    public double RotZ;         // rad/s
    public double GroundSpeed;  // knots
    public double VerticalSpeed;// ft/min
    public int OnGround;
    public int IsUser;
    public double GearHandle;   // 0..1
    public int FlapsIndex;      // FLAPS HANDLE PERCENT is read-only for SetData; the index is what gets written back
    public int LightStrobe;
    public int LightLanding;
    public int LightTaxi;
    public int LightBeacon;
    public int LightNav;
    public int NumEngines;
    public int Comb1, Comb2, Comb3, Comb4;  // a created aircraft comes with engines running; this is what turns them off

    public int LightMask => LightStrobe | LightLanding << 1 | LightTaxi << 2 | LightBeacon << 3 | LightNav << 4;
    public int EngineMask => Comb1 | Comb2 << 1 | Comb3 << 2 | Comb4 << 3;

    public static void Define(SimConnect sim, Enum def)
    {
        Add(sim, def, "PLANE LATITUDE", "Degrees");
        Add(sim, def, "PLANE LONGITUDE", "Degrees");
        Add(sim, def, "PLANE ALTITUDE", "Feet");
        Add(sim, def, "PLANE PITCH DEGREES", "Degrees");
        Add(sim, def, "PLANE BANK DEGREES", "Degrees");
        Add(sim, def, "PLANE HEADING DEGREES TRUE", "Degrees");
        Add(sim, def, "VELOCITY BODY X", "Feet per second");
        Add(sim, def, "VELOCITY BODY Y", "Feet per second");
        Add(sim, def, "VELOCITY BODY Z", "Feet per second");
        Add(sim, def, "ROTATION VELOCITY BODY X", "Radians per second");
        Add(sim, def, "ROTATION VELOCITY BODY Y", "Radians per second");
        Add(sim, def, "ROTATION VELOCITY BODY Z", "Radians per second");
        Add(sim, def, "GROUND VELOCITY", "Knots");
        Add(sim, def, "VERTICAL SPEED", "Feet per minute");
        AddInt(sim, def, "SIM ON GROUND", "Bool");
        AddInt(sim, def, "IS USER SIM", "Bool");
        Add(sim, def, "GEAR HANDLE POSITION", "Percent Over 100");
        AddInt(sim, def, "FLAPS HANDLE INDEX", "Number");
        AddInt(sim, def, "LIGHT STROBE ON", "Bool");
        AddInt(sim, def, "LIGHT LANDING ON", "Bool");
        AddInt(sim, def, "LIGHT TAXI ON", "Bool");
        AddInt(sim, def, "LIGHT BEACON ON", "Bool");
        AddInt(sim, def, "LIGHT NAV ON", "Bool");
        AddInt(sim, def, "NUMBER OF ENGINES", "Number");
        AddInt(sim, def, "GENERAL ENG COMBUSTION:1", "Bool");
        AddInt(sim, def, "GENERAL ENG COMBUSTION:2", "Bool");
        AddInt(sim, def, "GENERAL ENG COMBUSTION:3", "Bool");
        AddInt(sim, def, "GENERAL ENG COMBUSTION:4", "Bool");
        sim.RegisterDataDefineStruct<ObjectState>(def);
    }

    internal static void Add(SimConnect sim, Enum def, string name, string units) =>
        sim.AddToDataDefinition(def, name, units, SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);

    internal static void AddInt(SimConnect sim, Enum def, string name, string units) =>
        sim.AddToDataDefinition(def, name, units, SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
}

/// <summary>Read once when an object first appears. This is what the receiver needs to create it.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct ObjectIdentity
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Title;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string AtcId;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string AtcAirline;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string AtcFlightNumber;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string AtcModel;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Category;
    public int IsUser;

    public static void Define(SimConnect sim, Enum def)
    {
        sim.AddToDataDefinition(def, "TITLE", null, SIMCONNECT_DATATYPE.STRING256, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "ATC ID", null, SIMCONNECT_DATATYPE.STRING64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "ATC AIRLINE", null, SIMCONNECT_DATATYPE.STRING64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "ATC FLIGHT NUMBER", null, SIMCONNECT_DATATYPE.STRING64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "ATC MODEL", null, SIMCONNECT_DATATYPE.STRING64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "CATEGORY", null, SIMCONNECT_DATATYPE.STRING64, 0f, SimConnect.SIMCONNECT_UNUSED);
        ObjectState.AddInt(sim, def, "IS USER SIM", "Bool");
        sim.RegisterDataDefineStruct<ObjectIdentity>(def);
    }
}

/// <summary>
/// MSFS 2024 only: packages that ship liveries separately report one here, and the receiver's
/// create call takes it. FSLTL-style packages bake the livery into the title and report an
/// empty string, which is also fine. Its own definition because on 2020 the name is rejected,
/// and an unknown datum inside a shared definition shifts every field after it.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct LiveryProbe
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string LiveryName;

    public static void Define(SimConnect sim, Enum def)
    {
        sim.AddToDataDefinition(def, "LIVERY NAME", null, SIMCONNECT_DATATYPE.STRING256, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<LiveryProbe>(def);
    }
}

/// <summary>The receiver's slow sweep for AI aircraft it did not create: the "switch off your own traffic" warning.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ForeignProbe
{
    public int IsUser;

    public static void Define(SimConnect sim, Enum def)
    {
        ObjectState.AddInt(sim, def, "IS USER SIM", "Bool");
        sim.RegisterDataDefineStruct<ForeignProbe>(def);
    }
}

/// <summary>
/// What the receiver writes to a frozen object every frame: the interpolated pose, plus the
/// velocities for the animations that read them.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DriveState
{
    public double Lat, Lon, Alt, Pitch, Bank, HeadingTrue;
    public double VelBodyX, VelBodyY, VelBodyZ;
    public double RotX, RotY, RotZ;

    public static void Define(SimConnect sim, Enum def)
    {
        ObjectState.Add(sim, def, "PLANE LATITUDE", "Degrees");
        ObjectState.Add(sim, def, "PLANE LONGITUDE", "Degrees");
        ObjectState.Add(sim, def, "PLANE ALTITUDE", "Feet");
        ObjectState.Add(sim, def, "PLANE PITCH DEGREES", "Degrees");
        ObjectState.Add(sim, def, "PLANE BANK DEGREES", "Degrees");
        ObjectState.Add(sim, def, "PLANE HEADING DEGREES TRUE", "Degrees");
        ObjectState.Add(sim, def, "VELOCITY BODY X", "Feet per second");
        ObjectState.Add(sim, def, "VELOCITY BODY Y", "Feet per second");
        ObjectState.Add(sim, def, "VELOCITY BODY Z", "Feet per second");
        ObjectState.Add(sim, def, "ROTATION VELOCITY BODY X", "Radians per second");
        ObjectState.Add(sim, def, "ROTATION VELOCITY BODY Y", "Radians per second");
        ObjectState.Add(sim, def, "ROTATION VELOCITY BODY Z", "Radians per second");
        sim.RegisterDataDefineStruct<DriveState>(def);
    }
}

/// <summary>
/// Gear, flaps and lights, written only when they change and only to aircraft: ground vehicles
/// reject every datum here.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Appearance
{
    public double GearHandle;
    public int FlapsIndex;
    public int LightStrobe, LightLanding, LightTaxi, LightBeacon, LightNav;

    public static void Define(SimConnect sim, Enum def)
    {
        ObjectState.Add(sim, def, "GEAR HANDLE POSITION", "Percent Over 100");
        ObjectState.AddInt(sim, def, "FLAPS HANDLE INDEX", "Number");
        ObjectState.AddInt(sim, def, "LIGHT STROBE", "Bool");
        ObjectState.AddInt(sim, def, "LIGHT LANDING", "Bool");
        ObjectState.AddInt(sim, def, "LIGHT TAXI", "Bool");
        ObjectState.AddInt(sim, def, "LIGHT BEACON", "Bool");
        ObjectState.AddInt(sim, def, "LIGHT NAV", "Bool");
        sim.RegisterDataDefineStruct<Appearance>(def);
    }
}

/// <summary>One engine's combustion, written per engine so a twin is never asked about engine 4.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct EngineState
{
    public int Combustion;

    public static void Define(SimConnect sim, Enum def, int engine)
    {
        ObjectState.AddInt(sim, def, $"GENERAL ENG COMBUSTION:{engine}", "Bool");
        sim.RegisterDataDefineStruct<EngineState>(def);
    }
}
