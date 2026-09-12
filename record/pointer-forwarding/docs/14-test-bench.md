# The bench

    Purpose:    How the transport, the session state machine and the panel channel are
                exercised on one machine, without a simulator.
    Depends on: 11-fsc-implementation-plan, 08-testbed
    Decides:    that the panel is faked rather than driven, that outages are made by
                suspending a process, and what the bench cannot settle

Stage 6 of [build/plan.md](build/plan.md) is "two machines", and its gap was never the
happy path — a three-way session on 2026-09-08 carried 70 replays with nothing missed.
What it left untested was every path where something goes wrong: the receiver side on
someone else's machine, degraded, resend, timeout. None of those occurred, because
nothing broke.

This runs two instances on one machine and breaks things on purpose.

    node testbed/bench.mjs            every fast scenario, about three minutes
    node testbed/bench.mjs --list     what there is
    node testbed/bench.mjs --slow     including the five-minute timeout

## The panel is faked, not driven

`testbed/lib/panel.mjs` speaks what `channel.js` speaks: hello on connect and on every
reconnect, `{t:"pointer"}` captures out, config/state/pointer/bye in. The app cannot tell
one from a cockpit panel, and two things follow that the cockpit cannot give.

**It knows what it sent.** Comparing a replay against the gesture that produced it is a
diff, not a judgement about what a display appeared to do.

**It can send what no real panel would.** A key nobody configured, a 1500-point path, a
gesture into a panel that is about to disappear. Half the scenarios below are inputs that
capture would never produce, against code written to survive them.

The app broadcasts `{t:"state"}` every two seconds, so the fake panel is also where the
bench watches the session state machine. There is no second channel for test state: what
the bench asserts on is what a pilot's panel would have been told.

## Outages are made by suspending a process

Three ways to remove an instance, and the code paths differ:

| | What the peer sees |
| --- | --- |
| `quit()` | the clean exit path — panels are told `{t:"bye"}` and the peer is told the app left |
| `crash()` | `taskkill /F`. No goodbye, no disconnect |
| `suspend()` | sockets stay open, nothing answers. `NtSuspendProcess` through PowerShell |

Suspend is the one worth explaining. A kill closes sockets, which is a link that *went*; a
suspended process holds them open and stops answering, which is a link that *stopped*. The
peer times out after LiteNetLib's 15 s `DisconnectTimeout` with nothing closed at either
end, and `resume()` brings the same process back rather than a new one — the case where
the receiver's dedupe state survives the outage, which a restart destroys.

`netsim` is also wired, setting LiteNetLib's own packet loss and latency simulation through
the control channel. The fields are present in the shipped 1.3.1 assembly. No scenario uses
it yet.

## What had to be added to the app

One file, `FsCopilot/Connection/BenchControl.cs`, started by `--bench <port>` and by nothing
else, plus `--peer-id` and `--relay` in `Program.cs` and three lines in `App.axaml.cs`. It
is newline-delimited JSON over loopback TCP: `configure`, `join`, `leave`, `peers`,
`netsim`, `quit`. The upstream PR is a subset cut from the branch, so it simply does not
take the file.

`configure` goes through `Coordinator.Load`, not `PanelServer.Configure`. Setting only the
second gives panels that capture and an app that drops everything they send — the filter on
both directions of the wire is the Coordinator's own copy. That was the first bench run's
only failure and it took a log read to see, which is an argument for the two lists having
one setter.

Building `Definitions` needs its private constructor, called by reflection. The alternative
was one shared profile on disk, which cannot give the two instances different lists, and a
peer whose profile differs is the case the inbound filter exists for.

## The rendezvous is local

`FsCopilot.Discovery` is in the repository, so `--relay 127.0.0.1` puts the two instances in
touch without reaching fscrelay.ihsan.dev. A run with no internet is the same run. The
project is pinned to `linux-x64` self-contained because that is what the server runs, so the
bench overrides the RID on the command line rather than touching the csproj.

Peer ids are numbered per world (`BNCHA001`, `BNCHB001`) rather than fixed. The rendezvous
holds a registration for a while after the process behind it dies, and a second world
reusing the first world's ids is rejected with `PEER_ID_TAKEN`.

## Do not run the simulator during a bench run

Three separate collisions, and the first one is silent:

- `sim.Aircraft → Definitions.Load → coordinator.Load` replaces the bench's pointer keys
  with the real aircraft's profile the moment one loads. Every capture then falls out of
  the filter, at whatever moment the pilot reached the aircraft.
- Both instances open SimConnect eagerly, are in a live session with each other, and sync
  variables. Instance A reads a change out of the running aircraft and instance B writes it
  back into the same one.
- With no real FS Copilot running, every cockpit panel document connects to the bench's
  instances: `channel.js` rotates 9020–9024 and takes the first port that answers.

No guard is built for this. Isolating the bench from the simulator means making `SimClient`
inert under `--bench`, which is real surface inside a shipping class, and the constraint is
cheap to honour.

## What it settles, and what it does not

**Settles:** wire fidelity for presses and drags including the 240-point path and the gap;
the session state machine through join, leave, quit, kill and blackhole; history, resend and
`(Session, Seq)` dedupe across an outage; the panel channel's hello, reconnect, config and
goodbye; routing, including both places an event is dropped; the profile filter on both ends.

**Does not settle** what the cockpit already settled, and this is why the bench is worth
having at all: DOM hit-testing, replay fidelity into a real display, and the overlays are
proven and stay proven in the sim.

Three more it cannot reach:

- **Replay pacing.** `GapMs` survives the wire and the bench checks that it does. The pacing
  itself is the replay queue in `pointer.js`, which a fake panel does not have.
  Reimplementing it here would test the reimplementation.
- **Decode rejection of an over-long path.** The encoder truncates to 1024 before writing,
  so the receiver's `> 1024` branch cannot be reached from a real sender. It is a defence
  against a corrupt or hostile peer and needs a hand-built packet.
- **Two machines.** Everything here shares one clock, one loopback and one build. Q04,
  whether two machines agree on the instrument rect, is untouched.

## Known failures

Two scenarios are red against `833ea67`, and in both the bench is asserting the documented
intent. They are [Q11](06-open-questions.md) and [Q12](06-open-questions.md).

`quit-says-goodbye` — a deliberate quit reaches the peer as a timeout.
`outage-and-recovery` — a blackholed link does not re-establish in three minutes.
