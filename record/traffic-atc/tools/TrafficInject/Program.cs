using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.FlightSimulator.SimConnect;
using Traffic.Sim;

// TrafficInject — stage 1 of record/traffic-atc.
//
// Replays a TrafficRead capture into the sim: creates each object at its first state,
// releases it from the AI engine, drives it on every following state, and removes it on `rm`
// or at exit. Three ways to drive:
//
//   default         write pose + body velocities as each sample arrives; the sim dead-reckons
//   --interp <hz>   write interpolated poses at a fixed rate, one sample interval behind
//   --interp frame  write an interpolated pose on every sim frame (the "Frame" system event)
//
// With --freeze the sim stops simulating the object and only our writes move it, so
// --interp frame is the natural partner. Without it, the velocities written alongside an
// interpolated pose are derived from the path so the sim's own reckoning agrees with it.
//
//   TrafficInject <capture.ndjson> [--speed <x>] [--rate <hz>] [--interp <hz>|frame] [--freeze]
//                 [--measure] [--max <n>] [--offset <nm>,<deg>] [--tail-prefix <s>]
//                 [--fallback "<title>"] [--plain] [--no-release] [--no-appearance]
//                 [--no-engines] [--leave] [--loop] [--quiet]

var opts = Options.Parse(args);
if (opts is null) return 2;

var capture = Capture.Load(opts.File);
Console.WriteLine($"TrafficInject  {Path.GetFileName(opts.File)}: {capture.Objects.Count} objects, {capture.States.Count} states, {capture.DurationMs / 1000.0:0} s" +
                  $"  speed={opts.Speed}x rate={(opts.RateHz > 0 ? opts.RateHz + " Hz" : "as recorded")} interp={opts.InterpLabel} freeze={opts.Freeze} max={opts.Max}");
if (!capture.HasEngines) Console.WriteLine("  (capture predates engine state; engines will be left as the sim creates them)");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

SimLink? link = null;
while (link is null && !cts.IsCancellationRequested)
{
    try
    {
        link = new SimLink("FSC TrafficInject");
        if (!link.WaitOpen(TimeSpan.FromSeconds(5))) { link.Dispose(); link = null; }
    }
    catch (Exception)
    {
        link = null; Console.Write(".");
        try { await Task.Delay(2000, cts.Token); } catch (OperationCanceledException) { }
    }
}
if (link is null) return 1;
Console.WriteLine($"\nConnected to {link.SimName} {link.SimVersion} ({(link.IsMsfs2024 ? "MSFS 2024" : "MSFS 2020")})");

var injector = new Injector(link, opts, capture);
injector.Start();
link.Run(injector.Tick, cts.Token);
injector.Finish();
return 0;

// ---------------------------------------------------------------------------------------

sealed class Options
{
    public string File = "";
    public double Speed = 1.0;
    public double RateHz;             // 0 = as recorded
    public double InterpHz;           // timed interpolation
    public bool InterpFrame;          // per-frame interpolation
    public bool Interp => InterpHz > 0 || InterpFrame;
    public string InterpLabel => InterpFrame ? "frame" : InterpHz > 0 ? InterpHz + " Hz" : "off";
    public int Max = int.MaxValue;
    public double OffsetNm, OffsetDeg;
    public string TailPrefix = "";    // tails truncate at 9 chars, and Q08 made a marker unnecessary
    public string Fallback = "Asobo PassiveAircraft Citation CJ4";
    public bool Plain, NoRelease, NoAppearance, NoEngines, Leave, Loop, Quiet;
    public bool Freeze, Measure;

