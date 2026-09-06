/*
 * P02 — What input events does MSFS actually deliver to a panel document?
 *
 * Settles Q01 (which event families arrive) and Q02 (are they trusted).
 * Both gate whether press-and-hold and drag are capturable at all, and whether
 * replay needs real PointerEvents.
 *
 * Run in the Coherent GT debugger Console with an INTERACTIVE panel selected in
 * the frame picker. Good targets, in order: A220 CTP (React/SVG), A220 MKP
 * (canvas), A350 MFD (WASM live-view), any classic G1000 panel as a control.
 *
 * Then go and click, press-and-hold, and drag on that display in the cockpit.
 *
 *   __P02.report()    print what arrived
 *   __P02.clear()     empty the buffer and keep listening
 *   __P02.stop()      detach every listener
 *   __P02.rect()      the instrument's bounding rect, for Q04
 *
 * Paste the report into results/ as p02-<aircraft>-<panel>-<date>.txt.
 */

(function () {
  if (window.__P02 && window.__P02.stop) { window.__P02.stop(); console.log("[P02] replaced previous instance"); }

  // Everything a cockpit click could plausibly arrive as. Ordering matters in the
  // report — this is the order a browser would fire them in for one press.
  const TYPES = [
    "pointerover", "pointerenter", "pointerdown", "pointermove", "pointerup",
    "pointercancel", "pointerout", "pointerleave", "gotpointercapture", "lostpointercapture",
    "mouseover", "mouseenter", "mousedown", "mousemove", "mouseup",
    "mouseout", "mouseleave", "click", "dblclick", "contextmenu", "auxclick",
    "touchstart", "touchmove", "touchend", "touchcancel",
    "wheel", "keydown", "keypress", "keyup", "focus", "blur"
  ]

  // Moves flood. Keep the first few and every 10th after that, so the shape of a
  // drag survives without ten thousand rows.
  const MOVE = /move$/
  const MOVE_KEEP_FIRST = 5
  const MOVE_EVERY = 10

  const panel = document.getElementById("panel")
  const instr = panel && panel.children.length ? panel.children[0] : null

  const t0 = Date.now()
  const rows = []
  const counts = {}
  const moveSeen = {}
  const bound = []
  let announced = false

  function describe(n) {
    if (!n) return "(null)"
    if (n === document) return "#document"
    if (n === window) return "window"
    let s = n.tagName ? n.tagName.toLowerCase() : String(n.nodeName)
    if (n.id) s += "#" + n.id
    else if (n.getAttribute && n.getAttribute("class")) s += "." + String(n.getAttribute("class")).split(/\s+/)[0]
    // SVG nodes are the whole reason this project exists — mark them explicitly.
    if (n.ownerSVGElement || (n.namespaceURI && n.namespaceURI.indexOf("svg") !== -1)) s += " <svg-ns>"
    return s
  }

  function record(where, ev) {
    counts[ev.type] = (counts[ev.type] || 0) + 1

    if (MOVE.test(ev.type)) {
      const n = moveSeen[ev.type] = (moveSeen[ev.type] || 0) + 1
      if (n > MOVE_KEEP_FIRST && n % MOVE_EVERY !== 0) return
    }

    const r = instr ? instr.getBoundingClientRect() : null
    rows.push({
      t: Date.now() - t0,
      type: ev.type,
      where: where,
      trusted: ev.isTrusted,
      target: describe(ev.target),
      x: typeof ev.clientX === "number" ? ev.clientX : null,
      y: typeof ev.clientY === "number" ? ev.clientY : null,
      nx: r && r.width && typeof ev.clientX === "number" ? (ev.clientX - r.left) / r.width : null,
      ny: r && r.height && typeof ev.clientY === "number" ? (ev.clientY - r.top) / r.height : null,
      button: typeof ev.button === "number" ? ev.button : null,
      buttons: typeof ev.buttons === "number" ? ev.buttons : null,
      ptype: ev.pointerType || null,
      pid: typeof ev.pointerId === "number" ? ev.pointerId : null,
      key: ev.key || null,
      self: !!ev.selfEmit
    })

    if (!announced) {
      announced = true
      console.log("[P02] first event: " + ev.type + " (trusted=" + ev.isTrusted + "). Keep interacting, then __P02.report()")
    }
  }

  function bind(node, where) {
    if (!node) return
    TYPES.forEach((type) => {
      const fn = (ev) => { try { record(where, ev) } catch (e) { /* never break the sim's own handling */ } }
      // Capture phase: see the event before the page can stopPropagation it.
      node.addEventListener(type, fn, true)
      bound.push([node, type, fn])
    })
  }

  bind(document, "doc")
  if (instr) bind(instr, "instr")

  function pad(s, n) { s = String(s == null ? "" : s); return s.length >= n ? s.slice(0, n) : s + " ".repeat(n - s.length) }
  function num(v, d) { return v == null ? pad("", d === 4 ? 6 : 5) : pad(v.toFixed ? v.toFixed(d) : v, d === 4 ? 6 : 5) }

  window.__P02 = {
    rows: rows,
    counts: counts,
    rect: function () { return instr ? instr.getBoundingClientRect() : null },
    clear: function () { rows.length = 0; for (const k in counts) delete counts[k]; for (const k in moveSeen) delete moveSeen[k]; console.log("[P02] cleared") },
    stop: function () { bound.forEach((b) => b[0].removeEventListener(b[1], b[2], true)); bound.length = 0; console.log("[P02] stopped") },
    report: function () {
      const L = []
      L.push("=========================================================")
      L.push("P02  input delivery to a panel document")
      L.push("=========================================================")
      L.push("title      " + document.title)
      L.push("href       " + location.href)
      L.push("instrument " + (instr ? describe(instr) : "(no #panel child)"))
      if (instr) {
        L.push("             id=" + instr.instrumentIdentifier + "  interactive=" + instr.isInteractive)
        const r = instr.getBoundingClientRect()
        L.push("             rect  x=" + r.left + " y=" + r.top + " w=" + r.width + " h=" + r.height)
      }
      L.push("viewport   " + window.innerWidth + " x " + window.innerHeight +
             "   dpr=" + (window.devicePixelRatio || 1))

      L.push("")
      L.push("--- what arrived ---")
      const seen = TYPES.filter((t) => counts[t])
      if (!seen.length) {
        L.push("  NOTHING. Either the panel was not interacted with, or this document")
        L.push("  receives no DOM input at all — which for a WASM or iframe panel is")
        L.push("  itself the answer.")
      } else {
        seen.forEach((t) => L.push("  " + pad(t, 20) + " x" + counts[t]))
        const missing = ["pointerdown", "pointerup", "pointermove", "mousedown", "mousemove", "mouseup", "click"]
          .filter((t) => !counts[t])
        if (missing.length) L.push("  not seen: " + missing.join(", "))
      }

      const trusted = rows.filter((r) => r.trusted).length
      L.push("")
      L.push("--- trust ---")
      L.push("  " + trusted + " of " + rows.length + " trusted" +
             (rows.length && trusted === rows.length ? "  (isTrusted is usable as a loop breaker)" :
              rows.length && trusted === 0 ? "  (MSFS synthesises these — isTrusted is NOT usable)" : ""))

      L.push("")
      L.push("--- sequence ---")
      L.push("  " + pad("ms", 7) + pad("type", 18) + pad("at", 6) + pad("tr", 4) +
             pad("x", 6) + pad("y", 6) + pad("nx", 7) + pad("ny", 7) +
             pad("btn", 4) + pad("btns", 5) + pad("ptype", 7) + "target")
      rows.forEach((r) => {
        L.push("  " + pad(r.t, 7) + pad(r.type, 18) + pad(r.where, 6) + pad(r.trusted ? "y" : "n", 4) +
               pad(r.x, 6) + pad(r.y, 6) +
               pad(r.nx == null ? "" : r.nx.toFixed(4), 7) + pad(r.ny == null ? "" : r.ny.toFixed(4), 7) +
               pad(r.button, 4) + pad(r.buttons, 5) + pad(r.ptype, 7) + r.target)
      })

      L.push("")
      L.push("--- read this as ---")
      L.push("  pointerdown/up present  -> Q01 yes. Tiers 2 and 3 are on the table and")
      L.push("     replay must dispatch real PointerEvents.")
      L.push("  only mouseup            -> Q01 no. Press-only. Drag is not capturable")
      L.push("     from the DOM and tier 3 is dead.")
      L.push("  pointermove while down  -> drag is capturable; check the rate above.")
      L.push("  target column all <svg-ns> -> confirms why element naming fails here.")
      L.push("  'at' column: doc but never instr -> the event does not reach the")
      L.push("     instrument element; capture must stay on document.")
      console.log(L.join("\n"))
      return L.length
    }
  }

  console.log("[P02] listening on " + (instr ? "document + " + describe(instr) : "document") +
              ". Go click / hold / drag the panel in the cockpit, then run __P02.report()")
})();
