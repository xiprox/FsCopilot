# Log

Newest first. A negative result is a result.

## 2026-09-06 · Stage 4, the card laid out and verified against a live sim

Commits 8–9 on `ahead-traffic-atc`: the layout pass (581af52, committed unbuilt as WIP) and
the round that followed review (af8fefc). The card is now what ships.

**The layout pass built and ran clean the first time.** Feature rows pinned to 32 px so the
card does not change height when a toggle is replaced by a "shared by" line; the amber
other-AI-traffic warning left under the Traffic label; the ATC dropdown made to match the
Client code TextBox; the toggle's negative right margin swallowing the Fluent content
column. Verified on the two-instance bed against MSFS with FSLTL: A hosting 87 objects
(poll 44–52 ms, change gate holding sends to ~20 % of samples) and capturing BeyondATC, B
receiving all 87 with 0 failed, 0 fallback, 0 packet gaps, ATC playing out. The
`--traffic-shadow 80` copy is what makes B show the amber warning, so that state is
exercised rather than imagined.

Then, on review, six changes — the reasoning is worth more than the diff:

- **A feature is one line, not two.** The status was stacked under the label; it now sits
  beside it. That is what let "Capturing" and "shared by wayne" share a style: once they
  occupy the same slot, styling them differently is just noise. `ShareStatus` lost its
  stacking top margin to the warning, which is the one status that still stacks.
- **The status strings carried their own context and no longer need to.** "ATC audio —
  shared by wayne" next to a label reading "ATC Audio" said it twice; the string is now
  "shared by wayne". Likewise the capture status dropped the app name — the dropdown
  immediately below it names the app.
- **The 32 px row was right for features and wrong for the slider.** Centred in one, the
  slider sat 36 px below the ATC label — measured off a screenshot, not eyeballed. It is
  now the height of its own 16 px thumb, one 6 px content gap below the row, mute button
  cut to match. The standard height was a real idea applied one row too far.
- **The Onboard Traffic/ATC chips are gone.** The entry above records them as verified;
  they were removed because the card already says who shares what. Their wiring went with
  them — `Connection.HostsTraffic`/`HostsAtc` and `ShareViewModel.TrafficHostId`/`AtcHostId`
  had no other readers.
- Selected dropdown items take the switch/button blue outright instead of a 20 % tint, with
  black text. The Fluent `ComboBoxItemForeground*` keys are set in `Style.Resources` *and*
  a `ComboBoxItem:selected /template/ ContentPresenter` selector, because the resource key
  names could not be confirmed against the packaged theme (its resources are compressed
  inside the assembly — `strings` finds nothing).
- Copy: "traffic sync issues" to "traffic issues".

**The two-instance bed is scriptable, and the client code is not stable.** Rebuild, relaunch
both, join, toggle and screenshot runs unattended, which makes a layout change roughly a
20-second loop. One trap: the peer code is regenerated per run, not derived from the install
directory, so a driver that pins the previous run's code silently fails to join — and the
pair reconnected anyway, which made the failure look like success until the Onboard list
was read. Read the code off the window each run.

Settings note: a `settings.json` beside an older build can still hold `shareTraffic` /
`shareAtc` keys. `Settings` has no such properties — they are ignored on load and dropped on
the next write. The toggles are not persisted; a stale file is not evidence that they are.

## 2026-09-06 · Stage 4, audio and the card

Commits 5–7 on `ahead-traffic-atc`: audio (f24c9ca), the ATC & Traffic card (6508a74),
copy edits (0f6a6bf). A layout pass is uncommitted; see handoff.md.

- **Audio works in the app as it did in the tool**, with one trap: the trimmed publish
  crashed at capture start because the linker removed the process-loopback COM interop.
  `ILLink.Descriptors.xml` (a `TrimmerRootDescriptor`) preserves the nested COM types and
  the NAudio assemblies. Verified with a trimmed publish, not just Debug.
- **Arming was removed.** The first card let a toggle be switched on before a session, to
  host once one existed, and persisted that. The user found the half-state confusing and
  chose: controls disabled until a peer is connected, whoever shares first hosts, hosting
  ends with the session, toggles not persisted. `Settings` keeps only the ATC app, volume
  and mute.