    public static Options? Parse(string[] args)
    {
        var o = new Options();
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                switch (args[i])
                {
                    case "--speed": o.Speed = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--rate": o.RateHz = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--interp":
                        var v = Next();
                        if (v.Equals("frame", StringComparison.OrdinalIgnoreCase)) o.InterpFrame = true;
                        else o.InterpHz = double.Parse(v, CultureInfo.InvariantCulture);
                        break;
                    case "--max": o.Max = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--offset":
                        var parts = Next().Split(',');
                        o.OffsetNm = double.Parse(parts[0], CultureInfo.InvariantCulture);
                        o.OffsetDeg = parts.Length > 1 ? double.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
                        break;
                    case "--tail-prefix": o.TailPrefix = Next(); break;
                    case "--fallback": o.Fallback = Next(); break;
                    case "--plain": o.Plain = true; break;
                    case "--no-release": o.NoRelease = true; break;
                    case "--no-appearance": o.NoAppearance = true; break;
                    case "--no-engines": o.NoEngines = true; break;
                    case "--freeze": o.Freeze = true; break;
                    case "--measure": o.Measure = true; break;
                    case "--leave": o.Leave = true; break;
                    case "--loop": o.Loop = true; break;
                    case "--quiet": o.Quiet = true; break;
                    case "-h": case "--help": Usage(); return null;
                    default:
                        if (args[i].StartsWith("--")) throw new ArgumentException($"unknown option {args[i]}");
                        o.File = args[i];
                        break;
                }
            }
            if (o.File == "" || !System.IO.File.Exists(o.File)) throw new ArgumentException("capture file required");
            if (o.Freeze && !o.Interp) Console.Error.WriteLine("note: --freeze without --interp means objects only move when a sample arrives");
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            Console.Error.WriteLine(e.Message); Usage(); return null;
        }
        return o;
    }

    static void Usage() => Console.Error.WriteLine(
        "TrafficInject <capture.ndjson> [--speed x] [--rate hz] [--interp hz|frame] [--freeze] [--measure] [--max n]\n" +
        "              [--offset nm,deg] [--tail-prefix s] [--fallback \"title\"] [--plain] [--no-release]\n" +
        "              [--no-appearance] [--no-engines] [--leave] [--loop] [--quiet]\n" +
        "  --rate        thin samples to at most this many per object per second (0 = as recorded)\n" +
        "  --interp      write interpolated poses at this rate, or once per sim frame, instead of relying on the sim between samples\n" +
        "  --freeze      send FREEZE_LATITUDE_LONGITUDE/ALTITUDE/ATTITUDE_SET to each object so the sim stops simulating it\n" +
        "  --measure     read every moving object back each sim frame and report a motion-smoothness score with the stats\n" +
        "  --offset      shift every object by this distance and bearing, to tell copies from originals\n" +
        "  --plain       use the non-_EX1 create even on 2024\n" +
        "  --no-release  skip AIReleaseControl, to see what the AI engine does with a driven object\n" +
        "  --leave       exit without AIRemoveObject, to test whether the sim cleans up on disconnect (Q08)");
}

sealed record ObjInfo(uint SourceId, string Title, string Livery, string Tail, string Category, bool User)
{
    public bool IsAircraft => Category.Equals("Airplane", StringComparison.OrdinalIgnoreCase)
                              || Category.Equals("Helicopter", StringComparison.OrdinalIgnoreCase);
}

sealed class Capture
{
    public readonly Dictionary<uint, ObjInfo> Objects = new();
    public readonly List<(long T, uint Id, ObjectState S)> States = [];
    public readonly List<(long T, uint Id)> Removes = [];
    public long DurationMs;
    public bool HasEngines;

