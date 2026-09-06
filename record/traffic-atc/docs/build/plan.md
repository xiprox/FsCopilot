# Prototype plan

    Status:     Stages 0 and 1 done, stage 2 overtaken, stage 3 built and measured on one
                machine. Settled: Q01 Q03 Q05 Q08 Q10 Q12 Q13 Q16 Q17. Pushback confirmed
                smooth by eye; frozen per-frame drive is 3x smoother than BeyondATC's own
                traffic; the change gate sends ~20 % of samples at no cost to smoothness;
                audio is ~130 ms capture-to-speaker with PLC covering loss, heard live
                from BeyondATC (Q11), mixer level compensated (Q10).
                Q06 closed by assumption. Deferred by decision: TCAS (no reason it should
                not work), vehicles (Q07, not v1), Windows 10 (Q09, maybe later). The ATC-app
                picker's mechanics are proven (sessions list, attach-while-silent, silent
                exit). **Stage 4 built**: commits 1–8 on `ahead-traffic-atc` (traffic, audio,
                card, copy, layout) proven on the one-machine bed and reviewed by hand. What
                is left is the readme note, the `ahead` rebuild, and the two-machine test,
                which needs a second tester. See handoff.md.
    Supersedes: nothing
    Log:        log.md

**Start here.** [01-design](../01-design.md) is what is proposed; this file is what is being
run to find out whether the proposal holds, in what order, and where it stands. Stages are
ordered so a negative result stops work early.

Everything runs on **one machine** against one simulator. Two-machine testing is the last
stage and is the one thing this plan cannot do alone.

## Resuming

Read this file, then the top of [log.md](log.md), then
[02-open-questions](../02-open-questions.md).

## Tools

All under `record/traffic-atc/tools/`, .NET 9 console projects referencing the managed
SimConnect DLL the fork already ships. They share a small library for the SimConnect
plumbing so the reader and injector do not diverge on definitions.

| Tool | Does |
| --- | --- |
| `TrafficRead` | Polls `RequestDataOnSimObjectType` at a flat rate with a `--radius` (default and cap 200 km) and `--types` (default `aircraft,helicopter`; add `ground`), diffs for add/remove, writes NDJSON: one `identity` line per discovery, one `state` line per sample, `removed` lines. |
| `TrafficInject` | Replays an NDJSON capture into the sim: creates, releases, drives, removes. Optional latency and loss simulation on the replay. |
| `AtcAudio` | Process-loopback capture of a named process, Opus, UDP localhost (with loss simulation), decode, jitter buffer, play on a chosen device. |

The NDJSON format is the design's wire format written out as text; when the FSC packets are
written they encode the same records.

## The stages

### Stage 0 · Read — Q01, Q02, Q03
Fly with BeyondATC (or SayIntentions) injecting traffic. Run `TrafficRead` at 2 Hz and again
at 5 Hz for a full departure: gate, taxi, takeoff, climb. Capture to `recordings/`. Note
what the add-on holds beyond 200 km, if anything can tell.

Exit: a capture with 30 or more aircraft, with identities; the poll cost at both rates; and
what a livery looks like from the read side.

### Stage 1 · Inject — Q04, Q05, Q06, Q07, Q08
ATC add-on **off**, own AI traffic off. Replay the stage-0 capture with `TrafficInject` at
the recorded rate, then thinned to 2 Hz and 1 Hz. Watch the taxiing aircraft from the
ground, watch the TCAS, watch the frame counter. Then kill the injector without cleanup and
see whether the aircraft go with it. Then switch `GROUND` on and look at the GSX vehicles.

Exit: replayed traffic is on the ground, moving smoothly at the chosen rate, visible on
TCAS, at an FPS cost stated in the log; Q07 and Q08 answered yes or no.

### Stage 2 · Loop — Q14, Q15
> Mostly overtaken. Echo is a non-issue with one host and the injector's id set; bandwidth
> is arithmetic until the FSC packets exist (Q14 → stage 4); the receiver no longer uses the
> host's clock (Q15: dead reckoning is gone, interpolation runs on arrival times). What is
> left of this stage is *loss and jitter on the wire*, and that does not need a socket:
> `--rate` already thins samples, and a `--loss <pct>` on the injector would drop them at
> random. Do that if the 1 Hz boundary holds turn out to matter; otherwise skip to stage 3.

### Stage 3 · Audio — Q09, Q10, Q11, Q12, Q13
`AtcAudio` against the running ATC app, output to a second device (or the same device —
one machine, it is audible either way). Measure latency by recording the ATC app's speech
onset against the loopback output.

Exit: ATC speech audible through the tool with a stated latency, at a stated bitrate, with
silence gated.

### Stage 4 · Fold into FS Copilot
Cut `ahead-traffic-atc` from `main`. Host election, packets, reader/injector over
`SimClient`, audio module, UI toggles and the "same traffic pack, same scenery, own traffic
off" note. The record stays here; only implementation goes on the branch. **This is where
two-machine testing starts**, and it needs a second tester, as pointer-forwarding did.
