# 03 · Implementation plan

    Purpose:  How the proven prototype becomes FS Copilot code, on the ahead-traffic-atc branch.
    Depends:  01-design.md as amended by build/log.md; every decision in the table below.
    Status:   Approved by the user 2026-09-06. Amended by pointer only, like the design.

> Amended 2026-09-08 by [04-transport.md](04-transport.md): the transport paragraphs below
> ("rides on `INetwork` unchanged", "more channels is not an option", the 508-byte MTU in the
> `INetwork.cs` note) are superseded. Unreliable did not survive the relay at all; the relay
> protocol, the channel plan and the packet budgets are as 04 says.

## Context

A shared cockpit today carries the aircraft but not what it flies among: the AI traffic an ATC
add-on (BeyondATC, SayIntentions, FSLTL underneath) injects exists only in the host's sim, and
ATC audio reaches other pilots only if piped into Discord by hand. The prototype in
`ahead-record/record/traffic-atc/` (design `docs/01-design.md`, findings `docs/build/log.md`,
working code `tools/`) proved every mechanism on one machine, with numbers: traffic replayed
**frozen with one interpolated write per sim frame** scores jitter 0.17 vs 0.56 for BeyondATC's
own; the change gate sends ~20 % of samples (~1.8 KB/s for 130 aircraft at 2 Hz); ATC audio is
~130 ms capture-to-speaker at ~22 kbps with PLC, mixer-compensated, 0.2 % CPU; the ATC-app
picker's mechanics are measured.

This plan folds that into the app on a new `ahead-traffic-atc` branch. UI/UX target: the best
possible, inside the existing 500×700 card layout. **All user-facing copy is reviewed by the
user after implementation, before the feature is called done.**

## Decisions locked with the user (2026-09-06)

| # | Decision | Rejected |
| --- | --- | --- |
| 1 | Two independent features, **traffic** and **ATC audio**, each with at most one host; hosts independent of master/slave; different peers may host each. | One host for both. |
| 2 | **No take-over**: a toggle is disabled while someone else hosts that feature. | Take-control pattern. |
| 3 | Sharing may be **armed with no peers**. Traffic toggle needs the sim connected; ATC toggle does not. | — |
| 4 | Host stops or drops → receiver's injected traffic **removed at once**, row reverts. | Keep last known. |
| 5 | **Persistence: `settings.json` beside the app** (`AppContext.BaseDirectory`, where downloaded profiles already go). Persists ATC app choice (exe name), receive volume + mute, both toggles; ATC toggle forced off at launch if no known app is detected. | `%LocalAppData%`; none. |
| 6 | **Window stays 500×700**; the card is compact and takes its height from Onboard. | Grow to 800. |
| 7 | No "None"/"Automatic" dropdown entries; no counts; no "audio live"; nothing in the bottom badge strip — all messaging inside the card. | — |
| 8 | **No device-capture fallback**, ever. | Virtual cable. |
| 9 | Receiver warns when non-FSC AI aircraft are detected in its sim. | — |
| 10 | Elevated ATC app needs no handling (measured). Q06 closed by assumption. Ground vehicles behind a flag. Windows 10 deferred. | — |
| 11 | Record committed on `ahead-record` first, including the 4 MB gated pushback capture. | — |

Design choices made in planning (say if any should change):