    public static Capture Load(string path)
    {
        var c = new Capture();
        var liveries = new Dictionary<uint, string>();
        var ids = new Dictionary<uint, JsonElement>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            var r = JsonDocument.Parse(line).RootElement;
            var k = r.GetProperty("k").GetString();
            switch (k)
            {
                case "id": ids[r.GetProperty("o").GetUInt32()] = r; break;
                case "livery": liveries[r.GetProperty("o").GetUInt32()] = r.GetProperty("livery").GetString() ?? ""; break;
                case "s":
                {
                    var hasEngines = r.TryGetProperty("ne", out var ne);
                    c.HasEngines |= hasEngines;
                    var e = r.TryGetProperty("e", out var ep) ? ep.GetInt32() : 0;
                    c.States.Add((r.GetProperty("t").GetInt64(), r.GetProperty("o").GetUInt32(), new ObjectState
                    {
                        Lat = D(r, "lat"), Lon = D(r, "lon"), Alt = D(r, "alt"),
                        Pitch = D(r, "p"), Bank = D(r, "b"), HeadingTrue = D(r, "h"),
                        VelWorldX = D(r, "vwx"), VelWorldY = D(r, "vwy"), VelWorldZ = D(r, "vwz"),
                        VelBodyX = D(r, "vbx"), VelBodyY = D(r, "vby"), VelBodyZ = D(r, "vbz"),
                        RotX = D(r, "rx"), RotY = D(r, "ry"), RotZ = D(r, "rz"),
                        GroundSpeed = D(r, "gs"), VerticalSpeed = D(r, "vs"),
                        OnGround = r.GetProperty("g").GetInt32(), IsUser = r.GetProperty("u").GetInt32(),
                        GearHandle = D(r, "gear"), FlapsHandle = D(r, "flap"), FlapsIndex = (int)D(r, "fi"),
                        LightStrobe = Bit(r, 0), LightLanding = Bit(r, 1), LightTaxi = Bit(r, 2), LightBeacon = Bit(r, 3), LightNav = Bit(r, 4),
                        NumEngines = hasEngines ? ne.GetInt32() : -1,
                        Comb1 = e & 1, Comb2 = e >> 1 & 1, Comb3 = e >> 2 & 1, Comb4 = e >> 3 & 1
                    }));
                    break;
                }
                case "rm": c.Removes.Add((r.GetProperty("t").GetInt64(), r.GetProperty("o").GetUInt32())); break;
                case "end": c.DurationMs = r.GetProperty("t").GetInt64(); break;
            }
        }
        foreach (var (o, r) in ids)
        {
            c.Objects[o] = new ObjInfo(o,
                r.GetProperty("title").GetString() ?? "",
                liveries.GetValueOrDefault(o, ""),
                r.GetProperty("tail").GetString() ?? "",
                r.GetProperty("cat").GetString() ?? "",
                r.GetProperty("user").GetBoolean());
        }
        c.States.Sort((a, b) => a.T.CompareTo(b.T));
        if (c.DurationMs == 0 && c.States.Count > 0) c.DurationMs = c.States[^1].T;
        return c;

        static double D(JsonElement r, string n) => r.TryGetProperty(n, out var p) ? p.GetDouble() : 0;
        static int Bit(JsonElement r, int b) => (r.GetProperty("l").GetInt32() >> b) & 1;
    }
}

sealed class Injector(SimLink link, Options opts, Capture capture)
{
    enum DEF : uint { Drive = 1, Appearance, Eng1, Eng2, Eng3, Eng4, Measure }
    enum EVT : uint { FreezeLatLon = 1, FreezeAlt, FreezeAtt }
    enum GRP : uint { Highest = 1 }   // SIMCONNECT_GROUP_PRIORITY_HIGHEST
    // Request ids: create = 1000 + index, release = 2000 + index, remove = 3000 + index, measure = 5000 + index.
    const uint CreateBase = 1000, ReleaseBase = 2000, RemoveBase = 3000, MeasureBase = 5000;

    sealed class Live
    {
        public required ObjInfo Info;
        public required uint Index;
        public uint SimId;              // 0 until assigned
        public bool Released, Fallback, Failed;
        public long LastSentAt = long.MinValue;
        public Appearance? LastAppearance;
        public int LastEngines = -1;
        public bool Measuring, MeasureLogged;
        public int Writes;
        // Interpolation: the two most recent samples in replay time, and what was last rendered.
        public (long T, ObjectState S)? Prev, Curr;
        public long RenderedCurrT = -1; public bool RenderedFinal;
    }

    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Dictionary<uint, Live> _bySource = new();   // capture id -> live
    readonly Dictionary<uint, Live> _byRequest = new();  // create request id -> live
    readonly Dictionary<uint, Live> _bySim = new();      // sim object id -> live
    readonly Dictionary<uint, ObjectState> _lastState = new(); // states that arrived before the id was assigned
    readonly MotionScore _score = new();
    int _cursor, _rmCursor;
    uint _nextIndex;
    int _created, _failed, _writes, _exceptions;
    long _lastStatAt, _loopOffset, _nextRenderAt;

