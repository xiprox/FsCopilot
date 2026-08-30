# Probe plan

    Status:     Stages 0-5 done. The mechanism is proven, a real in-simulator
                module carries it over a WebSocket to a local host, capture is
                lossless at 33/33 including spam-clicking, and it all works with
                **DevMode off** - so the transport is shippable, not merely
                convenient.
                Settled: Q00, Q01, Q02, Q03, Q08 yes; Q05 no, permanently.
                Q04 deferred on the same-build assumption. Q06, Q07 and Q09 open,
                none of them blocking.
                Stage 6 is the only one left and it is blocked solely on there
                being no second machine. A remote tester needs the Community
                package and the host, and nothing else.
    Supersedes: nothing
    Log:        log.md

**Start here.** The design record in `docs/01-06` describes what is being proposed, including
parts that are conditional on results that do not exist yet. This file says what is actually
being run, in what order, and where it stands.

## Resuming

Read this file, then the top of [log.md](log.md). That is enough to continue without any of the
conversation that produced them. Then [06-open-questions](../06-open-questions.md) for what is
still unanswered, and the design docs only as needed — treat any of them as amended by the log
where the two disagree.

## The stages

Each stage exists to answer specific questions and has an exit criterion. Stages were ordered
so that a negative result would stop work early. In the event nothing did.

### Stage 0 · Automation — Q00
**DONE — yes.**

MSFS hosts a WebKit inspector on `127.0.0.1:19999`. `Runtime.evaluate` runs arbitrary
JavaScript in any panel; `Console.enable`, `Page.getResourceTree` and `Page.searchInResources`
do the rest. Driven by `probes/lib/inspector.mjs` through `npm run pages | probe | eval |
sources`.

The first attempt measured an orphaned socket and concluded nothing — see the log. Confirm a
port's owner is alive before reading anything into its silence.

### Stage 1 · Does the mechanism work — Q01, Q02, Q03
**DONE — the load-bearing stage passed.**

A synthetic `mousedown`/`mouseup`/`click` at captured coordinates opened a dropdown on the
A220's React/SVG DisplayUnits, target an SVG `rect` — exactly the class the existing scheme
cannot name. Q01: mouse events only, and `PointerEvent` does not exist in Coherent GT at all.
Q02: real input is trusted, so `isTrusted` works as a second loop breaker.

### Stage 2 · Reach — Q05, Q06, Q07
**Q05 done and negative. Q06 and Q07 still open, and neither blocks anything.**

WASM displays are permanently unreachable: the simulator owns the hit-target and discards the
coordinates we send, so an injected press fires whatever the *receiving* pilot is hovering —
worse than a no-op. Written up in full as [07-wasm-surface](../07-wasm-surface.md).

Q06 needs an aircraft with a genuine cross-origin panel; the A350's EFB turned out to be plain
HTML. Q07 needs the TDS GTN loaded.

### Stage 3 · Transport — Q08, Q09
**DONE — Q08 yes, which made Q09 moot.**

A `coui://` document can hold a WebSocket to loopback, full duplex. The prototype therefore
uses no CommBus, no WASM and no SimConnect, and Q09 — whether the 512-byte bus survives a
burst — no longer matters unless that path comes back.

Caveat on the record: demonstrated with the inspector running. DevMode off is untested.

### Stage 4 · A real module
**DONE, by a different route than planned.**

The plan said a drop-in for `fscopilot-bridge/html_ui/`. What exists is our own Community
package with its own WebSocket transport — see [10-module](../10-module.md). Panels connect,
presses appear live in the host, recording and replay both work.

An intermediate testbed that carried data over the inspector's console channel was built and
then retired: that channel silently drops repeated messages.

### Stage 5 · End-to-end on one machine
**DONE.**

Recording and replay work through the real module, and capture is verified clean: 33 presses
counted deliberately, including spam-clicking, produced 33 captures and 33 replays. The
earlier five-of-nine shortfall was entirely the retired console channel's dedup.

The original description of this stage — echoing `Interact` packets back through
CommBus/WASM/SimConnect — is obsolete, since none of those are in the path any more. The
paired-panel test named here is cancelled too: EFBs are out of scope and the A220's other
paired panels are bezel-driven rather than pointer-driven.

### Stage 6 · Two machines
**Blocked — there is no second machine.**

The only other tester is in another country, so this waits on a packaged build they can run.
[08-testbed](08-testbed.md) has the eventual shape. Q04, whether two machines agree on the
instrument rect, is deferred with it on the assumption that the rect is `panel.cfg`-derived and
therefore identical on the same addon build.

## Working notes

- **The sim is a slow, stateful, manual dependency.** Batch everything that needs it into
  deliberate sessions rather than reaching for it continuously. Getting into a flight with the
  right aircraft and the right panel selected is minutes of work that cannot be scripted.
- **Record findings as they happen**, in [log.md](log.md), not at the end. A finding that
  changes scope is worth more than the code written that day.
- **Amend design docs by pointer, never silently.** If a result overturns something in
  `docs/01-06`, add a line at the top of that doc naming the log entry. Do not rewrite the
  design record mid-build. The exception is
  [06-open-questions](../06-open-questions.md), which is a register and is updated in place.
- **Save raw probe output into `results/`** and commit it. `log.md` is the reading; `results/`
  is the evidence, and a later session will want to re-read it rather than trust a summary.
- **A negative result is a result.** Write it down with the same care as a positive one. The
  approaches that were tried and abandoned are the part git history cannot tell anyone.
