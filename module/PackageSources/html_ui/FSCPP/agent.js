/*
 * FSCPP agent — capture and replay of cockpit pointer input.
 *
 * This is the piece that eventually moves into FS Copilot, so it obeys two rules
 * that the rest of this repository does not have to:
 *
 *   1. It does not know how messages travel. It exposes onCapture(fn) and
 *      replay(msg) and nothing else. The testbed wires those to the inspector's
 *      console channel; FS Copilot would wire the same file to the CommBus.
 *      Anything transport-shaped — framing, batching, rate limits, the 512-byte
 *      question — belongs on the other side of that line.
 *
 *   2. It targets Coherent GT, which is Chrome 49 (2016). No optional chaining,
 *      no ??, no class fields, no Object.fromEntries, no Array.at, no
 *      String.replaceAll, no Promise.allSettled. Those are syntax errors here and
 *      take out the whole file, not one line. See docs/09-environment.md.
 *
 * It is also deliberately additive: it only ADDS listeners and dispatches events.
 * It wraps nothing and replaces nothing, so it cannot disable the aircraft the way
 * a Coherent.call wrapper did. stop() removes everything it added.
 *
 * Exposed as a factory so that both callers use this one file:
 *
 *   FSCPP_Agent(instrumentElement)   -> the agent, also parked on window.FSCPP
 *
 * The module calls it from boot.js with the instrument VCockpit.js just created.
 * The testbed calls it after injecting this file through the inspector. Neither
 * path has its own copy.
 *
 *   agent.onCapture(fn)   fn(msg) whenever the pilot presses
 *   agent.replay(msg)     apply a message from the peer
 *   agent.stop()          remove all listeners
 *   agent.stats()         counters
 */

