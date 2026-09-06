# 01 · Design

    Purpose:  What is proposed, and why each part is shaped the way it is.
    Scope:    Traffic and ATC audio. Not weather, not the pilot's own transmissions.
    Status:   Sketch. Written against the SDK header and the fork as of 2026-09-06;
              nothing here has run. Assumptions are numbered in 02-open-questions.

## The problem, precisely

An ATC add-on (BeyondATC, SayIntentions, FSLTL underneath either) injects AI aircraft into
**one** simulator and speaks into **one** pair of headphones. In a shared cockpit the other
pilots have neither. Today's workaround is to pipe the ATC app's audio into Discord, which
gives everyone the voice and nobody the traffic.

The session already carries the aircraft. It should carry what the aircraft is flying among.

## One host, and it need not be the master

Traffic and audio share almost nothing in mechanism, but they share the question of **who
is the host** — the one peer running the ATC app, whose traffic and audio everyone else gets.

- One host per session for both features together. Everyone else has disabled every other
  source of traffic in their own sim.
- Hosting is **orthogonal to master/slave**. The physics master and the traffic host are
  independent roles; a slave can host, a master can receive.
- Hosting is claimed by a broadcast packet naming the host peer, on the pattern of
  `MasterSwitch.SetMaster`. Changing host mid-session is allowed but not the normal case:
  the new host removes every aircraft it injected before it starts reading, and the old
  host stops sending.

Every traffic and audio packet carries the host's peer id; a receiver applies only the
current host's and drops the rest, so a late host-change message cannot leave two hosts'
aircraft mixed in one sim.

## Traffic

### Reading

`RequestDataOnSimObjectType(req, def, radiusMeters, type)`, polled at a flat rate: one call,
one reply per object in range. **The radius is capped at 200 km (~108 nm)** by the SDK — a
larger value is an `OUT_OF_BOUNDS` exception. The sync radius is a single parameter of the
reader, clamped to that cap and defaulting to it; that is what a later slider in the desktop
app would set, and what a host on a weak connection would turn down. 108 nm covers everything
a pilot will see or be told about.

The reader is **type-agnostic**: one poll per enabled `SIMCONNECT_SIMOBJECT_TYPE`, the same
state definition for all of them, `CATEGORY` carried in the identity so the injector knows
which create call to make. v1 enables `AIRCRAFT` and `HELICOPTER`; `GROUND` is a flag.

Polling was chosen over `SubscribeToSystemEvent("ObjectAdded")` deliberately. The event only
fires for objects created *after* the subscription, so a host restarting FS Copilot in cruise
would see none of the traffic that already existed, and the fix — a periodic sweep — is the
poll anyway. Adds and removes are the diff between consecutive polls. Rate is decided in
stage 1; 2–5 Hz is the range, and Little Navmap's 500 ms is the precedent that says the sim
copes.

Per object, two kinds of data:

- **Identity**, sent once when an object first appears and re-sent in full to a peer that
  joins late: `TITLE`, `LIVERY NAME` (2024 only, read through its own definition so that
  2020 can skip it — see the log of 2026-09-06), `ATC ID` (tail), `ATC AIRLINE`,
  `ATC FLIGHT NUMBER`, `ATC MODEL`, `CATEGORY`. Reliable.
- **State**, sent every poll: `PLANE LATITUDE/LONGITUDE/ALTITUDE`, heading/pitch/bank,
  `VELOCITY BODY X/Y/Z` or world velocities, `SIM ON GROUND`, `GEAR HANDLE POSITION`,
  `FLAPS HANDLE INDEX`, lights and transponder bits. Unreliable.

**Excluded from the read:** `SIMCONNECT_OBJECT_ID_USER`, and any object this instance
injected itself — which only exists on a peer that took over hosting mid-session, and is
handled by removing them first (see above) and by keeping the injector's object-id set as a
second guard. There is no tail-number marker: the sim removes a client's objects when the
client disconnects (Q08), so nothing of ours can outlive us to need identifying, and tails
truncate at 9 characters anyway. Injected aircraft carry the host's real callsigns.

