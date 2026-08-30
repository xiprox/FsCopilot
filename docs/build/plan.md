# Probe plan

    Status:     Gate A run once, inconclusive. Gate B not started — nothing about
                pointer forwarding has been tested against a running simulator yet.
                Everything in the design record is a proposal.
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

Each stage exists to answer specific questions and has an exit criterion. Stages are ordered so
that a negative result stops work as early as possible.

### Stage 0 · Automation — Q00
**Status: run once, inconclusive.**

Determine whether probes can be driven from outside the sim rather than pasted into a console.

*Exit:* either the debugger answers something and we build a runner, or it does not and we
accept the manual loop. **One more attempt only** — with the Coherent GT Debugger window
closed, to test the single-client hypothesis in the log.

### Stage 1 · Does the mechanism work — Q01, Q02, Q03
**Status: not started. `p02-capture.js` and `p03-replay.js` not written.**

The load-bearing stage. Everything after this assumes it passed.

- **Q01/Q02, capture:** attach a logging listener for the full pointer and mouse families on a
  panel document, click the panel in the sim, record which events arrive, in what order, with
  what `isTrusted` and what coordinates.
- **Q03, replay:** on the A220 `CTP`, dispatch the sequence from
  [02-approach](../02-approach.md) at a known softkey's coordinates and observe whether the
  display reacts.

*Exit:* a demonstrated synthetic press on a React/SVG display. **If Q03 fails, stop** — the
approach has no fallback and the remaining stages are wasted effort.

### Stage 2 · Reach — Q05, Q06, Q07
**Status: `p01-wasm-shell.js` written, not run.**

Independent of Stage 1 and can run alongside it, because it changes scope rather than
viability. Q05 first: it moves the most.

*Exit:* each of the five surfaces in [03-scope](../03-scope.md) marked reachable or not, with
evidence.

### Stage 3 · Transport — Q08, Q09
**Status: not started.**

*Exit:* a decision on whether the interaction path keeps the CommBus/WASM/SimConnect route or
moves to a local WebSocket, recorded in [04-transport](../04-transport.md) by pointer.

### Stage 4 · Instrumented bridge
**Status: not started.**

A drop-in for `fscopilot-bridge/html_ui/` that captures and replays for real, with an
install/uninstall script so the tree returns to stock. No protocol changes yet — it talks to
itself.

*Exit:* a real click on one panel producing a correct synthetic press on the same panel, with
per-instrument counters.

### Stage 5 · End-to-end on one machine
**Status: not started.**

A dev toggle that echoes received `Interact` packets straight back into the sim, so a message
traverses every hop — CommBus, WASM, SimConnect, C#, and back — on a single PC. Higher fidelity
than a JS-only loopback because it exercises the transport.

Then paired panels: several aircraft carry two instances of the same instrument (A220 `CTP_1` /
`CTP_2` and `MKP_1` / `MKP_2`, A350 `$EFIS_LEFT` / `$EFIS_RIGHT`, EFB captain and first officer).
Capture on one, replay on the other. Real cross-document routing, still one machine.

*Exit:* a press on the left panel appearing on the right.

### Stage 6 · Two machines
**Status: not started.**

Start with the A220 `CTP`.

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
