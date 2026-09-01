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

## 2026-09-01 - The FS Copilot implementation is built and verified offline

    Question:  none - executes the plan in 11-fsc-implementation-plan.md
    Stage:     graduation; feeds stage 6
    Expected:  The plan, unchanged.
    Found:     Built as planned, in a git worktree at ../fscopilot-pointer
               (branch pointer-forwarding off upstream/main), leaving the
               dev-var-replay checkout and its uncommitted state untouched.
               Five commits: the VCockpit.js pending-array fix; the inbound
               Interact ignore filter; the panel WebSocket channel
               (PanelServer.cs + channel.js, ports 9020-9024 with rotation);
               the pointer feature (pointer.js port of agent.js v5,
               PointerPress/PointerDrag with Session+Seq, two-mode gap-replay
               history, blue-lock/red-warning overlays, pointer: profile key);
               stats reports + a --dev echo mode.

               Verified without the sim, all passing:
               - Codec round-trips (press 60 bytes; 240-point drag 2430 bytes;
                 oversized-path decode rejected).
               - Live PanelServer over a real ClientWebSocket: hello ->
                 config+state, pending-buffer flush on late hello, inbound
                 JSON -> typed packets with delta conversion, outbound
                 absolute-time reconstruction, port rotation (second instance
                 lands on 9021), Configure broadcast.
               - All 212 recorded gestures from recordings/ survive
                 JSON -> record -> bytes -> record verbatim.
               - dotnet publish with PublishTrimmed+SingleFile: the trimmed
                 exe binds 9020 non-elevated and accepts a WebSocket upgrade
                 (HTTP 101) - the trimming and URL-ACL risks are retired.

               Two deviations from the written plan, both discovered against
               the real tree: upstream ships no aircraft profiles (only
               Definitions/modules/), so the ignore:/pointer: profile entries
               belong to the served-profile store, not the PR; and the
               packaged Packages/ output is not in git at all (built by the
               MSFS SDK from PackageSources/), so commits touch
               PackageSources/ only. One near-miss caught in review: the
               routing key's query must come from the instrument element's
               url attribute, as agent.js does - not location.href.

               What remains needs the sim or two machines: the single-machine
               --dev echo session, and the remote tester's session for
               Q04/Q10 (rect is in the hello; Seq gaps and per-key counters
               are in the logs).
    Changed:   Stage 6's deliverable exists. CLAUDE.md's "no .NET SDK on this
               machine" is stale: dotnet 9.0.317 is installed and builds the
               desktop app.
    Affects:   none (executes 11-fsc-implementation-plan.md as written)
    Evidence:  the pointer-forwarding branch in ../fscopilot-pointer; the
               verification harness output is reproducible from
               scratchpad (codec/PanelServer/recordings checks)

---

## 2026-09-01 - The production design for FS Copilot is decided and recorded

    Question:  none directly - this is the graduation decision 05-integration
               deferred to "its own piece of work". Gives Q04 and Q10 their
               answer path.
    Stage:     after stage 5; feeds stage 6
    Expected:  Graduating would mean choosing between the WebSocket sidecar and
               widening the bus, and porting agent.js with "about twenty lines
               of plumbing".
    Found:     The full within-FS-Copilot design was worked through against the
               real fscopilot source and every open choice was decided. Recorded
               in docs/11-fsc-implementation-plan.md; the decisions in brief:

               - Transport panel<->app: the WebSocket sidecar, per Q08. Ports
                 9020-9024, app binds first free, channel.js rotates the range
                 in its backoff. Bus untouched, bus-widening kept as the
                 documented fallback.
               - Peer wire: two new LiteNetLib packets, PointerPress and
                 PointerDrag (batched full-path drags), registered after
                 Surfaces. Session + Seq fields from day one because
                 Codecs.Schema hard-rejects any later wire change.
               - Profiles: top-level `pointer:` key, opt-in, FULL keys
                 (identifier|query, per the A220 CTP/MKP/FCP finding). Pointer
                 instruments excluded from the Interact path both directions.
               - Robustness: two-mode send history (small ring while live, full
                 accumulation from the moment the session degrades, bounded by
                 the 5-min session-end timeout) replayed on reconnect, deduped
                 by Session/Seq. Invariant: an outage ending within the timeout
                 loses nothing.
               - Locking: overlay DIVs, never event suppression (the p06
                 lesson). Blue blocking lock on the slave while degraded and on
                 both during "connecting"; red NON-blocking warning when a
                 configured panel loses the app link - blocking requires a live
                 app renewing the lock, so red never blocks. window.fscUnlock()
                 fixed global; renewal deadman ~8s.
               - Delivery: one PR against the src submodule, clean commits,
                 including two standalone fixes found along the way (the
                 VCockpit.js pending-array bug; the inbound Interact ignore
                 filter + ignore: WasmInstrument as the latch-press safety fix).

               Two facts verified in source while planning, worth keeping:
               Coordinator.cs:47-51 applies `ignore:` only to OUTBOUND Interact
               (inbound replays everything), and Definitions.cs:24 has
               IgnoreUnmatchedProperties() commented out, so any new top-level
               profile key bricks profile loading on older builds. Re-enabling
               it was ruled out of scope; no served profile carries `pointer:`
               until the release is adopted.

               Also settled honestly rather than around: concurrent conflicting
               inputs on the same panel can diverge the peers and no
               input-replay design can fix that without rollback. Recovery is
               documented instead - panel chrome sits at fixed coordinates, so
               both pilots pressing the same page button converges a diverged
               panel.
    Changed:   Stage 6 stops being "package the prototype for a tester" and
               becomes "the FS Copilot PR ships the instrumentation" - rect in
               the hello answers Q04, Session/Seq counters plus a scripted
               disconnect answer Q10, both from one remote-tester session.
    Affects:   04-transport (adopted, pointered), 05-integration (routing key,
               config delivery and graduation superseded, pointered),
               06-open-questions (Q04, Q10 answer path, updated in place),
               index.md (11 added, stale fresh-pickup note corrected)
    Evidence:  none - a design decision, not a probe. The plan itself is
               docs/11-fsc-implementation-plan.md.

