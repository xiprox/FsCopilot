/*
 * P07 — Does a WASM gauge need to be hovered before it accepts a press?
 *
 * P06 proved that a synthetic MouseEvent becomes WASM_MOUSE_DOWN(guid, x, y)
 * with the coordinates intact, and that the sim forwards it to the module. The
 * A350's MFD did not react.
 *
 * The hypothesis: WasmSimCanvas forwards coordinates, but the *module* does its
 * own hit-testing, and iniBuilds' gauge very likely tracks the hovered element
 * from WASM_MOUSE_MOVE and treats WASM_MOUSE_DOWN as "press whatever is
 * hovered" — ignoring the coordinates carried by the down event itself. A real
 * click is always preceded by a trail of moves; ours was not.
 *
 * So this replays a press the way a mouse actually produces one: enter, a short
 * approach of moves, a hover dwell, then down, hold, up, click.
 *
 *   __P07.press(x, y)             the realistic sequence
 *   __P07.show(x, y)              blink the display twice, then press — so a
 *                                 human watching knows exactly when to look
 *   __P07.press(x, y, {hover: 400, hold: 120, trail: 8})
 *   __P07.dry(x, y)               same sequence, suppressed
 *   __P07.log()                   what the module was told
 *
 * Run in a WasmInstrument panel.
 */

(function () {
  const canvas = document.querySelector("wasm-sim-canvas")
  if (!canvas) { console.log("[P07] no <wasm-sim-canvas> here — wrong panel"); return }
  const img = canvas.querySelector("img") || canvas
  const guid = canvas.m_wasmInstrumentGUid

  // Share P06's Coherent.call wrapper when it is already installed, so the two
  // probes cannot fight over who owns the native function.
  if (!window.__P06_STATE) window.__P06_STATE = { captured: [], suppress: false, native: Coherent.call, deadman: null }
  const S = window.__P06_STATE
  if (!window.__P06_WRAPPED) {
    Coherent.call = function (name) {
      if (typeof name === "string" && name.indexOf("WASM_") === 0) {
        S.captured.push({ name: name, args: Array.prototype.slice.call(arguments, 1), t: Date.now() })
        if (S.suppress) return
      }
      return S.native.apply(Coherent, arguments)
    }
    window.__P06_WRAPPED = true
  }

  function fire(type, x, y, buttons, button) {
    const e = new MouseEvent(type, {
      bubbles: true, cancelable: true, composed: true, view: window,
      clientX: x, clientY: y, screenX: x, screenY: y,
      button: button || 0, buttons: buttons, detail: 1
    })
    e.selfEmit = true
    img.dispatchEvent(e)
  }

  /* A real press, reconstructed. The module sees the pointer arrive, travel to
   * the control, settle, and only then go down — which is the only sequence it
   * has ever been written to expect. */
  function sequence(x, y, opts, live) {
    opts = opts || {}
    const trail = opts.trail == null ? 6 : opts.trail
    const hover = opts.hover == null ? 250 : opts.hover
    const hold = opts.hold == null ? 90 : opts.hold

    S.captured.length = 0
    if (S.touch) S.touch()
    S.suppress = !live

    // Approach from up and to the left, the way a cursor would arrive.
    const fromX = Math.max(0, x - 140)
    const fromY = Math.max(0, y - 110)

    fire("mouseover", fromX, fromY, 0)
    fire("mouseenter", fromX, fromY, 0)
    for (let i = 1; i <= trail; i++) {
      const k = i / trail
      fire("mousemove", Math.round(fromX + (x - fromX) * k), Math.round(fromY + (y - fromY) * k), 0)
    }

    // hover:0 means the same JS tick, not "a timeout of zero". If the sim
    // re-raycasts the cockpit every frame and rewrites the module's hover from
    // the real cursor, an injected move survives only until the next frame —
    // so the press has to follow it without yielding at all.
    if (hover === 0) {
      fire("mousedown", x, y, 1)
      if (hold === 0) {
        fire("mouseup", x, y, 0)
        fire("click", x, y, 0)
        setTimeout(report, 150)
      } else {
        setTimeout(function () {
          fire("mouseup", x, y, 0)
          fire("click", x, y, 0)
          setTimeout(report, 150)
        }, hold)
      }
      return
    }

    setTimeout(function () {
      fire("mousedown", x, y, 1)
      setTimeout(function () {
        fire("mouseup", x, y, 0)
        fire("click", x, y, 0)
        setTimeout(report, 150)
      }, hold)
    }, hover)

    function report() {
      S.suppress = false   // resting state, always
      const seen = S.captured.map(function (c) {
        return c.name.replace("WASM_", "") + "(" + c.args.slice(1).join(",") + ")"
      })
      const moves = seen.filter(function (s) { return s.indexOf("MOUSE_MOVE") === 0 }).length
      const rest = seen.filter(function (s) { return s.indexOf("MOUSE_MOVE") !== 0 })
      console.log(
        "[P07] " + (live ? "LIVE" : "dry") + " press at (" + x + ", " + y + ")  guid=" + guid + "\n" +
        "      " + moves + " MOUSE_MOVE forwarded, then: " + rest.join("  ") + "\n" +
        "      trail=" + trail + " hover=" + hover + "ms hold=" + hold + "ms")
    }
  }

  window.__P07 = {
    press: function (x, y, opts) { sequence(x, y, opts, true) },
    dry: function (x, y, opts) { sequence(x, y, opts, false) },

    /* Blink the display first. The whole difficulty with this surface is that
     * only a human can see whether it worked, so tell them when to look. */
    show: function (x, y, opts) {
      let n = 0
      const id = setInterval(function () {
        img.style.visibility = (n % 2 === 0) ? "hidden" : "visible"
        n++
        if (n > 3) {
          clearInterval(id)
          img.style.visibility = "visible"
          setTimeout(function () { sequence(x, y, opts, true) }, 800)
        }
      }, 400)
      console.log("[P07] blinking twice, then pressing (" + x + ", " + y + ") — watch the display")
    },

    log: function () {
      console.log(S.captured.map(function (c) {
        return c.name.replace("WASM_", "") + "(" + c.args.slice(1).join(",") + ")"
      }).join("\n"))
    }
  }

  console.log("[P07] ready on guid=" + guid + " (" + canvas.m_wasmGaugeName + "). Try __P07.show(x, y)")
})();