    long Now => (long)(_clock.ElapsedMilliseconds * opts.Speed) - _loopOffset;

    public void Start()
    {
        link.Exception += (call, ex, index) =>
        {
            _exceptions++;
            Say($"! {call}: {ex} (index {index})");
            if (!call.StartsWith("Create ")) return;
            // A create that the sim rejected: try once more with the fallback title.
            var src = uint.Parse(call[7..], CultureInfo.InvariantCulture);
            if (!_bySource.TryGetValue(src, out var live) || live.Fallback) { if (live is not null) { live.Failed = true; _failed++; } return; }
            live.Fallback = true;
            Say($"  retrying {live.Info.Title} as {opts.Fallback}");
            Create(live, opts.Fallback, "");
        };
        link.Sim.OnRecvAssignedObjectId += OnAssigned;
        link.Call("Define Drive", s => DriveState.Define(s, DEF.Drive));
        link.Call("Define Appearance", s => Appearance.Define(s, DEF.Appearance));
        link.Call("Define Measure", s => Measured.Define(s, DEF.Measure));
        for (var i = 1; i <= 4; i++)
        {
            var n = i;
            link.Call($"Define Eng{n}", s => EngineState.Define(s, (DEF)((uint)DEF.Eng1 + n - 1), n));
        }
        link.Call("Map FreezeLatLon", s => s.MapClientEventToSimEvent(EVT.FreezeLatLon, "FREEZE_LATITUDE_LONGITUDE_SET"));
        link.Call("Map FreezeAlt", s => s.MapClientEventToSimEvent(EVT.FreezeAlt, "FREEZE_ALTITUDE_SET"));
        link.Call("Map FreezeAtt", s => s.MapClientEventToSimEvent(EVT.FreezeAtt, "FREEZE_ATTITUDE_SET"));
        link.EnableFrameRate();
        if (opts.InterpFrame) link.Frame += () => RenderAll(Now);
        link.Sim.OnRecvSimobjectData += OnMeasured;
        _lastStatAt = 0;
    }

    public void Tick()
    {
        var now = Now;

        while (_cursor < capture.States.Count && capture.States[_cursor].T <= now)
        {
            var (t, id, s) = capture.States[_cursor++];
            if (s.IsUser != 0) continue;
            if (!capture.Objects.TryGetValue(id, out var info) || info.User) continue;
            Apply(id, info, s, t);
        }
        while (_rmCursor < capture.Removes.Count && capture.Removes[_rmCursor].T <= now)
        {
            var (_, id) = capture.Removes[_rmCursor++];
            if (_bySource.Remove(id, out var live)) Remove(live, "capture");
        }

        if (opts.InterpHz > 0 && now >= _nextRenderAt)
        {
            _nextRenderAt = now + (long)(1000 / opts.InterpHz);
            RenderAll(now);
        }

        if (_cursor >= capture.States.Count)
        {
            if (!opts.Loop) { Say("capture finished; Ctrl-C to remove and exit"); _cursor = int.MaxValue; }
            else { _loopOffset += capture.DurationMs; _cursor = 0; _rmCursor = 0; Say("looping"); }
        }

        if (_clock.ElapsedMilliseconds - _lastStatAt >= 5000)
        {
            _lastStatAt = _clock.ElapsedMilliseconds;
            var fps = link.TakeFps();
            var line = $"  {_created} created ({_bySource.Values.Count(l => l.Fallback)} fallback, {_failed} failed), {_bySource.Values.Count(l => l.SimId != 0)} live, {_writes} writes, {_exceptions} exceptions, fps {fps.Avg:0.0} (min {fps.Min:0.0})";
            if (opts.Measure) line += "  " + _score.Report();
            Say(line);
        }
    }