---

## 2026-08-30 - The drag drift candidate is real but small, and temporal not spatial

    Question:  bounds Q10 without settling it
    Stage:     gestures, after stage 5
    Expected:  The entry below flagged replay time-compression as a candidate
               cause of drag drift, unverified, and expected it to need cockpit
               time to check.
    Found:     It needed no cockpit time at all. The nine drags captured during
               the drag session were already sitting in `recordings/`, so the
               exact timing loop from `replayDrag` could be replayed over real
               data offline - `npm run drift`, `probes/p10-drag-drift.mjs`.

               **Replay runs 0-12% short.** Worst case 186ms lost on a 1534ms
               gesture; seven of nine lose under 4%; two lose nothing. Almost
               every gesture contains exactly one gap over the 250ms clamp, and
               from its position it is the pilot pausing after mousedown before
               starting to move - the natural hesitation at the start of a pan,
               not a mid-gesture stall.

               Two readings follow. The effect is **too small to explain a
               visible drift on its own**, and it is **temporal, not spatial**:
               the path coordinates are replayed verbatim, so a replayed drag
               ends exactly where the captured one did. It can only become
               spatial in a receiver that does velocity, easing or inertia work
               on the path.
    Changed:   Q10 goes from speculation to a bounded measurement, and the
               decision to defer it is better supported than when it was taken:
               the surviving-to-two-machines candidate is now known to be minor,
               which leaves single-machine replay as the main suspect, which is
               exactly the thing that stops mattering at stage 6.

               Worth noting as method rather than result: this was answered for
               free because the transport records everything it carries. Keeping
               `recordings/` committed turned a question that looked like it
               needed a sim session into one command.
    Affects:   06-open-questions (Q10, updated in place)
    Evidence:  results/p10-drag-drift-2026-08-30.txt

---

## 2026-08-30 - Drag works. Tier 3 is real, with a drift caveat

    Question:  settles the mousemove-under-held-button question that the
               withdrawn entry raised. Closes tier 3 of 02-approach.
    Stage:     gestures, after stage 5
    Expected:  Genuinely unknown. The failure mode that would have killed the
               feature - MSFS not delivering mousemove while a button is held -
               was still live going in, and p09 existed to separate it from a
               threshold fault.
    Found:     **Drags are captured and replayed in the cockpit.** Confirmed by
               hand against agent v5 through the real module and host.

               So MSFS does deliver mousemove to a panel document while a button
               is held, and the v5 thresholds (DRAG_MIN_PX 4, DRAG_SAMPLE_MS 33,
               DRAG_MIN_STEP_PX 2) classify real gestures correctly. p09 was not
               needed and was not run; it stays in the tree because it is the
               instrument that separates delivery faults from threshold faults if
               drag ever regresses.

               **Replayed drags sometimes drift** from the original gesture.
               Judged not worth chasing now - see below - and recorded as Q10.
    Changed:   Tier 3 of 02-approach is built, tested and working. The gesture
               work that has been in flight since the drag implementation landed
               is done, and there is no in-flight item left.

               Drift is accepted as a known limitation rather than investigated,
               on the reading that single-machine replay is the most likely
               source and that the case which matters is two machines, which
               cannot be tested yet. Revisit if it shows up in real use.

               One candidate cause is visible in the code and is worth writing
               down before it is forgotten, because it is not the
               single-machine explanation and would not go away on two:
               `replayDrag` reconstructs timing by summing per-step intervals
               clamped to DRAG_MAX_STEP_MS (250ms), so total replay duration is
               **less than** the captured duration whenever any gap between
               samples exceeded 250ms. Those gaps are not rare: DRAG_MIN_STEP_PX
               gates out sub-2px motion, so a slow deliberate pan emits sparse
               samples and can easily exceed 250ms between them. The replay then
               runs faster than the original gesture, and any receiver doing
               velocity, easing or inertia work on the path would land somewhere
               else. Unverified, and deliberately not acted on.
    Affects:   02-approach (tier 3), 06-open-questions (Q01 strengthened, Q10 new)
    Evidence:  confirmed by hand in the cockpit; no raw capture saved

---

## 2026-08-30 - The drag negative result was void: v5 was never installed

    Question:  retracts the previous entry's finding. Drag is untested, not
               broken.
    Stage:     gestures, after stage 5
    Expected:  That the cause of "drags are not detected" was one of the four
               hypotheses listed in the entry below, with (1) - MSFS not
               delivering mousemove while a button is held - being the one that
               would kill the feature.
    Found:     It was hypothesis 2, in a stronger form than that entry guessed.
               The entry supposed the panel might still be running agent v4
               because a reload had not happened. In fact **v4 was all there was
               to run**: `npm run module:build` was never executed after agent.js
               was written, so the Community package on disk still contained
               `var VERSION = 4`. No panel reload could have helped.

               Two independent confirmations, taken before the sim was closed:

               - The live panel reported `{version: 4}` via
                 `npm run eval -- 23 "window.FSCPP.version"`.
               - `diff` of the installed
                 `Community/fscpp-bridge/html_ui/FSCPP/agent.js` against
                 `module/PackageSources/html_ui/FSCPP/agent.js` showed the entire
                 v5 delta missing - `onMove`, `replayDrag`, the `DRAG_*`
                 constants, the `replaying` counter, and the
                 `listen(document, "mousemove", onMove)` line.

               So the cockpit test exercised a build with no drag capture in it
               at all. It could not have produced a drag message under any
               circumstances, and it says nothing whatever about whether MSFS
               delivers mousemove under a held button.
    Changed:   **The previous entry's finding is withdrawn.** Drag is unverified,
               which is a weaker and better position than "probably broken".
               Q-drag stays open and untouched.

               v5 is now installed and verified byte-identical to source; the
               build was clean, `module:check` reported no `fscopilot-bridge`
               conflict, and `derive-vcockpit` reproduced its output exactly
               (empty `git diff`), so nothing else moved with it. MSFS needs a
               restart before it is picked up, because layout.json is read at
               startup.

               The lesson is the same shape as the console-dedup one two entries
               down, and that is twice now: **a defect in the scaffolding
               presented as a defect in the mechanism.** Both times a round of
               reasoning was spent on the mechanism while the harness was the
               thing at fault. The cheap guard is to make the build state
               falsifiable before drawing any conclusion from a cockpit test -
               `window.FSCPP.version` is one eval and it would have ended the
               previous session in seconds rather than leaving a wrong finding
               in the log overnight.
    Affects:   none - it withdraws a log entry, not a design doc
    Evidence:  none saved; both checks are one-liners reproduced above