**Liveries.** Two packagings exist and both are handled. FSLTL-style packages make every
livery its own aircraft variation with its own `TITLE`; `LIVERY NAME` is empty and the title
alone selects the livery — confirmed by round-trip, the log of 2026-09-06. 2024-native
packages report a `LIVERY NAME` beside a base title, and the receiver passes it to `_EX1`.

**Ground vehicles** (GSX) are what the `GROUND` flag is for, as an experiment in stage 1.
They would arrive posed and static — their animation state is not in any simvar — and
jetways are scenery, not sim objects, so they would not arrive at all. Whether static
vehicles are worth having is a judgement to make by looking, not in advance.

### Rate

One poll rate, one send rate, flat across the radius. The receiver does **not** rely on the
sim to move objects between samples — see *Driving* below — so the sample rate is a
bandwidth choice, not a smoothness one. Measured (Q17): **2 Hz** is the default, **1 Hz** an
acceptable low-bandwidth setting, and 0.5 Hz stalls at sample boundaries and lags two
seconds.

**The change gate.** A state is sent only when it differs from the last one sent for that
object — position by ~0.1 m, altitude 0.5 ft, an angle 0.1°, speed 0.5 kt, or any discrete
field — or when a **heartbeat** (5 s) is due. The heartbeat is there because the state
channel is unreliable: without it a lost final "stopped, engines off" sample would leave a
receiver's copy one step short forever. It also gives the receiver liveness — an object
unheard-of for a few heartbeats is removed, which covers a host that dies without sending
removes. When a change follows a run of suppressed samples, the last suppressed sample is
sent first with its own timestamp, so the receiver knows when the quiet period ended and
does not stretch the first step of motion across it. At a busy gate the gate passes ~20 %
of samples: **~1.8 KB/s for 130 aircraft at 2 Hz**, against 8.3 KB/s ungated.

If bandwidth turns out to matter — it is measured in stage 2 — the fallback is to *send* far
objects on every Nth poll while still reading them every poll. That is a send-side filter,
not a second read mechanism, and it is not in v1.

### Driving

> Amended 2026-09-06 by measurement (log of that date). The first sketch trusted the sim to
> dead-reckon between samples from written velocities, as vPilot is said to. Measured, that
> fights the sim's own ground physics on anything that was spawned stationary, and no
> interpolation on top of it cures the fight.

Every injected object is **frozen** — `FREEZE_LATITUDE_LONGITUDE_SET`, `FREEZE_ALTITUDE_SET`
and `FREEZE_ATTITUDE_SET` transmitted to its object id after `AIReleaseControl` — so the sim
stops simulating it entirely. The receiver then writes an **interpolated pose once per sim
frame**, off the `Frame` system event, one sample interval behind real time so it is always
between two known samples. Velocities are still written for the animations that read them.
Parked objects change nothing and are not written. This is the discipline the WASM bridge
already uses for the user aircraft, applied from the desktop app to N objects; measured, it
is three times smoother than dead reckoning and uniform across every mover.

### Wire format