    public void Finish()
    {
        if (opts.Leave) { Console.WriteLine($"Leaving {_bySim.Count} objects in the sim (--leave); watch whether they vanish on disconnect."); return; }
        foreach (var live in _bySim.Values.ToArray()) Remove(live, "exit");
        // Give the removes a moment to go out before the connection closes.
        var until = _clock.ElapsedMilliseconds + 500;
        while (_clock.ElapsedMilliseconds < until) { try { link.Sim.ReceiveMessage(); } catch { break; } Thread.Sleep(10); }
        Console.WriteLine($"Removed {_created - _failed} objects.");
    }

    // -- sample intake ----------------------------------------------------------------------

    void Apply(uint id, ObjInfo info, ObjectState s, long t)
    {
        Offset(ref s);
        if (!_bySource.TryGetValue(id, out var live))
        {
            if (_bySource.Count >= opts.Max) return;
            live = new Live { Info = info, Index = ++_nextIndex };
            _bySource[id] = live;
            _lastState[id] = s;
            Create(live, info.Title, info.Livery, s);
            return;
        }
        if (live.Failed) return;
        if (live.SimId == 0) { _lastState[id] = s; return; }
        if (opts.Measure) Measure(live, s.GroundSpeed > 1);
        if (opts.RateHz > 0 && t - live.LastSentAt < 1000 / opts.RateHz) return;
        live.LastSentAt = t;

        if (opts.Interp)
        {
            live.Prev = live.Curr;
            live.Curr = (t, s);
            Dress(live, s);         // appearance and engines are discrete; no need to interpolate them
        }
        else Drive(live, s);
    }

    // -- create / remove --------------------------------------------------------------------

    void Create(Live live, string title, string livery, ObjectState? first = null)
    {
        var s = first ?? _lastState[live.Info.SourceId];
        var init = new SIMCONNECT_DATA_INITPOSITION
        {
            Latitude = s.Lat, Longitude = s.Lon, Altitude = s.Alt,
            Pitch = s.Pitch, Bank = s.Bank, Heading = s.HeadingTrue,
            OnGround = (uint)s.OnGround, Airspeed = (uint)Math.Max(0, s.GroundSpeed)
        };
        var tail = live.Info.Tail.Length > 0 ? opts.TailPrefix + live.Info.Tail : opts.TailPrefix + live.Index;
        var req = (REQ)(CreateBase + live.Index);
        _byRequest[(uint)req] = live;
        var useEx1 = link.IsMsfs2024 && !opts.Plain;
        var aircraft = live.Info.IsAircraft;
        link.Call($"Create {live.Info.SourceId}", sim =>
        {
            if (aircraft)
            {
                if (useEx1) sim.AICreateNonATCAircraft_EX1(title, livery, tail, init, req);
                else sim.AICreateNonATCAircraft(title, tail, init, req);
            }
            else
            {
                if (useEx1) sim.AICreateSimulatedObject_EX1(title, livery, init, req);
                else sim.AICreateSimulatedObject(title, init, req);
            }
        });
        Say($"+ {live.Index,3} {(aircraft ? "" : "[" + live.Info.Category + "] ")}{title}{(livery.Length > 0 ? " / " + livery : "")} [{tail}] {(s.OnGround != 0 ? "ground" : "air")} gs={s.GroundSpeed:0} eng={(s.NumEngines < 0 ? "?" : s.EngineMask.ToString())}");
    }

    void OnAssigned(SimConnect _, SIMCONNECT_RECV_ASSIGNED_OBJECT_ID a)
    {
        if (!_byRequest.Remove(a.dwRequestID, out var live)) return;
        live.SimId = a.dwObjectID;
        _bySim[a.dwObjectID] = live;
        _created++;
        if (!opts.NoRelease)
        {
            live.Released = true;
            link.Call($"Release {live.Info.SourceId}", sim => sim.AIReleaseControl(a.dwObjectID, (REQ)(ReleaseBase + live.Index)));
        }
        if (opts.Freeze)
        {
            foreach (var evt in new[] { EVT.FreezeLatLon, EVT.FreezeAlt, EVT.FreezeAtt })
                link.Call($"{evt} {live.Info.SourceId}", sim => sim.TransmitClientEvent(a.dwObjectID, evt, 1, GRP.Highest, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY));
        }
        if (_lastState.Remove(live.Info.SourceId, out var s))
        {
            Drive(live, s);
            if (opts.Interp) live.Curr = (live.LastSentAt = 0, s);
        }
    }

