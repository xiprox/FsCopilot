/*
 * P03 — Does a synthetic pointer sequence at coordinates drive a real display?
 *
 * Settles Q03, which is load-bearing: if this fails there is no fallback and the
 * approach is dead. Run it on the A220 CTP first — React over SVG, small, and its
 * failure mode under the existing scheme is fully understood.
 *
 * The honest test is: press a control for real, then press the same coordinates
 * synthetically and see whether the display does the same thing. So:
 *
 *   __P03.arm()            then click the control in the cockpit for real
 *   __P03.fire()           replay at exactly those coordinates
 *
 *   __P03.at(x, y)         replay at explicit viewport coordinates
 *   __P03.tree(x, y)       what is under a point, without pressing anything
 *   __P03.fire({mode:'mouse'})    mouse events only — isolates whether PointerEvent is required
 *   __P03.fire({hold:600})       press and hold
 *   __P03.last()           reprint the last result
 *
 * Reaction is measured, not eyeballed: a MutationObserver watches the instrument
 * subtree across the dispatch, so a React re-render shows up as a mutation count
 * even when the visual change is subtle. Watch the display too — a canvas repaint
 * mutates no nodes at all and only your eyes will catch it.
 */

(function () {
  const panel = document.getElementById("panel")
  const instr = panel && panel.children.length ? panel.children[0] : null
  if (!instr) console.warn("[P03] no #panel child — mutation watching will fall back to <body>")
  const watchRoot = instr || document.body

  const FULL = ["pointerdown", "mousedown", "pointerup", "mouseup", "click"]
  const MOUSE = ["mousedown", "mouseup", "click"]
  const POINTER = ["pointerdown", "pointerup"]

  let armed = null
  let last = null

  function describe(n) {
    if (!n) return "(null)"
    let s = n.tagName ? n.tagName.toLowerCase() : String(n.nodeName)
    if (n.id) s += "#" + n.id
    else if (n.getAttribute && n.getAttribute("class")) s += "." + String(n.getAttribute("class")).split(/\s+/)[0]
    if (n.ownerSVGElement || (n.namespaceURI && n.namespaceURI.indexOf("svg") !== -1)) s += " <svg-ns>"
    return s
  }

  function chain(el) {
    const out = []
    let n = el, guard = 0
    while (n && n.nodeType === 1 && guard++ < 12) { out.push(describe(n)); n = n.parentNode }
    return out
  }

  function make(type, x, y, down) {
    const init = {
      bubbles: true, cancelable: true, composed: true, view: window,
      clientX: x, clientY: y, screenX: x, screenY: y,
      button: 0, buttons: down ? 1 : 0, detail: 1
    }
    let ev
    if (type.indexOf("pointer") === 0) {
      if (typeof window.PointerEvent !== "function") return null
      init.pointerId = 1
      init.pointerType = "mouse"
      init.isPrimary = true
      init.width = 1; init.height = 1; init.pressure = down ? 0.5 : 0
      ev = new PointerEvent(type, init)
    } else {
      ev = new MouseEvent(type, init)
    }
    // Same flag the existing bridge uses, so a loaded hook does not echo this.
    ev.selfEmit = true
    return ev
  }

  function press(x, y, opts) {
    opts = opts || {}
    const mode = opts.mode || "full"
    const hold = opts.hold || 0
    const seq = mode === "mouse" ? MOUSE : mode === "pointer" ? POINTER : FULL

    const target = document.elementFromPoint(x, y)
    const L = []
    L.push("=========================================================")
    L.push("P03  synthetic press at (" + x + ", " + y + ")   mode=" + mode + (hold ? "  hold=" + hold + "ms" : ""))
    L.push("=========================================================")
    L.push("PointerEvent " + (typeof window.PointerEvent === "function" ? "available" : "NOT AVAILABLE"))
    L.push("elementFromPoint -> " + describe(target))
    if (!target) {
      L.push("")
      L.push("  Nothing at that point. Either the coordinates are outside the")
      L.push("  document, or the panel is covered by something with pointer-events.")
      console.log(L.join("\n")); return
    }
    L.push("ancestors        " + chain(target).join("  <  "))

    const rect = instr ? instr.getBoundingClientRect() : null
    if (rect && rect.width && rect.height) {
      L.push("normalised       nx=" + ((x - rect.left) / rect.width).toFixed(4) +
             "  ny=" + ((y - rect.top) / rect.height).toFixed(4))
    }

    // Objective reaction signal. A React re-render mutates nodes; a canvas
    // repaint does not, so zero mutations is not by itself a failure.
    let mutations = 0
    const samples = []
    const mo = new MutationObserver((muts) => {
      mutations += muts.length
      muts.forEach((m) => {
        if (samples.length < 6) {
          samples.push(m.type + " on " + describe(m.target) +
            (m.type === "attributes" ? " @" + m.attributeName : "") +
            (m.type === "childList" ? " +" + m.addedNodes.length + "/-" + m.removedNodes.length : ""))
        }
      })
    })
    mo.observe(watchRoot, { childList: true, subtree: true, attributes: true, characterData: true })

    const fired = []
    function dispatch(type, down) {
      const ev = make(type, x, y, down)
      if (!ev) { fired.push(type + ": SKIPPED (no PointerEvent)"); return }
      const ok = target.dispatchEvent(ev)
      fired.push(type + ": " + (ok ? "not cancelled" : "PREVENTED (a handler ran and called preventDefault)"))
    }

    const downs = seq.filter((t) => t.indexOf("down") !== -1)
    const ups = seq.filter((t) => t.indexOf("down") === -1)

    downs.forEach((t) => dispatch(t, true))

    const finish = () => {
      ups.forEach((t) => dispatch(t, false))
      setTimeout(() => {
        mo.disconnect()
        L.push("")
        L.push("--- dispatched ---")
        fired.forEach((f) => L.push("  " + f))
        L.push("")
        L.push("--- reaction ---")
        L.push("  " + mutations + " DOM mutations in the instrument subtree within 500ms")
        samples.forEach((s) => L.push("      " + s))
        L.push("")
        L.push("--- read this as ---")
        L.push("  mutations > 0            -> the display reacted. Q03 passes for this surface.")
        L.push("  PREVENTED on any event   -> a handler ran, which is also a pass.")
        L.push("  0 mutations, canvas panel-> inconclusive from here. WATCH THE DISPLAY.")
        L.push("  0 mutations, React panel -> nothing handled it. Try mode:'mouse' and")
        L.push("     mode:'pointer' separately before concluding.")
        last = L.join("\n")
        console.log(last)
      }, 500)
    }

    if (hold) setTimeout(finish, hold); else finish()
  }

  window.__P03 = {
    arm: function () {
      armed = null
      const grab = (ev) => {
        if (ev.selfEmit) return
        armed = { x: ev.clientX, y: ev.clientY, target: describe(ev.target), type: ev.type }
        document.removeEventListener("pointerdown", grab, true)
        document.removeEventListener("mousedown", grab, true)
        document.removeEventListener("mouseup", grab, true)
        console.log("[P03] captured " + ev.type + " at (" + armed.x + ", " + armed.y + ") on " + armed.target +
                    "\n[P03] now run __P03.fire()")
      }
      document.addEventListener("pointerdown", grab, true)
      document.addEventListener("mousedown", grab, true)
      document.addEventListener("mouseup", grab, true)
      console.log("[P03] armed. Click a control on this panel in the cockpit, for real.")
    },
    fire: function (opts) {
      if (!armed) { console.warn("[P03] nothing captured — run __P03.arm() and click a control first"); return }
      press(armed.x, armed.y, opts)
    },
    at: press,
    tree: function (x, y) {
      const el = document.elementFromPoint(x, y)
      console.log("(" + x + ", " + y + ") -> " + (el ? chain(el).join("  <  ") : "(nothing)"))
      return el
    },
    armedPoint: function () { return armed },
    last: function () { console.log(last || "[P03] nothing run yet") }
  }

  console.log("[P03] ready. __P03.arm() then click a control, then __P03.fire()")
})();