Roughly 32 bytes per aircraft state: a `u16` object index (the host's compact index, not
the sim's object id), lat/lon as `i32` in 1e-7 degrees, altitude `f32`, heading/pitch/bank
as scaled `i16`, ground speed and vertical speed as `i16`, one flag byte. States are
**batched into packets of at most ~1200 bytes** because LiteNetLib's `Sequenced` delivery —
the one the fork already uses for physics — does not fragment; a batch that exceeds the MTU
is dropped whole. Identity, add, and remove are separate reliable packets.

Budget: 100 aircraft at 5 Hz is ~16 KB/s, seven times what `Physics` costs today; at 2 Hz
it is ~6.4 KB/s. Whether either is a problem is a stage-2 measurement, and the send-side
filter above is the answer if it is.

### Injecting

On the receiver, for each object from the host:

1. By category: `AICreateNonATCAircraft_EX1(title, livery, tail, initPos, req)` for aircraft
   and helicopters, `AICreateSimulatedObject_EX1(title, livery, initPos, req)` for anything
   else. The `_EX1` forms take the livery and are **MSFS 2024 only**; on 2020 the plain forms
   are used and the livery is already inside the title (see below).
2. On the `AssignedObjectId` reply, `AIReleaseControl(objectId)` so the sim's AI engine stops
   driving it, the three freeze events so the sim's physics stop too, then
   `SetDataOnSimObject(def, objectId, ...)` with the interpolated pose every sim frame
   (*Driving*, above). Engines are set from the host's combustion state — a created
   aircraft otherwise arrives with them running.
3. `AIRemoveObject` on the host's remove packet, on host change, on session end, and on a
   per-object timeout when no state has arrived for a while. SimConnect is expected to remove
   a client's AI objects when that client disconnects (Q08), which is what makes a receiver
   restart clean up on its own.

This is the same path the ATC add-ons take on the host machine, from the desktop app over
managed SimConnect. **It does not go through the WASM module**: the module's SimConnect
surface is the data-definition subset and the AI calls are not in it, and nothing about
traffic needs the frame-locked path that made the module necessary for the user aircraft.

### Model matching

The hard part, and the one to do least of first. The receiver must own the container title
the host reports. Same add-on set on both machines makes it exact; anything else does not.

The expectation is that everyone in a session runs the **same traffic pack** — FSLTL,
FS Traffic, whichever the ATC add-on uses — and the UI says so. With the same pack, titles
are identical and exact `TITLE` match is enough. On a miss, one fallback per category
(airliner / GA / helicopter) from the base sim, so the aircraft exists and shows on TCAS even
when it is the wrong shape. `EnumerateSimObjectsAndLiveries` is in the managed DLL and lets
the receiver know its own inventory up front instead of learning it from spawn failures.
vPilot-style rules by `ATC MODEL` and airline are a later stage, not this one.

### MSFS 2020 and 2024

Both. The calls the design stands on — `AICreateNonATCAircraft`, `AICreateSimulatedObject`,
`AIReleaseControl`, `AIRemoveObject`, `RequestDataOnSimObjectType` — are FSX-era and exist
in both sims. What is 2024-only is the `_EX1` family with its separate livery string, and
`EnumerateSimObjectsAndLiveries`. On 2020 that costs nothing: every livery there is its own
container title, so the plain create call already carries it.

The injector picks the form from the sim version in `SIMCONNECT_RECV_OPEN` — application
major 11 is 2020, 12 is 2024 — rather than by catching the exception. A host on 2024 and a
receiver on 2020 (or the reverse) is a working but degraded session: the two sims' aircraft
packages differ, so anything the receiver cannot match by title arrives as the generic
fallback. Same version, same traffic pack, matches exactly.

### What the receiving pilot has to do

Turn off their own AI traffic and any traffic add-on, and be on the same traffic pack and
the same scenery as the host. Otherwise they get both, or the wrong shape, or an aircraft
taxiing a metre under the apron. FS Copilot can detect non-FSC AI aircraft on a receiver and
warn, but it should not delete them — they are not ours. Ground clamping against mismatched
scenery elevation is **not a v1 concern**; same scenery is the recommendation.

Multiplayer is already off in a shared cockpit — FS Copilot requires it — so other players'
aircraft never enter the enumeration.

### Why this is worth doing at all

Injected AI aircraft show on TCAS and the traffic displays. So the receiving pilot does not
only see the traffic out of the window; their instruments agree with what ATC is saying.

## ATC audio

### Capturing

The ATC app is an ordinary Windows process playing through an ordinary output device. The
right capture is **WASAPI process loopback**: `ActivateAudioInterfaceAsync` with
`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`, include-process-tree, targeting the app's
PID. It captures what that process renders regardless of which device it renders to, and
nothing else — not Discord, not the sim. Microsoft documents it from build 20348, which is
above every Windows 10 consumer build, but OBS's Application Audio Capture uses the same API
and runs on Windows 10 2004 (19041) and later, so in practice it is Windows 10 2004+ and
Windows 11 (Q09). This machine is build 26200. **The per-app volume mixer level is applied
before the tap** (Q10, measured): the host reads the ATC app's session volume through the
same `SimpleAudioVolume` interface and divides it back out, so a user who keeps BeyondATC
at 30 % in the mixer still sends full-level audio. NAudio does not wrap the activation, so it
is ~150 lines of COM interop on the pattern of Microsoft's ApplicationLoopback sample;
built and working in `tools/AtcAudio`.

There is **no device-capture fallback** (decided 2026-09-06). Capturing a whole output
device would need the user to install a virtual cable, route the ATC app to it and route it
back to their headphones; that is not a feature, it is a support ticket. Process loopback
is the mechanism, and Q11 confirmed it hears BeyondATC.

### Choosing the ATC app

The host should almost never have to choose. Measured on 2026-09-06 (log of that date):

- **Known apps are found by exe name** — `BeyondATC`, SayIntentions, and whatever else the
  table grows to — with `Process.GetProcessesByName` on a 2 s poll: microseconds, no WMI,
  no elevation. The first known app running is selected automatically unless the user has
  chosen manually; an app started *after* FS Copilot is picked up on the next poll.
- **Attach immediately.** Process loopback activates on a process that is silent and streams
  silence until it speaks; BeyondATC's audio session is `Active` from launch. So the host
  attaches the moment the process exists, not when ATC first talks.
- **"Choose another…" lists processes that own an audio session**, enumerated from the render
  devices' `AudioSessionManager` — the same API the mixer compensation reads. On this machine
  that is eight entries (browser, media player, the sim, Discord, GSX, BeyondATC, system
  sounds) rather than every process on the box. A manual choice is remembered by exe name,
  not PID, so it survives restarts of either app.
- **Exit is silent.** When the captured process quits, the loopback keeps delivering −90 dBFS
  with no error and no event. The host watches the process (`Process.Exited`, or the same
  poll) to detach, and re-attaches by name when it returns.
- **Elevated ATC app: not a case.** Measured on an admin-started BeyondATC from a normal
  process: activation succeeds and audio arrives at full level (Q18). Elevation is
  detectable (`GetTokenInformation(TokenElevation)` reads `ELEVATED` across the boundary)
  and the tool shows it, but the picker has no reason to treat such an app differently.
- A launcher-plus-app pattern is covered by attaching to the oldest process of the name with
  include-process-tree.

### Encoding and transport

Opus, mono, 48 kHz (its native rate; no resampling), 20 ms frames, 24 kbps VBR — Concentus,
pure managed, at 0.2 % CPU (Q13). One frame per `Sequenced` packet with a sequence number;
loss is concealed by the decoder's PLC, measured packet-for-packet at 5 % loss. A 60 ms
jitter buffer on the receiver absorbed 80 ± 30 ms of simulated delay without a late packet.
RMS gating at −50 dBFS with 300 ms hangover on the host so silence is not streamed: ATC
audio is bursty, and gating is most of the bandwidth saving and all of the "why is there
hiss" avoidance. The gate is on *sending* only — the capture stays attached through any
length of silence, and a transmission returning is just the next frame over the threshold:
it goes out at once, with the frame before it as pre-roll so an onset inside a frame is not
clipped, and the receiver refills its 60 ms buffer and plays. Each talkspurt therefore
arrives with the same latency as the first. **Measured end to end: ~130 ms
capture-to-speaker plus the network** (Q12).

### Playing

Through the desktop app, on an output device the receiver picks, on the pattern the app
already uses for its connect/disconnect sounds. Not into the sim: nothing there needs it.
Scaling gain by the receiver's own `COM VOLUME` simvar is a cheap later touch, not v1.

### What it deliberately does not carry

The host pilot's **own** transmissions. Loopback hears what the ATC app plays, not what
goes into its microphone. The other pilots hear the readback anyway — they are on the voice
call with the pilot who made it. Capturing the mic on PTT is Discord's job.

## Where the pieces go in the fork

| Piece | Where | Precedent |
| --- | --- | --- |
| Host election | `Simulation/` beside `MasterSwitch` | `SetMaster` |
| Traffic reader / injector | `Simulation/Traffic*.cs` over `SimClient` | `Coordinator.AddLink` |
| Packets and codecs | beside `Physics` | `Physics.Codec`; batching is new |
| Audio capture / play | new `Audio/` | connect.wav playback |
| UI | share toggles on the session view | master toggle |

None of it is built until the prototype in `build/plan.md` has answered its questions.