- **The card, verified on the two-instance bed**: no session (toggles disabled, "Join a
  session to share."); A hosting both (status line, app dropdown); B receiving ("shared by
  wayne" rows, amber other-AI-traffic warning because BeyondATC was in the sim, slider and
  mute, Traffic/ATC badges on A in Onboard); B leaves → A's shares switch off and hosting
  stops, B clears, both cards disabled.
- **Fluent theme details that cost time**: `StringEmptyToBool` treats null as not-empty, so
  every "hidden when empty" string must return `string.Empty`; the ToggleSwitch template
  keeps a 12 px content column after the switch even with no content (negative right
  margin fixes the alignment); the ComboBox tints itself with the system accent when
  focused (`ComboBoxBackgroundUnfocused`) and for the selected item — override the brushes
  in `Style.Resources`, which in Avalonia resolve window-wide, not per selector.
- Icon: Fluent has no radio tower; a hand-drawn one was rejected in favour of the headset.

## 2026-09-06 · Stage 4, traffic: what the app needed that the prototype did not

Commits 1–4 on `ahead-traffic-atc`: the traffic connection, settings, packets and election,
then the host and receiver. Tested on one machine with two instances of the app on one sim,
the receiver started with `--traffic-offset 2,90` (later `--traffic-shadow 80`, each copy
80 m behind its original along its heading — the most direct comparison there is). Five
things the prototype's file replay could never have shown, each found by measurement:

1. **Echo.** The host's poll sees the copies the other instance injects into the same sim
   and announces them; the receiver copies the copies. 850 tracked, 1020 injected in a
   minute. Only the same-sim test bed has this; in production the host's own-object set
   covers it. Fix for the bed: a receiver in offset mode prefixes tails with `~` and every
   host skips such tails.
2. **The shared Sequenced channel drops a quarter of the state batches over the relay** —
   30–40 gaps per 30 s, 0 after switching states to LiteNetLib `Unreliable` with our own
   sequence numbers. The risk named in the plan, measured, and worse than estimated; the
   user reversed the "no network changes" decision on the number. `INetwork.SendAll` gained a
   `Delivery` overload; the bool overload and physics are untouched.
3. **A render delay tied to the current pair of samples jumps on every arrival.** The
   receiver keeps a playout buffer of recent samples and a slowly slewed delay (smoothed
   spacing + 200 ms), interpolating between whichever two bracket the render point;
   extrapolates up to 600 ms past the newest before holding. The two-sample interpolator
   was fine for a file with metronome timestamps and wrong for a network.
4. **Sample times must not be arrival times.** Batches carry the host's clock; the receiver
   estimates its offset from the least-delayed packets. And samples must be aged from the
   moment they were *read*, not the batch's send time.
5. **Duplicates.** When NAT punch succeeds on both the LAN and the hairpin address, the two
   peers hold two P2P connections and every packet is sent on both; the relay does the same
   for two link directions. A zero-spacing duplicate pulled the delay estimate below the
   sample spacing and the receiver extrapolated into every sample — "a stutter as the
   aircraft interpolates ahead". A 64-wide replay window by sequence drops duplicates and
   stragglers before the interpolator.

Two dead ends worth recording so nobody repeats them: a render clock stepped by the sim's
*reported* frame rate drifts from real time and snaps on re-sync (rhythmic, grows with
speed); and GC pauses were suspected and measured at 0–3 ms per 30 s — not it.

Instrumentation that stays behind `--debug`: the receiver reads its movers back every frame
and reports the same smoothness score the prototype used, the spacing of the sim's frame
events, GC counts and pause time, packet gaps and duplicates; the traffic connection logs
any job or `ReceiveMessage` over 15 ms. The prototype injector run alongside the two app
instances scored 0.24, the reference the app is judged against.

End state, by eye and by the shadow test: "works perfectly".

## 2026-09-06 · Elevated ATC app: detectable, and it does not matter — Q18 settled

