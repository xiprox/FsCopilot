using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.FlightSimulator.SimConnect;
using Traffic.Sim;

// TrafficRead — stage 0 of record/traffic-atc.
//
// Polls RequestDataOnSimObjectType for every enabled object type at a flat rate, diffs
// consecutive polls for adds and removes, reads identity once per new object, and writes it
// all as NDJSON. Also subscribes to ObjectAdded/ObjectRemoved purely to log them beside the
// diff, so the two can be compared.
//
//   TrafficRead [--radius <km>] [--rate <hz>] [--types aircraft,helicopter,ground,boat,all]
//               [--out <file|dir>] [--minutes <n>] [--quiet]

var opts = Options.Parse(args);
if (opts is null) return 2;

var outPath = opts.ResolveOut();
Console.WriteLine($"TrafficRead  radius={opts.RadiusMeters / 1000.0:0.#} km  rate={opts.RateHz} Hz  types={string.Join(",", opts.Types)}  out={outPath}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
if (opts.Minutes > 0) cts.CancelAfter(TimeSpan.FromMinutes(opts.Minutes));

// Wait for the sim; the tool is started before the flight more often than not.
SimLink? link = null;
while (link is null && !cts.IsCancellationRequested)
{
    try
    {
        link = new SimLink("FSC TrafficRead");
        if (!link.WaitOpen(TimeSpan.FromSeconds(5))) { link.Dispose(); link = null; }
    }
    catch (Exception)
    {
        link = null;
        Console.Write("."); // no sim yet
        try { await Task.Delay(2000, cts.Token); } catch (OperationCanceledException) { }
    }
}
if (link is null) return 1;
Console.WriteLine($"\nConnected to {link.SimName} {link.SimVersion} ({(link.IsMsfs2024 ? "MSFS 2024" : "MSFS 2020")})");

using var file = new StreamWriter(outPath, false, new UTF8Encoding(false)) { AutoFlush = false };
var reader = new Reader(link, opts, file);
reader.Start();
link.Run(reader.Tick, cts.Token);
reader.Finish();
file.Flush();
Console.WriteLine($"\nWrote {outPath}");
return 0;

// ---------------------------------------------------------------------------------------

sealed class Options
{
    public uint RadiusMeters = Geo.MaxRadiusMeters;
    public double RateHz = 2;
    public List<SIMCONNECT_SIMOBJECT_TYPE> Types = [SIMCONNECT_SIMOBJECT_TYPE.AIRCRAFT, SIMCONNECT_SIMOBJECT_TYPE.HELICOPTER];
    public string? Out;
    public double Minutes;
    public bool Quiet;
    public bool Measure;
    public bool EverySample;          // disable the change gate: record every poll of every object
    public double HeartbeatS = 5;     // unchanged objects are re-sent this often

    public static Options? Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            try
            {
                switch (args[i])
                {
                    case "--radius":
                        var km = double.Parse(Next(), CultureInfo.InvariantCulture);
                        o.RadiusMeters = (uint)Math.Min(Geo.MaxRadiusMeters, Math.Max(1000, km * 1000));
                        break;
                    case "--rate": o.RateHz = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--types":
                        o.Types = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(t => t.ToLowerInvariant() switch
                            {
                                "aircraft" => SIMCONNECT_SIMOBJECT_TYPE.AIRCRAFT,
                                "helicopter" => SIMCONNECT_SIMOBJECT_TYPE.HELICOPTER,
                                "ground" => SIMCONNECT_SIMOBJECT_TYPE.GROUND,
                                "boat" => SIMCONNECT_SIMOBJECT_TYPE.BOAT,
                                "all" => SIMCONNECT_SIMOBJECT_TYPE.ALL,
                                var other => throw new ArgumentException($"unknown type {other}")
                            }).Distinct().ToList();
                        break;
                    case "--out": o.Out = Next(); break;
                    case "--minutes": o.Minutes = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--quiet": o.Quiet = true; break;
                    case "--measure": o.Measure = true; break;
                    case "--every-sample": o.EverySample = true; break;
                    case "--heartbeat": o.HeartbeatS = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "-h": case "--help": Usage(); return null;
                    default: throw new ArgumentException($"unknown option {args[i]}");
                }
            }
            catch (Exception e) when (e is ArgumentException or FormatException)
            {
                Console.Error.WriteLine(e.Message);
                Usage();
                return null;
            }
        }
        if (o.RateHz <= 0 || o.RateHz > 20) { Console.Error.WriteLine("--rate must be in (0, 20]"); return null; }
        return o;
    }

    public string ResolveOut()
    {
        var name = $"traffic-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.ndjson";
        if (Out is null)
            return Directory.Exists("recordings") ? Path.Combine("recordings", name) : name;
        return Directory.Exists(Out) ? Path.Combine(Out, name) : Out;
    }

    static void Usage() => Console.Error.WriteLine(
        "TrafficRead [--radius <km, max 200>] [--rate <hz>] [--types aircraft,helicopter,ground,boat,all] [--out <file|dir>] [--minutes <n>]\n" +
        "            [--measure] [--every-sample] [--heartbeat <s>] [--quiet]\n" +
        "  --measure        read every moving object back each sim frame and report a motion-smoothness score with the stats (the baseline)\n" +
        "  --every-sample   record every poll of every object; by default an unchanged object is only re-sent every --heartbeat seconds (5)");
}

