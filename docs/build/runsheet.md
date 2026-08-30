# Session runsheet

    Purpose:  The order to run probes in during one sim session, and what to save.
    Plan:     plan.md
    Log:      log.md

The simulator is the expensive dependency — getting into a flight with the right
aircraft and the right panel selected is minutes of work that cannot be scripted.
So one session should answer as many questions as it can. This is the order.

Save every printed report into `results/` as `pNN-<aircraft>-<panel>-<date>.txt` and
commit it, then write the reading into [log.md](log.md).

---

## Before starting the sim

**Clear the port.** 19999 has been held by an orphaned socket from a dead process.
Close the CoherentGT Debugger, then confirm:

```bash
npm run probe:00
```

Expect `NOT-LISTENING`. If it still says `ORPHAN`, something is still holding the
inherited handle — a reboot clears it for certain.

Then start MSFS with DevMode enabled and run it again. Expect `ALIVE <pid>
FlightSimulator2024`. Only then does anything the probe reports below that line
mean anything.

- **Answers something** → automation is on. Say so and stop pasting probes by hand.
- **Silence from a live owner** → Q00 is genuinely negative. Accept the manual loop
  and move on; do not spend more on it.

## In the cockpit

Every console probe is pasted whole into the Coherent GT debugger Console with the
**right panel selected in the frame picker**. Panels are separate documents and see
entirely different things — a probe run against the wrong one is not a weaker
result, it is a meaningless one.

### 1 · A220 — CTP  *(React over SVG)*

The most important target in the session. Q03 is load-bearing and this is where it
gets answered.

| | Probe | Then |
| --- | --- | --- |
| a | `p04-iframe.js` | confirms the surface classification before anything else |
| b | `p02-capture.js` | click, press-and-hold, and drag on the CTP, then `__P02.report()` |
| c | `p03-replay.js` | `__P03.arm()` → click a real softkey → `__P03.fire()` |

If (c) shows mutations, **Q03 passes and the project is alive.** If it shows nothing,
try `__P03.fire({mode:'mouse'})` and `{mode:'pointer'}` separately before concluding —
they isolate whether `PointerEvent` is the thing that matters.

### 2 · A220 — MKP  *(canvas)*

`p02-capture.js` then `p03-replay.js`, same sequence. Canvas repaints mutate no DOM
nodes, so the mutation count will read zero either way — **watch the display itself**.
This is the one target where eyes beat instrumentation.

### 3 · A350 — MFD  *(WASM live-view)*

| | Probe | Answers |
| --- | --- | --- |
| a | `p01-wasm-shell.js` | Q05 — the whole WASM-gauge question |
| b | `p02-capture.js` | whether the panel document sees input at all |

Q05 has three outcomes and they are in [../03-scope.md](../03-scope.md). This is the
difference between fixing two aircraft and fixing five.

### 4 · Fenix EFB and TDS GTN 750  *(iframes, maybe)*

`p04-iframe.js` on each. Answers Q06 and Q07. On the Fenix, if the frame comes back
reachable, `__P04.watch(0)` then click inside the EFB — that shows whether capture
inside a child document is viable.

### 5 · Any panel — loopback

In a terminal:

```bash
npm run probe:05-server
```

then paste `p05-websocket.js` in the console. Watch the server window for an
`UPGRADED` line; that is proof independent of whatever the console reports.
Answers Q08.

## A control worth running

If time allows, run `p02` and `p03` on a **classic G1000 panel** — something the
existing scheme already syncs correctly. It establishes what a passing result looks
like on this machine, which makes a failure elsewhere much easier to read.

## After

Write each result into [log.md](log.md) in the entry format, newest first, with the
raw output committed under `results/`. Then update the statuses in
[../06-open-questions.md](../06-open-questions.md) and add pointer lines to any
design doc a result overturned.