BeyondATC restarted as administrator. From a normal process: the session list reads
`ELEVATED` (the token query is *not* refused across the boundary, so detection is exact, not
inferred from access-denied); process loopback activates; and with BeyondATC producing
sound, 45 s at −9 dBFS peak, 100 % of frames sent. The caveat carried since the first sketch
is retired. Elevation detection stays in `--list` as a diagnostic.

Also decided today: **no device-capture fallback**, ever. Process loopback is the mechanism;
a Windows 10 host either has it (2004+, per OBS's experience) or cannot host audio.

Noted for the threshold: the run showed continuous sound for the whole window — it was ATIS,
which is continuous by nature; menu silence reads −90 dBFS, so −50 dBFS stays.

## 2026-09-06 · Picking the ATC app: what the API allows

Three quick experiments toward an effortless "which app is ATC" control, before any UI:

- **Audio sessions as the pick list.** `--list` now enumerates every process owning an
  audio session on a render device: 8 entries here (zen, mpv, FlightSimulator2024, Discord,
  Couatl64 = GSX, BeyondATC, system sounds, NVIDIA Broadcast) with state, mixer volume and
  mute. **BeyondATC's session is `Active` while it is silent**, so it is visible — and
  attachable — from launch.
- **Attach before speech** was already shown earlier today: activation on the silent
  BeyondATC streamed silence, then voice when ATC spoke.
- **Target exit is silent.** A helper that played two rings and quit: the loopback kept
  delivering −90 dBFS, 250 frames per 5 s, no exception, no event. The app must watch the
  process itself. Nothing crashes.

Written into the design as *Choosing the ATC app*. Also today: Q06 closed by assumption
(no collision from frozen objects; revisit only if production says otherwise), and the
mixer compensation confirmed by the reporter against BeyondATC.

## 2026-09-06 · Stage 3: the audio path works on one machine — Q10, Q12, Q13 settled

`tools/AtcAudio` built: WASAPI process loopback by PID (hand-rolled
`ActivateAudioInterfaceAsync` with `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`, include
process tree; NAudio's `IAudioClient` for the rest, own `IAudioCaptureClient` because NAudio's
is internal), 48 kHz mono 16-bit, 20 ms frames, RMS gate at −50 dBFS with 300 ms hangover,
Opus via Concentus at 24 kbps VBR, real UDP to localhost with `--loss/--delay/--jitter`,
sequence-numbered jitter buffer (60 ms) with Opus PLC for gaps, NAudio `WasapiOut` at 50 ms.

BeyondATC was in the menu and silent, so the target was a throwaway process looping a Windows
ring tone, captured by PID:

- **Activation works**; 250 frames per 5 s (exact 20 ms framing); the helper's audio arrived
  at −16 dBFS peak. Both halves ran in one process over a real socket.
- **Gate**: 44–72 % of frames sent through a ring tone with pauses; 16–27 kbps on the wire
  including UDP/IP headers.
- **5 % loss, 80 ± 30 ms delay**: 52 dropped, 50 concealed by PLC, zero late behind the
  60 ms buffer. Underruns equal talkspurt ends, as they should.
- **Latency ~126–155 ms** from capture stamp to leaving the speaker, against a budget of
  ~140 (capture 10 + frame 20 + buffer 60 + output 50). **Q12 settled**: ~130 ms plus the
  network. Well inside the 300 ms where a readback on Discord would overtake the call.
- **Q10 settled, the inconvenient way**: setting the target's volume-mixer session to 25 %
  dropped the captured peak from −16 to −28 dBFS — exactly −12 dB. **The mixer level is
  applied before the process-loopback tap.** Since the session volume is readable through the
  same API that set it, the host compensates by dividing it back out; no need to lecture the
  user about their mixer.
- **Q13 settled**: Concentus at 0.1–0.2 % CPU. No native libopus.

**Q11 settled the same evening**: `AtcAudio --process BeyondATC` in a live flight with ATC
talking — "works well". The reporter also noticed the Q10 effect first-hand: turning
BeyondATC down in the mixer turned the tool's copy down too. Compensation added: the host
reads the session's `SimpleAudioVolume` twice a second and multiplies the captured audio by
its inverse, capped at +20 dB (below a 10 % mixer level the audio is too quantised to be
worth rescuing); a muted session is reported rather than compensated. Verified with the
helper process (mixer 0.25 → gain ×4.0, captured peak unchanged) and then by the reporter
against BeyondATC live: "perfect".

Still owed: **Q09**, Windows 10, if a machine turns up.

## 2026-09-06 · The change gate: 20 % of samples, 8.3 → 1.8 KB/s

Simulated over the two long captures first (same thresholds as the design): 64 079 samples
→ 14 225 sent (22.2 %, 27 resends) over 242 s at the pushback gate; 8 575 → 1 727 (20.1 %)
over the 60 s baseline. **8.3 KB/s becomes 1.8 KB/s** for 130 aircraft at 2 Hz. Then built
into `TrafficRead` as the default (`--every-sample` to disable, `--heartbeat` to tune).

**Verified end-to-end on the same recording that fixed the jitter**: the gate applied
offline to the every-sample pushback capture (16.9 MB → 4.0 MB, 22.1 % of samples) and
replayed frozen per-frame scores **0.14–0.23, median ~0.18** — indistinguishable from the
ungated 0.17. Traced at a real pushback start: heartbeats every 5.1 s while parked, the held
sample re-sent at the moment before motion, then 3 cm, 18 cm, 35 cm… each over 0.5 s. The
receiver never interpolates a moving step across a quiet period.

Design point worth keeping: the heartbeat is not a rate, it is loss insurance and liveness
on an unreliable channel. The "re-send the last held sample before a change" rule is not
about time at all; it is what keeps the first step of a motion from being interpolated
across the preceding quiet period.

## 2026-09-06 · Baseline: BeyondATC's own traffic scores 0.56; visual confirmation of the fix

`TrafficRead --measure` for a minute in a live flight with BeyondATC injecting, at the gate:
**72–74 aircraft, 7–9 moving, jitter median 0.56, worst = median** — the add-on's own
taxiing traffic has exactly the texture of our first dead-reckoning version (0.50). Our
frozen per-frame drive at 0.17 is therefore **three times smoother than the traffic the
host is watching**. Visually confirmed on the pushback by the reporter: "looks good".

FPS, same machine, same scene: empty 44.7; BeyondATC's 73 objects 38.0; our 132 injected
32.5. The per-object price is the same whoever drives — it is the models — so **Q05 is
settled: no receiver cap is needed beyond what the host already pays.**

Two consequences noted in the design: frozen objects are not ground-clamped by the sim, so
the same-scenery requirement is now firm rather than advisory (the AGL fix is known if ever
needed); and the receiver runs entirely on arrival times, so frame rate on either side is
irrelevant to the mechanism.

## 2026-09-06 · Jitter measured, not eyeballed — freeze + per-frame writes wins; Q16 settled

The pushback jitter survived `--interp 20`, and the reporter's description — "pushed-back
aircraft keep jittering even when they later move forward; taxiing-from-spawn ones never
do" — said it was state on the object, not the motion. Rather than argue about it, the
injector got `--measure`: every moving injected object is read back on each sim frame and
scored as *mean frame-to-frame change in displacement over mean displacement* (0 = constant
motion, ~1 = stand-still-then-snap). Both tools now also report the sim's frame rate from the
`Frame` system event. Seven configurations, the 4-minute pushback capture, all objects
offset 2 nm east over the bay so they interfere with nothing:

| config | jitter median | worst | fps |
| --- | --- | --- | --- |
| empty sim (no traffic at all) | — | — | 44.7 |
| dead reckoning, 2 Hz samples | 0.50 | 1.2 | 32.6 |
| interp 20 Hz, sample velocities | 0.52 | 1.0 | 32.8 |
| interp 20 Hz + freeze | 1.65 | 1.7 | 34.4 |
| interp 20 Hz, path-derived velocities | 0.36 | 0.9 | 31.3 |
| per-frame interp, unfrozen, path velocities | 0.28 | 0.7 | 31.0 |
| **per-frame interp + freeze** | **0.17** | **0.17** | 32.5 |

What each row taught:

- **`FREEZE_LATITUDE_LONGITUDE/ALTITUDE/ATTITUDE_SET` transmitted to an AI object is
  accepted** (`IS … FREEZE ON` reads back 1/1/1) and stops the sim simulating it. That is
  what vPilot does and it is the cure: once the sim no longer moves the object, nothing
  fights the writes. The residual jitter is gone and every mover scores the same — worst
  equals median — which is what "no fight" looks like.
- **A frozen object must be written every frame.** At 20 Hz into a ~33 fps sim it moves on
  some frames and not others (the 1.65 row). Writing on the `Frame` event fixes that. It is
  the same discipline the WASM bridge uses for the user aircraft.
- **Unfrozen interpolation only helps if the velocities match the path.** Handing the sim the
  newest *sample's* velocities while walking an interpolated path made it fight itself
  (0.52 ≈ dead reckoning); deriving them from the path helped (0.36 / 0.28) but never
  reached the frozen result.
- **Parking brake was not it.** `BRAKE PARKING POSITION` reads 0 on injected aircraft and
  `SetData` on it is rejected (120 exceptions). Option removed.
- **FPS**: ~12 fps for 132 injected objects on this machine, similar across configs;
  freezing is if anything cheaper. There is no BeyondATC-only figure yet (the sim was in the
  menu when the baseline ran: 1 object, honest "no movers measured"). That and a baseline
  smoothness score for the add-on's own taxiing traffic are the two numbers still owed, both
  from `TrafficRead --measure` in a live flight.

**Decision for the design:** the receiver freezes every injected object and writes an
interpolated pose once per sim frame, one sample interval behind. Smoothness is now
decoupled from the sample rate, which is why the next run lowered the rate to find the
bandwidth floor.

**Rate floor (Q17)**, same config, samples thinned on intake:

| host rate | jitter median | worst | what it means |
| --- | --- | --- | --- |
| 2 Hz | 0.17 | 0.17 | uniform |
| 1 Hz | 0.15 | 0.5–0.75 spikes | occasional holds when a sample is late |
| 0.5 Hz | 0.13 | 1.6–1.7 spikes | frequent stalls, and everything is 2 s behind |

The median is flat because the in-between motion is interpolated either way; what degrades
is the segment boundary — an object reaches the end of its known path and holds until the
next sample. The metric does not see corner-cutting on turns, which also grows with the
interval. **2 Hz default, 1 Hz as a low-bandwidth option, nothing lower.** Bandwidth at
32 bytes a state: ~8 KB/s for 130 aircraft at 2 Hz, ~4 KB/s at 1 Hz. A small refinement for
later: extrapolate a fraction of an interval past the newest sample along the path
velocity instead of holding, which would soften the 1 Hz spikes.

## 2026-09-06 · First visual replay; engines, pushback, ground vehicles

**Seen from the gate** (the 88 s capture replayed with BeyondATC closed): parked aircraft
stable on the apron, taxiing aircraft smooth at the recorded 2 Hz, TCAS not yet checked.
Two defects:

- **Every parked aircraft had its engines running.** `AICreateNonATCAircraft` creates an
  aircraft ready to fly, and the reader never captured engine state. Fixed: the reader now
  records `NUMBER OF ENGINES` and `GENERAL ENG COMBUSTION:1–4` (`ne`, `e`), the injector
  writes combustion per engine through four one-datum definitions so a twin is never asked
  about engine 4. **Verified by read-back**: 100 injected parked aircraft reported `e=0`,
  the movers kept theirs on. `SetData` on combustion is accepted, as the docs say.
- **Pushback is jittery** where taxi is smooth. Reversing at 2–3 kt with a turn is where the
  sim's dead reckoning between 500 ms samples disagrees most with the next write, and the
  ground model resists moving backwards. Added `--interp <hz>`: pose written at a fixed rate,
  interpolated between the last two samples one interval behind — the WASM bridge's approach
  for the user aircraft. Parked objects cost no writes. **Not yet compared visually.**

**Ground vehicles** (`--types aircraft,helicopter,ground`, BeyondATC + GSX up): 11
`GroundVehicle` objects enumerate — FSDT tugs, fuel trucks, catering, Asobo baggage carts —
and **all 11 spawn** through `AICreateSimulatedObject_EX1` with no failures. They reject
every appearance datum (gear, flaps, lights: `DATA_ERROR` on each); the injector now skips
appearance for anything that is not an aircraft. Vehicles carry no tail, so they cannot be
told apart from originals by name — only by the injector's own id set. How they *look*
driven is still to be judged.

**Numbers**: 132 objects (121 aircraft + 11 vehicles) at 2 Hz is ~260 writes/s from the
injector, no drive exceptions. A 264-object enumeration (originals plus offset copies) still
polled fine.

## 2026-09-06 · Stage 0 capture read; TrafficInject built and smoke-tested — Q01, Q08 settled

**The capture** — 88 s at the SFO gate with BeyondATC + FSLTL, 2 Hz, aircraft+helicopter:

- 110 aircraft, every one identified, **all within 25 nm**. Ten were taxiing (16–22 kt,
  clean traces with body velocities); the rest parked. No adds or removes during the run.
- **Poll cost at 110 objects: 13 ms average, 30 ms max per type per poll, 0.1 % CPU.**
  Sample gaps 503 ms median, 533 ms p95. Q01 is settled for practical purposes — 5 Hz would
  be ~7 % of the sim's message time — without the 5 Hz run having been made.
- **Zero `ObjectAdded` events for all 109 AI aircraft**: they all predated the subscription.
  The restart case the design was changed for, seen directly.
- **`LIVERY NAME` empty for every FSLTL aircraft**, present only for the user's 2024-native
  A220. FSLTL titles carry the livery (`FSLTL_FAIB_B738_UAL-United_NC`). Bizjets are the
  base sim's `Asobo PassiveAircraft Citation CJ4` and friends.
- **`ATC MODEL` is unreliable** as a matching key: `777` / `A320` for some, a localisation
  token like `ATCCOM.ATC_NAME CITATION.0.tts` for others. `CATEGORY` was `Airplane`
  throughout.

**TrafficInject**, replaying three of those aircraft 2 nm east over the bay, twice:

- `AICreateNonATCAircraft_EX1(title, "", tail, init)` **created every FSLTL title**; the
  assigned-id reply, `AIReleaseControl` and 200+ pose writes ran without an exception.
- **Livery round-trips.** A second `TrafficRead` during the replay read the injected
  aircraft back as exactly `FSLTL_FAIB_B739_ASA-Alaska Airlines_SSWNC`,
  `FSLTL_TFS_B77W_AIC-Air India`, `FSLTL_FAIB_B737_Southwest_BW`, same `ATC MODEL`. In
  FSLTL's packaging the title is the variation and the variation is the livery, so this is
  the Alaska 739 in Alaska colours. Q03's "empty" was never a problem.
- **Q08 settled yes.** Both runs were killed by `timeout` before their cleanup ran. A fresh
  read afterwards found no injected tails: the sim removes a client's AI objects when the
  client disconnects. So a receiver crash cleans up, and the `FSC` tail marker had no job —
  removed. Tails also **truncate at 9 characters** (`FSCSWA4524` → `FSCSWA452`).
- `FLAPS HANDLE PERCENT` is **read-only for SetData** (`DATA_ERROR` at definition index 2).
  Switched to `FLAPS HANDLE INDEX` on both sides; the reader now records `fi`. Gear and the
  five lights were accepted.

**Not yet seen:** what any of it looks like. Stage 1 proper is the full replay with
BeyondATC closed, watched from the gate — smoothness at the recorded rate and at `--rate 1`,
TCAS, frame cost (Q04, Q05, Q06), then `--types ground` on a fresh capture (Q07).

## 2026-09-06 · TrafficRead built and smoke-tested — Q03 settled yes

`tools/TrafficRead` builds and ran for 30 s against MSFS 2024 (12.2 build 282174.999) with no
ATC add-on running: only the user aircraft (A220-300, object 589824) enumerated. Two things
learned before any traffic was present:

- **`LIVERY NAME` is a real simvar on 2024.** The Q03 probe answered `Synaptic House
  A220-300`. The host can read the livery; the receiver's `_EX1` create gets the right one.
  It is read through its own one-shot definition, because on 2020 the name would be rejected
  and an unknown datum in a shared definition shifts every field after it. That is the
  2020/2024 branch on the read side.
- **Poll cost at one object:** 21–30 ms from request to last reply per type per poll at
  2 Hz, 0.1 % CPU. One poll in 116 did not complete inside its 500 ms window and was
  skipped rather than diffed, which is the intended behaviour. The number that matters is
  the same figure at 100+ objects; that needs the stage-0 capture.
- One `ObjectAdded` event fired (object 218644480, reported type `ALL`) for something the
  aircraft/helicopter polls never returned — a non-aircraft sim object. The events are
  logged beside the diff for exactly this kind of comparison.

The managed wrapper's `GetLastSentPacketID()` returns the id rather than taking an `out`
parameter; the native signature in the docs is the `out` form. Noted so nobody trips on it
twice.

**Next:** a real capture. Start BeyondATC, then from `record/traffic-atc/`:
`dotnet run --project tools/TrafficRead -- --rate 2`, fly gate → taxi → takeoff → climb,
Ctrl-C. Then once more with `--rate 5` for the Q01 comparison.

## 2026-09-06 · Design revised on review, before any probe

Decisions taken in review of the first sketch, and what they replaced:

- **Polling replaces `ObjectAdded`.** The event misses everything that existed before the
  subscription, so a host restarting FS Copilot in cruise would have seen no traffic until
  new objects appeared. A periodic sweep fixes that and *is* the poll, so the event was
  dropped rather than kept alongside.
- **200 km is the radius, flat rate, no tiers.** The 200/300 nm constants and the
  distance-rate tiers are gone from v1. 108 nm covers what a pilot sees or is told about. A
  send-side "far objects every Nth poll" filter is the fallback if stage 2's bandwidth
  figure demands one.
- **One host per session, not one source per feature.** The host runs the ATC app and
  provides traffic and audio together. The host need not be the master. Host change
  mid-session is allowed and handled by teardown-then-read.
- **Same traffic pack, same scenery, multiplayer off** are stated requirements, not things
  the design defends against. Ground clamping across mismatched scenery and other players'
  aircraft dropped from the questions.
- **Ground vehicles** are an experiment in stage 1, with the reader and injector written
  type-agnostic so switching them on is a flag. Expectation set: posed and static, no
  jetways.
- **Windows 10** for process loopback: Microsoft says build 20348+, OBS ships the same API
  for 2004+. Recorded as Q09, not assumed either way. Volume-mixer interaction recorded as
  Q10.
- Questions renumbered; the old Q09 (multiplayer) removed.
- **Radius is a parameter**, clamped to the SDK cap and defaulting to it, so a slider or a
  low-bandwidth setting is a one-line change later.
- **MSFS 2020 is supported.** The `_EX1` calls are 2024-only; the injector branches on the
  version in `SIMCONNECT_RECV_OPEN` (11 = 2020, 12 = 2024) and uses the plain forms on
  2020, where the livery is already part of the title.

## 2026-09-06 · Sketch written

No probe has run. What was checked against the machine while writing the design:

- `Microsoft.FlightSimulator.SimConnect.dll` shipped with the fork exports
  `AICreateNonATCAircraft`, `AICreateNonATCAircraft_EX1`, `AIReleaseControl`,
  `AIRemoveObject`, `RequestDataOnSimObjectType`, `SubscribeToSystemEvent`,
  `EnumerateSimObjectsAndLiveries`. Nothing the design needs is missing from the managed
  wrapper.
- `SimConnect.h` in `C:\MSFS 2024 SDK\SimConnect SDK\include` has the `_EX1` forms with the
  livery parameter. There is no `SimConnect.h` under `WASM\include`; the module compiles
  against the SDK one and uses only the data-definition subset. Injection stays in the
  desktop app.
- Windows build 26200 — process loopback is available.
- BeyondATC and SayIntentions are both installed here. SayIntentions runs a local
  `simconnect_ws` server on `127.0.0.1:53800`; not relevant to the design, noted in case
  it is useful for a probe.
- The `ahead-dev-var-replay` branch has `Recorded<T>` / `Playback()` helpers in
  `ObservableExtensions.cs` that the injector's replay timing can reuse.