sealed class Reader(SimLink link, Options opts, StreamWriter file)
{
    enum DEF : uint { State = 1, Identity, Livery, Measure }
    enum REQ : uint { Identity = 1, Livery, StateBase = 100, MeasureBase = 5000 }
    readonly MotionScore _score = new();
    uint _nextMeasureReq = (uint)REQ.MeasureBase;
    enum EVT : uint { ObjectAdded = 1, ObjectRemoved }

    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Dictionary<uint, Tracked> _objects = new();
    readonly HashSet<uint> _seenThisPoll = [];
    readonly Dictionary<uint, TypePoll> _polls = new();   // keyed by request id
    readonly JsonSerializerOptions _json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    double _userLat, _userLon; bool _haveUser;
    long _nextPollAt;
    int _pollsDone, _pollsIncomplete, _messages, _linesWritten, _samplesSeen, _samplesSent;
    double _pollMsSum, _pollMsMax;
    TimeSpan _cpuAt; long _cpuClockAt; long _lastStatAt;
    readonly long _pollPeriodMs = (long)(1000 / opts.RateHz);

    sealed class Tracked
    {
        public SIMCONNECT_SIMOBJECT_TYPE Type;
        public bool HaveIdentity;
        public string? Title;
        public long FirstSeen;
        public long LastSeen;
        public double LastDistanceNm;
        public int Samples;
        public uint MeasureReq;     // 0 = not being measured
        // The change gate: what was last sent, and the newest sample held back as unchanged.
        public ObjectState? Sent; public long SentAt;
        public ObjectState? Held; public long HeldAt; public double HeldDist;
    }

    sealed class TypePoll
    {
        public SIMCONNECT_SIMOBJECT_TYPE Type;
        public long StartedAt;
        public int Received;
        public uint OutOf;
        public bool Complete;
    }

    public void Start()
    {
        var sim = link.Sim;
        link.Exception += (call, ex, index) =>
        {
            Line(new { t = T(), k = "exception", call, ex = ex.ToString(), index });
            Say($"! {call}: {ex} (index {index})");
        };

        link.Call("Define State", s => ObjectState.Define(s, DEF.State));
        link.Call("Define Identity", s => ObjectIdentity.Define(s, DEF.Identity));
        link.Call("Define Livery", s => LiveryProbe.Define(s, DEF.Livery));
        link.Call("Define Measure", s => Measured.Define(s, DEF.Measure));

        sim.OnRecvSimobjectDataBytype += OnByType;
        sim.OnRecvSimobjectData += OnObjectData;
        sim.OnRecvEventObjectAddremove += OnAddRemove;
        link.EnableFrameRate();
        link.Call("Subscribe ObjectAdded", s => s.SubscribeToSystemEvent(EVT.ObjectAdded, "ObjectAdded"));
        link.Call("Subscribe ObjectRemoved", s => s.SubscribeToSystemEvent(EVT.ObjectRemoved, "ObjectRemoved"));

        Line(new
        {
            t = 0, k = "meta", started = DateTime.Now.ToString("o"),
            sim = link.SimName, version = link.SimVersion, msfs2024 = link.IsMsfs2024,
            radius_m = opts.RadiusMeters, rate_hz = opts.RateHz, types = opts.Types.Select(t => t.ToString()).ToArray()
        });

        _cpuAt = Process.GetCurrentProcess().TotalProcessorTime;
        _cpuClockAt = _clock.ElapsedMilliseconds;
        _lastStatAt = _cpuClockAt;
        _nextPollAt = 0;
    }

