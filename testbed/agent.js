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
 *   window.FSCPP.onCapture(fn)   fn(msg) whenever the pilot presses
 *   window.FSCPP.replay(msg)     apply a message from the peer
 *   window.FSCPP.stop()          remove all listeners
 *   window.FSCPP.stats()         counters
 */

(function () {
  var VERSION = 3

  // Replacing a previous install must not leave the old listeners attached.
  if (window.FSCPP && window.FSCPP.stop) {
    try { window.FSCPP.stop() } catch (e) { /* keep going */ }
  }

  var panel = document.getElementById("panel")
  var instr = panel && panel.children.length ? panel.children[0] : null

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

  // Set while replaying, so a synthetic event that somehow loses its selfEmit flag
  // still cannot be captured and echoed back.
  var replaying = false
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
    return ev.selfEmit === true || ev.isTrusted === false || replaying
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
    pendingDown = { nx: n.nx, ny: n.ny, at: Date.now(), button: ev.button }
  }

  function onUp(ev) {
    if (isOurs(ev)) { stats.ignored++; return }
    var n = normalise(ev)
    if (!n) return

    // Hold duration is carried rather than a fixed constant, so a press-and-hold
    // replays as a press-and-hold. Without a matching down — which happens when
    // the press began outside the panel — fall back to a nominal press.
    var held = pendingDown ? Date.now() - pendingDown.at : 0
    var from = pendingDown || n
    pendingDown = null

    emit({
      v: VERSION,
      k: "press",
      key: KEY,
      nx: from.nx,
      ny: from.ny,
      ux: n.nx,
      uy: n.ny,
      hold: held > 1500 ? 1500 : held,
      button: typeof ev.button === "number" ? ev.button : 0
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

  function replay(msg) {
    if (!msg || msg.k !== "press") return false
    if (msg.key && msg.key !== KEY) return false

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
    replaying = true
    fire(target, "mousedown", x, y, 1, msg.button)

    var finish = function () {
      var upTarget = document.elementFromPoint(x2, y2) || target
      fire(upTarget, "mouseup", x2, y2, 0, msg.button)
      fire(upTarget, "click", x2, y2, 0, msg.button)
      replaying = false
      stats.replayed++
    }

    // A held press is reconstructed with its original duration; a tap goes
    // through synchronously so the three events share one tick.
    if (msg.hold && msg.hold > 40) setTimeout(finish, msg.hold)
    else finish()

    return true
  }

  /* Surface ---------------------------------------------------------------- */

  window.FSCPP = {
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
      replaying = false
    }
  }

  console.log("[FSCPP] agent v" + VERSION + " on " + KEY +
    "  rect=" + Math.round(rect().width) + "x" + Math.round(rect().height) +
    (instr ? "" : "  WARNING: no instrument element found"))
})();