    void Remove(Live live, string why)
    {
        if (live.SimId == 0) return;
        if (live.Measuring) Measure(live, false);
        _bySim.Remove(live.SimId);
        link.Call($"Remove {live.Info.SourceId}", sim => sim.AIRemoveObject(live.SimId, (REQ)(RemoveBase + live.Index)));
        Say($"- {live.Index,3} {live.Info.Title} ({why}, {live.Writes} writes)");
        live.SimId = 0;
    }

    // -- driving ----------------------------------------------------------------------------

    void Drive(Live live, ObjectState s)
    {
        Write(live, DriveState.From(s));
        Dress(live, s);
    }

    void Write(Live live, DriveState d)
    {
        link.Call($"Drive {live.Info.SourceId}", sim => sim.SetDataOnSimObject(DEF.Drive, live.SimId, SIMCONNECT_DATA_SET_FLAG.DEFAULT, d));
        _writes++; live.Writes++;
    }

    void Dress(Live live, ObjectState s)
    {
        if (!live.Info.IsAircraft) return;   // vehicles reject every appearance datum (DATA_ERROR on gear, flaps, lights)
        if (!opts.NoAppearance)
        {
            var ap = new Appearance
            {
                GearHandle = s.GearHandle, FlapsIndex = s.FlapsIndex,
                LightStrobe = s.LightStrobe, LightLanding = s.LightLanding, LightTaxi = s.LightTaxi, LightBeacon = s.LightBeacon, LightNav = s.LightNav
            };
            if (live.LastAppearance is not { } last || !last.Equals(ap))
            {
                live.LastAppearance = ap;
                link.Call($"Appearance {live.Info.SourceId}", sim => sim.SetDataOnSimObject(DEF.Appearance, live.SimId, SIMCONNECT_DATA_SET_FLAG.DEFAULT, ap));
            }
        }
        if (opts.NoEngines || s.NumEngines <= 0) return;
        var mask = s.EngineMask;
        if (mask == live.LastEngines) return;
        live.LastEngines = mask;
        for (var i = 1; i <= Math.Min(4, s.NumEngines); i++)
        {
            var def = (DEF)((uint)DEF.Eng1 + i - 1);
            var st = new EngineState { Combustion = mask >> (i - 1) & 1 };
            link.Call($"Engine{i} {live.Info.SourceId}", sim => sim.SetDataOnSimObject(def, live.SimId, SIMCONNECT_DATA_SET_FLAG.DEFAULT, st));
        }
    }

    void RenderAll(long now)
    {
        foreach (var live in _bySim.Values) Render(live, now);
    }

