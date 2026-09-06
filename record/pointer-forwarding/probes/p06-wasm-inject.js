/*
 * P06 — Can we drive a WASM gauge by dispatching mouse events at coordinates?
 *
 * Follows P01, which found that WasmSimCanvas listens for mouse events on itself
 * and forwards viewport coordinates to the module:
 *
 *   OnMouseDown(_e) { Coherent.call("WASM_MOUSE_DOWN", guid, _e.clientX, _e.clientY, _e.button) }
 *
 * This checks the path end to end WITHOUT pressing anything in the running sim.
 * Coherent.call is wrapped so WASM_* calls are captured and *suppressed* — the
 * synthetic events travel the real path, the sim never hears about them, and we
 * see exactly what the module would have been told.
 *
 *   __P06.dry(x, y)      inject at viewport coordinates, suppressed  (default)
 *   __P06.dry()          inject at the middle of the gauge
 *   __P06.live(x, y)     inject for real — the display WILL react. Ask first.
 *   __P06.restore()      put Coherent.call back
 *
 * Run in a panel whose instrument is a WasmInstrument.
 */

(function () {
  const canvas = document.querySelector("wasm-sim-canvas")
  const panel = document.getElementById("panel")
  const instr = panel && panel.children.length ? panel.children[0] : null

  const L = []
  const say = (s) => L.push(s == null ? "" : String(s))
  const flush = () => { console.log(L.join("\n")); L.length = 0 }

  say("=========================================================")
  say("P06  injecting mouse events into a WASM gauge")
  say("=========================================================")
  say("title    " + document.title)
  if (!canvas) {
    say("")
    say("  No <wasm-sim-canvas> in this document. This probe only applies to a")
    say("  WasmInstrument panel — check the page you selected.")
    flush(); return
  }

  const guid = canvas.m_wasmInstrumentGUid
  const img = canvas.querySelector("img")
  const rect = canvas.getBoundingClientRect()

  say("guid     " + guid + "   (instrument Guid attribute)")
  say("module   " + canvas.m_wasmModuleName + "   gauge=" + canvas.m_wasmGaugeName)
  say("liveview " + (img ? img.getAttribute("src") : "(no img)"))
  say("rect     x=" + rect.left + " y=" + rect.top + " w=" + rect.width + " h=" + rect.height)
  if (instr) say("instr    " + instr.instrumentIdentifier + "  interactive=" + instr.isInteractive)

  // ---- wrap Coherent.call -------------------------------------------------
  // State lives on window, not in this closure: the wrapper is installed once
  // and survives re-running the probe, so a fresh closure would leave the live
  // wrapper writing into an array nobody reads.
  //
  // This wrapper sits in the path of every cockpit click on this gauge. Left
  // suppressing, it silently disables the instrument for the pilot — which has
  // happened, and the pilot has no way to know why. So: suppression is never
  // the resting state, restore is reachable from a fixed global that survives
  // losing every other handle, and a deadman puts the native function back
  // whether or not anybody remembers to.
  if (!window.__P06_STATE) window.__P06_STATE = { captured: [], suppress: false, native: Coherent.call, deadman: null }
  const S = window.__P06_STATE
  const captured = S.captured

  const DEADMAN_MS = 120000

  window.__FSCPP_RESTORE = function () {
    if (S.native) Coherent.call = S.native
    S.suppress = false
    window.__P06_WRAPPED = false
    if (S.deadman) { clearTimeout(S.deadman); S.deadman = null }
    console.log("[FSCPP] Coherent.call restored — the gauge is under the sim's control again")
    return true
  }

  function touch() {
    if (S.deadman) clearTimeout(S.deadman)
    S.deadman = setTimeout(function () {
      console.warn("[FSCPP] deadman fired after " + (DEADMAN_MS / 1000) + "s idle — restoring Coherent.call")
      window.__FSCPP_RESTORE()
    }, DEADMAN_MS)
  }
  S.touch = touch

  if (!window.__P06_WRAPPED) {
    Coherent.call = function (name) {
      const args = Array.prototype.slice.call(arguments, 1)
      if (typeof name === "string" && name.indexOf("WASM_") === 0) {
        S.captured.push({ name: name, args: args, t: Date.now() })
        if (S.suppress) return   // the sim never hears this one
      }
      return S.native.apply(Coherent, arguments)
    }
    window.__P06_WRAPPED = true
  }
  touch()

  function fire(x, y, live) {
    captured.length = 0
    touch()
    S.suppress = !live
    const target = document.elementFromPoint(x, y) || canvas

    // Exactly what WasmSimCanvas listens for. No PointerEvents — it binds none.
    const seq = [
      ["mousedown", 1],
      ["mouseup", 0],
      ["click", 0]
    ]
    const dispatched = []
    seq.forEach(function (pair) {
      const ev = new MouseEvent(pair[0], {
        bubbles: true, cancelable: true, composed: true, view: window,
        clientX: x, clientY: y, screenX: x, screenY: y,
        button: 0, buttons: pair[1], detail: 1
      })
      ev.selfEmit = true
      dispatched.push(pair[0] + (target.dispatchEvent(ev) ? "" : " (prevented)"))
    })

    say("")
    say("--- inject at (" + x + ", " + y + ")   " + (live ? "LIVE — the sim WILL act on this" : "dry run — suppressed"))
    say("  elementFromPoint  " + (target.tagName ? target.tagName.toLowerCase() : String(target)) +
        (target === canvas ? "  (the canvas itself)" : "") +
        (target === img ? "  (the live-view img — events bubble to the canvas)" : ""))
    say("  dispatched        " + dispatched.join(", "))
    say("")
    say("  Coherent.call intercepted:")
    if (!captured.length) {
      say("      NOTHING. The events did not reach WasmSimCanvas's handlers.")
      say("      Either the target is outside the canvas subtree, or something")
      say("      stopped propagation before it got there.")
    } else {
      captured.forEach(function (c) {
        say("      " + c.name + "(" + c.args.join(", ") + ")")
      })
      const down = captured.filter(function (c) { return c.name === "WASM_MOUSE_DOWN" })[0]
      if (down && down.args[1] === x && down.args[2] === y) {
        say("")
        say("      Coordinates arrived intact. This is the whole mechanism working:")
        say("      a synthetic MouseEvent at (x, y) becomes WASM_MOUSE_DOWN(guid, x, y).")
      }
    }
    S.suppress = false   // resting state, always
    flush()
  }

  window.__P06 = {
    dry: function (x, y) {
      if (x == null) { x = Math.round(rect.left + rect.width / 2); y = Math.round(rect.top + rect.height / 2) }
      fire(x, y, false)
    },
    live: function (x, y) {
      if (x == null) { x = Math.round(rect.left + rect.width / 2); y = Math.round(rect.top + rect.height / 2) }
      fire(x, y, true)
    },

    /* The honest test: press a control for real, then press the same
     * coordinates synthetically and see whether it does the same thing.
     *
     *   __P06.arm()     then click the gauge in the cockpit, for real
     *   __P06.armed()   what the real click told the module
     *   __P06.replay()  do it again, synthetically
     *
     * arm() runs in pass-through, so the real click works normally. */
    arm: function () {
      captured.length = 0
      touch()
      S.suppress = false
      console.log("[P06] armed and passing through. Click the gauge in the cockpit, then __P06.armed()")
    },
    armed: function () {
      if (!captured.length) { console.log("[P06] nothing captured yet — click the gauge, then try again"); return null }
      const lines = captured.map(function (c) { return "  " + c.name + "(" + c.args.join(", ") + ")" })
      const down = captured.filter(function (c) { return c.name === "WASM_MOUSE_DOWN" || c.name === "WASM_CLICK" })[0]
      console.log("[P06] the real click produced:\n" + lines.join("\n") +
        (down ? "\n[P06] replay point (" + down.args[1] + ", " + down.args[2] + ") — run __P06.replay()" : ""))
      if (down) S.point = { x: down.args[1], y: down.args[2] }
      return S.point || null
    },
    replay: function () {
      if (!S.point) { console.warn("[P06] no captured point — run __P06.arm(), click, then __P06.armed()"); return }
      fire(S.point.x, S.point.y, true)
    },

    captured: function () { return captured },
    restore: function () { return window.__FSCPP_RESTORE() }
  }

  say("")
  say("--- why this matters for the existing scheme ---")
  say("  FS Copilot's replay builds `new MouseEvent(type, {bubbles, cancelable})`")
  say("  with no clientX/clientY, so both default to 0. Those events DO reach")
  say("  WasmSimCanvas and DO become WASM_MOUSE_DOWN — at (0, 0), the corner of")
  say("  the screen. The A350 does not ignore FS Copilot's clicks. It receives")
  say("  every one of them at the wrong place.")

  flush()

  // Auto-run the safe version so a single paste answers the question.
  window.__P06.dry()
})();