    public void Tick()
    {
        var now = _clock.ElapsedMilliseconds;
        if (now >= _nextPollAt)
        {
            FinishPoll(now, forced: true);
            StartPoll(now);
            _nextPollAt = now + _pollPeriodMs;
        }
        if (now - _lastStatAt >= 5000) { Stats(now); _lastStatAt = now; }
    }

    public void Finish()
    {
        Stats(_clock.ElapsedMilliseconds);
        Line(new { t = T(), k = "end", objects = _objects.Count });
    }

    // -- polling ----------------------------------------------------------------------------

    void StartPoll(long now)
    {
        _polls.Clear();
        _seenThisPoll.Clear();
        var i = 0u;
        foreach (var type in opts.Types)
        {
            var req = (uint)REQ.StateBase + i++;
            _polls[req] = new TypePoll { Type = type, StartedAt = now };
            var t = type;
            link.Call($"Poll {type}", s => s.RequestDataOnSimObjectType((REQ)req, DEF.State, opts.RadiusMeters, t));
        }
    }

    /// <summary>Close the poll in flight: emit removes for objects that were not in it.</summary>
    void FinishPoll(long now, bool forced)
    {
        if (_polls.Count == 0) return;
        var allComplete = _polls.Values.All(p => p.Complete);
        if (!allComplete)
        {
            if (!forced) return;
            _pollsIncomplete++;
            // An incomplete poll says nothing about who is gone; skip the diff rather than
            // remove aircraft that simply had not been reported yet.
            _polls.Clear();
            return;
        }

        foreach (var (id, tracked) in _objects.ToArray())
        {
            if (_seenThisPoll.Contains(id)) continue;
            _objects.Remove(id);
            Measure(id, tracked, false);
            Line(new { t = T(), k = "rm", o = id, reason = "poll", seen_s = (now - tracked.FirstSeen) / 1000.0 });
            Say($"- {id} {tracked.Title ?? "?"} gone ({tracked.Samples} samples)");
        }
        _polls.Clear();
    }

    void OnByType(SimConnect _, SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE d)
    {
        _messages++;
        if (!_polls.TryGetValue(d.dwRequestID, out var poll)) return; // late reply from a poll already closed

        var now = _clock.ElapsedMilliseconds;
        if (d.dwoutof == 0 || d.dwData is not { Length: > 0 })
        {
            Complete(poll, now);
            return;
        }

        var st = (ObjectState)d.dwData[0];
        var id = d.dwObjectID;
        if (st.IsUser != 0) { _userLat = st.Lat; _userLon = st.Lon; _haveUser = true; }

        _seenThisPoll.Add(id);
        if (!_objects.TryGetValue(id, out var tracked))
        {
            tracked = new Tracked { Type = poll.Type, FirstSeen = now };
            _objects[id] = tracked;
            Line(new { t = T(), k = "add", o = id, type = poll.Type.ToString(), user = st.IsUser != 0 });
            link.Call($"Identity {id}", s => s.RequestDataOnSimObject(REQ.Identity, DEF.Identity, id, SIMCONNECT_PERIOD.ONCE, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0));
            link.Call($"Livery {id}", s => s.RequestDataOnSimObject(REQ.Livery, DEF.Livery, id, SIMCONNECT_PERIOD.ONCE, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0));
        }
        tracked.LastSeen = now;
        tracked.Samples++;
        var dist = _haveUser && st.IsUser == 0 ? Geo.DistanceNm(_userLat, _userLon, st.Lat, st.Lon) : 0;
        tracked.LastDistanceNm = dist;
        if (opts.Measure && st.IsUser == 0) Measure(id, tracked, st.GroundSpeed > 1);

        Gate(id, tracked, st, dist, now);

        poll.Received++;
        poll.OutOf = d.dwoutof;
        if (d.dwentrynumber >= d.dwoutof) Complete(poll, now);
    }

    void Complete(TypePoll poll, long now)
    {
        if (poll.Complete) return;
        poll.Complete = true;
        var ms = now - poll.StartedAt;
        _pollMsSum += ms; _pollMsMax = Math.Max(_pollMsMax, ms); _pollsDone++;
        if (_polls.Values.All(p => p.Complete)) FinishPoll(now, forced: false);
    }

    // -- the change gate --------------------------------------------------------------------