    /// <summary>
    /// Interpolated pose one sample interval behind real time, so the object is always moving
    /// between two known samples rather than being extrapolated past the newest one.
    /// </summary>
    void Render(Live live, long now)
    {
        if (live.SimId == 0 || live.Curr is not { } curr) return;
        if (live.Prev is not { } prev)
        {
            return; // one sample: placed at create, nothing to interpolate yet
        }
        var a = prev.S; var b = curr.S;
        var stationary = a.Lat == b.Lat && a.Lon == b.Lon && a.Alt == b.Alt && a.HeadingTrue == b.HeadingTrue && a.Pitch == b.Pitch && a.Bank == b.Bank;
        if (stationary && live.RenderedCurrT == curr.T) return;   // parked: nothing changed since it was last written

        var interval = Math.Max(50, curr.T - prev.T);
        var rt = now - interval;
        var alpha = Math.Clamp((rt - prev.T) / (double)interval, 0, 1);
        if (alpha >= 1 && live.RenderedFinal && live.RenderedCurrT == curr.T) return; // already sat on the newest sample

        var heading = LerpAngle(a.HeadingTrue, b.HeadingTrue, alpha);
        var d = new DriveState
        {
            Lat = Lerp(a.Lat, b.Lat, alpha), Lon = Lerp(a.Lon, b.Lon, alpha), Alt = Lerp(a.Alt, b.Alt, alpha),
            Pitch = LerpAngle(a.Pitch, b.Pitch, alpha), Bank = LerpAngle(a.Bank, b.Bank, alpha), HeadingTrue = heading
        };
        if (opts.Freeze)
        {
            // Frozen: velocities are cosmetic (wheel spin, sounds). Use the sample's own.
            d.VelBodyX = b.VelBodyX; d.VelBodyY = b.VelBodyY; d.VelBodyZ = b.VelBodyZ;
            d.RotX = b.RotX; d.RotY = b.RotY; d.RotZ = b.RotZ;
        }
        else
        {
            // Not frozen: the sim will reckon forward from this pose until the next write, so
            // give it the velocity of the path we are walking, not the sample's.
            var dt = interval / 1000.0;
            var north = (b.Lat - a.Lat) * 60 * Geo.MetersPerNm / dt;
            var east = (b.Lon - a.Lon) * Math.Cos(a.Lat * Math.PI / 180) * 60 * Geo.MetersPerNm / dt;
            var up = (b.Alt - a.Alt) * 0.3048 / dt;
            var h = heading * Math.PI / 180;
            d.VelBodyZ = (north * Math.Cos(h) + east * Math.Sin(h)) / 0.3048;    // forward, ft/s
            d.VelBodyX = (-north * Math.Sin(h) + east * Math.Cos(h)) / 0.3048;   // right, ft/s
            d.VelBodyY = up / 0.3048;
        }
        Write(live, d);
        live.RenderedCurrT = curr.T;
        live.RenderedFinal = alpha >= 1;
    }

    // -- measurement ------------------------------------------------------------------------

    /// <summary>Start or stop the per-frame read-back of one object as it starts or stops moving.</summary>
    void Measure(Live live, bool moving)
    {
        if (moving == live.Measuring || live.SimId == 0) return;
        live.Measuring = moving;
        var period = moving ? SIMCONNECT_PERIOD.SIM_FRAME : SIMCONNECT_PERIOD.NEVER;
        link.Call($"Measure {live.Info.SourceId}", sim => sim.RequestDataOnSimObject((REQ)(MeasureBase + live.Index), DEF.Measure, live.SimId, period, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0));
        if (!moving) _score.Forget(live.SimId);
    }

    void OnMeasured(SimConnect _, SIMCONNECT_RECV_SIMOBJECT_DATA d)
    {
        if (d.dwRequestID < MeasureBase || d.dwData is not { Length: > 0 }) return;
        if (!_bySim.TryGetValue(d.dwObjectID, out var live)) return;
        var m = (Measured)d.dwData[0];
        if (!live.MeasureLogged)
        {
            live.MeasureLogged = true;
            Say($"  measured {live.Info.Title}: freeze latlon={m.FreezeLatLon} alt={m.FreezeAlt} att={m.FreezeAtt}");
        }
        _score.Add(d.dwObjectID, live.Info.Title, m.Lat, m.Lon);
    }

    static double Lerp(double a, double b, double t) => a + (b - a) * t;
    static double LerpAngle(double a, double b, double t)
    {
        var delta = ((b - a) % 360 + 540) % 360 - 180;
        return a + delta * t;
    }

    void Offset(ref ObjectState s)
    {
        if (opts.OffsetNm == 0) return;
        var brg = opts.OffsetDeg * Math.PI / 180;
        var dLat = opts.OffsetNm / 60.0 * Math.Cos(brg);
        var dLon = opts.OffsetNm / 60.0 * Math.Sin(brg) / Math.Cos(s.Lat * Math.PI / 180);
        s.Lat += dLat; s.Lon += dLon;
    }

    void Say(string m) { if (!opts.Quiet) Console.WriteLine($"[{_clock.ElapsedMilliseconds / 1000.0,8:0.0}] {m}"); }

    enum REQ : uint;
}
