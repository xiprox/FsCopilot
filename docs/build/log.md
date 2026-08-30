# Probe log

    Purpose:  What the probes found, and what it changed.
    Plan:     plan.md
    Order:    newest first

Findings recorded as they happen. A session picking this work up reads [plan.md](plan.md) for
scope and status, then the top of this file for anything the plan does not yet reflect.

## What belongs here

- A probe result, whether it settled a question or not
- Anything that contradicts a design doc in `docs/01-06`
- A decision taken while probing that a later session would otherwise re-derive
- An approach tried and abandoned, **including why** — this is the part git history cannot
  tell anyone

Not here: what got written and when. That is what commits are for.

## Entry format

```
## YYYY-MM-DD — one-line summary
Question:  which question this moves, and to what status
Stage:     which stage of plan.md
Expected:  what was believed going in
Found:     what turned out to be true
Changed:   what this changes about the plan or the design, or "nothing"
Affects:   design docs this amends, or "none"
Evidence:  file under results/, or "none"
```

`Affects:` is what makes "does this override the design?" answerable by grep rather than by
reading everything. When an entry amends a design doc, add a pointer line at the top of that
doc naming this entry — see the working notes in [plan.md](plan.md).

---

## 2026-08-30 — The debugger port accepts connections and says nothing

    Question:  Q00 — still open, one hypothesis left
    Stage:     0
    Expected:  Coherent GT is a WebKit derivative, so either a Chrome DevTools
               Protocol endpoint (/json, /json/list) or a WebKit Web Inspector
               WebSocket. Either would make every console probe scriptable.
    Found:     Something listens on 0.0.0.0:19999 while MSFS 2024 is in a flight
               with the debugger window open — pid 33168, whose process name could
               not be read back through either Get-Process or Get-CimInstance,
               which usually means elevation.

               TCP connects succeed. Nothing answers. Not `GET /`, not any of
               eleven paths including both the CDP and WebKit Inspector shapes;
               not a WebSocket upgrade on six paths; not a bare CRLF, a JSON-RPC
               line, or a `COHERENT` token.

               It does not close the connection either. It holds the socket open
               and idle — verified out to 12 seconds, well past any plausible
               response latency. That is the informative part: an HTTP server that
               dislikes a path returns 400 or 404. Accepting and then going silent
               is not rejection.

               MSFS's own listeners at the time were 49266, 127.0.0.1:50153 and
               ::1:50154. 49266 had gone by the follow-up run; 50153/50154 look
               like SimConnect's auto-assigned pair.
    Changed:   Two explanations survive and only one is cheap to test.

               1. **One client at a time.** The debugger UI was connected
                  throughout. If the backend serves a single session, our
                  connections were accepted into a queue and starved. Next step:
                  close the Coherent GT Debugger window entirely and re-run
                  `npm run probe:00`. Any response at all puts automation back on
                  the table.
               2. **A framed protocol** — length-prefixed rather than newline- or
                  HTTP-delimited, so the server is still waiting for a frame that
                  never completes.

               Worth exactly one more attempt. If closing the debugger changes
               nothing, take the manual console loop and stop paying attention to
               this: every probe still works pasted by hand, it just costs a round
               trip each.
    Affects:   none
    Evidence:  results/p00-debugger-2026-08-30-09-44-57.txt

---

## 2026-08-30 — The A350's WASM displays are not in an iframe

    Question:  background to Q05 — narrows it, does not settle it
    Stage:     2
    Expected:  Working assumption carried in from the FS Copilot deep dive was
               that WASM instruments live in an iframe, which would have explained
               the failure straightforwardly: synthetic events do not cross an
               iframe boundary, and a parent's listener never sees a click inside
               one.
    Found:     Coherent GT debugger on an A350 MFD panel shows no iframe at all:

                 <vcockpit-panel id="panel">
                   <wasm-instrument data-input-group="WASM-INSTRUMENT" guid="118"
                       url="coui://…/WasmInstrument.html?wasm_module=inibuilds-A350.wasm&wasm_gauge=MFD">
                     <div id="Mainframe">
                       <wasm-sim-canvas>
                         <img src="WasmGaugeLiveView_25">

               Every node there is an HTMLElement, so FS Copilot's existing scheme
               *does* name them and *does* emit interactions for A350 clicks
               today. They land on an `<img>` that is a live-view texture blit
               with nothing behind it.

               Separately confirmed from panel.cfg: every A350 display is
               WasmInstrument — EFIS, SD, MFD, ISIS, FCU. Only the EFB is real
               HTML.

               `data-input-group="WASM-INSTRUMENT"` is a sim input-routing
               attribute, which points at input being delivered to the gauge
               natively rather than through Coherent.
    Changed:   Worsens the outlook for the A350 rather than improving it. The
               iframe framing suggested a boundary that could be descended
               through; a live-view `<img>` suggests there is nothing on the other
               side of the DOM to reach. Q05 is now specifically "does
               WasmInstrument.html forward DOM input", and probes/p01-wasm-shell.js
               exists to answer it.

               Corrects the FS Copilot deep dive, which still describes the A350
               as an iframe. To be folded in alongside the p01 result rather than
               revised twice.
    Affects:   01-problem, 03-scope — both already written with this correction
    Evidence:  Coherent GT debugger screenshot, 2026-08-30. Not reproduced in
               results/ because it is a DOM inspection rather than probe output;
               p01 will capture the same tree in text.

---

## 2026-08-30 — The Fenix EFB is a cross-origin iframe, and capture never happens

    Question:  background to Q06 — splits it into two problems
    Stage:     2
    Expected:  An iframe whose contents could be reached by descending from the
               parent, as a same-origin child would allow.
    Found:     Coherent GT debugger on the Fenix first-officer EFB panel:

                 <efb-host-instrument-fo data-input-group="EFB-HOST-INSTRUMENT-FO"
                     url="coui://…/FNX32X/EFB/EfbHost_FO.html">
                   <div id="EfbHostContent">
                     <div class="efb-host-component">
                       <iframe id="iframe" src="http://localhost:8083/index.html">

               Parent is `coui://`, child is `http://localhost:8083` — served by
               Fenix's own external process. Cross-origin, so `contentDocument`
               should be null under normal same-origin policy.

               The larger finding is separate from reach: a real click inside an
               iframe is dispatched into the **child's** document. The parent's
               document-level listener never fires. FS Copilot is not failing to
               replay Fenix EFB interactions — it never captures them.
    Changed:   Q06 is two questions, not one. Reach (can we touch the child) is
               untested and depends on how permissive Coherent GT is about SOP.
               Capture location is settled and applies even to a same-origin
               child: capture has to run inside the child document either way.

               Also incidental evidence for Q08 — Coherent GT does outbound HTTP
               to loopback from a cockpit document, which is most of what a local
               WebSocket transport would need.
    Affects:   01-problem, 03-scope, 04-transport — all already written with this
    Evidence:  Coherent GT debugger screenshot, 2026-08-30.