---

## 2026-08-30 - Drag is implemented and does NOT work; diagnosis not started

> **Withdrawn by the entry above.** The panel was running agent v4 because v5 was
> never installed, so this test could not have detected a drag. The four
> hypotheses below are superseded; only the diagnostic they describe is still
> worth keeping, and it now exists as `probes/p09-drag-delivery.js`.

    Question:  none yet - this is where the next session starts
    Stage:     gestures, after stage 5
    Expected:  Drag capture and replay were added to the agent (v5) on the
               assumption that MSFS delivers mousemove while a button is held.
               That assumption was never tested.
    Found:     **Drags are not detected.** Reported from the cockpit: dragging on
               the panel produces no drag messages at the host.

               Nothing further was established. The session ended before the
               diagnostic ran, so the cause is one of these and it is not yet
               known which:

               1. **MSFS does not deliver mousemove while a button is down.** This
                  is the one that would kill the feature outright. There is weak
                  evidence against it - an A350 WASM capture showed
                  MOUSE_MOVE(238,86) between a MOUSE_DOWN and its MOUSE_UP - but
                  that was a different panel and a different delivery path, so it
                  does not settle the DOM case.
               2. **The panel was still running agent v4.** Editing files under
                  FSCPP/ needs a panel reload to take effect and it is not certain
                  one happened. Check `window.FSCPP.version` first; if it says 4,
                  nothing else matters.
               3. **The thresholds are wrong.** DRAG_MIN_PX 4, DRAG_SAMPLE_MS 33,
                  DRAG_MIN_STEP_PX 2. A gesture that travelled but produced no
                  sample between down and up is classified as a press by design,
                  and `d.path.length > 1` is required for a drag.
               4. **Classification is buggy.** Reading it back, `onMove` and the
                  press/drag branch in `onUp` looked right, but that was eyes, not
                  a test.

               The separating question is whether the host prints `press` lines
               when a drag is made, or nothing at all: `press` means captured but
               misclassified, nothing means not captured.
    Changed:   Nothing yet. Drag should be treated as unverified and probably
               broken until this is run.

               The diagnostic is ready and needs no code: paste a raw
               mousedown/mousemove/mouseup logger into the Coherent GT console on
               the DisplayUnits panel, drag slowly, and read off the move count
               and total travel. If moves are zero, the feature is dead and
               02-approach's tier 3 needs a pointer line saying so.
    Affects:   none yet
    Evidence:  none - this is the gap

---

## 2026-08-30 - The module works with DevMode off

    Question:  closes the caveat on Q08
    Stage:     6 prerequisite
    Expected:  Uncertain, and it gated everything downstream. Every result all
               session had been obtained with the inspector enabled. If a coui://
               document could only reach loopback in DevMode, a remote tester
               would have to enable it too, and shipping into FS Copilot would
               need the CommBus/WASM/SimConnect pipeline back - the exact pipeline
               this design had just deleted.
    Found:     It works. DevMode off, panels connect to the host and interaction
               flows normally.
    Changed:   **The WebSocket transport is shippable, not merely convenient.**

               For a remote tester this is the difference between "install a
               package and run a program" and "enable developer mode first". They
               need the Community package and the host, and nothing else.

               For FS Copilot it means 04-transport's "bypass the bus" option is a
               real option rather than a thought experiment. The 512-byte cap, the
               single-slot client-data area, the broadcast every panel parses and
               the schema handshake are all avoidable, and the C# side costs no new
               dependency - HttpListener and AcceptWebSocketAsync are BCL, which
               the ~120-line dependency-free server in host/ws.mjs demonstrates in
               miniature.

               Stage 6 is now blocked only on there being no second machine.
    Affects:   04-transport, 10-module
    Evidence:  tested in the cockpit with DevMode disabled

---

## 2026-08-30 - Capture does not drop presses; the earlier loss was console dedup

    Question:  closes the open question from the console-dedup entry
    Stage:     5
    Expected:  Unclear. Nine-ish deliberate actions had produced five recorded
               events, and consecutive-message dedup did not obviously account for
               all of the shortfall. The possibility that capture itself was
               losing presses was left open, because capture is the part that
               ships and a defect there would matter more than anything else
               outstanding.
    Found:     Capture is clean. Counted deliberately through the real module:
               **33 captured, 33 replayed.** Including deliberate spam-clicking,
               which is the case most likely to expose a dropped press.

               So the earlier shortfall was entirely the console channel
               collapsing messages identical to their predecessor. It was a
               property of the debugger-as-transport and it left with it.
    Changed:   Stage 5 is done. Nothing further is owed on capture fidelity.

               Worth keeping as a general lesson rather than a footnote: the
               defect was in the scaffolding, presented as a defect in the
               mechanism, and cost a round of investigation aimed at the wrong
               layer. The tell was available and ignored - a channel that has a
               `repeatCount` field is a channel that deduplicates.
    Affects:   none
    Evidence:  counted in the cockpit, 33/33

---

