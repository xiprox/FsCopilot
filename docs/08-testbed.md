# The testbed

    Purpose:    How pointer forwarding is exercised end to end without installing
                anything into the simulator.
    Depends on: 02-approach
    Decides:    that experiments run through the remote inspector rather than
                through a modified fscopilot-bridge, and how the result ports back

Stage 4 in [build/plan.md](build/plan.md) originally proposed a drop-in for
`fscopilot-bridge/html_ui/`. That is no longer the best option, because Q00 turned out
yes: the simulator hosts a remote inspector that will both inject and report.

> **Scope, decided 2026-08-30.** There is no second machine available — the only
> other tester is in another country — so this is built **single-machine first**:
> record a real interaction to a file, replay the file into a live panel, verify
> the same outcome. Two-machine work is deferred, and the LAN diagram below
> describes the eventual shape rather than what is being built now.
>
> A recording is better than a live peer for the core science anyway: deterministic,
> re-runnable after every change to the agent, and committable as evidence. The
> same reasoning produced `fsc-editor/scripts/sim-sandbox.ts`.

## The channel

Q00 established `Runtime.evaluate` — arbitrary JavaScript into any panel document.
`Console.enable` completes it in the other direction: anything the page logs arrives as
a `Console.messageAdded` event on the same socket.

```
        Machine A                                  Machine B
   ┌──────────────────┐                       ┌──────────────────┐
   │ MSFS panel doc   │                       │ MSFS panel doc   │
   │   agent.js       │                       │   agent.js       │
   └────▲────────┬────┘                       └────▲────────┬────┘
 inject │        │ console.log            inject │        │ console.log
        │        ▼                                │        ▼
   ┌────┴─────────────┐    ws over LAN       ┌────┴─────────────┐
   │  testbed (Node)  │◄───────────────────► │  testbed (Node)  │
   └──────────────────┘                      └──────────────────┘
```

Nothing is installed in the simulator. No package, no `VCockpit.js` patch, no WASM
module, no SimConnect, no build step.

## Why not modify fscopilot-bridge

The original reason to avoid a second package was that `fscopilot-bridge` already
overrides `VCockpit.js` and two packages patching one core file is a conflict. That
still holds, but the stronger reasons are practical:

- **Iteration cost.** Editing the agent and re-injecting is seconds. Anything involving
  a package is a sim restart.
- **Blast radius.** Code injected for a test cannot break FS Copilot, and cannot be left
  behind in a shipped package. The morning's `Coherent.call` incident — an experiment
  that silently disabled an aircraft's clicks — is the argument for keeping experiments
  out of anything a user runs.
- **Clean attribution.** With no FS Copilot code in the path, a failure is ours.
- **Coverage.** It works on any aircraft immediately, including ones nobody has written
  a profile for.

## The pieces

| File | What it is |
| --- | --- |
| `testbed/agent.js` | The injected payload: capture, normalisation, replay, loop breaking. The only file with real logic. |
| `testbed/bridge.mjs` | Attaches to pages, injects the agent, drains `Console.messageAdded`, applies inbound replays. |
| `testbed/link.mjs` | Node-to-Node over the LAN. The dependency-free WebSocket server from `probes/p05-server.mjs` is the starting point. |
| `testbed/session.mjs` | CLI. Pick panels by title, connect to a peer, run. |

## The rule that makes this portable

**`agent.js` must not know how messages travel.** It exposes two things:

    agent.onCapture(fn)     // called with a message when the pilot interacts
    agent.replay(msg)       // apply a message from the peer

The testbed wires those to `console.log` and `Runtime.evaluate`. FS Copilot would wire
the identical file to the CommBus. If that boundary stays clean the port is about twenty
lines of plumbing rather than a rewrite, and the mechanism that was proven is the
mechanism that ships.

Everything transport-shaped — message framing, batching, rate limiting, the 512-byte
question in [04-transport](04-transport.md) — belongs on the Node side of that line, not
in the agent.

## Out of scope

**EFBs.** FS Copilot handles them adequately today; improving that is separate work.
This means no cross-panel test on paired EFB instances — the recorder and player
target the same panel instead.

**Changing simulator display settings** to probe rect stability. The measured
DisplayUnits rect was exactly `7410 x 1110`, which is the `pixel_size` from the
aircraft's `panel.cfg`, so the rect is taken as panel.cfg-derived and therefore
identical across machines running the same addon build. Assumed rather than proven;
see Q04.

## What it proves, and what it cannot

**Proves:** capture fidelity; coordinate normalisation; replay correctness across two
machines; loop breaking; behaviour per surface; and Q04, whether two machines agree on
the instrument rect.

**Cannot prove:** anything about the CommBus / WASM / SimConnect path, since it does not
use it. Nor anything about behaviour with DevMode off — the inspector does not exist
there. Both are acceptable: transport is deliberately deferred, and this is a testbed
rather than a product.

Latency through the inspector is also not representative and should not be quoted as
evidence about the real thing.

## Known rough edges

Three things to settle while building rather than discover later:

- **The console channel replays its backlog.** Connecting produced 27 `Console.messageAdded`
  events from panel initialisation before anything of ours. Messages need a distinctive
  prefix and the reader needs a start marker.
- **`Console.messageAdded` may truncate or rate-limit.** Unmeasured. The wire format
  should be short regardless, and throughput wants measuring before anyone trusts it for
  a drag stream.
- **A polled buffer is the fallback.** If push disappoints, the agent can accumulate into
  an array that `Runtime.evaluate` drains on a timer. Less elegant, entirely predictable,
  and it is what the probes already do.

## Relationship to the probes

The probes stay. They answer one question each and are thrown at a panel by hand;
`probes/lib/inspector.mjs` is the shared driver and the testbed builds on it rather than
replacing it. The testbed is the first thing here that runs continuously rather than
once.
