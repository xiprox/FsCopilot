# The one sim session still owed

    Purpose:    Three open questions need the simulator and nothing else. This is what to
                run, and what to look for, so one session answers all three.
    Depends on: 06-open-questions (Q04), 13-pr-prep (§16 R12)
    Decides:    nothing — it collects the evidence three decisions are waiting on

Every probe below is already in the code on `ahead-pointer-forwarding`. Nothing needs
building for the occasion beyond a Debug-logging build of that branch.

---

## Before starting

- App log level at **Debug**. All three probes log at Debug and nothing else is needed.
- The Coherent inspector is on **19999** and works with DevMode off (Q00).
- Q04 alone needs **two machines**. The other two are answerable solo.

---

## 1 · R12 — what Origin does Coherent actually send?

**Why it is open:** `PanelServer` accepts any WebSocket upgrade on loopback. The intended
next step is to reject an `Origin` starting with `http://` or `https://`, which would close
the hole where any local browser page can read the peer's gestures or inject its own. That
has not been done because rejecting on a guess could lock out the simulator itself.

**Run:** load any aircraft with a panel, so at least one panel connects.

**Look for:**

    [PanelServer] Panel connected, origin <X> (1 total)

**What each answer means:**

| `<X>` | Then |
| --- | --- |
| `coui://...` or `(none)` | Implement the reject. R12 closes. |
| `http://` or `https://` | Do **not** implement it as specified — Coherent and a browser tab are indistinguishable by Origin, and a different discriminator is needed. |

---

## 2 · Q04 — do two machines agree on the instrument rect?

**Why it is open:** coordinates are normalised to fractions of the instrument's bounding
rect. The one rect ever measured is `7410 x 1110`, exactly the `pixel_size` from the
aircraft's `panel.cfg` — which, if the rect is panel.cfg-derived, makes it identical on any
machine running the same addon build. That was assumed, never confirmed, and it has only
ever been measured on one machine.

**Run:** both pilots load the *same aircraft build*, with a `pointer:` instrument on screen.
Deliberately differ the graphics settings — display resolution above all — or the test
proves nothing.

**Look for, on each machine:**

    [PanelServer] Hello from <key> rect <W>x<H> (<url>)

**What each answer means:**

| Result | Then |
| --- | --- |
| Rects identical | Q04 closes affirmatively. Normalising stays as free insurance, and the PR message stays honest by not claiming the machines differ. |
| Rects differ | Normalising is load-bearing, not insurance. Say so in the PR — it becomes a much stronger justification than it currently has. |

---

## 3 · Does the multi-instrument slow path ever fire?

**Why it is open:** the first commit of the PR fixes a single pending-instrument slot that
drops every instrument but the last. The mechanism is real and reachable — the FS Copilot
script chain loads asynchronously, and instrument creation waits on a *different* queue —
but no capture has ever shown it happening, and the fix's justification currently rests on
structural reasoning alone.

**Run:** a **multi-instrument** panel document. Per `10-module.md` the A350 has one. Open
the inspector on that document and read the console from the top.

**Look for:**

    [Hook] References loaded; N instrument(s) waited.

**What each answer means:**

| `N` | Then |
| --- | --- |
| `0` or `1`, every time, across several loads and aircraft | The slot never lost anything. The fix is defensible only as insurance against the longer script chain this feature adds — consider dropping it from the PR, or say plainly that it is precautionary. |
| `2` or more, ever | The bug is real and was silently dropping instruments. Put the number in the commit message; it is the evidence that commit has been missing. |

Worth watching even on single-instrument panels: a consistent `1` shows the slow path itself
fires routinely, which makes the multi-instrument case a matter of when rather than whether.

---

## Afterwards

Promote the session's app logs into `results/` under a `q`-prefixed name, the way
`q11-quit-not-announced` and `q12-outage-no-recovery` were, and close Q04 and R12 in
[06-open-questions](06-open-questions.md) and [13-pr-prep](13-pr-prep.md) §16.