    /// <summary>
    /// Send a sample only when it differs from the last one sent, or the heartbeat is due. When
    /// a change follows a run of suppressed samples, the last suppressed one goes first with its
    /// own timestamp, so the receiver knows the motion began after that moment and does not
    /// interpolate the first step across the whole quiet period.
    /// </summary>
    void Gate(uint id, Tracked tracked, in ObjectState st, double dist, long now)
    {
        _samplesSeen++;
        if (opts.EverySample || tracked.Sent is not { } sent) { Send(id, st, dist, now); tracked.Sent = st; tracked.SentAt = now; tracked.Held = null; return; }

        var changed = Changed(sent, st);
        var heartbeat = now - tracked.SentAt >= opts.HeartbeatS * 1000;
        if (!changed && !heartbeat)
        {
            tracked.Held = st; tracked.HeldAt = now; tracked.HeldDist = dist;
            return;
        }
        if (changed && tracked.Held is { } held && tracked.HeldAt > tracked.SentAt)
            Send(id, held, tracked.HeldDist, tracked.HeldAt, resend: true);
        Send(id, st, dist, now);
        tracked.Sent = st; tracked.SentAt = now; tracked.Held = null;
    }

    static bool Changed(in ObjectState a, in ObjectState b) =>
        Math.Abs(a.Lat - b.Lat) > 1e-6 || Math.Abs(a.Lon - b.Lon) > 1e-6 ||   // ~0.1 m
        Math.Abs(a.Alt - b.Alt) > 0.5 ||
        Math.Abs(a.HeadingTrue - b.HeadingTrue) > 0.1 || Math.Abs(a.Pitch - b.Pitch) > 0.1 || Math.Abs(a.Bank - b.Bank) > 0.1 ||
        Math.Abs(a.GroundSpeed - b.GroundSpeed) > 0.5 ||
        a.OnGround != b.OnGround || a.FlapsIndex != b.FlapsIndex || Math.Abs(a.GearHandle - b.GearHandle) > 0.05 ||
        a.EngineMask != b.EngineMask || a.NumEngines != b.NumEngines ||
        (a.LightStrobe, a.LightLanding, a.LightTaxi, a.LightBeacon, a.LightNav) != (b.LightStrobe, b.LightLanding, b.LightTaxi, b.LightBeacon, b.LightNav);

    void Send(uint id, in ObjectState st, double dist, long t, bool resend = false)
    {
        _samplesSent++;
        Line(new
        {
            t, k = "s", o = id,
            lat = R(st.Lat, 7), lon = R(st.Lon, 7), alt = R(st.Alt, 1),
            p = R(st.Pitch, 2), b = R(st.Bank, 2), h = R(st.HeadingTrue, 2),
            vwx = R(st.VelWorldX, 2), vwy = R(st.VelWorldY, 2), vwz = R(st.VelWorldZ, 2),
            vbx = R(st.VelBodyX, 2), vby = R(st.VelBodyY, 2), vbz = R(st.VelBodyZ, 2),
            rx = R(st.RotX, 4), ry = R(st.RotY, 4), rz = R(st.RotZ, 4),
            gs = R(st.GroundSpeed, 1), vs = R(st.VerticalSpeed, 0),
            g = st.OnGround, u = st.IsUser,
            gear = R(st.GearHandle, 2), flap = R(st.FlapsHandle, 0), fi = st.FlapsIndex,
            ne = st.NumEngines, e = st.EngineMask,
            l = st.LightStrobe | st.LightLanding << 1 | st.LightTaxi << 2 | st.LightBeacon << 3 | st.LightNav << 4,
            d = R(dist, 2),
            rs = resend ? 1 : (int?)null
        });
    }

    /// <summary>Start or stop the per-frame read-back of one object as it starts or stops moving.</summary>
    void Measure(uint id, Tracked tracked, bool moving)
    {
        if (moving == (tracked.MeasureReq != 0)) return;
        var req = moving ? _nextMeasureReq++ : tracked.MeasureReq;
        var period = moving ? SIMCONNECT_PERIOD.SIM_FRAME : SIMCONNECT_PERIOD.NEVER;
        link.Call($"Measure {id}", s => s.RequestDataOnSimObject((REQ)req, DEF.Measure, id, period, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0));
        tracked.MeasureReq = moving ? req : 0;
        if (!moving) _score.Forget(id);
    }

