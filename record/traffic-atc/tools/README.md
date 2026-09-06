# tools

The single-machine prototype. .NET 9, x64, against the managed SimConnect DLL the fork ships
(referenced in place from `FsCopilot/`, so this only builds inside the repository).

```bash
dotnet build tools/Traffic.sln
```

## TrafficRead (stage 0)

Run from `record/traffic-atc/` so captures land in `recordings/`:

```bash
dotnet run --project tools/TrafficRead -- --rate 2
```

| Option | Default | Meaning |
| --- | --- | --- |
| `--radius <km>` | 200 | Enumeration radius. Clamped to the SDK cap of 200 km. |
| `--rate <hz>` | 2 | Poll rate. Each poll is one `RequestDataOnSimObjectType` per type. |
| `--types a,b` | `aircraft,helicopter` | Any of `aircraft`, `helicopter`, `ground`, `boat`, `all`. |
| `--out <file\|dir>` | `recordings/` if it exists | Where the NDJSON goes. |
| `--minutes <n>` | unlimited | Stop after this long. Ctrl-C also stops cleanly. |
| `--quiet` | | Only stats on the console. |
| `--measure` | | Score the smoothness of the sim's own moving traffic (see TrafficInject); the baseline for the injector. |
| `--every-sample` | | Disable the change gate and record every poll of every object. |
| `--heartbeat <s>` | 5 | With the gate on, an unchanged object is still re-sent this often. |

It waits for the sim if started first, and stops when the sim quits.

**The change gate** is on by default because it is what the host will do on the wire: a
sample is written only when it differs from the last one written (position ~0.1 m, altitude
0.5 ft, angles 0.1°, speed 0.5 kt, or any discrete field), or when the heartbeat is due. When
a change follows a run of suppressed samples, the last suppressed one is written first with
its own timestamp and `rs:1`, so the replay knows when the quiet period ended. At the gate
this sends ~20 % of samples.

### The NDJSON

One object per line, `t` is milliseconds since the tool started, `k` is the kind:

| `k` | Fields | When |
| --- | --- | --- |
| `meta` | sim, version, msfs2024, radius_m, rate_hz, types | First line. |
| `add` | o, type, user | An object id appeared in a poll that was not in the previous one. |
| `id` | o, title, tail, airline, flight, model, type, cat, user | Identity, read once per `add`. |
| `livery` | o, livery | The Q03 probe, if the sim answered it. |
| `s` | o, lat, lon, alt, p, b, h, vwx/vwy/vwz, vbx/vby/vbz, rx/ry/rz, gs, vs, g, u, gear, flap, fi, ne, e, l, d, rs | One per object per poll that passes the gate. `flap` is percent (read-only in the sim), `fi` the handle index (what gets written back); `ne` engine count, `e` combustion bitmask; `l` a light bitmask (strobe, landing, taxi, beacon, nav); `d` nm from the user aircraft; `rs:1` marks a held sample re-sent ahead of a change. |
| `rm` | o, reason, seen_s | The id was missing from a complete poll. |
| `event_add` / `event_rm` | o, type | `ObjectAdded` / `ObjectRemoved` system events, logged for comparison with the diff. |
| `exception` | call, ex, index | Anything the sim rejected, named by the call that caused it. |
| `stat` | objects, by_type, polls, poll_ms_avg/max, polls_incomplete, msgs, cpu_pct, mem_mb, dist_nm, unidentified | Every 5 s. |
| `end` | objects | Last line. |

## TrafficInject (stage 1)