## 2026-08-30 - The prototype module works end to end in the simulator

    Question:  none - a build milestone, and the end of stage 4
    Stage:     4
    Expected:  Several things had to hold at once and any of them could have
               failed quietly: the derived VCockpit.js had to be a valid stock
               file, the override had to win, Include.addImports had to resolve
               three of our scripts, the agent had to find its instrument, and the
               WebSocket had to reach loopback from a coui:// document in a normal
               panel rather than one being driven by the inspector.
    Found:     All of it works. Reported by the operator as working perfectly:
               panels connect, presses appear live in the host, recording and
               replay both function.

               So the full production-shaped chain is real - a Community package
               overriding a core simulator file, loading our scripts into every
               panel, capturing cockpit input, and carrying it to a local process
               over a WebSocket. No WASM module, no SimConnect, no CommBus, no C#.
    Changed:   Stage 4 is done, and by a different route than planned: the plan
               said a drop-in for fscopilot-bridge/html_ui, and what exists is our
               own package with its own transport. Documented in 10-module.

               Note what this does NOT yet settle. The open question from the
               console-dedup entry - whether capture itself was also losing
               presses, since nine actions produced five events and dedup does not
               obviously account for all of it - is now *answerable* rather than
               answered. The host prints every capture live and `s` reports its
               count against the agent's own counter, so a disagreement is
               transport and a matching-but-low pair is capture. Worth confirming
               deliberately on the next session rather than assuming the new
               transport fixed it.
    Affects:   08-testbed (superseded as the way experiments are run), and adds
               10-module
    Evidence:  reported from the cockpit; the package is at
               Community/fscpp-bridge

---

## 2026-08-30 - Q08 yes: a cockpit document can hold a WebSocket to loopback

    Question:  Q08 ANSWERED YES
    Stage:     3
    Expected:  Plausible but unproven. The Fenix EFB reaches localhost, but it is
               an iframe with an http:// origin of its own, so it said nothing
               about what a coui:// document may do.
    Found:     All three work from `coui://html_UI/Pages/VCockpit/Core/VCockpit.html`:

                 XHR    ok   status=200
                 fetch  ok   status=200
                 WS     OPEN, full duplex

               Confirmed from the server side independently: an UPGRADED line,
               the panel's message received, and the echo delivered back to the
               panel. Server is ~60 lines of RFC 6455 on node:net and node:crypto,
               no dependencies - which also demonstrates that hosting one costs no
               new package in a trimmed single-file build.
    Changed:   **The prototype needs no WASM module, no SimConnect, no CommBus and
               no C#.** A panel document can talk directly to a local process.

               That removes the entire pipeline behind the constraints in
               04-transport: the 512-byte str_msg, the single-slot client-data
               area, the broadcast that every panel has to parse, and the schema
               handshake that makes any wire change a hard compatibility break.
               It also removes the console channel's dedup defect, since that
               belonged to the debugger-as-transport.

               For FS Copilot itself this stays a proposal rather than a decision -
               a shipped build must work with DevMode off, and the WebSocket path
               has only been shown to work with the inspector running. Whether a
               coui:// document can reach loopback in a normal session is a
               separate question and should be checked before anyone plans on it.
    Affects:   04-transport, 08-testbed
    Evidence:  results/p05-websocket-vcockpit02-displayunits-*.txt

---

## 2026-08-30 - The console channel silently drops repeated messages

    Question:  none - a defect in the testbed's own transport, found by a user
               noticing that a recording was short
    Stage:     4
    Expected:  That Console.messageAdded either delivers or visibly fails.
    Found:     A recording of roughly nine deliberate cockpit actions contained
               five events. Measured directly:

                 40 unique messages sent  -> 40 received.  No loss under load.
                 20 identical messages    ->  1 received.
                 A, B, A                  ->  3 received.  Dedup is consecutive-only.
                 C, wait 400ms, C         ->  1 received.  But it spans any gap.

               Coherent's console collapses a message identical to the one
               immediately before it and increments `repeatCount` instead of
               emitting a second event. Two presses on the same control with the
               same rounded hold produce byte-identical JSON, so the second one
               vanishes with no error anywhere.

               This is a property of the console channel, not of capture and not
               of the mechanism. The 40-unique test rules out throughput.
    Changed:   Fixed for the testbed by prefixing each message with a monotonic
               sequence number, which also makes any future gap visible instead
               of silent - the recorder now prints "GAP, expected #n".

               The larger consequence is architectural. This class of defect
               belongs to the debugger-as-transport, and debugging our own
               scaffolding is not what the project is for. The prototype moves to
               an in-simulator module with a real transport; the inspector stays,
               but for inspecting rather than for carrying data.

               Note what was NOT established: whether capture also missed events.
               Nine-ish actions to five recorded is more loss than dedup alone
               obviously accounts for, and the reconciliation that would have
               separated the two - the agent's own `captured` counter against
               lines received - was not in place. It is now, and it is worth
               re-checking once the real transport is in, because if capture is
               also dropping presses that is a defect in the part that ships.
    Affects:   08-testbed (the rough edge it listed as unmeasured is now measured
               and is worse than a rate limit)
    Evidence:  the burst measurements above; recordings/fpln-session-*.ndjson

---