    void OnObjectData(SimConnect _, SIMCONNECT_RECV_SIMOBJECT_DATA d)
    {
        _messages++;
        if (d.dwData is not { Length: > 0 }) return;
        if (d.dwRequestID >= (uint)REQ.MeasureBase)
        {
            var m = (Measured)d.dwData[0];
            _score.Add(d.dwObjectID, _objects.TryGetValue(d.dwObjectID, out var tr) ? tr.Title ?? "?" : "?", m.Lat, m.Lon);
            return;
        }
        switch ((REQ)d.dwRequestID)
        {
            case REQ.Identity:
            {
                var idn = (ObjectIdentity)d.dwData[0];
                if (_objects.TryGetValue(d.dwObjectID, out var tracked)) { tracked.HaveIdentity = true; tracked.Title = idn.Title; }
                Line(new
                {
                    t = T(), k = "id", o = d.dwObjectID,
                    title = idn.Title, tail = idn.AtcId, airline = idn.AtcAirline, flight = idn.AtcFlightNumber,
                    model = idn.AtcModel, type = idn.AtcType, cat = idn.Category, user = idn.IsUser != 0
                });
                Say($"+ {d.dwObjectID} {idn.Title} [{idn.AtcId}] {idn.AtcModel} {idn.Category}{(idn.IsUser != 0 ? " (user)" : "")}");
                break;
            }
            case REQ.Livery:
            {
                var lv = (LiveryProbe)d.dwData[0];
                Line(new { t = T(), k = "livery", o = d.dwObjectID, livery = lv.LiveryName });
                break;
            }
        }
    }

    void OnAddRemove(SimConnect _, SIMCONNECT_RECV_EVENT_OBJECT_ADDREMOVE e)
    {
        _messages++;
        var kind = (EVT)e.uEventID == EVT.ObjectAdded ? "event_add" : "event_rm";
        Line(new { t = T(), k = kind, o = e.dwData, type = e.eObjType.ToString() });
    }

    // -- stats ------------------------------------------------------------------------------

    void Stats(long now)
    {
        var proc = Process.GetCurrentProcess();
        var cpu = proc.TotalProcessorTime;
        var cpuPct = (cpu - _cpuAt).TotalMilliseconds / Math.Max(1, now - _cpuClockAt) * 100.0 / Environment.ProcessorCount;
        _cpuAt = cpu; _cpuClockAt = now;

        var byType = _objects.Values.Where(o => o.Type != SIMCONNECT_SIMOBJECT_TYPE.USER).GroupBy(o => o.Type)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());
        var dists = _objects.Values.Select(o => o.LastDistanceNm).Where(d => d > 0).OrderBy(d => d).ToArray();
        var fps = link.TakeFps();
        var stat = new
        {
            t = T(), k = "stat",
            fps = R(fps.Avg, 1), fps_min = R(fps.Min, 1),
            objects = _objects.Count, by_type = byType,
            polls = _pollsDone, polls_incomplete = _pollsIncomplete,
            poll_ms_avg = _pollsDone > 0 ? R(_pollMsSum / _pollsDone, 1) : 0, poll_ms_max = _pollMsMax,
            msgs = _messages, lines = _linesWritten,
            samples_seen = _samplesSeen, samples_sent = _samplesSent,
            gate_pct = _samplesSeen > 0 ? R(100.0 * _samplesSent / _samplesSeen, 1) : 100,
            cpu_pct = R(cpuPct, 1), mem_mb = R(proc.WorkingSet64 / 1048576.0, 0),
            dist_nm = dists.Length == 0 ? null : new { min = R(dists[0], 1), median = R(dists[dists.Length / 2], 1), max = R(dists[^1], 1) },
            unidentified = _objects.Values.Count(o => !o.HaveIdentity),
            jitter = opts.Measure ? _score.Report() : null
        };
        Line(stat);
        file.Flush();
        Say($"  {_objects.Count} objects, {_pollsDone} polls (avg {stat.poll_ms_avg} ms, max {_pollMsMax} ms, {_pollsIncomplete} incomplete), cpu {stat.cpu_pct}%, far {stat.dist_nm?.max ?? 0} nm, fps {stat.fps} (min {stat.fps_min}), sent {stat.gate_pct}% of samples{(opts.Measure ? "  " + stat.jitter : "")}");
    }

    // -- output -----------------------------------------------------------------------------

    long T() => _clock.ElapsedMilliseconds;
    static double R(double v, int digits) => Math.Round(v, digits);

    void Line<T>(T record)
    {
        file.WriteLine(JsonSerializer.Serialize(record, _json));
        _linesWritten++;
    }

    void Say(string s)
    {
        if (!opts.Quiet) Console.WriteLine($"[{T() / 1000.0,8:0.0}] {s}");
    }
}
