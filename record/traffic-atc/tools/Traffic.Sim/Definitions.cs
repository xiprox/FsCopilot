using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;

namespace Traffic.Sim;

/// <summary>
/// Everything the reader samples per object on every poll, and everything the injector writes
/// back. One definition for every object type: the fields that do not apply to a ground vehicle
/// read as zero and cost nothing.
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
    public double VelWorldX;    // ft/s, east
    public double VelWorldY;    // ft/s, up
    public double VelWorldZ;    // ft/s, north
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
    public double FlapsHandle;  // percent
    public int LightStrobe;
    public int LightLanding;
    public int LightTaxi;
    public int LightBeacon;
    public int LightNav;
    public int FlapsIndex;      // FLAPS HANDLE PERCENT is read-only for SetData; the index is what gets written back
    public int NumEngines;
    public int Comb1, Comb2, Comb3, Comb4;  // a created aircraft comes with engines running; this is what turns them off

    public int EngineMask => Comb1 | Comb2 << 1 | Comb3 << 2 | Comb4 << 3;

    public static void Define(SimConnect sim, Enum def)
    {
        sim.AddToDataDefinition(def, "PLANE LATITUDE", "Degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "PLANE LONGITUDE", "Degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "PLANE ALTITUDE", "Feet", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "PLANE PITCH DEGREES", "Degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "PLANE BANK DEGREES", "Degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "PLANE HEADING DEGREES TRUE", "Degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "VELOCITY WORLD X", "Feet per second", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "VELOCITY WORLD Y", "Feet per second", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "VELOCITY WORLD Z", "Feet per second", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "VELOCITY BODY X", "Feet per second", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "VELOCITY BODY Y", "Feet per second", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "VELOCITY BODY Z", "Feet per second", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "ROTATION VELOCITY BODY X", "Radians per second", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "ROTATION VELOCITY BODY Y", "Radians per second", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "ROTATION VELOCITY BODY Z", "Radians per second", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "GROUND VELOCITY", "Knots", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "VERTICAL SPEED", "Feet per minute", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "SIM ON GROUND", "Bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "IS USER SIM", "Bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "GEAR HANDLE POSITION", "Percent Over 100", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "FLAPS HANDLE PERCENT", "Percent", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "LIGHT STROBE ON", "Bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "LIGHT LANDING ON", "Bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "LIGHT TAXI ON", "Bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "LIGHT BEACON ON", "Bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "LIGHT NAV ON", "Bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "FLAPS HANDLE INDEX", "Number", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "NUMBER OF ENGINES", "Number", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "GENERAL ENG COMBUSTION:1", "Bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "GENERAL ENG COMBUSTION:2", "Bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "GENERAL ENG COMBUSTION:3", "Bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "GENERAL ENG COMBUSTION:4", "Bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<ObjectState>(def);
    }
}

/// <summary>
/// Per-frame read-back of an injected object, for scoring how smoothly it actually moves and
/// checking what the sim did with freeze and brakes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Measured
{
    public double Lat, Lon, Alt, HeadingTrue, GroundSpeed;
    public int FreezeLatLon, FreezeAlt, FreezeAtt;
    public double ParkingBrake;

    public static void Define(SimConnect sim, Enum def)
    {
        sim.AddToDataDefinition(def, "PLANE LATITUDE", "Degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "PLANE LONGITUDE", "Degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "PLANE ALTITUDE", "Feet", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "PLANE HEADING DEGREES TRUE", "Degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "GROUND VELOCITY", "Knots", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "IS LATITUDE LONGITUDE FREEZE ON", "Bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "IS ALTITUDE FREEZE ON", "Bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "IS ATTITUDE FREEZE ON", "Bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "BRAKE PARKING POSITION", "Position", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<Measured>(def);
    }
}

/// <summary>
/// One engine's combustion, written per engine so a twin never gets asked about engine 4.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct EngineState
{
    public int Combustion;

    public static void Define(SimConnect sim, Enum def, int engine)
    {
        sim.AddToDataDefinition(def, $"GENERAL ENG COMBUSTION:{engine}", "Bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<EngineState>(def);
    }
}

/// <summary>
/// What the injector writes to a released object on every state: pose plus body velocities, so
/// the sim dead-reckons between writes. Nothing here is read-only in the SDK.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DriveState
{
    public double Lat, Lon, Alt, Pitch, Bank, HeadingTrue;
    public double VelBodyX, VelBodyY, VelBodyZ;
    public double RotX, RotY, RotZ;

    public static DriveState From(in ObjectState s) => new()
    {
        Lat = s.Lat, Lon = s.Lon, Alt = s.Alt, Pitch = s.Pitch, Bank = s.Bank, HeadingTrue = s.HeadingTrue,
        VelBodyX = s.VelBodyX, VelBodyY = s.VelBodyY, VelBodyZ = s.VelBodyZ,
        RotX = s.RotX, RotY = s.RotY, RotZ = s.RotZ
    };

    public static void Define(SimConnect sim, Enum def)
    {
        sim.AddToDataDefinition(def, "PLANE LATITUDE", "Degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "PLANE LONGITUDE", "Degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "PLANE ALTITUDE", "Feet", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "PLANE PITCH DEGREES", "Degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "PLANE BANK DEGREES", "Degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "PLANE HEADING DEGREES TRUE", "Degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "VELOCITY BODY X", "Feet per second", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "VELOCITY BODY Y", "Feet per second", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "VELOCITY BODY Z", "Feet per second", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "ROTATION VELOCITY BODY X", "Radians per second", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "ROTATION VELOCITY BODY Y", "Radians per second", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "ROTATION VELOCITY BODY Z", "Radians per second", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<DriveState>(def);
    }
}

/// <summary>
/// Gear, flaps and lights, written only when they change. Kept apart from the drive definition
/// so that a datum the sim refuses to set breaks this one and not the pose.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Appearance
{
    public double GearHandle;
    public int FlapsIndex;
    public int LightStrobe, LightLanding, LightTaxi, LightBeacon, LightNav;

    public static void Define(SimConnect sim, Enum def)
    {
        sim.AddToDataDefinition(def, "GEAR HANDLE POSITION", "Percent Over 100", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "FLAPS HANDLE INDEX", "Number", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "LIGHT STROBE", "Bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "LIGHT LANDING", "Bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "LIGHT TAXI", "Bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "LIGHT BEACON", "Bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "LIGHT NAV", "Bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<Appearance>(def);
    }
}

/// <summary>
/// Read once when an object first appears. This is what the injector needs to create it.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct ObjectIdentity
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Title;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string AtcId;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string AtcAirline;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string AtcFlightNumber;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string AtcModel;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string AtcType;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Category;
    public int IsUser;

    public static void Define(SimConnect sim, Enum def)
    {
        sim.AddToDataDefinition(def, "TITLE", null, SIMCONNECT_DATATYPE.STRING256, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "ATC ID", null, SIMCONNECT_DATATYPE.STRING64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "ATC AIRLINE", null, SIMCONNECT_DATATYPE.STRING64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "ATC FLIGHT NUMBER", null, SIMCONNECT_DATATYPE.STRING64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "ATC MODEL", null, SIMCONNECT_DATATYPE.STRING64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "ATC TYPE", null, SIMCONNECT_DATATYPE.STRING64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "CATEGORY", null, SIMCONNECT_DATATYPE.STRING64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(def, "IS USER SIM", "Bool", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<ObjectIdentity>(def);
    }
}

/// <summary>
/// Q03 probe. No livery simvar is documented; this asks for one anyway, in its own definition so
/// that the exception (or the value) lands without disturbing the identity read.
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