## 2026-08-30 - Q03 PASSES: a synthetic mouse click drives a React/SVG display

    Question:  Q03 ANSWERED YES. Q01 and Q02 answered with it.
    Stage:     1 - the load-bearing stage
    Expected:  Genuinely uncertain. The WASM surface had just closed, and while
               the mechanisms are different there was no evidence either way for
               a React display doing its own hit-testing inside the panel
               document.
    Found:     It works.

               On the A220 DisplayUnits, with a real click first captured at
               (3229,191) - the FPLN/PERF dropdown, target `rect` in the SVG
               namespace - a synthetic sequence of mousedown, mouseup and click
               carrying those coordinates **opened the dropdown**. Confirmed two
               ways: a MutationObserver A/B showed {characterData:2} in the idle
               baseline against {childList:2} in the 900ms after the click, and
               the operator confirmed the dropdown visibly open.

               The target was an SVG element - precisely the class the existing
               scheme cannot name, and the reason the A220 does not sync today.

               **Q01: MSFS delivers mouse events only.** Captured over a real
               interaction: mousedown x4, mouseup x6, mousemove x754, click x4,
               mouseover/out/enter/leave, and keyboard events. Never seen:
               pointerdown, pointerup, pointermove.

               The reason is stronger than "not delivered" - **window.PointerEvent
               does not exist in Coherent GT at all**. The constructor is absent.
               So the A220's own onPointerDown handlers are dead code in the
               simulator, and its buttons are driven by onClick.

               **Q02: isTrusted is a usable discriminator.** 571 of 627 captured
               events were trusted. The untrusted 56 are exactly the
               mouseenter/mouseleave pairs reported at (0,0) - which is
               VCockpit.js dispatching new MouseEvent("mouseenter") with no
               coordinates from its own Coherent.on("OnMouseEnter") handler.
               Real cockpit input is trusted and carries real coordinates.

               Geometry, for the record: the DisplayUnits panel is ONE document
               7410x1110 containing five display units of 1480x1110 side by side
               at x = 0, 1481, 2963, 4447, 5930. Only the mount and five
               zero-height wrapper divs are HTMLElements; everything visual is
               SVG. So a coordinate identifies both the DU and the control,
               which the existing scheme cannot do at either level.
    Changed:   **The approach is viable.** Three rows of the 03-scope table -
               React over HTML DOM, React over SVG, canvas - move from "expected
               to work" to demonstrated on the middle one, and the mechanism that
               makes it work is shared by all three.

               **02-approach needs correcting on one point.** It specifies
               dispatching real PointerEvents and calls that a requirement. It is
               not merely unnecessary, it is impossible - the constructor does not
               exist in this engine. The replay sequence is mousedown, mouseup,
               click, with MouseEvent only. The hold and trail machinery in p07 is
               not needed on this surface either; a bare three-event sequence at
               the right coordinate was sufficient.

               Note the contrast with the WASM surface, which is the useful
               generalisation: where the panel document does its own hit-testing,
               coordinates work. Where the simulator sits between the event and
               the hit-test, they are discarded. The dividing line is not the
               rendering technology, it is whether the sim is in the path.
    Affects:   02-approach (PointerEvent), 03-scope (three rows), 01-problem
    Evidence:  results/p02-capture-vcockpit02-displayunits-*.txt and the A/B
               mutation measurement above; dropdown state confirmed visually.

---

## 2026-08-30 - A220 panel identifiers collide, and MKP is not interactive

    Question:  none - two findings for integration, from the A220 page list
    Stage:     1
    Expected:  That instrumentIdentifier distinguishes panels well enough to
               route an interaction, as 05-integration assumes.
    Found:     It does not, on this aircraft. Both CTPs report `CTP`, both MKPs
               report `MKP`, and all four FCPs report `FCP` - the identifier is
               the templateID, and the side is carried only in the URL query
               (?side=left / ?side=right), which never reaches it. An interaction
               routed by identifier alone would replay on **every** panel sharing
               it.

               The EFBs are the counter-example and show the fix is the addon's:
               they load with ?Index=1 / ?Index=2 and report efbA220_1 and
               efbA220_2 correctly.

               Separately: the MKP declares isInteractive = false, so FS
               Copilot's hook skips DOM sync for it entirely at gate G4,
               regardless of anything else about it.

               Also worth recording as scope: the A220's CTP, MKP, FCP and ISI
               are driven by physical bezel buttons rather than by clicking the
               display. Their interactions are model-behaviour events and are
               already covered by FS Copilot's definitions mechanism. The
               DisplayUnits and the EFBs are the only pointer-scope surfaces on
               this aircraft.
    Changed:   05-integration's routing key needs more than instrumentIdentifier
               where an aircraft reuses it. The URL is available on the element as
               the `url` attribute and already distinguishes these panels, so the
               key wants to be identifier plus a discriminator derived from the
               URL query rather than the identifier alone.
    Affects:   05-integration
    Evidence:  page listing and p04 output for pages 69-79

---

## 2026-08-30 — The cursor cannot be synced either: no position variable, and the screen var is an output

    Question:  Q05 — confirms the closure from a second direction
    Stage:     2
    Expected:  A promising escape: rather than inject hover, sync the cursor that
               owns it. The A350 has two cursors (captain and first officer), so
               each machine plausibly drives one, and a synced cursor would make
               a synced click land correctly by construction.
    Found:     Three separate results, all negative.

               **Injected moves do not move the drawn cursor.** 61 WASM_MOUSE_MOVE
               calls were driven to the bottom-right corner of the MFD and all
               were confirmed delivered through Coherent.call. The cursor is
               drawn on that display and did not move at all. So the simulator
               accepts the call and discards the coordinates — this is not the
               module ignoring them, it is the sim never passing them on. That
               completes the mechanism: of the WASM_MOUSE_* channel, the press
               half is honoured and the position half is inert.

               **There is no cursor position variable.** inibuilds-A350.wasm ships
               unstripped, so its symbols are readable with `strings`. They
               describe the architecture plainly — DrawCursorCPT(NVGcontext*,
               float, float, bool), DrawCursorFO(...), SwitchCaptainCursor(int),
               SwitchFOCursor(int), CursorLock/Unlock/Hint. Of 1697 INI_
               variables in the binary, exactly two concern the cursor:
               INI_CPT_CURSOR_SCREEN and INI_FO_CURSOR_SCREEN. Which screen, and
               nothing else. No X, no Y.

               **The screen variable is an output.** Both read 2. Writing 1 to
               INI_CPT_CURSOR_SCREEN reads straight back as 2 — the module
               re-asserts it.

               The aircraft's behaviour XML has no KCCU input events either; its
               <Cursor>Hand</Cursor> entries are MSFS mouse-pointer shape hints
               and unrelated.
    Changed:   Closes the last avenue for this surface. Written up in full as
               [07-wasm-surface](../07-wasm-surface.md), including the complete
               WASM_MOUSE_* channel table, so none of it is re-derived.

               Notable for generalisation: WasmSimCanvas.js is a **core sim file**
               and the coordinates are discarded on the sim's side of
               Coherent.call, so the finding belongs to WasmInstrument as a
               mechanism, not to iniBuilds. The cursor specifics are vendor
               detail; the input path is shared.

               Also notable as a feature request rather than a workaround:
               WASM_MOUSE_DOWN already carries clientX and clientY, already
               correct. If Asobo honoured them, every WASM display would become
               syncable with no change on the JavaScript side at all.
    Affects:   03-scope, and adds 07-wasm-surface
    Evidence:  results/a350-ini-vars.txt (the extracted variable table);
               cursor movement observed from the cockpit