window.FSCPP_Agent = function (instrument) {
  var VERSION = 5

  /* Drag sampling.
   *
   * A press and a drag are told apart by how far the pointer travelled between
   * down and up, in viewport pixels, because a threshold in normalised units
   * would mean something different on every panel size.
   *
   * The path is sampled rather than recorded whole: MSFS delivers mousemove at a
   * high rate (754 of them in one short capture session), and a map pan does not
   * need every one of them to look right on the other side. */
  var DRAG_MIN_PX = 4        // below this the gesture was a press, not a drag
  var DRAG_SAMPLE_MS = 33    // ~30 Hz
  var DRAG_MIN_STEP_PX = 2   // ignore jitter between samples
  var DRAG_MAX_POINTS = 240  // bounds the message; ~8s of dragging at 30 Hz
  var DRAG_MAX_STEP_MS = 250 // a pause mid-drag replays as a pause, but a bounded one

  // Replacing a previous install must not leave the old listeners attached.
  if (window.FSCPP && window.FSCPP.stop) {
    try { window.FSCPP.stop() } catch (e) { /* keep going */ }
  }

  // Given an instrument, use it. Otherwise fall back to finding one, which is
  // what an injected testbed session does.
  var instr = instrument || null
  if (!instr) {
    var panel = document.getElementById("panel")
    instr = panel && panel.children.length ? panel.children[0] : null
  }

  /* The routing key.
   *
   * instrumentIdentifier alone is not enough: the A220 reports "CTP" for both
   * CTPs, "MKP" for both MKPs and "FCP" for all four FCPs, because the identifier
   * is the templateID and the side lives only in the URL query. Appending the
   * query distinguishes them, and stays identical across machines because it comes
   * from the aircraft's panel.cfg. */
  function keyFor(el) {
    if (!el) return "none"
    var id = el.instrumentIdentifier || el.tagName.toLowerCase()
    var url = el.getAttribute("url") || el.getAttribute("Url") || ""
    var q = url.indexOf("?") === -1 ? "" : url.slice(url.indexOf("?") + 1)
    return q ? id + "|" + q : id
  }

  var KEY = keyFor(instr)

  var listeners = []
  var captureFns = []
  var stats = { captured: 0, replayed: 0, missed: 0, ignored: 0 }

  // Non-zero while replaying, so a synthetic event that somehow loses its
  // selfEmit flag still cannot be captured and echoed back. A counter rather than
  // a flag because a drag replay spans many timeouts and a second message can
  // arrive inside it — a boolean would be cleared early by whichever finished
  // first, reopening the echo path mid-drag.
  var replaying = 0
  var pendingDown = null

  function rect() {
    return instr ? instr.getBoundingClientRect() : { left: 0, top: 0, width: 0, height: 0 }
  }

  function emit(msg) {
    stats.captured++
    for (var i = 0; i < captureFns.length; i++) {
      try { captureFns[i](msg) } catch (e) { /* a bad consumer must not break capture */ }
    }
  }

  /* Capture ---------------------------------------------------------------- */

  function isOurs(ev) {
    // Two independent guards. selfEmit is what FS Copilot already uses and is set
    // on everything we dispatch. isTrusted is the belt: real cockpit input is
    // trusted (Q02), and nothing we synthesise ever can be.
    return ev.selfEmit === true || ev.isTrusted === false || replaying > 0
  }

  function normalise(ev) {
    var r = rect()
    if (!r.width || !r.height) return null
    return {
      nx: Math.round(((ev.clientX - r.left) / r.width) * 1e4) / 1e4,
      ny: Math.round(((ev.clientY - r.top) / r.height) * 1e4) / 1e4
    }
  }

  function onDown(ev) {
    if (isOurs(ev)) { stats.ignored++; return }
    var n = normalise(ev)
    if (!n) return
    pendingDown = {
      nx: n.nx, ny: n.ny,
      x: ev.clientX, y: ev.clientY,        // raw, for the pixel thresholds
      lastX: ev.clientX, lastY: ev.clientY,
      at: Date.now(), lastAt: 0,
      button: ev.button,
      travelled: 0,
      path: [[0, n.nx, n.ny]]
    }
  }

  /* Sampled only while a button is down. The listener still fires on every move
   * MSFS delivers — and it delivers a great many — so the early return matters
   * more than it looks. */
  function onMove(ev) {
    var d = pendingDown
    if (!d) return
    // Not counted in stats.ignored: that counter is meaningful precisely because
    // it tracks 2x replayed, and folding moves into it would destroy the signal.
    if (ev.selfEmit === true || ev.isTrusted === false || replaying > 0) return

    var far = Math.abs(ev.clientX - d.x) + Math.abs(ev.clientY - d.y)
    if (far > d.travelled) d.travelled = far

    var dt = Date.now() - d.at
    if (dt - d.lastAt < DRAG_SAMPLE_MS) return
    if (Math.abs(ev.clientX - d.lastX) + Math.abs(ev.clientY - d.lastY) < DRAG_MIN_STEP_PX) return

    var n = normalise(ev)
    if (!n) return
    d.lastX = ev.clientX
    d.lastY = ev.clientY
    d.lastAt = dt
    if (d.path.length < DRAG_MAX_POINTS) d.path.push([dt, n.nx, n.ny])
  }

  function onUp(ev) {
    if (isOurs(ev)) { stats.ignored++; return }
    var n = normalise(ev)
    if (!n) return

    // Hold duration is carried rather than a fixed constant, so a press-and-hold
    // replays as a press-and-hold. Without a matching down — which happens when
    // the press began outside the panel — fall back to a nominal press.
    var d = pendingDown
    var held = d ? Date.now() - d.at : 0
    var from = d || n
    var button = typeof ev.button === "number" ? ev.button : 0
    pendingDown = null

    // Far enough to be a drag, and with somewhere to drag along. A gesture that
    // travelled but produced no intermediate samples — a flick faster than the
    // sample interval — is still a press, which is the right reading of it.
    if (d && d.travelled >= DRAG_MIN_PX && d.path.length > 1) {
      var path = d.path.slice()
      path.push([Date.now() - d.at, n.nx, n.ny])
      emit({ v: VERSION, k: "drag", key: KEY, button: button, path: path })
      return
    }

    emit({
      v: VERSION,
      k: "press",
      key: KEY,
      nx: from.nx,
      ny: from.ny,
      ux: n.nx,
      uy: n.ny,
      hold: held > 1500 ? 1500 : held,
      button: button
    })
  }

  function listen(target, type, fn) {
    target.addEventListener(type, fn, true)
    listeners.push([target, type, fn])
  }

  if (instr) {
    // Capture phase on document: the panel's own handlers may stopPropagation,
    // and MSFS delivers to the deepest node, not to the instrument element.
    listen(document, "mousedown", onDown)
    listen(document, "mousemove", onMove)
    listen(document, "mouseup", onUp)
  }

  /* Replay ----------------------------------------------------------------- */

  function fire(target, type, x, y, buttons, button) {
    var ev = new MouseEvent(type, {
      bubbles: true,
      cancelable: true,
      composed: true,
      view: window,
      clientX: x,
      clientY: y,
      screenX: x,
      screenY: y,
      button: button || 0,
      buttons: buttons,
      detail: 1
    })
    ev.selfEmit = true
    return target.dispatchEvent(ev)
  }

  /* A drag replays as its recorded path, with its original timing.
   *
   * Each move is dispatched at whatever is under that point, which is what a real
   * mouse does — there is no pointer capture to imitate, since Chrome 49 has no
   * setPointerCapture at all.
   *
   * Deliberately no `click` at the end. A browser fires one only when down and up
   * share a target, and a map pan that ended somewhere else should not also
   * register as a selection on the far side. */
  function replayDrag(msg) {
    var pts = msg.path
    if (!pts || pts.length < 2) { stats.missed++; return false }

    var r = rect()
    if (!r.width || !r.height) { stats.missed++; return false }

    var px = function (i) {
      return {
        x: Math.round(r.left + pts[i][1] * r.width),
        y: Math.round(r.top + pts[i][2] * r.height)
      }
    }

    var first = px(0)
    var start = document.elementFromPoint(first.x, first.y)
    if (!start) { stats.missed++; return false }

    replaying++
    fire(start, "mousedown", first.x, first.y, 1, msg.button)

    // `replaying` suppresses capture while it is above zero, so a drag whose final
    // timeout never runs would silently disable capture for the rest of the
    // session with nothing to show for it. Release exactly once, and guarantee the
    // release even if the path never completes.
    var released = false
    var release = function () {
      if (released) return
      released = true
      replaying--
      stats.replayed++
    }

    // Scheduled against the start of the gesture rather than chained, so one slow
    // timeout cannot compound into drift across a long path. A pause the pilot
    // made mid-drag is preserved but bounded, so a gesture that sat still for
    // thirty seconds does not hold the replay open for thirty seconds.
    var elapsed = 0
    var prev = 0
    var i
    for (i = 1; i < pts.length; i++) {
      var step = pts[i][0] - prev
      if (step < 0) step = 0
      if (step > DRAG_MAX_STEP_MS) step = DRAG_MAX_STEP_MS
      prev = pts[i][0]
      elapsed += step

      ;(function (index, when, isLast) {
        setTimeout(function () {
          var p = px(index)
          var target = document.elementFromPoint(p.x, p.y) || start
          if (isLast) {
            fire(target, "mouseup", p.x, p.y, 0, msg.button)
            release()
          } else {
            fire(target, "mousemove", p.x, p.y, 1, msg.button)
          }
        }, when)
      })(i, elapsed, i === pts.length - 1)
    }

    // The deadman. If the path above never reaches its last point, this releases
    // capture anyway and says so, rather than leaving the agent mute.
    setTimeout(function () {
      if (released) return
      console.warn("[FSCPP] drag replay did not complete — releasing capture")
      release()
    }, elapsed + 3000)

    return true
  }

  function replay(msg) {
    if (!msg) return false
    if (msg.key && msg.key !== KEY) return false
    if (msg.k === "drag") return replayDrag(msg)
    if (msg.k !== "press") return false

    var r = rect()
    if (!r.width || !r.height) { stats.missed++; return false }

    var x = Math.round(r.left + msg.nx * r.width)
    var y = Math.round(r.top + msg.ny * r.height)
    var ux = msg.ux == null ? msg.nx : msg.ux
    var uy = msg.uy == null ? msg.ny : msg.uy
    var x2 = Math.round(r.left + ux * r.width)
    var y2 = Math.round(r.top + uy * r.height)

    var target = document.elementFromPoint(x, y)
    if (!target) { stats.missed++; return false }

    // MouseEvent only. PointerEvent does not exist in this engine, so a handler
    // bound to onPointerDown is unreachable by any means — see docs/09-environment.md.
    replaying++
    fire(target, "mousedown", x, y, 1, msg.button)

    var finish = function () {
      var upTarget = document.elementFromPoint(x2, y2) || target
      fire(upTarget, "mouseup", x2, y2, 0, msg.button)
      fire(upTarget, "click", x2, y2, 0, msg.button)
      replaying--
      stats.replayed++
    }

    // A held press is reconstructed with its original duration; a tap goes
    // through synchronously so the three events share one tick.
    if (msg.hold && msg.hold > 40) setTimeout(finish, msg.hold)
    else finish()

    return true
  }

  /* Surface ---------------------------------------------------------------- */

  var api = {
    version: VERSION,
    key: KEY,
    instrument: instr ? instr.tagName.toLowerCase() : null,
    rect: function () { var r = rect(); return { x: r.left, y: r.top, w: r.width, h: r.height } },
    onCapture: function (fn) { captureFns.push(fn) },
    replay: replay,
    stats: function () { return JSON.parse(JSON.stringify(stats)) },
    stop: function () {
      for (var i = 0; i < listeners.length; i++) {
        listeners[i][0].removeEventListener(listeners[i][1], listeners[i][2], true)
      }
      listeners = []
      captureFns = []
      replaying = 0
      pendingDown = null
    }
  }

  window.FSCPP = api

  console.log("[FSCPP] agent v" + VERSION + " on " + KEY +
    "  rect=" + Math.round(rect().width) + "x" + Math.round(rect().height) +
    (instr ? "" : "  WARNING: no instrument element found"))

  return api
};