- **Transport rides on `INetwork` unchanged** (LiteNetLib UDP, direct + relay), like every other feature: identity/remove/hosting reliable, states and audio `unreliable: true` (= `Sequenced`, as physics). Known and accepted for v1: Sequenced ordering is per channel, so a state or audio packet that arrives after a newer physics packet (or vice versa) is dropped — only on genuine reordering, rare on direct and relay alike; a dropped physics packet is invisible behind the 400 ms buffer, an audio frame is 20 ms of PLC, a traffic state is a hold of ≤ 1.5 s backstopped by the heartbeat. States and audio carry a per-host `Seq` so receivers log gap counts; if Tier 2 shows it matters, a `Delivery.Unreliable` overload on `SendAll` is the 20-line follow-up (more channels is not an option: the relay server at `p2p.fscopilot.com` is upstream's, `ChannelsCount = 2`).
- **ATC app choice is a preference, not a lock**: effective target = the remembered app if it is running, else the first detected known app. The dropdown lists running known apps by name and **Other…**; the selected item always reflects the effective target. No "Automatic"/"None" entries, no "(detected)" suffix.
- **No new string-catalogue file** (the app keeps strings in XAML and view models). The copy review is done from a list of every new string pulled from the diff.
- **No offline check harness** (`--traffic-check` dropped by decision). The gate/interpolator are proven with the sim in Tier 1; the dev flags that remain are `--traffic-offset` and `--traffic-ground`.
- **Election = per-feature claim set, ordinal-smallest peer id wins** (deterministic on every peer regardless of packet order; a peer whose claim loses reverts its toggle with a notice).
- **Each wire state carries `u16 AgeMs`** (how old the sample was when sent) so the "re-send the held sample first" rule survives two samples arriving in one packet; the receiver never needs the host's clock.
- **Traffic gets its own SimConnect connection** (`SimTraffic`), so the per-frame writes run synchronously inside the `Frame` callback on that thread, failures are named (not `Log.Fatal`), and disposing it is a complete cleanup (Q08).
- The card's note line is shown only while something is shared (either side), to keep the idle card two rows tall.

## Verified source facts the design rests on

(fork at `C:\Users\wayne\dev\fsc\ahead`)

- `Program.cs:70-118`: singletons via `UseReactiveUIWithMicrosoftDependencyResolver`; `MainViewModel` built by a factory lambda whose arguments resolve left-to-right (`:99-108`) — this orders `MasterSwitch → Coordinator`. Peer id = `Random.String(8)` uppercase (`:67`). `App.axaml.cs:33-42`: Exit handler is the only teardown; nothing disposed.
- `SimConnectConsumer.cs:130-193`: `Configure(configure, deconfigure)` only (replayed on reconnect, `:156-167`); no one-shot `Post`; only `OnRecvSimobjectData/ClientData/Event/EventFilename/SystemState` wired; `ReceiveMessage` and jobs on the consumer's own thread; job exceptions are `Log.Fatal` (`:175`). `EVT.AircraftLoaded = uint.MaxValue-1` (`:233`). `SimClient.cs:45-51` opens four connections; every request targets the user object.
- `INetwork.cs`: `SendAll` only, no per-peer send, no sender id (packets embed it — `SetMaster(string Peer)`). `unreliable: true` → `DeliveryMethod.Sequenced`: **no fragmentation**, `TooBigPacketException` over MTU; no `MtuOverride` (`P2PNetwork.cs:44-49, 361-367`). LiteNetLib's initial MTU is 508 payload bytes until discovery. `Codecs.cs:23-38`: id = registration index; `Schema` gates both handshakes — both peers must run the same build (existing rule). Relay forwards channel + delivery verbatim (`FsCopilot.Discovery/Relay.cs:287-311`).
- `MasterSwitch.cs:16-33`: election idiom (`BehaviorSubject`, `RegisterPacket` in ctor, broadcast on change). `Coordinator.cs:53-61` registers ids 2–7 after `SetMaster` (1) and `PeerTags` (0). `Coordinator.cs:106-112`: `net.Peers → OnPeersChanged`.
- `MainViewModel.cs:174-190`: peer list rebuilt every 250 ms into `record Connection(...)` (`:281`); idiom `ObserveOn(RxApp.MainThreadScheduler)` + `DisposeWith(_d)`; `ViewErrors` flags → `ErrorMessage`. `ValueConverters.cs`: `Empty`, `NotEmpty`, `Inverse`, `BoolToRowHeight` (unused).
- `MainWindow.axaml:53` rows `Auto,Auto,*,Auto`, Onboard is the star row; card idiom `:85-120`; direct/relay badges `:220-229`. `MainWindowStyles.axaml`: `Border.Card :20-25`, `Note :133`, badges `:191-212`, an orphan `ComboBox.Aircraft :179-184`. **No ToggleSwitch/Slider styles.** `Icons.axaml`: `StreamGeometry` keys (Fluent UI System Icons).
- Persistence: none. Files written: profiles into `AppContext.BaseDirectory\Definitions` (`Installer.cs:201`), bridge into Community, Serilog `log` in the working directory.
- `UiSounds.cs`: `System.Media.SoundPlayer`; no NAudio. `FsCopilot.csproj:17-23`: `PublishTrimmed` + `PublishSingleFile` + `SelfContained`; no trimmer descriptors; `Definitions.cs:48-50` already needed `DynamicDependency`.
- Managed SimConnect DLL exports everything needed (`AICreate*_EX1`, `AIReleaseControl`, `AIRemoveObject`, `RequestDataOnSimObjectType`, `EnumerateSimObjectsAndLiveries`, `OnRecvSimobjectDataBytype`, `OnRecvAssignedObjectId`, `OnRecvEventFrame`, `OnRecvException`, `OnRecvOpen`, `GetLastSentPacketID`).
- Prototype code to lift: gate `tools/TrafficRead/Program.cs` (`Gate/Changed/Send`); create/assigned/release/freeze/render/dress `tools/TrafficInject/Program.cs`; structs `tools/Traffic.Sim/Definitions.cs`; `MotionScore.cs`; version + exception naming `tools/Traffic.Sim/SimLink.cs`; `tools/AtcAudio/ProcessLoopback.cs` whole; host/receiver pipelines and session/elevation helpers `tools/AtcAudio/Program.cs`.
- Branch model (`C:\Users\wayne\dev\fsc\CLAUDE.md`, `ahead/record/conventions.md`): implementation on `ahead-traffic-atc` cut from `main`, worktree `C:\Users\wayne\dev\fsc\ahead-traffic-atc`; record stays on `ahead-record`; `ahead` rebuilt with `profiles/tools/ahead-rebuild.sh`. Panel code is unaffected (nothing runs in a panel).

## Packets

Registered in `ShareSwitch`'s ctor, after Coordinator's (ids 0–7 unchanged): **8 ShareHost, 9 TrafficIdentity, 10 TrafficStates, 11 TrafficRemove, 12 AtcFrame.** Reliable unless noted; strings are `BinaryWriter` UTF-8.

```
ShareHost        string Peer | u8 Feature (0 Traffic, 1 Atc) | u8 Hosting
TrafficIdentity  string Host | u16 Index | string Title | string Livery | string Tail
                 | string Airline | string FlightNumber | string Model | u8 Category (0 Airplane,1 Helicopter,2 GroundVehicle,3 Other)
TrafficRemove    string Host | u16 Index
TrafficStates    string Host | u32 Seq | u8 Count | Count × State            UNRELIABLE (unreliable: true, Sequenced)
  State (44 B):  u16 Index | u16 AgeMs | i32 Lat 1e-7° | i32 Lon 1e-7° | f32 AltFt
                 | u16 Hdg ×100 | i16 Pitch ×100 | i16 Bank ×100 | i16 GsKt ×10 | i16 VsFpm
                 | i16 VbX ×10 | i16 VbY ×10 | i16 VbZ ×10 | i16 RotX ×1000 | i16 RotY ×1000 | i16 RotZ ×1000
                 | u8 Flags (bit0 OnGround) | u8 GearPct | u8 FlapsIndex | u8 Lights (strobe,landing,taxi,beacon,nav)
                 | u8 Engines (bits0-3 combustion, bits4-6 count)
  ≤ 10 states per packet → ≤ 455 B, under the 480 B budget (initial MTU 508).
AtcFrame         string Host | u32 Seq | u16 Len | Opus bytes (Len ≤ 1275)   UNRELIABLE (unreliable: true, Sequenced)
```

Quantisation error (≤ 1.1 cm, ≤ 0.005°, ≤ 0.05 kt) is an order below the gate thresholds; the gate runs on raw doubles.

## File-by-file design

### New `Connection/SimTraffic.cs` — fifth SimConnect connection ("FS Copilot (Traffic)")
Shape of `SimConnectConsumer` (own long-running thread, `AutoResetEvent`, 2 s reconnect, `Configure` replayed on connect) plus: `Post(Action<SimConnect>)` one-shot jobs (dropped if disconnected; exceptions `Log.Error("[Traffic] …")`, never Fatal); `Call(sim, name, action)` recording `GetLastSentPacketID → name` (cap 4096) so `OnRecvException` is raised as `(callName, exception, index)`; `IsMsfs2024` from `OnRecvOpen` (major ≥ 12); events raised **synchronously on the SimTraffic thread**: `ByType`, `ObjectData`, `Assigned`, `Liveries`, `Frame` (subscribed `"Frame"` system event, `EVT.Frame = uint.MaxValue-2`), `Exception`, `Disconnected`; `Connected` observable. Invariant: only handlers of these events and posted jobs may call `sim.*`. Registered as a singleton in both DI branches; disposed in the Exit handler (Q08: closing it removes any leftovers).

### New `Simulation/TrafficDefinitions.cs`
`ObjectState`, `ObjectIdentity`, `LiveryProbe`, `DriveState`, `Appearance`, `EngineState` lifted from `tools/Traffic.Sim/Definitions.cs`, plus `ForeignProbe { int IsUser }`. Id tables: `DEF { State=1, Identity, Livery, Foreign, Drive=10, Appearance, Eng1..Eng4 }`, `REQ { PollAircraft=1, PollHelicopter, PollGround, Identity=10, Livery, Foreign=20, CreateBase=1000, ReleaseBase=70000, RemoveBase=140000 }` (+ u16 index). `Livery` defined/requested only on 2024.

### New `Simulation/TrafficPackets.cs`
`TrafficIdentity`, `TrafficStates` (+ `TrafficState` readonly record struct in real units with `AgeMs`; `From(in ObjectState, age)`, `ToDrive()`), `TrafficRemove`; nested `Codec`s doing the quantisation above; `TrafficStates.MaxPerPacket = 10`.

### New `Simulation/TrafficGate.cs` (pure)
Per-object: `Sent/SentAt/Held/HeldAt`; `Decide(sample, now, out samples)` returns 0–2 samples `[held (age = now−HeldAt), fresh (age 0)]` per the prototype rule (`TrafficRead.Gate`); `Changed` with the prototype thresholds (1e-6°, 0.5 ft, 0.1°, 0.5 kt, any discrete); heartbeat 5 s; `ForceResend()` for late joins.

### New `Simulation/TrafficInterpolator.cs` (pure)
`Push(sampleTimeMs, state)` (drops out-of-order), `TryRender(now, out DriveState)` = `TrafficInject.Render`: `interval = clamp(Curr.T−Prev.T, 50, 1500)`, render at `now − interval`, lerp position, `LerpAngle` for attitude, velocities from `Curr`; returns false when parked or already final. The 1500 ms clamp is new (catch up after loss instead of crawling).

### New `Simulation/ShareSwitch.cs` — election + packet registration
`Feature { Traffic, Atc }`; per feature a `SortedSet<string>` of claimants under one lock; host = ordinal `Min`; `IObservable<string?> Host(f)`, `CurrentHost(f)`, `IsHosting(f)`, `IObservable<Feature> Lost`, `IObservable<string> PeerJoined`, `Request(f, on)`, `StopAll()`. Rules: `Request(true)` only when host is null/self → add self, broadcast; `Request(false)` → remove, broadcast; `Apply(ShareHost)` ignores own id; `Recompute`: if we want it and `Min ≠ self` → withdraw, broadcast false, `Lost`, `Log.Information("[Share] …")`. `net.Peers` (1 s): prune claims not in `peers ∪ {self}`; for each newly seen id re-broadcast our claims (`PeerJoined`). Registers the five packets in its ctor; every traffic/audio class takes `ShareSwitch` in its ctor so DI pins registration order after Coordinator.

### New `Simulation/TrafficHost.cs`
Active ⇔ `share.Host(Traffic) == self`. Activate (posted): `Configure` State/Identity/Livery definitions; 500 ms interval → `Post(Poll)`. `Poll` = `TrafficRead` lifted: one `RequestDataOnSimObjectType(req, DEF.State, 200_000, type)` per type (AIRCRAFT, HELICOPTER; GROUND when `opts.Ground`); incomplete poll → no diff; skip `IsUser` and ids in `receiver.OwnObjectIds`; new id → index, request identity (+livery on 2024); identity reply → `SendAll(TrafficIdentity)`, only then does the gate start; states passing the gate batched into `TrafficStates` chunks of 10, `unreliable: true`; absent from a complete poll → `TrafficRemove`. `PeerJoined` → re-send all identities + `ForceResend()`. `Disconnected` → clear tables. Debug log of poll cost / sent % every 30 s.

### New `Simulation/TrafficReceiver.cs`
Active ⇔ foreign host. Activate: `Configure` Drive/Appearance/Eng1-4/Foreign definitions + the three FREEZE event maps; on 2024 `EnumerateSimObjectsAndLiveries` once per connection → `_knownTitles` (empty = unknown, fall through to exception retry); 1 s maintenance tick; 10 s foreign poll. Every handler: `if (p.Host != share.CurrentHost(Traffic)) return;` and `sim.Post`. `Live { Index, Identity, SimId, Fallback, Failed, LastStateAt, Interp, LastAppearance, LastEngines }` tables `_byIndex/_byRequest/_bySim`, `_pending` (state before identity, 10 s). Create per category (`_EX1` on 2024, plain on 2020), exception → retry once with `TrafficFallbacks` title → `Failed` (retried every 30 s, covering "receiver in the menu"). Assigned → `AIReleaseControl`, three `TransmitClientEvent(objectId, FREEZE_*, 1, GRP.Highest, GROUPID_IS_PRIORITY)`, initial drive + dress. State → `Interp.Push(arrival − AgeMs)`, dress (appearance + per-engine combustion, aircraft only, on change). `sim.Frame` → `foreach live: if TryRender → SetDataOnSimObject(DEF.Drive, …)`. Maintenance: remove after 15 s without state; retry `Failed`; expire `_pending`. Foreign poll: count AIRCRAFT/HELICOPTER ids ∉ `_bySim`, not user → `ForeignAiCount`. `RemoveAll()` on deactivate/host change/exit; `Disconnected` → clear. `opts.Offset` applied to every state (dev).

### New `Simulation/TrafficFallbacks.cs`, `Simulation/TrafficOptions.cs`
Fallback titles per category for 2024/2020 (`Asobo PassiveAircraft Citation CJ4` verified for 2024 airplane; helicopter/2020 confirmed in Tier 1). `TrafficOptions(OffsetNm, OffsetDeg, Ground)` from `--traffic-offset`, `--traffic-ground`.

### New `Audio/ProcessLoopback.cs`, `Audio/AtcApps.cs`, `Audio/AtcCapture.cs`, `Audio/AtcPlayer.cs`, `Audio/AtcHost.cs`, `Audio/AtcReceiver.cs`, `Audio/AtcFrame.cs`
`ProcessLoopback` verbatim from the tool (+ `IsSupported` build ≥ 19041 for the status line only). `AtcApps`: `Known = [BeyondATC, SayIntentions, Pilot2ATC, PF3, FSHud, VoxATC]`, `DetectedKnown()`, `Oldest(exe)`, `AudioSessionProcesses()`, `SessionVolume(pid)`. `AtcCapture(pid, send)`: loopback → 960-sample frames → RMS gate −50 dBFS, 15-frame hangover, one-frame pre-roll → mixer compensation (×10 cap, 500 ms timer) → Concentus VOIP 24 kbps VBR → `send`. `AtcPlayer`: `Enqueue(seq, opus)`, 60 ms start depth, 20 ms play thread, PLC on gaps, `BufferedWaveProvider → VolumeWaveProvider16 → WasapiOut(default endpoint, shared, event-sync, 50 ms)`; `Volume`, `Muted`; re-create output on default-device change / playback exception. `AtcHost`: `Phase { Waiting, Capturing, Closed, Unsupported }` status, `Detected`/`Sessions` observables (2 s poll), `PreferredApp` (persisted), target rule = preferred-if-running else first detected; attach when active and target exists (silent is fine), detach on `Process.HasExited`, re-attach on return; `send = SendAll(AtcFrame(self, seq++, …), unreliable: true)`. `AtcReceiver`: frames from the current foreign ATC host → player; volume/mute from settings.

### New `Settings.cs`
`ShareTraffic`, `ShareAtc`, `AtcApp` (exe or null), `AtcVolume` (1.0), `AtcMuted`; `Path = AppContext.BaseDirectory\settings.json`; `Load()` (defaults on any failure, `Log.Warning("[Settings] …")`), `Save()` debounced 500 ms, temp + move; `System.Text.Json` **source-generated** `SettingsContext` (trim-safe). Singleton in both DI branches.

### `Network/*`: untouched
No transport changes in v1 (see design choices). Receivers count `Seq` gaps on states and audio and log them with the 30 s stats, which is the evidence for or against the follow-up.

### New user-facing strings (no catalogue file; strings live in XAML/VM as today)
Card title, row labels, "{0} · shared by {1}", traffic-needs-sim tooltip, foreign-traffic warning, "● capturing {0}" / "○ waiting for an ATC app…" / "○ {0} closed" / "○ {0} is muted in the volume mixer" / "○ audio capture needs Windows 10 2004 or later", "Other…", "{0} is already sharing {1}.", the note, badge texts, Mute/Unmute. **All listed from the diff and reviewed with the user before done.**

### New `ViewModels/ShareViewModel.cs`
`TrafficOn` (setter → `share.Request(Traffic, value && simConnected)`, persisted), `TrafficCanHost`, `TrafficSharedBy`, `TrafficReceiving`, `ForeignTrafficWarning`, `TrafficDisabledReason`; `AtcOn` (persisted; forced off at launch if no known app), `AtcCanHost`, `AtcSharedBy`, `AtcApps` (running known apps + Other… + sessions when expanded), `SelectedAtcApp` (reflects the effective target; picking sets the preference; Other… expands), `AtcStatus`, `AtcReceiving`, `AtcVolume`, `AtcMuted`, `ToggleMuteCommand`, `Notice` (lost tie-break, clears after 5 s), `ShowNote` (anything shared either side). `share.Lost` → revert toggle without re-entering `Request`. Peer names from `net.Peers`. All on `RxApp.MainThreadScheduler`, `DisposeWith(_d)`.

### Edits
- `ViewModels/MainViewModel.cs`: ctor gains `ShareViewModel` (exposed as `Share`); `Connection` record gains `HostsTraffic`, `HostsAtc` filled in the 250 ms rebuild.
- `Program.cs`: parse `--traffic-offset` / `--traffic-ground`; register `Settings`, `SimTraffic`, `TrafficOptions` in both branches; non-dev branch after `Coordinator`: `ShareSwitch`, `TrafficHost`, `TrafficReceiver`, `AtcHost`, `AtcReceiver`, `ShareViewModel`; extend the `MainViewModel` factory with `ShareViewModel` last.
- `App.axaml.cs` Exit handler, before `Disconnect()`: `ShareSwitch.StopAll()`, `AtcHost.Dispose()`, `AtcReceiver.Dispose()`, `TrafficReceiver.RemoveAll()`, `SimTraffic.Dispose()`, `Settings.Save()`.
- `Views/MainWindow.axaml`: **height stays 700**; rows `Auto,Auto,Auto,*,Auto`; new card at row 2 in the existing idiom — title row; traffic row (`TextBlock.ShareLabel` + right-docked `ToggleSwitch.Share`, or the "· shared by" text when foreign-hosted; warning `Note ErrorNote` beneath when receiving with foreign AI); ATC row (same; beneath, when hosting: `ComboBox.Share` + status `Note`; when receiving: `Slider.Share` + icon mute `Button.Icon`); `Notice`; note line (visible only while something is shared). Strings inline as the file does today. Onboard item template: `traffic` / `ATC` badges after direct/relay. Nothing in the bottom strip.
- `Views/MainWindowStyles.axaml`: `ToggleSwitch.Share` (checked track `#8ec6ff`, knob black; unchecked `#66000000` with `#94a3b8` border; disabled opacity 0.4; no On/Off content), `Slider.Share` (track `#66000000`, fill/thumb `#8ec6ff`, `Height 20`), `ComboBox.Share` (rename of `ComboBox.Aircraft`), `TextBlock.ShareLabel` (+ `.Muted`), `Button.Icon`, `Border.Traffic`/`.Atc` + `TextBlock.Traffic`/`.Atc` mirroring the direct/relay badges (background `#8ec6ff`, black text, FontSize 8).
- `Icons.axaml`: `radar_regular` (card title), `speaker_2_regular`, `speaker_mute_regular` — path data from `microsoft/fluentui-system-icons` (`assets/<Name>/SVG/ic_fluent_<name>_24_regular.svg`).
- `FsCopilot.csproj`: `NAudio.Wasapi` 2.2.1 (not the umbrella package), `Concentus` 2.2.2; `TrimmerRootAssembly` entries for `NAudio.Wasapi`/`NAudio.Core` kept ready (see risks). TFM may need `net9.0-windows` if `AudioSessionManager` is missing from the netstandard asset.
- `SimConnectConsumer.cs`, `SimClient.cs`: **untouched.**
- `readme.md`: a short note (same traffic pack and scenery, own AI traffic off; ATC hosting needs Windows 10 2004+).

## Key invariants
- **Threading**: SimTraffic thread owns all traffic tables and `sim.*` calls; network and timers `Post` in. Audio: loopback thread → encode → `SendAll`; play thread owns the jitter buffer. VM on the UI scheduler.
- **Gate**: send iff changed or heartbeat due; changed after a held sample → held (with age) then fresh; identity precedes any state.
- **Interpolation**: receiver clock only; sample time = arrival − age; render one clamped interval behind; parked/final poses not written.
- **Election**: ordinal-min claimant; claims pruned on `hosting=false`/peer loss; own losing claim withdrawn and toggle reverted; claims re-broadcast to new peers.
- **Host filtering**: every traffic/audio handler drops packets whose `Host` ≠ current host or == self; per-host `Seq` on states and audio is for gap statistics (and would make an `Unreliable` follow-up safe).
- **Teardown**: toggle off → `hosting=false` → receivers `RemoveAll`; host lost → claim pruned → `RemoveAll`; 15 s per-object timeout; exit → `StopAll` + `SimTraffic.Dispose`; sim disconnect → tables cleared and hosting withdrawn until reconnect; host change → old host's objects removed before the new host's identities; a receiver that becomes host removes its injected objects first (`OwnObjectIds` guard).

## Order of work

0. **Record**: commit `record/traffic-atc/` on `ahead-record` with the gated capture; add this plan there as `docs/03-implementation-plan.md`. Cut `ahead-traffic-atc` from `main`; worktree `C:\Users\wayne\dev\fsc\ahead-traffic-atc`.
1. **Sim plumbing** — `SimTraffic`, `TrafficDefinitions`, DI + Exit dispose. Behaviour-neutral (one idle client, `[Traffic] connected (MSFS 2024)`).
2. **Settings** — `Settings.cs` + context, DI; startup log only.
3. **Packets + election** — `TrafficPackets`, `AtcFrame`, `ShareSwitch`, registration. Schema changes here (same-build rule already applies).
4. **Traffic** — gate, interpolator, host, receiver, fallbacks, options, flags; hosting driven by the persisted setting.
5. **Audio** — packages, `Audio/*`; trimmed-publish smoke test noted in the commit.
6. **UI** — `ShareViewModel`, `MainViewModel`, XAML, styles, icons, wiring.
7. **Copy pass** — every new string listed from the diff and shown on screen to the user; edits applied; readme note. Then rebuild `ahead`.

## Verification

**Tier 0 — no sim.** Codec round-trips for all five packets (including a full 10-state batch ≤ 480 B and the quantisation bounds); `ShareSwitch` tie-break vectors on three in-process instances (claims in either order resolve to the same host; release promotes; peer loss prunes). `Codecs.Schema` before/after: ids 0–7 unchanged. `dotnet publish -c Release -r win-x64` and run the trimmed single-file exe: loopback activation on any audio-session process, player output, settings round-trip. Two instances (`--skip-install`) joined by session code: A hosts ATC with a media player via Other…, B hears it; volume/mute; A quits → B's row reverts; both toggle traffic in the same second → exactly one keeps it, the other shows the notice.

**Tier 1 — one machine + sim.** A hosts traffic with BeyondATC/FSLTL; B with `--traffic-offset 2,90` receives into the same sim: copies 2 nm east, taxi smooth, parked engines off, lights right; `[Traffic]` poll cost / sent %. Fallback path by pointing a fallback at a wrong title. Foreign-AI warning by enabling the sim's own AI traffic on B. Teardown: A toggles off → copies vanish at once; kill A → within ~15 s; B in the menu at start → creates fail then succeed on the 30 s retry. ATC: BeyondATC auto-detected, close → "closed", reopen → re-attached in 2 s; mixer at 25 % on A → level unchanged on B. Sim restart on A → hosting withdrawn, re-claimed, copies rebuilt.

**Tier 2 — two machines.** Real session over direct and relay: bandwidth from stats (~2 KB/s, Q14), smoothness and TCAS by eye, audio latency by ear against the Discord readback. MSFS 2020 receiver and Windows 10 host if machines exist.

## Open risks and fallbacks
1. Trimming vs NAudio COM interop / Concentus → `TrimmerRootAssembly`, then `PublishTrimmed=false` (the csproj already calls trimming "try later").
2. `NAudio.Wasapi` netstandard asset lacking `AudioSessionManager` → TFM `net9.0-windows` (RID is already win-x64; `System.Windows.Extensions` is already a Windows dependency).
3. Fallback titles for helicopter / 2020 unverified → Tier 1; worst case the object is absent, logged.
4. MSFS 2020 untested end to end → plain-form API is FSX-era; degraded-but-working by design.
5. Windows 10 loopback (Q09) → surfaced as a status line; no fallback by decision.
6. Peer-loss latency (15 s `DisconnectTimeout`) → receivers hold a dead host's traffic ≤ ~15 s; the per-object timeout bounds it independently.
6b. Shared Sequenced channel drops on reordering-prone paths → visible only as PLC frames / brief traffic holds; measured by the receiver's gap counters; follow-up is an `Unreliable` `SendAll` overload, not a v1 change.
7. Two instances on one machine share the `log` file (Serilog `shared: true`, fine) and need `--skip-install`.
8. Default output device changes → re-create `WasapiOut` on notification; if flaky, on next playback exception only.
9. Three-peer partition → claim sets converge when it heals (re-broadcast on new peer); same window `SetMaster` already tolerates.