Replays a capture into the sim. **Close BeyondATC first** (its own traffic goes with it — the
sim removes a client's AI objects on disconnect) and have your own AI traffic off, or the
replayed aircraft spawn inside the originals.

```bash
dotnet run --project tools/TrafficInject -- recordings/<capture>.ndjson
```

| Option | Default | Meaning |
| --- | --- | --- |
| `--speed <x>` | 1 | Replay speed multiplier. |
| `--rate <hz>` | as recorded | Thin samples to at most this many per object per second. The stage-1 rate experiment. |
| `--interp <hz>` | off | Write interpolated poses at this rate, one sample interval behind, instead of relying on the sim's dead reckoning between samples. Parked objects cost no writes. |
| `--no-engines` | | Leave engines as the sim creates them (running). |
| `--interp frame` | | Write the interpolated pose once per sim frame, off the `Frame` system event. The pairing for `--freeze`. |
| `--freeze` | | Send `FREEZE_LATITUDE_LONGITUDE/ALTITUDE/ATTITUDE_SET` to each object after release, so the sim stops simulating it and only our writes move it. |
| `--measure` | | Read every moving object back each sim frame and print a smoothness score with the stats: mean frame-to-frame change in displacement over mean displacement. ~0 is constant motion, ~1 is stop-and-snap. `TrafficRead --measure` gives the same score for the add-on's own traffic — the baseline. |

## AtcAudio (stage 3)

The whole audio path in one process: process-loopback capture of the ATC app by PID, Opus,
real UDP to localhost with a simulated wire, jitter buffer with PLC, WASAPI playback. Needs
no sim.

```bash
dotnet run --project tools/AtcAudio -- --list
dotnet run --project tools/AtcAudio -- --process BeyondATC --loss 5 --delay 80 --jitter 30
```

| Option | Default | Meaning |
| --- | --- | --- |
| `--list` | | Output devices, known ATC apps running, and every process that owns an audio session (the "Choose another…" list) with its mixer level and whether it runs elevated. |
| `--process <name\|pid>` | | Capture what this process (and its children) renders, whatever device it uses. |
| `--device-capture <device>` | | The fallback: capture a whole output device instead. |
| `--out <device>` | default | Playback device, by index from `--list` or a name fragment. |
| `--bitrate <bps>` | 24000 | Opus target. |
| `--vad <dBFS>` / `--no-vad` | −50 | RMS gate; frames below it are not sent (300 ms hangover). |
| `--buffer <ms>` | 60 | Jitter buffer depth before a talkspurt starts playing. |
| `--loss <pct>`, `--delay <ms>`, `--jitter <ms>` | 0 | The simulated wire. |
| `--session-volume <0..1>` | | After 10 s, set the captured process's mixer level — the Q10 test. Windows remembers it per app; set it back to 1 afterwards. |
| `--no-compensate` | | Do not divide the captured process's mixer level back out. By default the host reads it twice a second and compensates up to +20 dB, because the mixer sits before the tap. |

Stats every 5 s: input peak/RMS and the mixer level/gain in force, frames sent and %, kbps on
the wire, received / dropped / late, PLC frames, underruns (= talkspurt ends), and **latency**
from capture stamp to leaving the speaker (~130 ms on this machine).

Both tools print the sim's frame rate (from the `Frame` system event) in their 5-second stats,
so FPS is measured, not eyeballed: `TrafficRead` with the add-on running, `TrafficRead` with
it closed, `TrafficInject` replaying — same scene, three numbers.
| `--max <n>` | all | Only the first n objects to appear. |
| `--offset <nm>,<deg>` | none | Shift every object by a distance and bearing, so copies can be told from originals with the add-on still running. |
| `--tail-prefix <s>` | none | Prepended to every tail number. Tails truncate at 9 characters. |
| `--fallback "<title>"` | `Asobo PassiveAircraft Citation CJ4` | Used when the sim rejects a title. |
| `--plain` | | Use the non-`_EX1` create even on 2024. |
| `--no-release` | | Skip `AIReleaseControl`, to see what the AI engine does with a driven object. |
| `--no-appearance` | | Do not write gear/flaps/lights. |
| `--leave` | | Exit without removing, to watch what the sim does on disconnect. |
| `--loop` | | Replay again when the capture ends. |

Ctrl-C removes every injected object and exits. Each object is created at its first state
(`AICreateNonATCAircraft_EX1` for aircraft and helicopters, `AICreateSimulatedObject_EX1`
for anything else, plain forms on 2020), released from the AI engine on the assigned-id
reply, then written pose + body velocities on every sample, and gear/flaps/lights/engines
when they change. A created aircraft comes with its engines running; the recorded
combustion state turns them off, which needs a capture that has it (`ne`/`e` fields).