---

## 2026-08-30 — Same-tick injection loses too: the sim owns hover absolutely

    Question:  Q05 — CLOSED, negative. WASM gauges cannot be driven by injecting
               DOM mouse events.
    Stage:     2
    Expected:  The last surviving hypothesis: that the sim re-raycasts the cockpit
               every frame and overwrites the module's hover, so an injected move
               survives ~16ms. If so, dispatching the move and the press in one JS
               tick with no yield should land before the overwrite.
    Found:     It does not. With the real cursor held on IRS at (240,683), a
               same-tick sequence — four moves converging on NAVAIDS at (207,803),
               then mousedown, mouseup and click, all synchronous, `hover:0`
               `hold:0` — produced exactly the forwarded calls we expect:

                 MOUSE_OVER(67,693)  MOUSE_ENTER()  MOUSE_MOVE(102,721)
                 MOUSE_MOVE(137,748) MOUSE_MOVE(172,776) MOUSE_MOVE(207,803)
                 MOUSE_DOWN(207,803) MOUSE_UP(207,803) CLICK(207,803)

               **IRS opened.** 120 pixels from where every one of those events
               said to press.

               So it is not a race and not a timing window. WASM_MOUSE_MOVE does
               not write the module's hover under any timing, and the coordinates
               on WASM_MOUSE_DOWN are not consulted. Hover is established
               exclusively by the simulator's own cockpit raycast against the
               physical cursor, and the Coherent WASM_MOUSE_* channel only says
               "a button went down" against whatever that raycast currently says.
    Changed:   **Q05 closed. The WASM row of 03-scope is unreachable, permanently,
               by any DOM-level scheme.** A350, A400M, PMDG and every other
               WasmInstrument display are out of range for pointer forwarding.

               This is not a partial or a "needs more work" result. Three
               independent attempts — bare click, realistic trail with a 400ms
               dwell, same-tick with no dwell — all pressed the local pilot's
               hover instead of the target. The mechanism is understood and it
               excludes us.

               What is NOT excluded, and is now the only avenue for this surface:
               something that writes the sim's own cursor or hover state. All
               known candidates (`ShowVirtualMouse`, `Coherent.on("OnMouseEnter",
               _target,_x,_y)`, the Raycast channel) are sim-to-JS. Whether any
               accepts traffic in the other direction is unanswered, and it is a
               different question from this one — it belongs to the sim's input
               system, not to the DOM.

               Practical consequence for FS Copilot today, worth carrying back:
               its existing replay on a WasmInstrument panel presses whatever the
               *receiving* pilot is hovering. Adding `ignore: WasmInstrument` to
               affected profiles is a real safety improvement independent of this
               project, and PMDG's profile already does it.
    Affects:   03-scope (WASM row -> unreachable), 02-approach (coordinate-selects-
               target is false on this surface), 01-problem (the "nothing behind
               the name" mechanism is wrong in both directions)
    Evidence:  results/p07-wasm-hover-vcockpit06-wasminstrument-*.txt; the
               distinguishing observation is which page opened, reported from the
               cockpit.

---

## 2026-08-30 — A probe that wraps a sim API disabled the aircraft, and could not be undone

    Question:  none — an operational lesson
    Stage:     2
    Expected:  That leaving the Coherent.call wrapper installed between tests was
               harmless, since it passes calls through by default.
    Found:     It was not harmless. p06's report path left `suppress` true as its
               resting state, so every subsequent **real** cockpit click on that
               gauge was swallowed. The operator lost the ability to click the
               MFD entirely, with hover still working — because hover comes from
               the sim's raycast and never passes through the wrapper. From the
               cockpit this is indistinguishable from a broken aircraft.

               Recovery was worse. The first version of p06 held the native
               function in a closure and exposed it only as `window.__P06.restore`.
               A reset had already deleted `window.__P06`. On that page the native
               Coherent.call was unreachable from any handle, and the only way
               back was `location.reload()` on the panel document.
    Changed:   Three rules, now implemented in p06 and shared by p07:

               1. **Suppression is never the resting state.** It is set for the
                  duration of a dispatch and cleared unconditionally afterwards.
               2. **Restore lives at a fixed global**, `window.__FSCPP_RESTORE()`,
                  which does not depend on holding any probe object.
               3. **A deadman restores after 120s idle**, refreshed on each use,
                  so a forgotten wrapper un-installs itself.

               General form, worth applying to anything this project injects: code
               that sits in the path of the pilot's own input must fail open, must
               be removable without the handle that installed it, and must expire
               on its own. The sim has no undo and the pilot gets no error message.
    Affects:   none
    Evidence:  none — reported from the cockpit

---

## 2026-08-30 — A WASM gauge presses whatever the REAL cursor hovers, ignoring the coordinates we send

    Question:  Q05 — the optimism below is wrong. Coordinate injection does not
               drive a WASM gauge.
    Stage:     2
    Expected:  That WASM_MOUSE_MOVE at (x,y) would set the module's hover, and a
               following WASM_MOUSE_DOWN would press whatever is there — the
               mechanism a real mouse appears to use.
    Found:     Observed directly, and the observation is unambiguous.

               Immediately before a replay, the real cursor was parked hovering
               the **IRS** button, without clicking. The replay sent a full move
               trail converging on (238,87) — a different control — a 400ms
               dwell, then down/up/click. **The IRS page opened.** Not the
               control at (238,87). The one the real cursor was over.

               The second replay, at (269,69), did nothing at all: after the page
               changed, the real cursor was over empty space, so there was no
               hover for the press to land on. Our move trail did not create one.

               Then the decisive part: the injected click had not been discarded.
               When the real cursor was moved onto the **RETURN** button, that
               button fired immediately, with no new click from us. The press we
               injected was latched, waiting for a hover to resolve against.

               So the module maintains one "currently hovered control", it is
               driven by the simulator's own cockpit raycast against the real
               cursor, and WASM_MOUSE_MOVE does not write to it. WASM_MOUSE_DOWN
               and WASM_CLICK are honoured — they mean "press the current hover"
               — but the coordinates they carry are ignored.
    Changed:   **The WASM row of 03-scope goes back to unreachable, for a new and
               much more specific reason.** It is not that events fail to arrive;
               they arrive and are acted on. It is that the coordinate carried by
               the event is not what selects the control, and the thing that does
               select it is owned by the sim's input system, keyed to a physical
               cursor that on the peer's machine is somewhere else entirely.

               Worse than a no-op: injecting a click into a WASM gauge presses
               whatever the local pilot happens to be hovering. A sync built this
               way would fire the wrong control on the receiving machine, which is
               strictly more harmful than doing nothing. The latching behaviour
               makes it worse again — a press can sit pending and fire later, on
               whatever the pilot's cursor next touches.

               Note this also revises the (0,0) reading in the entry below. FS
               Copilot's coordinate-less replay is not landing at the corner of
               the screen; it is pressing whatever the receiving pilot is hovering
               at that moment. The bug is worse than described, not milder.

               What remains open is whether any channel writes hover. WASM_MOUSE_MOVE
               does not. Candidates worth enumerating before declaring this dead:
               the virtual-mouse path (VCockpit.js `ShowVirtualMouse`,
               `Coherent.on("OnMouseEnter", _target, _x, _y)`), and whatever
               drives the cockpit raycast. All of them are sim-to-JS today; the
               question is whether any accepts traffic in the other direction.

               One long shot untested: every injected move here was followed by a
               dwell of 250-400ms. If the sim re-raycasts and rewrites hover every
               frame, an injected move would be overwritten within ~16ms. A
               same-tick move-then-down, with no dwell, is cheap to try before
               giving up.
    Affects:   03-scope (WASM row), 02-approach (the replay sequence assumes the
               coordinate selects the target, which is false on this surface)
    Evidence:  Observed in the cockpit by the operator; the injected call
               sequences are in results/p07-wasm-hover-*.txt. The distinguishing
               evidence is what the display did, which no probe can capture.

---

## 2026-08-30 — WASM gauges take DOM mouse events, and FS Copilot has been sending them to (0,0)

> **Superseded in its conclusion.** The transport finding here holds — the events
> do reach the module — but "coordinates arrived intact" describes the Coherent
> call, not what the module does with them. See "A WASM gauge presses whatever
> the REAL cursor hovers" above.

    Question:  Q05 — ANSWERED, best case. Also settles the mechanism for Q03 on
               the WASM surface.
    Stage:     2
    Expected:  Three outcomes were on the table (03-scope). The pessimistic one —
               that the sim routes cockpit input to WASM gauges natively and the
               DOM is only a display surface — looked most likely, because the
               instrument carries data-input-group="WASM-INSTRUMENT" and the leaf
               is a live-view <img>.
    Found:     The optimistic outcome, and then some. `coui://html_ui/JS/WasmSimCanvas.js`
               is a core sim file, 6778 bytes, and its connectedCallback binds
               click, dblclick, mousemove, mousedown, mouseup, mouseenter,
               mouseleave, mouseover and mousewheel **on the wasm-sim-canvas
               element itself**. Every handler forwards viewport coordinates
               straight through:

                 OnMouseDown(_e) {
                   Coherent.call("WASM_MOUSE_DOWN",
                     parseInt(this.m_wasmInstrumentGUid),
                     _e.clientX, _e.clientY, _e.button)
                 }

               So there are two routes into a WASM gauge, not one:
                 1. dispatch a MouseEvent with coordinates anywhere in the canvas
                    subtree — it bubbles to the canvas and is forwarded;
                 2. call Coherent.call("WASM_MOUSE_DOWN", guid, x, y, button)
                    directly, skipping the DOM entirely.

               P06 proves route 1 live, with Coherent.call wrapped so WASM_*
               calls were captured and suppressed — the synthetic events took the
               real path and the sim never heard them:

                 inject at (800, 642) on the live-view <img>
                   WASM_MOUSE_DOWN(105, 800, 642, 0)
                   WASM_MOUSE_UP(105, 800, 642, 0)
                   WASM_CLICK(105, 800, 642, 0)

               **And this explains the existing failure.** FS Copilot's replay
               builds `new MouseEvent(type, {bubbles, cancelable})` with no
               clientX/clientY, so both default to 0. Those events do reach
               WasmSimCanvas and do become WASM_MOUSE_DOWN — at (0,0). The A350
               has never been ignoring FS Copilot's clicks. It has been receiving
               every one of them, at the corner of the screen.

               Two details that constrain the design: WasmSimCanvas binds **only
               mouse events**, no pointer events, so PointerEvent is not required
               for this surface (it is still required for the A220's React
               handlers). And it binds `mousewheel`, the legacy name — knob
               scrolling on a WASM gauge is forwardable too.
    Changed:   The WASM row of the 03-scope table moves from **unknown** to
               **reachable**. A350, A400M and PMDG displays come into range. This
               is the "fixing five aircraft rather than two" branch.

               It also raises a cheaper question than the whole project: adding
               clientX/clientY to the existing replay might fix WASM gauges on its
               own, without pointer forwarding at all. Worth knowing, though it
               does nothing for the A220 — a coordinate on the mount div is still
               a coordinate on the mount div.
    Affects:   01-problem (the "nothing behind the name" mechanism is wrong —
               there IS something behind it), 03-scope (WASM row), 02-approach
               (route 2 is a better replay path for this surface)
    Evidence:  results/p01-wasm-shell-vcockpit17-wasminstrument-*.txt
               results/p06-wasm-inject-vcockpit17-wasminstrument-*.txt

---

## 2026-08-30 — Q00 is yes: the sim hosts a WebKit inspector and it evaluates anything

    Question:  Q00 — ANSWERED yes
    Stage:     0
    Expected:  Nothing, after the orphaned-socket fiasco below.
    Found:     Once port 19999 was freed, **MSFS bound it immediately and lazily**
               — no restart, no DevMode toggle. The stale socket had been holding
               the sim out of its own debugger port.

               What is behind it is a full WebKit Web Inspector backend:

                 GET /pagelist.json                     every inspectable document
                 ws://127.0.0.1:19999/devtools/page/N   inspector protocol for one

               `Runtime.evaluate` works and returns values:

                 -> {"id":1,"method":"Runtime.evaluate","params":{"expression":"1+1"}}
                 <- {"result":{"result":{"type":"number","value":2}},"id":1}

               Paths `/devtools/page/N` and `/N` both work; `/`, `/?page=N` and
               `/inspector/Main.html?page=N` accept the upgrade and then never
               answer, so the path does carry the page selection.

               The page list names every panel by document.title, which
               VCockpit.js sets to `VCockpitNN - <instrumentIdentifier>` — so
               panels can be selected by name rather than by a page id that
               changes between sessions.

               Incidentally visible in that list: `VCockpit02 - WasmInstrument -
               WasmInstrument`, a panel with two instruments in one document.
               That is the multi-gauge amplification case from the deep dive,
               present in a shipping aircraft.
    Changed:   **Every probe is now scriptable.** probes/lib/inspector.mjs drives
               the protocol; probes/run.mjs adds `npm run pages`, `npm run probe`
               and `npm run eval`. Console probes run unchanged — a shim buffers
               console.* so output written for a human console comes back over the
               wire.

               The only thing still needing a human is a *real* cockpit click,
               for the events only MSFS can generate.
    Affects:   none — this is about how the work is done, not what is being built
    Evidence:  results/p00-debugger-2026-08-30-10-27-58.txt

---

## 2026-08-30 — The port was squatted by a dead process's inherited socket

    Question:  Q00 — unblocked it
    Stage:     0
    Expected:  That closing the CoherentGT Debugger would free 19999.
    Found:     It did not. The holder was `TDSGTNXiFlightSimEXE` (4124) and
               `map-server` (27736) — both children of the dead pid 33168, both
               having outlived it, one of them holding the inherited listening
               socket. MSFS meanwhile was listening on 61657/62487/62488 and had
               never got 19999.

               Killing both freed the port, and MSFS bound it within seconds
               without a restart.
    Changed:   Worth remembering as a class of failure: an addon's external
               process outliving the sim and holding one of the sim's own ports.
               TDS GTN is a probe target for Q07, so it will need relaunching —
               a sim restart brings it back.
    Affects:   none
    Evidence:  none

---

## 2026-08-30 — The first Q00 run probed a dead socket

    Question:  Q00 — back to untested. The entry below it is void.
    Stage:     0
    Expected:  That the silent listener on 19999 was a live backend refusing to
               talk to us, and that the single-client hypothesis would explain it.
    Found:     Port 19999 is held by **pid 33168, which does not exist**. Checked
               with Get-Process (no such process) while netstat still reports
               `0.0.0.0:19999 LISTENING 33168` — an orphaned listening socket whose
               owner has gone, with the handle presumably inherited by a surviving
               child.

               It has been orphaned for the whole session. When the first run
               happened, `FlightSimulator2024` was pid 23444 and 19999 was already
               33168 — two different processes — and Get-Process already failed on
               33168 then. That was read as "elevated, name hidden". It was not.
               It was dead.

               So "accepts TCP, holds the socket open, answers nothing, to
               everything, for 12 seconds" has a much duller explanation than a
               framed protocol: the OS completes the handshake on behalf of a
               listening socket that nobody is reading from. No server was ever
               on the other end.

               Also running: `C:\MSFS 2024 SDK\Tools\CoherentGT Debugger\Debugger.exe`
               as pid 24348 — a live process, and a different one. It is the SDK
               tool that *connects to* the sim, not the endpoint.
    Changed:   **Q00 is untested, not "open with one hypothesis left".** Discard
               the entry below and its conclusions; the single-client theory was
               explaining an artefact.

               Worse, the orphan may be actively harmful: a stale bind on
               0.0.0.0:19999 can stop the simulator from opening the debugger port
               at all, which would explain why a later MSFS instance was running
               without owning it.

               Re-run sequence, in order: close Debugger.exe, confirm 19999 frees,
               start MSFS with DevMode, confirm MSFS itself now owns 19999, and
               only then run `npm run probe:00`.
    Affects:   none
    Evidence:  none beyond the process listings above — the earlier results file
               records a probe of nothing.

    Lesson worth keeping: check that a port's owner is alive before interpreting
    its silence. Ten seconds of Get-Process would have saved the whole first
    entry.

---

## 2026-08-30 — VOID: the debugger port accepts connections and says nothing

> **Void.** This probed an orphaned socket, not a server. See "The first Q00 run
> probed a dead socket" above. Kept because the reasoning it contains — that
> accept-then-silence is not how an HTTP server rejects a path — is sound, and
> was applied to the wrong facts.

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
