/*
 * p09 — does MSFS deliver mousemove while a mouse button is held?
 *
 * This is the one question that can kill drag forwarding outright. Agent v5
 * samples a drag path from mousemove events received between mousedown and
 * mouseup; if the simulator delivers no moves while a button is down, there is no
 * path to sample and tier 3 of docs/02-approach.md is dead as designed.
 *
 * Panel: any DOM panel. The A220's DisplayUnits ("VCockpit02 - DisplayUnits") is
 * the one every other result in this project was taken on.
 *
 *   npm run probe -- 23 p09-drag-delivery.js
 *   ... then drag slowly on the panel in the cockpit, several times ...
 *   npm run eval -- 23 "__P09.report()"
 *
 * It listens only. It adds nothing to the input path, wraps nothing, and
 * dispatches nothing, so it can run alongside a live FSCPP agent without
 * interfering with it — and it deliberately does NOT filter out the agent's own
 * synthetic events, it labels them instead, so a replayed drag is visible here
 * too.
 *
 * Chrome 49. No optional chaining, no ??, no class fields.
 */

(function () {
  if (window.__P09 && window.__P09.stop) {
    try { window.__P09.stop() } catch (e) { /* keep going */ }
  }

  // The v5 constants, copied so the classifier can be replayed over real data
  // without installing v5. Keep in step with agent.js if those ever change.
  var DRAG_MIN_PX = 4
  var DRAG_SAMPLE_MS = 33
  var DRAG_MIN_STEP_PX = 2

  var MAX_GESTURES = 40
  var MAX_MOVES = 2000

  var gestures = []
  var current = null
  var freeMoves = 0        // moves delivered with no button down
  var listeners = []
  var startedAt = Date.now()

  function panel() {
    var p = document.getElementById("panel")
    return p && p.children.length ? p.children[0] : null
  }

  function onDown(ev) {
    current = {
      n: gestures.length + 1,
      at: Date.now(),
      x: ev.clientX, y: ev.clientY,
      button: ev.button,
      trusted: ev.isTrusted === true,
      self: ev.selfEmit === true,
      moves: [],
      movesWithButton: 0,
      movesWithoutButton: 0,
      travel: 0,
      up: null
    }
    if (gestures.length < MAX_GESTURES) gestures.push(current)
  }

  function onMove(ev) {
    if (!current) { freeMoves++; return }
    var g = current
    // `buttons` is the bitmask of what is held. A move delivered during a drag
    // but reporting buttons=0 is a different failure from no move at all, and the
    // two want telling apart.
    var held = typeof ev.buttons === "number" ? ev.buttons : -1
    if (held > 0) g.movesWithButton++
    else g.movesWithoutButton++

    var far = Math.abs(ev.clientX - g.x) + Math.abs(ev.clientY - g.y)
    if (far > g.travel) g.travel = far

    if (g.moves.length < MAX_MOVES) {
      g.moves.push({
        t: Date.now() - g.at, x: ev.clientX, y: ev.clientY, b: held,
        tr: ev.isTrusted === true
      })
    }
  }

  function onUp(ev) {
    if (!current) return
    current.up = { t: Date.now() - current.at, x: ev.clientX, y: ev.clientY }
    current = null
  }

  function listen(type, fn) {
    document.addEventListener(type, fn, true)
    listeners.push([type, fn])
  }

  listen("mousedown", onDown)
  listen("mousemove", onMove)
  listen("mouseup", onUp)

  /* Replay agent v5's classifier over a recorded gesture, so hypotheses 3 and 4
   * (thresholds wrong / classification buggy) are answerable from this data
   * rather than needing another cockpit session. */
  function classify(g) {
    var path = [[0, g.x, g.y]]
    var lastX = g.x, lastY = g.y, lastAt = 0
    var i
    for (i = 0; i < g.moves.length; i++) {
      var m = g.moves[i]
      if (m.t - lastAt < DRAG_SAMPLE_MS) continue
      if (Math.abs(m.x - lastX) + Math.abs(m.y - lastY) < DRAG_MIN_STEP_PX) continue
      lastX = m.x; lastY = m.y; lastAt = m.t
      path.push([m.t, m.x, m.y])
    }
    var isDrag = g.travel >= DRAG_MIN_PX && path.length > 1
    return { verdict: isDrag ? "drag" : "press", points: path.length, path: path }
  }

  function pad(s, n) {
    s = String(s)
    while (s.length < n) s += " "
    return s
  }

  function report() {
    var el = panel()
    var lines = []
    lines.push("")
    lines.push("[P09] drag delivery — is mousemove delivered while a button is held?")
    lines.push("panel   " + (el ? (el.instrumentIdentifier || el.tagName.toLowerCase()) : "(none found)"))
    lines.push("agent   " + (window.FSCPP ? "FSCPP v" + window.FSCPP.version : "not installed"))
    lines.push("window  " + Math.round((Date.now() - startedAt) / 1000) + "s")
    lines.push("moves with no button down: " + freeMoves +
      (freeMoves ? "   (so mousemove is delivered at all)" : "   (mousemove may not be delivered AT ALL)"))
    lines.push("")

    if (!gestures.length) {
      lines.push("NO GESTURES RECORDED — no mousedown reached this document.")
      lines.push("Either nothing was clicked on this panel, or the wrong panel is selected.")
      console.log(lines.join("\n"))
      return
    }

    lines.push(pad("#", 4) + pad("down", 13) + pad("up", 13) + pad("dur", 8) +
      pad("moves", 8) + pad("btn>0", 8) + pad("btn=0", 8) + pad("travel", 9) +
      pad("v5 says", 9) + "pts")
    lines.push(new Array(90).join("-"))

    var i, totalWith = 0, totalMoves = 0, drags = 0
    for (i = 0; i < gestures.length; i++) {
      var g = gestures[i]
      var c = classify(g)
      totalWith += g.movesWithButton
      totalMoves += g.moves.length
      if (c.verdict === "drag") drags++
      lines.push(
        pad(g.n, 4) +
        pad(g.x + "," + g.y, 13) +
        pad(g.up ? g.up.x + "," + g.up.y : "(no up)", 13) +
        pad((g.up ? g.up.t : 0) + "ms", 8) +
        pad(g.moves.length, 8) +
        pad(g.movesWithButton, 8) +
        pad(g.movesWithoutButton, 8) +
        pad(g.travel + "px", 9) +
        pad(c.verdict, 9) +
        c.points +
        (g.trusted ? "" : "   [synthetic]"))
    }

    lines.push("")
    lines.push("gestures " + gestures.length + "   would classify as drag: " + drags)
    lines.push("")
    lines.push("VERDICT")
    if (totalMoves === 0) {
      lines.push("  NO moves between any down and up.")
      lines.push("  MSFS does not deliver mousemove while a button is held in this document.")
      lines.push("  Drag forwarding as designed is DEAD — agent v5 can never build a path.")
      lines.push("  02-approach tier 3 needs a pointer line saying so.")
    } else if (totalWith === 0) {
      lines.push("  Moves ARE delivered between down and up, but every one reports buttons=0.")
      lines.push("  Path sampling still works (v5 does not check `buttons`), but nothing")
      lines.push("  downstream may rely on the bitmask — check the replay's buttons argument.")
    } else {
      lines.push("  Moves are delivered while the button is held: " + totalWith +
        " of " + totalMoves + " carried buttons>0.")
      lines.push("  The mechanism is viable. If drags still do not reach the host, the fault")
      lines.push("  is downstream of capture — check the `v5 says` column above: any row")
      lines.push("  reading `press` for a gesture you meant as a drag is a threshold problem.")
    }
    lines.push("")
    lines.push("__P09.dump(n) for one gesture's sampled path; __P09.raw for everything.")
    console.log(lines.join("\n"))
  }

  function dump(n) {
    var g = gestures[n - 1]
    if (!g) { console.log("[P09] no gesture " + n); return }
    var c = classify(g)
    console.log("[P09] gesture " + n + "  travel=" + g.travel + "px  moves=" + g.moves.length +
      "  v5=" + c.verdict + "  sampled points=" + c.points)
    var i
    for (i = 0; i < c.path.length; i++) {
      console.log("  " + pad(c.path[i][0] + "ms", 9) + c.path[i][1] + "," + c.path[i][2])
    }
  }

  window.__P09 = {
    report: report,
    dump: dump,
    raw: gestures,
    classify: classify,
    stop: function () {
      var i
      for (i = 0; i < listeners.length; i++) {
        document.removeEventListener(listeners[i][0], listeners[i][1], true)
      }
      listeners = []
    }
  }

  console.log("[P09] listening. Drag on the panel in the cockpit, several times,\n" +
    "      slowly and over a decent distance, then run __P09.report()")
})();
